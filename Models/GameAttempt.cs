namespace KuSaFeBackend.Models;

public class GameAttempt
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid GameId { get; set; }
    public Game Game { get; set; } = null!;

    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }

    public long TotalTimeMs { get; set; }

    public int Score { get; set; }
    public int MaxScore { get; set; }
    public bool IsPerfect { get; set; }

    public ICollection<GameTaskAnswer> Answers { get; set; } = new List<GameTaskAnswer>();
}
