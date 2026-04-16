using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class GameTask
{
    public Guid Id { get; set; }

    public Guid GameId { get; set; }
    public Game Game { get; set; } = null!;

    public GameTaskType Type { get; set; }
    public int Order { get; set; }

    [Required]
    public string Text { get; set; } = null!;

    public int Points { get; set; }
    public int TimeLimitMs { get; set; }

    public Guid? CorrectOptionId { get; set; }
    public string? OpenEndedAcceptedAnswer { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<AnswerOption> Options { get; set; } = new List<AnswerOption>();
    public ICollection<GameTaskAnswer> Answers { get; set; } = new List<GameTaskAnswer>();
}
