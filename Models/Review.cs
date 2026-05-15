using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class Review
{
    public Guid Id { get; set; }

    public Guid? GameId { get; set; }
    public Game? Game { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public int Rating { get; set; }

    [Required, MaxLength(2000)]
    public string Text { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
