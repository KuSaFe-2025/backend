namespace KuSaFeBackend.Models;

public class GameTaskAnswer
{
    public Guid Id { get; set; }

    public Guid AttemptId { get; set; }
    public GameAttempt Attempt { get; set; } = null!;

    public Guid GameTaskId { get; set; }
    public GameTask GameTask { get; set; } = null!;

    public Guid? SelectedOptionId { get; set; }
    public AnswerOption? SelectedOption { get; set; }

    public string? TextAnswer { get; set; }
    public string? SubmittedOrder { get; set; }

    public bool? IsCorrect { get; set; }
    public int TimeSpentMs { get; set; }
}
