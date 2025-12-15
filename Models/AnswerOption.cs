using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class AnswerOption
{
    public Guid Id { get; set; }

    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    [Required]
    public string Text { get; set; } = null!;

    public bool IsActive { get; set; } = true;
}
