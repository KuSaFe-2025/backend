using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class User
{
    public Guid Id { get; set; }

    [Required, MaxLength(320)]
    public string Email { get; set; } = null!;

    // Лучше чем чистый SHA256: хеш формата PBKDF2/BCrypt/ASP.NET PasswordHasher (строкой)
    [Required, MaxLength(500)]
    public string PasswordHash { get; set; } = null!;

    [Required, MaxLength(64)]
    public string DisplayName { get; set; } = null!;

    public bool IsAdmin { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Game> Games { get; set; } = new List<Game>();
    public ICollection<GameAttempt> Attempts { get; set; } = new List<GameAttempt>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<Review> Reviews { get; set; } = new List<Review>();
}
