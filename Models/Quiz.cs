using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class Quiz
{
    public Guid Id { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = null!;

    public string? Description { get; set; }
    public DescriptionFormat DescriptionFormat { get; set; } = DescriptionFormat.Markdown;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Question> Questions { get; set; } = new List<Question>();
    public ICollection<QuizAttempt> Attempts { get; set; } = new List<QuizAttempt>();
}
