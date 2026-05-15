using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Authorize(Policy = "AdminOnly")]
[Route("v1/admin/games")]
public class AdminGamesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminGamesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> ListAll()
    {
        var items = await _db.Games
            .AsNoTracking()
            .OrderByDescending(g => g.UpdatedAtUtc)
            .Select(g => new GameListItemDto(
                g.Id,
                g.Title,
                g.Description,
                g.DescriptionFormat,
                g.Tasks.Count,
                g.ThemeColor,
                g.Status,
                g.LastModeratedAtUtc,
                g.ModerationDecision,
                g.ModerationYesVotes,
                g.ModerationNoVotes,
                g.OwnerUser.DisplayName,
                true
            ))
            .ToListAsync();

        return Ok(items);
    }

    [HttpGet("{gameId:guid}")]
    public async Task<IActionResult> GetOne(Guid gameId)
    {
        var game = await _db.Games
            .AsNoTracking()
            .Where(g => g.Id == gameId)
            .Select(MyGamesController.ToEditorDto())
            .FirstOrDefaultAsync();

        return game is null ? NotFound() : Ok(game);
    }

    [HttpPut("{gameId:guid}")]
    public async Task<IActionResult> Update(Guid gameId, [FromBody] GameUpsertRequest req)
    {
        var game = await _db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        var title = (req.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest("Title is required.");
        if (title.Length > 200) return BadRequest("Title too long (max 200).");

        game.Title = title;
        game.Description = req.Description;
        game.DescriptionFormat = req.DescriptionFormat;
        game.ThemeColor = MyGamesController.NormalizeHexColor(req.ThemeColor) ?? game.ThemeColor ?? "#7C3AED";
        MyGamesController.TouchForContentChange(game);

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{gameId:guid}")]
    public async Task<IActionResult> Delete(Guid gameId)
    {
        var game = await _db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        _db.Games.Remove(game);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{gameId:guid}/tasks")]
    public async Task<IActionResult> CreateTask(Guid gameId, [FromBody] GameTaskUpsertRequest req)
    {
        var game = await _db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        try
        {
            var requestedOrder = req.Order;
            var task = MyGamesController.BuildTask(req, gameId);
            var correctOptionId = task.CorrectOptionId;
            task.CorrectOptionId = null;
            task.Order = await MyGamesController.NextTemporaryTaskOrder(_db, gameId);
            _db.GameTasks.Add(task);
            MyGamesController.TouchForContentChange(game);
            await _db.SaveChangesAsync();

            if (correctOptionId.HasValue) task.CorrectOptionId = correctOptionId;
            var tasks = await _db.GameTasks.Where(t => t.GameId == gameId).OrderBy(t => t.Order).ToListAsync();
            await MyGamesController.ReorderTasksAsync(_db, tasks, task.Id, requestedOrder);
            return CreatedAtAction(nameof(GetOne), new { gameId }, new { task.Id });
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }
    }

    [HttpPut("{gameId:guid}/tasks/{taskId:guid}")]
    public async Task<IActionResult> UpdateTask(Guid gameId, Guid taskId, [FromBody] GameTaskUpsertRequest req)
    {
        var game = await _db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        var tasks = await _db.GameTasks
            .Include(t => t.Options)
            .Where(t => t.GameId == gameId)
            .OrderBy(t => t.Order)
            .ToListAsync();

        var task = tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null) return NotFound();

        try
        {
            MyGamesController.ApplyTaskUpdate(task, req, updateOrder: false);
            MyGamesController.TouchForContentChange(game);
            await MyGamesController.ReorderTasksAsync(_db, tasks, task.Id, req.Order);
            return NoContent();
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }
    }

    [HttpDelete("{gameId:guid}/tasks/{taskId:guid}")]
    public async Task<IActionResult> DeleteTask(Guid gameId, Guid taskId)
    {
        var game = await _db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        var task = await _db.GameTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.GameId == gameId);
        if (task is null) return NotFound();

        var attemptIds = await _db.GameTaskAnswers
            .Where(a => a.GameTaskId == taskId && a.Attempt.GameId == gameId)
            .Select(a => a.AttemptId)
            .Distinct()
            .ToListAsync();

        var answers = await _db.GameTaskAnswers
            .Where(a => a.GameTaskId == taskId && a.Attempt.GameId == gameId)
            .ToListAsync();
        _db.GameTaskAnswers.RemoveRange(answers);
        task.CorrectOptionId = null;
        _db.GameTasks.Remove(task);
        MyGamesController.TouchForContentChange(game);
        await _db.SaveChangesAsync();
        await MyGamesController.NormalizeTaskOrdersAsync(_db, gameId);
        await MyGamesController.RecalculateAttemptsAsync(_db, attemptIds);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{gameId:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid gameId, [FromQuery] GameStatus status)
    {
        var game = await _db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        game.Status = status;
        game.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{gameId:guid}/stats")]
    public async Task<IActionResult> Stats(Guid gameId)
    {
        var game = await _db.Games
            .Include(g => g.Tasks).ThenInclude(t => t.Options)
            .Include(g => g.Attempts).ThenInclude(a => a.Answers)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        return game is null ? NotFound() : Ok(MyGamesController.BuildStatsDto(game));
    }

    [HttpDelete("{gameId:guid}/stats")]
    public async Task<IActionResult> ResetStats(Guid gameId)
    {
        var game = await _db.Games.Include(g => g.Attempts).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        _db.GameAttempts.RemoveRange(game.Attempts);
        game.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{gameId:guid}/tasks/{taskId:guid}/stats")]
    public async Task<IActionResult> ResetTaskStats(Guid gameId, Guid taskId)
    {
        var game = await _db.Games.FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        var taskExists = await _db.GameTasks.AnyAsync(t => t.Id == taskId && t.GameId == gameId);
        if (!taskExists) return NotFound();

        var attemptIds = await _db.GameTaskAnswers
            .Where(a => a.GameTaskId == taskId && a.Attempt.GameId == gameId)
            .Select(a => a.AttemptId)
            .Distinct()
            .ToListAsync();

        var answers = await _db.GameTaskAnswers
            .Where(a => a.GameTaskId == taskId && a.Attempt.GameId == gameId)
            .ToListAsync();
        _db.GameTaskAnswers.RemoveRange(answers);
        await _db.SaveChangesAsync();

        await MyGamesController.RecalculateAttemptsAsync(_db, attemptIds);
        game.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{gameId:guid}/reviews")]
    public async Task<IActionResult> Reviews(Guid gameId, [FromQuery] int skip = 0, [FromQuery] int take = 10, [FromQuery] string sort = "new")
    {
        var exists = await _db.Games.AsNoTracking().AnyAsync(g => g.Id == gameId);
        if (!exists) return NotFound();

        return Ok(await GamesController.BuildReviewsPage(_db.Reviews.AsNoTracking().Where(r => r.GameId == gameId), true, skip, take, sort));
    }

    [HttpGet("{gameId:guid}/stats/export.csv")]
    public async Task<IActionResult> ExportStatsCsv(Guid gameId)
    {
        var game = await _db.Games
            .Include(g => g.Tasks).ThenInclude(t => t.Options)
            .Include(g => g.Attempts).ThenInclude(a => a.User)
            .Include(g => g.Attempts).ThenInclude(a => a.Answers).ThenInclude(a => a.SelectedOption)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        return game is null
            ? NotFound()
            : File(System.Text.Encoding.UTF8.GetBytes(MyGamesController.BuildResultsCsv(game)), "text/csv; charset=utf-8", $"game-{game.Id}-results.csv");
    }

    [HttpGet("{gameId:guid}/tasks/{taskId:guid}/open-answers")]
    public async Task<IActionResult> GetOpenAnswers(Guid gameId, Guid taskId, [FromQuery] int skip = 0, [FromQuery] int take = 5)
    {
        var exists = await _db.GameTasks
            .AsNoTracking()
            .AnyAsync(t => t.Id == taskId && t.GameId == gameId);

        return exists
            ? Ok(await MyGamesController.BuildOpenAnswersPage(_db, gameId, taskId, skip, take))
            : NotFound();
    }
}
