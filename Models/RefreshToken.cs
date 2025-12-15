using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Храним ХЕШ токена, не сам токен
    [Required, MaxLength(200)]
    public string TokenHash { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    // для rotation (можно null)
    [MaxLength(200)]
    public string? ReplacedByTokenHash { get; set; }

    [MaxLength(64)]
    public string? CreatedByIp { get; set; }

    [MaxLength(300)]
    public string? UserAgent { get; set; }
}
