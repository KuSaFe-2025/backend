using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class Question
{
    public Guid Id { get; set; }

    public Guid QuizId { get; set; }
    public Quiz Quiz { get; set; } = null!;

    [Required]
    public string Text { get; set; } = null!;

    public int Points { get; set; }               // баллы за правильный
    public int TimeLimitMs { get; set; }
    public int Order { get; set; }                // порядок в квизе

    // один правильный вариант:
    public Guid? CorrectOptionId { get; set; }

    public ICollection<AnswerOption> Options { get; set; } = new List<AnswerOption>();
}
