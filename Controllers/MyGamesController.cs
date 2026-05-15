using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using KuSaFeBackend.Services;
using System.Text;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Authorize]
[Route("v1/my/games")]
public class MyGamesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IGameModerationService _moderation;
    private readonly IAiAssistantService _ai;

    public MyGamesController(AppDbContext db, IGameModerationService moderation, IAiAssistantService ai)
    {
        _db = db;
        _moderation = moderation;
        _ai = ai;
    }

    [HttpGet]
    public async Task<IActionResult> ListMine()
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var items = await _db.Games
            .AsNoTracking()
            .Where(g => g.OwnerUserId == userId.Value)
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

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] GameUpsertRequest req)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var title = (req.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest("Title is required.");
        if (title.Length > 200) return BadRequest("Title too long (max 200).");

        var game = new Game
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId.Value,
            Title = title,
            Description = req.Description,
            DescriptionFormat = req.DescriptionFormat,
            ThemeColor = NormalizeHexColor(req.ThemeColor) ?? "#7C3AED",
            Status = GameStatus.Unverified,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.Games.Add(game);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetMine), new { gameId = game.Id }, new { game.Id });
    }

    [HttpGet("{gameId:guid}")]
    public async Task<IActionResult> GetMine(Guid gameId)
    {
        var game = await GetOwnedGameQuery()
            .Where(g => g.Id == gameId)
            .Select(ToEditorDto())
            .FirstOrDefaultAsync();

        return game is null ? NotFound() : Ok(game);
    }

    [HttpPut("{gameId:guid}")]
    public async Task<IActionResult> Update(Guid gameId, [FromBody] GameUpsertRequest req)
    {
        var game = await GetOwnedGameQuery().FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        var title = (req.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest("Title is required.");
        if (title.Length > 200) return BadRequest("Title too long (max 200).");

        game.Title = title;
        game.Description = req.Description;
        game.DescriptionFormat = req.DescriptionFormat;
        game.ThemeColor = NormalizeHexColor(req.ThemeColor) ?? game.ThemeColor ?? "#7C3AED";
        TouchForContentChange(game);

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{gameId:guid}")]
    public async Task<IActionResult> Delete(Guid gameId)
    {
        var game = await GetOwnedGameQuery().FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        _db.Games.Remove(game);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{gameId:guid}/submit-for-verification")]
    public async Task<IActionResult> SubmitForVerification(Guid gameId)
    {
        var game = await GetOwnedGameQuery()
            .Include(g => g.Tasks)
            .ThenInclude(t => t.Options)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game is null) return NotFound();
        if (!game.Tasks.Any()) return BadRequest("Game must contain at least one task.");

        game.Status = GameStatus.PendingModeration;
        game.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var result = await _moderation.ModerateAsync(game, HttpContext.RequestAborted);
        game.Status = result.Approved ? GameStatus.Verified : GameStatus.Rejected;
        game.LastModeratedAtUtc = DateTime.UtcNow;
        game.ModerationDecision = result.Decision;
        game.ModerationYesVotes = result.YesVotes;
        game.ModerationNoVotes = result.NoVotes;
        game.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new
        {
            game.Status,
            game.LastModeratedAtUtc,
            game.ModerationDecision,
            game.ModerationYesVotes,
            game.ModerationNoVotes
        });
    }

    [HttpGet("{gameId:guid}/stats")]
    public async Task<IActionResult> GetStats(Guid gameId)
    {
        var game = await GetOwnedGameQuery()
            .Include(g => g.Tasks).ThenInclude(t => t.Options)
            .Include(g => g.Attempts).ThenInclude(a => a.Answers)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game is null) return NotFound();
        return Ok(BuildStatsDto(game));
    }

    [HttpDelete("{gameId:guid}/stats")]
    public async Task<IActionResult> ResetStats(Guid gameId)
    {
        var game = await GetOwnedGameQuery().Include(g => g.Attempts).FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        _db.GameAttempts.RemoveRange(game.Attempts);
        game.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{gameId:guid}/tasks/{taskId:guid}/stats")]
    public async Task<IActionResult> ResetTaskStats(Guid gameId, Guid taskId)
    {
        var game = await GetOwnedGameQuery().FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        var taskExists = await _db.GameTasks.AnyAsync(t => t.Id == taskId && t.GameId == gameId);
        if (!taskExists) return NotFound();

        var attemptIds = await _db.GameTaskAnswers
            .Where(a => a.GameTaskId == taskId && a.Attempt.GameId == gameId)
            .Select(a => a.AttemptId)
            .Distinct()
            .ToListAsync();

        var answers = await _db.GameTaskAnswers.Where(a => a.GameTaskId == taskId && a.Attempt.GameId == gameId).ToListAsync();
        _db.GameTaskAnswers.RemoveRange(answers);
        await _db.SaveChangesAsync();

        await RecalculateAttemptsAsync(_db, attemptIds);
        game.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{gameId:guid}/reviews")]
    public async Task<IActionResult> GetReviews(Guid gameId, [FromQuery] int skip = 0, [FromQuery] int take = 10, [FromQuery] string sort = "new")
    {
        var exists = await GetOwnedGameQuery().AnyAsync(g => g.Id == gameId);
        if (!exists) return NotFound();

        var canDelete = User.IsCurrentUserAdmin();
        var query = _db.Reviews.AsNoTracking().Where(r => r.GameId == gameId);
        return Ok(await GamesController.BuildReviewsPage(query, canDelete, skip, take, sort));
    }

    [HttpPost("{gameId:guid}/ai/rewrite/stream")]
    public async Task RewriteWithAi(Guid gameId, [FromBody] AiRewriteRequest req)
    {
        var exists = await GetOwnedGameQuery().AnyAsync(g => g.Id == gameId);
        if (!exists)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (CountWords(req.Text) < 2)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("Для AI-переписывания нужно минимум два слова.", HttpContext.RequestAborted);
            return;
        }

        Response.ContentType = "text/plain; charset=utf-8";
        var result = await _ai.RewriteAsync(req.Field, req.Mode, req.Text, HttpContext.RequestAborted);
        foreach (var chunk in Chunk(result, 24))
        {
            await Response.WriteAsync(chunk, HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }
    }

    [HttpPost("{gameId:guid}/ai/suggest-option")]
    public async Task<IActionResult> SuggestOption(Guid gameId, [FromBody] AiSuggestOptionRequest req)
    {
        var exists = await GetOwnedGameQuery().AnyAsync(g => g.Id == gameId);
        if (!exists) return NotFound();

        try
        {
            return Ok(await _ai.SuggestOptionAsync(req, HttpContext.RequestAborted));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(e.Message);
        }
    }

    [HttpPost("{gameId:guid}/ai/suggest-task")]
    public async Task<IActionResult> SuggestTask(Guid gameId, [FromBody] AiSuggestTaskRequest req)
    {
        var exists = await GetOwnedGameQuery().AnyAsync(g => g.Id == gameId);
        if (!exists) return NotFound();

        try
        {
            return Ok(await _ai.SuggestTaskAsync(req, HttpContext.RequestAborted));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(e.Message);
        }
    }

    [HttpGet("{gameId:guid}/stats/export.csv")]
    public async Task<IActionResult> ExportStatsCsv(Guid gameId)
    {
        var game = await GetOwnedGameQuery()
            .Include(g => g.Tasks).ThenInclude(t => t.Options)
            .Include(g => g.Attempts).ThenInclude(a => a.User)
            .Include(g => g.Attempts).ThenInclude(a => a.Answers).ThenInclude(a => a.SelectedOption)
            .FirstOrDefaultAsync(g => g.Id == gameId);

        if (game is null) return NotFound();
        var csv = BuildResultsCsv(game);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"game-{game.Id}-results.csv");
    }

    [HttpGet("{gameId:guid}/tasks/{taskId:guid}/open-answers")]
    public async Task<IActionResult> GetOpenAnswers(Guid gameId, Guid taskId, [FromQuery] int skip = 0, [FromQuery] int take = 5)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var exists = await _db.GameTasks
            .AsNoTracking()
            .AnyAsync(t => t.Id == taskId && t.GameId == gameId && t.Game.OwnerUserId == userId.Value);

        if (!exists) return NotFound();

        return Ok(await BuildOpenAnswersPage(_db, gameId, taskId, skip, take));
    }

    [HttpPost("{gameId:guid}/tasks")]
    public async Task<IActionResult> CreateTask(Guid gameId, [FromBody] GameTaskUpsertRequest req)
    {
        var game = await GetOwnedGameQuery().FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        try
        {
            var requestedOrder = req.Order;
            var task = BuildTask(req, gameId);
            var correctOptionId = task.CorrectOptionId;
            task.CorrectOptionId = null;
            task.Order = await NextTemporaryTaskOrder(_db, gameId);
            _db.GameTasks.Add(task);
            TouchForContentChange(game);

            await _db.SaveChangesAsync();

            if (correctOptionId.HasValue) task.CorrectOptionId = correctOptionId;
            var tasks = await _db.GameTasks.Where(t => t.GameId == gameId).OrderBy(t => t.Order).ToListAsync();
            await ReorderTasksAsync(_db, tasks, task.Id, requestedOrder);
            return CreatedAtAction(nameof(GetMine), new { gameId }, new { task.Id });
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }
    }

    [HttpPut("{gameId:guid}/tasks/{taskId:guid}")]
    public async Task<IActionResult> UpdateTask(Guid gameId, Guid taskId, [FromBody] GameTaskUpsertRequest req)
    {
        var game = await GetOwnedGameQuery().FirstOrDefaultAsync(g => g.Id == gameId);
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
            ApplyTaskUpdate(task, req, updateOrder: false);
            TouchForContentChange(game);

            await ReorderTasksAsync(_db, tasks, task.Id, req.Order);
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
        var game = await GetOwnedGameQuery().FirstOrDefaultAsync(g => g.Id == gameId);
        if (game is null) return NotFound();

        var task = await _db.GameTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.GameId == gameId);
        if (task is null) return NotFound();

        _db.GameTasks.Remove(task);
        TouchForContentChange(game);

        await _db.SaveChangesAsync();
        return NoContent();
    }

    internal IQueryable<Game> GetOwnedGameQuery()
    {
        var userId = User.GetCurrentUserId();
        return _db.Games.Where(g => userId.HasValue && g.OwnerUserId == userId.Value);
    }

    internal static async Task<int> NextTemporaryTaskOrder(AppDbContext db, Guid gameId)
    {
        var maxOrder = await db.GameTasks
            .Where(t => t.GameId == gameId)
            .Select(t => (int?)t.Order)
            .MaxAsync();

        return (maxOrder ?? 0) + 1000;
    }

    internal static Expression<Func<Game, GameEditorDto>> ToEditorDto() =>
        g => new GameEditorDto(
            g.Id,
            g.Title,
            g.Description,
            g.DescriptionFormat,
            g.ThemeColor,
            g.Status,
            g.LastModeratedAtUtc,
            g.ModerationDecision,
            g.ModerationYesVotes,
            g.ModerationNoVotes,
            g.OwnerUserId,
            g.OwnerUser.DisplayName,
            g.CreatedAtUtc,
            g.UpdatedAtUtc,
            g.Tasks
                .OrderBy(t => t.Order)
                .Select(t => new GameTaskEditorDto(
                    t.Id,
                    t.Type,
                    t.Order,
                    t.Text,
                    t.Points,
                    t.TimeLimitMs,
                    t.CorrectOptionId,
                    t.Options
                        .OrderBy(o => o.SortOrder)
                        .Select(o => new EditorOptionDto(o.Id, o.Text, o.IsActive, o.SortOrder, o.IsCorrect))
                        .ToList()
                ))
                .ToList()
        );

    internal static OwnerGameStatsDto BuildStatsDto(Game game)
    {
        var attempts = game.Attempts.ToList();
        var tasks = game.Tasks.OrderBy(t => t.Order).ToList();

        return new OwnerGameStatsDto(
            game.Id,
            attempts.Count,
            attempts.Count == 0 ? 0 : attempts.Average(a => a.Score),
            attempts.Count == 0 ? 0 : attempts.Average(a => a.TotalTimeMs),
            attempts.Count == 0 ? 0 : attempts.Count(a => a.IsPerfect) / (double)attempts.Count,
            tasks.Select(t =>
            {
                var answers = attempts.SelectMany(a => a.Answers).Where(a => a.GameTaskId == t.Id).ToList();
                var correctAnswers = answers.Count(a => a.IsCorrect == true);
                var incorrectAnswers = answers.Count(a => a.IsCorrect == false);
                var neutralAnswers = answers.Count(a => a.IsCorrect == null);
                var scoredAnswers = correctAnswers + incorrectAnswers;
                return new OwnerGameStatsTaskItemDto(
                    t.Id,
                    t.Text,
                    t.Type,
                    attempts.Count,
                    correctAnswers,
                    incorrectAnswers,
                    neutralAnswers,
                    answers.Count,
                    scoredAnswers == 0 ? 0 : correctAnswers / (double)scoredAnswers,
                    answers
                        .Where(a => !string.IsNullOrWhiteSpace(a.TextAnswer))
                        .OrderByDescending(a => a.Id)
                        .Take(5)
                        .Select(a => a.TextAnswer!)
                        .ToList(),
                    t.Type != GameTaskType.Poll
                        ? new List<PollOptionStatsDto>()
                        : t.Options
                            .Where(o => o.IsActive)
                            .OrderBy(o => o.SortOrder)
                            .Select(o => new PollOptionStatsDto(o.Id, o.Text, answers.Count(a => a.SelectedOptionId == o.Id)))
                            .ToList()
                );
            }).ToList()
        );
    }

    internal static async Task<OpenAnswersPageDto> BuildOpenAnswersPage(AppDbContext db, Guid gameId, Guid taskId, int skip, int take)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 50);

        var query = db.GameTaskAnswers
            .AsNoTracking()
            .Where(a => a.GameTaskId == taskId && a.Attempt.GameId == gameId && !string.IsNullOrWhiteSpace(a.TextAnswer))
            .OrderByDescending(a => a.Attempt.FinishedAtUtc)
            .ThenByDescending(a => a.Id);

        var total = await query.CountAsync();
        var items = await query
            .Skip(skip)
            .Take(take)
            .Select(a => new OpenAnswerItemDto(a.TextAnswer!))
            .ToListAsync();

        return new OpenAnswersPageDto(items, total, skip, take, skip + items.Count < total);
    }

    internal static string BuildResultsCsv(Game game)
    {
        var lines = new List<string>
        {
            string.Join(',', new[]
            {
                "attemptId", "userId", "displayName", "startedAtUtc", "finishedAtUtc", "totalTimeMs",
                "score", "maxScore", "isPerfect", "taskId", "taskType", "taskOrder", "taskText",
                "selectedOptionId", "selectedOptionText", "textAnswer", "submittedOrder", "isCorrect", "timeSpentMs"
            })
        };

        var tasks = game.Tasks.ToDictionary(t => t.Id);
        foreach (var attempt in game.Attempts.OrderByDescending(a => a.FinishedAtUtc))
        {
            foreach (var answer in attempt.Answers.OrderBy(a => tasks.TryGetValue(a.GameTaskId, out var task) ? task.Order : 0))
            {
                tasks.TryGetValue(answer.GameTaskId, out var task);
                lines.Add(string.Join(',', new[]
                {
                    Csv(attempt.Id),
                    Csv(attempt.UserId),
                    Csv(attempt.User.DisplayName),
                    Csv(attempt.StartedAtUtc),
                    Csv(attempt.FinishedAtUtc),
                    Csv(attempt.TotalTimeMs),
                    Csv(attempt.Score),
                    Csv(attempt.MaxScore),
                    Csv(attempt.IsPerfect),
                    Csv(answer.GameTaskId),
                    Csv(task?.Type.ToString() ?? ""),
                    Csv(task?.Order ?? 0),
                    Csv(task?.Text ?? ""),
                    Csv(answer.SelectedOptionId?.ToString() ?? ""),
                    Csv(answer.SelectedOption?.Text ?? ""),
                    Csv(answer.TextAnswer ?? ""),
                    Csv(answer.SubmittedOrder ?? ""),
                    Csv(answer.IsCorrect?.ToString() ?? ""),
                    Csv(answer.TimeSpentMs)
                }));
            }
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    internal static async Task RecalculateAttemptsAsync(AppDbContext db, IEnumerable<Guid> attemptIds)
    {
        var ids = attemptIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var attempts = await db.GameAttempts.Where(a => ids.Contains(a.Id)).ToListAsync();
        foreach (var attempt in attempts)
        {
            var maxScore = await db.GameTasks
                .Where(t => t.GameId == attempt.GameId && t.Type != GameTaskType.OpenEnded && t.Type != GameTaskType.Poll)
                .SumAsync(t => (int?)t.Points) ?? 0;
            var score = await db.GameTaskAnswers
                .Where(a => a.AttemptId == attempt.Id && a.IsCorrect == true)
                .Join(db.GameTasks, a => a.GameTaskId, t => t.Id, (_, t) => t.Points)
                .SumAsync(x => (int?)x) ?? 0;

            attempt.MaxScore = maxScore;
            attempt.Score = score;
            attempt.IsPerfect = score == maxScore && maxScore > 0;
        }
    }

    private static string Csv(object? value)
    {
        var s = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    internal static void TouchForContentChange(Game game)
    {
        game.UpdatedAtUtc = DateTime.UtcNow;
        if (game.Status is GameStatus.Verified or GameStatus.PendingModeration or GameStatus.Rejected)
            game.Status = GameStatus.Unverified;
        game.LastModeratedAtUtc = null;
        game.ModerationDecision = null;
        game.ModerationYesVotes = 0;
        game.ModerationNoVotes = 0;
    }

    internal static GameTask BuildTask(GameTaskUpsertRequest req, Guid gameId)
    {
        var task = new GameTask
        {
            Id = Guid.NewGuid(),
            GameId = gameId
        };

        ApplyTaskUpdate(task, req);
        return task;
    }

    internal static async Task ReorderTasksAsync(AppDbContext db, List<GameTask> tasks, Guid movingTaskId, int requestedOrder)
    {
        if (tasks.Count == 0) return;

        var ordered = tasks.OrderBy(t => t.Order).ToList();
        var moving = ordered.First(t => t.Id == movingTaskId);
        ordered.Remove(moving);

        var target = Math.Clamp(requestedOrder, 0, ordered.Count);
        ordered.Insert(target, moving);

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Order = -100000 - i;

        await db.SaveChangesAsync();

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Order = i;

        await db.SaveChangesAsync();
    }

    internal static void ApplyTaskUpdate(GameTask task, GameTaskUpsertRequest req, bool updateOrder = true)
    {
        var text = (req.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Task text is required.");
        if (req.Points < 0) throw new ArgumentException("Points must be >= 0.");
        if (req.TimeLimitMs <= 0) throw new ArgumentException("TimeLimitMs must be > 0.");

        var options = (req.Options ?? new List<string>())
            .Select(o => (o ?? string.Empty).Trim())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .ToList();

        task.Type = req.Type;
        if (updateOrder) task.Order = req.Order;
        task.Text = text;
        task.Points = req.Type is GameTaskType.OpenEnded or GameTaskType.Poll ? 0 : req.Points;
        task.TimeLimitMs = req.TimeLimitMs;
        task.OpenEndedAcceptedAnswer = null;
        task.UpdatedAtUtc = DateTime.UtcNow;
        if (task.CreatedAtUtc == default) task.CreatedAtUtc = DateTime.UtcNow;

        switch (req.Type)
        {
            case GameTaskType.Quiz:
                if (options.Count < 2) throw new ArgumentException("Quiz requires at least 2 options.");
                if (!req.CorrectOptionIndex.HasValue || req.CorrectOptionIndex < 0 || req.CorrectOptionIndex >= options.Count)
                    throw new ArgumentException("CorrectOptionIndex is out of range.");
                SyncOptions(task, options);
                MarkCorrectOptions(task, new[] { req.CorrectOptionIndex.Value });
                task.CorrectOptionId = task.Options.OrderBy(o => o.SortOrder).ElementAt(req.CorrectOptionIndex.Value).Id;
                break;
            case GameTaskType.TrueFalse:
                SyncOptions(task, new List<string> { "Правда", "Ложь" });
                var tfIndex = req.CorrectOptionIndex ?? 0;
                if (tfIndex is < 0 or > 1) throw new ArgumentException("CorrectOptionIndex must be 0 or 1 for TrueFalse.");
                MarkCorrectOptions(task, new[] { tfIndex });
                task.CorrectOptionId = task.Options.OrderBy(o => o.SortOrder).ElementAt(tfIndex).Id;
                break;
            case GameTaskType.Puzzle:
                if (options.Count < 2) throw new ArgumentException("Puzzle requires at least 2 items.");
                SyncOptions(task, options);
                task.CorrectOptionId = null;
                break;
            case GameTaskType.Multichoice:
                if (options.Count < 2) throw new ArgumentException("Multichoice requires at least 2 options.");
                var correctIndexes = (req.CorrectOptionIndexes ?? new List<int>()).Distinct().OrderBy(x => x).ToList();
                if (correctIndexes.Count == 0) throw new ArgumentException("Multichoice requires at least one correct option.");
                if (correctIndexes.Any(i => i < 0 || i >= options.Count))
                    throw new ArgumentException("CorrectOptionIndexes contain an out of range value.");
                SyncOptions(task, options);
                MarkCorrectOptions(task, correctIndexes);
                task.CorrectOptionId = null;
                break;
            case GameTaskType.OpenEnded:
                task.CorrectOptionId = null;
                DeactivateAllOptions(task);
                break;
            case GameTaskType.Poll:
                if (options.Count < 2) throw new ArgumentException("Poll requires at least 2 options.");
                SyncOptions(task, options);
                task.CorrectOptionId = null;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static void SyncOptions(GameTask task, List<string> texts)
    {
        var existing = task.Options.OrderBy(o => o.SortOrder).ToList();

        for (var i = 0; i < texts.Count; i++)
        {
            if (i < existing.Count)
            {
                existing[i].Text = texts[i];
                existing[i].SortOrder = i;
                existing[i].IsActive = true;
                existing[i].IsCorrect = false;
            }
            else
            {
                task.Options.Add(new AnswerOption
                {
                    Id = Guid.NewGuid(),
                    GameTaskId = task.Id,
                    Text = texts[i],
                    SortOrder = i,
                    IsActive = true,
                    IsCorrect = false
                });
            }
        }

        for (var i = texts.Count; i < existing.Count; i++)
        {
            existing[i].IsActive = false;
            existing[i].IsCorrect = false;
            existing[i].SortOrder = i;
        }
    }

    private static void MarkCorrectOptions(GameTask task, IEnumerable<int> correctIndexes)
    {
        var correct = correctIndexes.ToHashSet();
        var active = task.Options.Where(o => o.IsActive).OrderBy(o => o.SortOrder).ToList();
        for (var i = 0; i < active.Count; i++)
            active[i].IsCorrect = correct.Contains(i);
    }

    private static void DeactivateAllOptions(GameTask task)
    {
        foreach (var option in task.Options)
        {
            option.IsActive = false;
            option.IsCorrect = false;
        }
    }

    internal static string? NormalizeHexColor(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var s = input.Trim();
        if (!s.StartsWith("#")) s = "#" + s;
        if (s.Length != 7) return null;

        for (var i = 1; i < 7; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return null;
        }

        return s.ToUpperInvariant();
    }
}
