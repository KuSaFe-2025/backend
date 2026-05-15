using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class Game
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }
    public User OwnerUser { get; set; } = null!;

    [Required, MaxLength(200)]
    public string Title { get; set; } = null!;

    public string? Description { get; set; }
    public DescriptionFormat DescriptionFormat { get; set; } = DescriptionFormat.Markdown;
    public string? ThemeColor { get; set; }
    public bool IsPrivate { get; set; }
    public int? MaxAttemptsPerUser { get; set; }
    public DateTime? AvailableFromUtc { get; set; }
    public DateTime? AvailableUntilUtc { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Unverified;
    public DateTime? LastModeratedAtUtc { get; set; }
    public string? ModerationDecision { get; set; }
    public int ModerationYesVotes { get; set; }
    public int ModerationNoVotes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<GameTask> Tasks { get; set; } = new List<GameTask>();
    public ICollection<GameAttempt> Attempts { get; set; } = new List<GameAttempt>();
    public ICollection<Review> Reviews { get; set; } = new List<Review>();
}
