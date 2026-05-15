using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Route("v1/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly PasswordHasher<User> _hasher = new();

    public AuthController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public record RegisterRequest(string Email, string Password, string DisplayName);
    public record LoginRequest(string Email, string Password);

    public record AuthResponse(Guid UserId, string Email, string DisplayName, string AccessToken, int ExpiresInSeconds);

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var display = (req.DisplayName ?? "").Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(display))
            return BadRequest("Email, password, displayName are required.");

        if (display.Length > 64) return BadRequest("DisplayName too long (max 64).");
        if (req.Password.Length < 8) return BadRequest("Password too short (min 8).");

        var exists = await _db.Users.AnyAsync(x => x.Email == email);
        if (exists) return Conflict("Email already registered.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = display,
            CreatedAtUtc = DateTime.UtcNow,
            IsAdmin = false
        };
        user.PasswordHash = _hasher.HashPassword(user, req.Password);

        _db.Users.Add(user);

        var (refreshToken, refreshHash, refreshExp) = CreateRefreshToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = refreshExp,
            CreatedByIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await _db.SaveChangesAsync();

        var (accessToken, accessExpSeconds) = CreateAccessToken(user);
        SetRefreshCookie(refreshToken, refreshExp);

        return Ok(new AuthResponse(user.Id, user.Email, user.DisplayName, accessToken, accessExpSeconds));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.Email == email);
        if (user is null) return Unauthorized("Invalid credentials.");

        var vr = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "");
        if (vr == PasswordVerificationResult.Failed) return Unauthorized("Invalid credentials.");

        var (refreshToken, refreshHash, refreshExp) = CreateRefreshToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = refreshExp,
            CreatedByIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _db.SaveChangesAsync();

        var (accessToken, accessExpSeconds) = CreateAccessToken(user);
        SetRefreshCookie(refreshToken, refreshExp);

        return Ok(new AuthResponse(user.Id, user.Email, user.DisplayName, accessToken, accessExpSeconds));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var raw = Request.Cookies["refresh_token"];
        if (string.IsNullOrWhiteSpace(raw))
            return Unauthorized("No refresh_token cookie.");

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var tokenHash = Convert.ToHexString(hashBytes);

        var rt = await _db.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash);

        if (rt is null)
            return Unauthorized("Invalid refresh token.");

        if (rt.ExpiresAtUtc <= DateTime.UtcNow)
            return Unauthorized("Refresh token expired.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == rt.UserId);
        if (user is null)
            return Unauthorized("User not found.");

        // Rotation: refresh-токен одноразовый
        _db.RefreshTokens.Remove(new RefreshToken { Id = rt.Id });

        var (newRefresh, newHash, newExp) = CreateRefreshToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = newHash,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = newExp,
            CreatedByIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await _db.SaveChangesAsync();

        var (accessToken, accessExpSeconds) = CreateAccessToken(user);
        SetRefreshCookie(newRefresh, newExp);

        return Ok(new AuthResponse(user.Id, user.Email, user.DisplayName, accessToken, accessExpSeconds));
    }

    private (string jwt, int expiresInSeconds) CreateAccessToken(User user)
    {
        var keyStr = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        var issuer = _cfg["Jwt:Issuer"];
        var audience = _cfg["Jwt:Audience"];
        var minutes = int.TryParse(_cfg["Jwt:AccessMinutes"], out var m) ? m : 15;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyStr));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expires = DateTime.UtcNow.AddMinutes(minutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("displayName", user.DisplayName),
            new("isAdmin", user.IsAdmin ? "true" : "false")
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return (jwt, minutes * 60);
    }

    private (string token, string tokenHash, DateTime expiresAtUtc) CreateRefreshToken()
    {
        var days = int.TryParse(_cfg["Jwt:RefreshDays"], out var d) ? d : 30;

        var bytes = RandomNumberGenerator.GetBytes(64);
        var token = Base64UrlEncode(bytes);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var tokenHash = Convert.ToHexString(hashBytes); // uppercase hex

        return (token, tokenHash, DateTime.UtcNow.AddDays(days));
    }

    private void SetRefreshCookie(string refreshToken, DateTime expiresAtUtc)
    {
        var secure = !bool.TryParse(_cfg["Jwt:RefreshCookieSecure"], out var secureCfg) || secureCfg;
        var sameSite = Enum.TryParse<SameSiteMode>(_cfg["Jwt:RefreshCookieSameSite"], true, out var sameSiteCfg)
            ? sameSiteCfg
            : SameSiteMode.None;

        Response.Cookies.Append("refresh_token", refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = sameSite,
            Expires = expiresAtUtc,
            Path = "/"
        });
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
