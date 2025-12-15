namespace KuSaFeBackend.Models;

public class AttemptAnswer
{
    public Guid Id { get; set; }

    public Guid AttemptId { get; set; }
    public QuizAttempt Attempt { get; set; } = null!;

    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public Guid SelectedOptionId { get; set; }
    public AnswerOption SelectedOption { get; set; } = null!;

    public bool IsCorrect { get; set; }
    public int TimeSpentSeconds { get; set; }
}
