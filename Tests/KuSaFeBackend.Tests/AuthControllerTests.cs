using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace KuSaFeBackend.Tests;

public class AuthControllerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Register_Succeeds_ReturnsAuthResponse_AndSetsRefreshCookie()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var req = new
        {
            email = "  ArSeNiYFeDoRoV190405@GMAIL.COM  ",
            password = "password123",
            displayName = "Arseniy"
        };

        var resp = await client.PostAsJsonAsync("/v1/auth/register", req, JsonOpts);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        Assert.NotNull(body);

        // email нормализуется в lower + trim
        Assert.Equal("arseniyfedorov190405@gmail.com", body!.Email);
        Assert.Equal("Arseniy", body.DisplayName);
        Assert.NotEqual(Guid.Empty, body.UserId);

        Assert.False(string.IsNullOrWhiteSpace(body.AccessToken));
        Assert.True(body.ExpiresInSeconds > 0);

        // refresh cookie установлен
        Assert.Contains(resp.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : Array.Empty<string>(),
            c => c.StartsWith("refresh_token=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Register_Fails_WhenMissingFields()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/auth/register",
            new { email = "", password = "", displayName = "" }, JsonOpts);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Fails_WhenPasswordTooShort()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/auth/register",
            new { email = "a@b.com", password = "1234567", displayName = "A" }, JsonOpts);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Fails_WhenDisplayNameTooLong()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var longName = new string('x', 65);

        var resp = await client.PostAsJsonAsync("/v1/auth/register",
            new { email = "a@b.com", password = "password123", displayName = longName }, JsonOpts);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Fails_WithConflict_WhenEmailAlreadyRegistered()
    {
        await using var app = new TestAppFactory();

        // сидим уже существующего пользователя
        await app.SeedAsync(db =>
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = "x@y.com",
                DisplayName = "X",
                CreatedAtUtc = DateTime.UtcNow,
                IsAdmin = false
            };
            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, "password123");
            db.Users.Add(user);
            return Task.CompletedTask;
        });

        var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/auth/register",
            new { email = "x@y.com", password = "password123", displayName = "X2" }, JsonOpts);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Login_Succeeds_SetsRefreshCookie()
    {
        await using var app = new TestAppFactory();

        var userId = Guid.NewGuid();

        await app.SeedAsync(db =>
        {
            var user = new User
            {
                Id = userId,
                Email = "user@test.com",
                DisplayName = "User",
                CreatedAtUtc = DateTime.UtcNow,
                IsAdmin = false
            };
            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, "password123");
            db.Users.Add(user);
            return Task.CompletedTask;
        });

        var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/auth/login",
            new { email = "user@test.com", password = "password123" }, JsonOpts);

        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        Assert.NotNull(body);
        Assert.Equal(userId, body!.UserId);

        Assert.Contains(resp.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : Array.Empty<string>(),
            c => c.StartsWith("refresh_token=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Login_Fails_With401_OnWrongPassword()
    {
        await using var app = new TestAppFactory();

        await app.SeedAsync(db =>
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = "user@test.com",
                DisplayName = "User",
                CreatedAtUtc = DateTime.UtcNow,
                IsAdmin = false
            };
            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, "password123");
            db.Users.Add(user);
            return Task.CompletedTask;
        });

        var client = app.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/auth/login",
            new { email = "user@test.com", password = "WRONG_PASSWORD" }, JsonOpts);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_Succeeds_AndRotatesToken_OldTokenStopsWorking()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        // 1) register → получаем refresh cookie
        var reg = await client.PostAsJsonAsync("/v1/auth/register",
            new { email = "r@t.com", password = "password123", displayName = "R" }, JsonOpts);
        reg.EnsureSuccessStatusCode();

        var refreshCookie1 = ExtractRefreshCookie(reg);
        Assert.False(string.IsNullOrWhiteSpace(refreshCookie1));

        // 2) refresh с cookie #1 → должен успех + новая cookie
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        req1.Headers.Add("Cookie", $"refresh_token={refreshCookie1}");
        var r1 = await client.SendAsync(req1);
        r1.EnsureSuccessStatusCode();

        var refreshCookie2 = ExtractRefreshCookie(r1);
        Assert.False(string.IsNullOrWhiteSpace(refreshCookie2));
        Assert.NotEqual(refreshCookie1, refreshCookie2);

        // 3) попробовать refresh со старой cookie #1 → должно быть 401 (одноразовый)
        var reqOld = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        reqOld.Headers.Add("Cookie", $"refresh_token={refreshCookie1}");
        var oldResp = await client.SendAsync(reqOld);

        Assert.Equal(HttpStatusCode.Unauthorized, oldResp.StatusCode);

        // 4) refresh с новой cookie #2 → снова успех
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        req2.Headers.Add("Cookie", $"refresh_token={refreshCookie2}");
        var r2 = await client.SendAsync(req2);

        r2.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Refresh_Fails_With401_WhenNoCookie()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var resp = await client.PostAsync("/v1/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_Fails_With401_WhenCookieInvalid()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        req.Headers.Add("Cookie", "refresh_token=totally-invalid-token");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_Fails_With401_WhenCookieExpired()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        // Создаём пользователя и протухший refresh в БД вручную,
        // НО нам нужно знать raw token, чтобы положить его в cookie и чтобы его SHA256 совпал.
        // Поэтому: сами генерим raw, сами считаем hash и пишем в БД.
        var raw = "raw_refresh_token_for_test_" + Guid.NewGuid();

        await app.SeedAsync(async db =>
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = "exp@test.com",
                DisplayName = "Exp",
                CreatedAtUtc = DateTime.UtcNow,
                IsAdmin = false
            };
            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, "password123");
            db.Users.Add(user);

            var hash = ComputeSha256Hex(raw);

            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = hash,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1), // уже протух
                CreatedByIp = "127.0.0.1",
                UserAgent = "tests"
            });

            await db.SaveChangesAsync();
        });

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        req.Headers.Add("Cookie", $"refresh_token={raw}");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- helpers ----

    private static string ExtractRefreshCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            return "";

        // Ищем "refresh_token=....;"
        var header = setCookies.FirstOrDefault(h => h.StartsWith("refresh_token=", StringComparison.OrdinalIgnoreCase));
        if (header is null) return "";

        var valuePart = header.Split(';', 2)[0]; // refresh_token=XYZ
        var token = valuePart.Substring("refresh_token=".Length);
        return token;
    }

    private static string ComputeSha256Hex(string raw)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    public record AuthResponse(Guid UserId, string Email, string DisplayName, string AccessToken, int ExpiresInSeconds);
}