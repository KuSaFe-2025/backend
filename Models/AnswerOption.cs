using System.ComponentModel.DataAnnotations;

namespace KuSaFeBackend.Models;

public class AnswerOption
{
    public Guid Id { get; set; }

    public Guid GameTaskId { get; set; }
    public GameTask GameTask { get; set; } = null!;

    [Required]
    public string Text { get; set; } = null!;

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsCorrect { get; set; }
}
