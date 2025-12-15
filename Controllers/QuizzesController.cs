using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Route("v1/quizzes")]
public class QuizzesController : ControllerBase
{
    private readonly AppDbContext _db;
    public QuizzesController(AppDbContext db) => _db = db;
    public record QuizListItemDto(Guid Id, string Title, string? Description, DescriptionFormat DescriptionFormat, int QuestionsCount);

    // Публично: список квизов для главной
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var items = await _db.Quizzes
            .AsNoTracking()
            .OrderByDescending(q => q.CreatedAtUtc)
            .Select(q => new QuizListItemDto(
                q.Id,
                q.Title,
                q.Description,
                q.DescriptionFormat,
                q.Questions.Count
            ))
            .ToListAsync();

        return Ok(items);
    }

    // Публично: получить квиз (пока без вопросов, чисто мета)
    [HttpGet("{quizId:guid}")]
    public async Task<IActionResult> GetOne(Guid quizId)
    {
        var q = await _db.Quizzes
            .AsNoTracking()
            .Where(x => x.Id == quizId)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Description,
                x.DescriptionFormat,
                x.CreatedAtUtc,
                QuestionsCount = x.Questions.Count
            })
            .FirstOrDefaultAsync();

        return q is null ? NotFound() : Ok(q);
    }
}
