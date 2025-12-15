using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Authorize(Policy = "AdminOnly")]
[Route("v1/admin/quizzes")]
public class AdminQuizzesController : ControllerBase
{
    private readonly AppDbContext _db;
    public AdminQuizzesController(AppDbContext db) => _db = db;

    // -------- DTOs --------
    public record OptionDto(Guid Id, string Text);

    public record QuizUpsertRequest(string Title, string? Description, DescriptionFormat DescriptionFormat, string? ThemeColor);

    public record QuestionDto(
        Guid Id,
        int Order,
        string Text,
        int Points,
        int TimeLimitMs,
        Guid? CorrectOptionId,
        List<OptionDto> Options
    );

    public record QuizWithQuestionsDto(
        Guid Id,
        string Title,
        string? Description,
        DescriptionFormat DescriptionFormat,
        string? ThemeColor,
        DateTime CreatedAtUtc,
        List<QuestionDto> Questions
    );


    public record CreateQuestionRequest(
        int Order,
        string Text,
        int Points,
        int TimeLimitMs,
        List<string> Options,
        int CorrectOptionIndex // 0-based
    );

    public record UpdateQuestionRequest(
        int Order,
        string Text,
        int Points,
        int TimeLimitMs,
        List<string> Options,
        int CorrectOptionIndex // 0-based
    );

    // Админ: создать квиз
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] QuizUpsertRequest req)
    {
        var title = (req.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest("Title is required.");
        if (title.Length > 200) return BadRequest("Title too long (max 200).");

        var themeColor = NormalizeHexColor(req.ThemeColor) ?? "#7C3AED";
        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = req.Description,
            DescriptionFormat = req.DescriptionFormat,
            CreatedAtUtc = DateTime.UtcNow,
            ThemeColor = themeColor,
        };

        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetQuizWithQuestions), new { quizId = quiz.Id }, new { quiz.Id });
    }

    // Админ: обновить квиз
    [HttpPut("{quizId:guid}")]
    public async Task<IActionResult> Update(Guid quizId, [FromBody] QuizUpsertRequest req)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(x => x.Id == quizId);
        if (quiz is null) return NotFound();

        var title = (req.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest("Title is required.");
        if (title.Length > 200) return BadRequest("Title too long (max 200).");

        quiz.ThemeColor = NormalizeHexColor(req.ThemeColor) ?? quiz.ThemeColor ?? "#7C3AED";
        quiz.Title = title;
        quiz.Description = req.Description;
        quiz.DescriptionFormat = req.DescriptionFormat;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Админ: удалить квиз (каскадом удалятся вопросы/опции/attempt-ы если так настроено)
    [HttpDelete("{quizId:guid}")]
    public async Task<IActionResult> Delete(Guid quizId)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(x => x.Id == quizId);
        if (quiz is null) return NotFound();

        _db.Quizzes.Remove(quiz);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // -------- Admin read: quiz with questions --------
    [HttpGet("{quizId:guid}")]
    public async Task<IActionResult> GetQuizWithQuestions(Guid quizId)
    {
        var quiz = await _db.Quizzes
            .AsNoTracking()
            .Where(q => q.Id == quizId)
            .Select(q => new QuizWithQuestionsDto(
                q.Id,
                q.Title,
                q.Description,
                q.DescriptionFormat,
                q.ThemeColor,
                q.CreatedAtUtc,
                q.Questions
                    .OrderBy(x => x.Order)
                    .Select(x => new QuestionDto(
                        x.Id,
                        x.Order,
                        x.Text,
                        x.Points,
                        x.TimeLimitMs,
                        x.CorrectOptionId,
                        x.Options
                            .OrderBy(o => o.Id)
                            .Select(o => new OptionDto(o.Id, o.Text))
                            .ToList()
                    ))
                    .ToList()
            ))
            .FirstOrDefaultAsync();

        return quiz is null ? NotFound() : Ok(quiz);
    }

    // -------- Create question --------
    [HttpPost("{quizId:guid}/questions")]
    public async Task<IActionResult> CreateQuestion(Guid quizId, [FromBody] CreateQuestionRequest req)
    {
        var quizExists = await _db.Quizzes.AnyAsync(q => q.Id == quizId);
        if (!quizExists) return NotFound("Quiz not found.");

        ValidateQuestionInput(req.Text, req.Points, req.TimeLimitMs, req.Options, req.CorrectOptionIndex);

        var questionId = Guid.NewGuid();
        var optionEntities = req.Options
            .Select(text => new AnswerOption
            {
                Id = Guid.NewGuid(),
                QuestionId = questionId,
                Text = text
            })
            .ToList();

        var correctId = optionEntities[req.CorrectOptionIndex].Id;

        var question = new Question
        {
            Id = questionId,
            QuizId = quizId,
            Order = req.Order,
            Text = req.Text.Trim(),
            Points = req.Points,
            TimeLimitMs = req.TimeLimitMs,
            CorrectOptionId = null,
            Options = optionEntities
        };

        try
        {
            _db.Questions.Add(question);
            await _db.SaveChangesAsync();        // вставились Question + Options

            question.CorrectOptionId = correctId;
            await _db.SaveChangesAsync();        // теперь апдейтнули correct option
        }
        catch (DbUpdateException e)
        {
            // Часто это уникальный индекс (QuizId, Order)
            return BadRequest("Failed to create question (maybe duplicate order?). " + e.Message);
        }

        return CreatedAtAction(nameof(GetQuizWithQuestions), new { quizId }, new { question.Id });
    }

    // -------- Update question (replaces options) --------
    [HttpPut("{quizId:guid}/questions/{questionId:guid}")]
    public async Task<IActionResult> UpdateQuestion(Guid quizId, Guid questionId, [FromBody] UpdateQuestionRequest req)
    {
        var question = await _db.Questions
            .Include(q => q.Options)
            .FirstOrDefaultAsync(q => q.Id == questionId && q.QuizId == quizId);

        if (question is null) return NotFound();

        ValidateQuestionInput(req.Text, req.Points, req.TimeLimitMs, req.Options, req.CorrectOptionIndex);

        // 1) обновляем поля вопроса (пока без correct)
        question.Order = req.Order;
        question.Text = req.Text.Trim();
        question.Points = req.Points;
        question.TimeLimitMs = req.TimeLimitMs;

        // 2) отцепляем CorrectOptionId (иначе FK помешает удалить старые опции)
        question.CorrectOptionId = null;

        try
        {
            await _db.SaveChangesAsync(); // фиксируем обновление вопроса + NULL correct
        }
        catch (DbUpdateException e)
        {
            return BadRequest("Failed to update question (maybe duplicate order?). " + e.Message);
        }

        // 3) удаляем старые опции
        _db.AnswerOptions.RemoveRange(question.Options);

        try
        {
            await _db.SaveChangesAsync(); // фиксируем удаление
        }
        catch (DbUpdateException e)
        {
            return BadRequest("Failed to delete old options. " + e.Message);
        }

        // 4) создаём и сохраняем новые опции
        var newOptions = req.Options
            .Select(text => new AnswerOption
            {
                Id = Guid.NewGuid(),
                QuestionId = question.Id,
                Text = text.Trim()
            })
            .ToList();

        _db.AnswerOptions.AddRange(newOptions);

        try
        {
            await _db.SaveChangesAsync(); // вставили новые опции
        }
        catch (DbUpdateException e)
        {
            return BadRequest("Failed to insert new options. " + e.Message);
        }

        // 5) ставим correct на существующую (уже вставленную) опцию
        question.CorrectOptionId = newOptions[req.CorrectOptionIndex].Id;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException e)
        {
            return BadRequest("Failed to set correct option. " + e.Message);
        }

        return NoContent();
    }


    // -------- Delete question --------
    [HttpDelete("{quizId:guid}/questions/{questionId:guid}")]
    public async Task<IActionResult> DeleteQuestion(Guid quizId, Guid questionId)
    {
        var question = await _db.Questions.FirstOrDefaultAsync(q => q.Id == questionId && q.QuizId == quizId);
        if (question is null) return NotFound();

        _db.Questions.Remove(question);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // -------- Validation helper --------
    private static void ValidateQuestionInput(string text, int points, int timeLimitMs, List<string> options, int correctIndex)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Question text is required.");
        if (points < 0) throw new ArgumentException("Points must be >= 0.");
        if (timeLimitMs <= 0) throw new ArgumentException("TimeLimitMs must be > 0.");
        if (options is null || options.Count < 2) throw new ArgumentException("At least 2 options are required.");
        if (correctIndex < 0 || correctIndex >= options.Count) throw new ArgumentException("CorrectOptionIndex out of range.");

        for (int i = 0; i < options.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(options[i]))
                throw new ArgumentException($"Option[{i}] is empty.");
            options[i] = options[i].Trim();
        }
    }

    private static string? NormalizeHexColor(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var s = input.Trim();
        if (!s.StartsWith("#")) s = "#" + s;

        // поддержим только #RRGGBB
        if (s.Length != 7) return null;

        for (int i = 1; i < 7; i++)
        {
            var c = s[i];
            var isHex =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');

            if (!isHex) return null;
        }

        return s.ToUpperInvariant();
    }

}
