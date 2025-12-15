namespace KuSaFeBackend.Models;

public class QuizAttempt
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid QuizId { get; set; }
    public Quiz Quiz { get; set; } = null!;

    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }

    public int TotalTimeSeconds { get; set; }

    public int Score { get; set; }
    public int MaxScore { get; set; }

    // “максимальный балл” = лидерборд
    public bool IsPerfect { get; set; }

    public ICollection<AttemptAnswer> Answers { get; set; } = new List<AttemptAnswer>();
}
