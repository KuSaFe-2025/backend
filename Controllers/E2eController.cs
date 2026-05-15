using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Route("v1/e2e")]
public class E2eController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly PasswordHasher<User> _hasher = new();

    public E2eController(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        if (!IsE2E()) return NotFound();

        await _db.Database.EnsureDeletedAsync();
        await _db.Database.EnsureCreatedAsync();
        return NoContent();
    }

    [HttpPost("seed-users")]
    public async Task<IActionResult> SeedUsers()
    {
        if (!IsE2E()) return NotFound();

        await UpsertUser("author@e2e.test", "Author", "password123", false);
        await UpsertUser("player@e2e.test", "Player", "password123", false);
        await UpsertUser("admin@e2e.test", "Admin", "password123", true);
        await _db.SaveChangesAsync();
        return Ok(new
        {
            author = "author@e2e.test",
            player = "player@e2e.test",
            admin = "admin@e2e.test",
            password = "password123"
        });
    }

    private async Task UpsertUser(string email, string displayName, string password, bool isAdmin)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                DisplayName = displayName,
                CreatedAtUtc = DateTime.UtcNow
            };
            _db.Users.Add(user);
        }

        user.IsAdmin = isAdmin;
        user.PasswordHash = _hasher.HashPassword(user, password);
    }

    private bool IsE2E() => string.Equals(_env.EnvironmentName, "E2E", StringComparison.OrdinalIgnoreCase);
}
