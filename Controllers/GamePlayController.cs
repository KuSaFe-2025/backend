using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;
using KuSaFeBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace KuSaFeBackend.Controllers;

[ApiController]
[Route("v1/games")]
public class GamePlayController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IAiAssistantService _ai;

    public GamePlayController(AppDbContext db, IConfiguration cfg, IAiAssistantService ai)
    {
        _db = db;
        _cfg = cfg;
        _ai = ai;
    }

    public record OptionDto(Guid Id, string Text);

    public record PublicGameTaskDto(
        Guid Id,
        GameTaskType Type,
        int Order,
        string Text,
        int Points,
        int TimeLimitMs,
        List<OptionDto> Options
    );

    public record StartResponse(
        Guid AttemptId,
        string QuestionToken,
        DateTime QuestionExpiresAtUtc,
        PublicGameTaskDto Task
    );

    public record AnswerRequest(
        Guid AttemptId,
        string QuestionToken,
        Guid? SelectedOptionId,
        List<Guid>? SelectedOptionIds,
        string? TextAnswer,
        List<Guid>? OrderedOptionIds
    );

    public record AnswerResponse(
        bool Finished,
        string? Reason,
        bool? LastAnswerCorrect,
        int Score,
        int MaxScore,
        int CorrectAnswers,
        int TotalTasks,
        int ScoredTasks,
        int NeutralTasks,
        long TotalTimeMs,
        string? NextQuestionToken,
        DateTime? NextQuestionExpiresAtUtc,
        PublicGameTaskDto? NextTask
    );

    public record LeaderboardItem(
        Guid UserId,
        string DisplayName,
        long TotalTimeMs,
        DateTime FinishedAtUtc,
        int Score,
        int MaxScore,
        int CorrectAnswers,
        int TotalTasks,
        int ScoredTasks,
        int NeutralTasks
    );

    [Authorize]
    [HttpPost("{gameId:guid}/start")]
    public async Task<IActionResult> Start(Guid gameId)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var game = await _db.Games
            .AsNoTracking()
            .Where(g => g.Id == gameId)
            .Select(g => new
            {
                g.Id,
                g.OwnerUserId,
                g.Status,
                g.MaxAttemptsPerUser,
                g.AvailableFromUtc,
                g.AvailableUntilUtc
            })
            .FirstOrDefaultAsync();

        if (game is null) return NotFound();
        if (game.Status != GameStatus.Verified && !User.IsCurrentUserAdmin() && game.OwnerUserId != userId.Value)
            return NotFound();

        var now = DateTime.UtcNow;
        if (game.AvailableFromUtc.HasValue && now < game.AvailableFromUtc.Value)
            return BadRequest("Игра ещё недоступна для прохождения.");
        if (game.AvailableUntilUtc.HasValue && now > game.AvailableUntilUtc.Value)
            return BadRequest("Период прохождения игры завершён.");

        if (game.MaxAttemptsPerUser.HasValue)
        {
            var attemptsCount = await _db.GameAttempts.CountAsync(a => a.GameId == gameId && a.UserId == userId.Value);
            if (attemptsCount >= game.MaxAttemptsPerUser.Value)
                return Conflict($"Вы уже использовали максимум попыток для этой игры: {game.MaxAttemptsPerUser.Value}.");
        }

        var firstTask = await _db.GameTasks
            .AsNoTracking()
            .Include(t => t.Options)
            .Where(t => t.GameId == gameId)
            .OrderBy(t => t.Order)
            .FirstOrDefaultAsync();

        if (firstTask is null) return BadRequest("Game has no tasks.");

        var maxScore = await GetMaxScore(gameId);

        var attempt = new GameAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            GameId = gameId,
            StartedAtUtc = now,
            FinishedAtUtc = now,
            TotalTimeMs = 0,
            Score = 0,
            MaxScore = maxScore,
            IsPerfect = false
        };

        _db.GameAttempts.Add(attempt);
        await _db.SaveChangesAsync();

        var token = CreateQuestionToken(attempt.Id, gameId, firstTask.Id, firstTask.TimeLimitMs, out var expUtc);

        return Ok(new StartResponse(attempt.Id, token, expUtc, ToPublicTaskDto(firstTask)));
    }

    [Authorize]
    [HttpPost("{gameId:guid}/answer")]
    public async Task<IActionResult> Answer(Guid gameId, [FromBody] AnswerRequest req)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var attempt = await _db.GameAttempts.FirstOrDefaultAsync(a => a.Id == req.AttemptId && a.UserId == userId.Value);
        if (attempt is null) return NotFound("Attempt not found.");
        if (attempt.GameId != gameId) return BadRequest("Attempt/game mismatch.");

        ClaimsPrincipal qp;
        JwtSecurityToken jwt;
        try
        {
            (qp, jwt) = ValidateQuestionTokenNoLifetime(req.QuestionToken);
        }
        catch
        {
            return Unauthorized("Invalid questionToken.");
        }

        if (jwt.ValidTo <= DateTime.UtcNow)
        {
            var finished = await FinishAttempt(req.AttemptId, "Timeout", null);
            return Ok(finished);
        }

        var tokenAttemptId = qp.FindFirstValue("attemptId");
        var tokenGameId = qp.FindFirstValue("gameId");
        var tokenTaskId = qp.FindFirstValue("taskId");

        if (tokenAttemptId != req.AttemptId.ToString()) return BadRequest("questionToken attemptId mismatch.");
        if (tokenGameId != gameId.ToString()) return BadRequest("questionToken gameId mismatch.");
        if (!Guid.TryParse(tokenTaskId, out var taskId)) return BadRequest("questionToken taskId invalid.");

        var expected = await _db.GameTasks
            .AsNoTracking()
            .Include(t => t.Options)
            .Where(t => t.GameId == gameId)
            .Where(t => !_db.GameTaskAnswers.Where(a => a.AttemptId == req.AttemptId).Select(a => a.GameTaskId).Contains(t.Id))
            .OrderBy(t => t.Order)
            .FirstOrDefaultAsync();

        if (expected is null)
        {
            var finished = await FinishAttempt(req.AttemptId, "AlreadyFinished", null);
            return Ok(finished);
        }

        if (expected.Id != taskId) return BadRequest("Not the current task.");
        bool? lastAnswerCorrect;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var issuedMsStr = qp.FindFirstValue("issuedAtMs");
        _ = long.TryParse(issuedMsStr, out var issuedMs);
        var spentMs = (int)Math.Clamp(nowMs - issuedMs, 0, (long)expected.TimeLimitMs);

        try
        {
            var answer = BuildAnswer(req, expected, spentMs);

            _db.GameTaskAnswers.Add(new GameTaskAnswer
            {
                Id = answer.Id,
                AttemptId = req.AttemptId,
                GameTaskId = expected.Id,
                SelectedOptionId = answer.SelectedOptionId,
                TextAnswer = answer.TextAnswer,
                SubmittedOrder = answer.SubmittedOrder,
                IsCorrect = answer.IsCorrect,
                TimeSpentMs = answer.TimeSpentMs
            });
            lastAnswerCorrect = answer.IsCorrect;
            await _db.SaveChangesAsync();
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        var next = await _db.GameTasks
            .AsNoTracking()
            .Include(t => t.Options)
            .Where(t => t.GameId == gameId)
            .Where(t => !_db.GameTaskAnswers.Where(a => a.AttemptId == req.AttemptId).Select(a => a.GameTaskId).Contains(t.Id))
            .OrderBy(t => t.Order)
            .FirstOrDefaultAsync();

        if (next is null)
        {
            var finished = await FinishAttempt(req.AttemptId, "Completed", lastAnswerCorrect);
            return Ok(finished);
        }

        var nextToken = CreateQuestionToken(req.AttemptId, gameId, next.Id, next.TimeLimitMs, out var nextExpUtc);
        return Ok(await BuildIntermediateResponse(req.AttemptId, next, nextToken, nextExpUtc, lastAnswerCorrect));
    }

    [HttpGet("{gameId:guid}/leaderboard")]
    public async Task<IActionResult> Leaderboard(Guid gameId, [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var taskCounts = await GetTaskCounts(gameId);
        var items = await _db.GameAttempts
            .AsNoTracking()
            .Include(a => a.User)
            .Include(a => a.Answers)
            .Where(a => a.GameId == gameId && a.IsPerfect)
            .OrderBy(a => a.TotalTimeMs)
            .ThenBy(a => a.FinishedAtUtc)
            .Select(a => new LeaderboardItem(
                a.UserId,
                a.User.DisplayName,
                a.TotalTimeMs,
                a.FinishedAtUtc,
                a.Score,
                a.MaxScore,
                a.Answers.Count(x => x.IsCorrect == true),
                taskCounts.Total,
                taskCounts.Scored,
                taskCounts.Neutral
            ))
            .Take(limit)
            .ToListAsync();

        return Ok(items);
    }

    [Authorize]
    [HttpGet("{gameId:guid}/attempts/{attemptId:guid}/review")]
    public async Task<IActionResult> ReviewAttempt(Guid gameId, Guid attemptId)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var attempt = await LoadAttemptForReview(gameId, attemptId);
        if (attempt is null) return NotFound();
        if (!CanReadAttempt(attempt, userId.Value)) return NotFound();
        if (!IsAttemptFinished(attempt)) return Conflict("Attempt is not finished.");

        return Ok(BuildAttemptReviewDto(attempt));
    }

    [Authorize]
    [HttpPost("{gameId:guid}/attempts/{attemptId:guid}/answers/{answerId:guid}/explain")]
    public async Task<IActionResult> ExplainAnswer(Guid gameId, Guid attemptId, Guid answerId)
    {
        var userId = User.GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var attempt = await LoadAttemptForReview(gameId, attemptId);
        if (attempt is null) return NotFound();
        if (!CanReadAttempt(attempt, userId.Value)) return NotFound();
        if (!IsAttemptFinished(attempt)) return Conflict("Attempt is not finished.");

        var answer = attempt.Answers.FirstOrDefault(a => a.Id == answerId);
        if (answer is null) return NotFound("Answer not found.");

        var explanation = await _ai.ExplainAnswerAsync(attempt.Game, answer.GameTask, answer, HttpContext.RequestAborted);
        return Ok(new AiExplainAnswerResponse(explanation));
    }

    private static PublicGameTaskDto ToPublicTaskDto(GameTask task) =>
        new(
            task.Id,
            task.Type,
            task.Order,
            task.Text,
            task.Points,
            task.TimeLimitMs,
            BuildRuntimeOptions(task)
        );

    private static List<OptionDto> BuildRuntimeOptions(GameTask task)
    {
        var options = task.Options
            .Where(o => o.IsActive)
            .OrderBy(o => o.SortOrder)
            .Select(o => new OptionDto(o.Id, o.Text))
            .ToList();

        if (task.Type is GameTaskType.Quiz or GameTaskType.TrueFalse or GameTaskType.Poll or GameTaskType.Puzzle or GameTaskType.Multichoice)
        {
            for (var i = options.Count - 1; i > 0; i--)
            {
                var j = RandomNumberGenerator.GetInt32(i + 1);
                (options[i], options[j]) = (options[j], options[i]);
            }
        }

        return options;
    }

    private Task<GameAttempt?> LoadAttemptForReview(Guid gameId, Guid attemptId) =>
        _db.GameAttempts
            .Include(a => a.Game)
            .ThenInclude(g => g.Tasks)
            .ThenInclude(t => t.Options)
            .Include(a => a.Answers)
            .ThenInclude(a => a.GameTask)
            .ThenInclude(t => t.Options)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == attemptId && a.GameId == gameId);

    private bool CanReadAttempt(GameAttempt attempt, Guid userId) =>
        attempt.UserId == userId || attempt.Game.OwnerUserId == userId || User.IsCurrentUserAdmin();

    private static bool IsAttemptFinished(GameAttempt attempt)
    {
        var taskCount = attempt.Game.Tasks.Count;
        return attempt.FinishedAtUtc > attempt.StartedAtUtc || (taskCount > 0 && attempt.Answers.Count >= taskCount);
    }

    private static AttemptReviewDto BuildAttemptReviewDto(GameAttempt attempt)
    {
        var items = attempt.Answers
            .OrderBy(a => a.GameTask.Order)
            .Select(BuildAttemptReviewItemDto)
            .ToList();

        return new AttemptReviewDto(
            attempt.Id,
            attempt.GameId,
            attempt.Game.Title,
            attempt.Score,
            attempt.MaxScore,
            attempt.TotalTimeMs,
            attempt.StartedAtUtc,
            attempt.FinishedAtUtc,
            items
        );
    }

    private static AttemptReviewItemDto BuildAttemptReviewItemDto(GameTaskAnswer answer)
    {
        var task = answer.GameTask;
        var options = task.Options
            .Where(o => o.IsActive)
            .OrderBy(o => o.SortOrder)
            .ToList();
        var optionMap = options.ToDictionary(o => o.Id, o => o.Text);
        var selectedIds = BuildSelectedOptionIds(answer);
        var submittedOrderIds = task.Type == GameTaskType.Puzzle ? ParseGuidList(answer.SubmittedOrder) : new List<Guid>();
        var correctIds = BuildCorrectOptionIds(task, options);

        return new AttemptReviewItemDto(
            answer.Id,
            task.Id,
            task.Type,
            task.Order,
            task.Text,
            task.Points,
            answer.TimeSpentMs,
            answer.IsCorrect,
            options.Select(o => new AttemptReviewOptionDto(o.Id, o.Text)).ToList(),
            selectedIds,
            selectedIds.Select(id => optionMap.TryGetValue(id, out var text) ? text : id.ToString()).ToList(),
            answer.TextAnswer,
            submittedOrderIds,
            submittedOrderIds.Select(id => optionMap.TryGetValue(id, out var text) ? text : id.ToString()).ToList(),
            correctIds,
            correctIds.Select(id => optionMap.TryGetValue(id, out var text) ? text : id.ToString()).ToList()
        );
    }

    private static List<Guid> BuildSelectedOptionIds(GameTaskAnswer answer)
    {
        if (answer.SelectedOptionId.HasValue) return new List<Guid> { answer.SelectedOptionId.Value };
        return answer.GameTask.Type == GameTaskType.Multichoice ? ParseGuidList(answer.SubmittedOrder) : new List<Guid>();
    }

    private static List<Guid> BuildCorrectOptionIds(GameTask task, List<AnswerOption> options) =>
        task.Type switch
        {
            GameTaskType.Quiz or GameTaskType.TrueFalse => task.CorrectOptionId.HasValue ? new List<Guid> { task.CorrectOptionId.Value } : new List<Guid>(),
            GameTaskType.Multichoice => options.Where(o => o.IsCorrect).Select(o => o.Id).ToList(),
            GameTaskType.Puzzle => options.OrderBy(o => o.SortOrder).Select(o => o.Id).ToList(),
            _ => new List<Guid>()
        };

    private static List<Guid> ParseGuidList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<Guid>();
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? new List<Guid>();
        }
        catch (JsonException)
        {
            return new List<Guid>();
        }
    }

    private record BuiltAnswer(Guid Id, Guid AttemptId, Guid GameTaskId, Guid? SelectedOptionId, string? TextAnswer, string? SubmittedOrder, bool? IsCorrect, int TimeSpentMs);

    private static BuiltAnswer BuildAnswer(AnswerRequest req, GameTask task, int spentMs)
    {
        return task.Type switch
        {
            GameTaskType.Quiz or GameTaskType.TrueFalse => BuildSingleChoiceAnswer(req, task, spentMs),
            GameTaskType.Multichoice => BuildMultichoiceAnswer(req, task, spentMs),
            GameTaskType.Poll => BuildPollAnswer(req, task, spentMs),
            GameTaskType.OpenEnded => BuildOpenEndedAnswer(req, task, spentMs),
            GameTaskType.Puzzle => BuildPuzzleAnswer(req, task, spentMs),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static BuiltAnswer BuildSingleChoiceAnswer(AnswerRequest req, GameTask task, int spentMs)
    {
        if (!req.SelectedOptionId.HasValue) throw new ArgumentException("SelectedOptionId is required.");
        if (!task.Options.Any(o => o.IsActive && o.Id == req.SelectedOptionId.Value))
            throw new ArgumentException("SelectedOptionId is not in this task.");

        return new BuiltAnswer(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty,
            req.SelectedOptionId,
            null,
            null,
            req.SelectedOptionId == task.CorrectOptionId,
            spentMs
        );
    }

    private static BuiltAnswer BuildPollAnswer(AnswerRequest req, GameTask task, int spentMs)
    {
        if (!req.SelectedOptionId.HasValue) throw new ArgumentException("SelectedOptionId is required.");
        if (!task.Options.Any(o => o.IsActive && o.Id == req.SelectedOptionId.Value))
            throw new ArgumentException("SelectedOptionId is not in this task.");

        return new BuiltAnswer(Guid.NewGuid(), Guid.Empty, Guid.Empty, req.SelectedOptionId, null, null, null, spentMs);
    }

    private static BuiltAnswer BuildMultichoiceAnswer(AnswerRequest req, GameTask task, int spentMs)
    {
        var selected = (req.SelectedOptionIds ?? new List<Guid>()).Distinct().OrderBy(x => x).ToList();
        if (selected.Count == 0) throw new ArgumentException("SelectedOptionIds is required.");

        var active = task.Options.Where(o => o.IsActive).ToList();
        if (selected.Except(active.Select(o => o.Id)).Any())
            throw new ArgumentException("SelectedOptionIds contain unknown options.");

        var correct = active.Where(o => o.IsCorrect).Select(o => o.Id).OrderBy(x => x).ToList();
        var isCorrect = correct.SequenceEqual(selected);

        return new BuiltAnswer(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty,
            null,
            null,
            JsonSerializer.Serialize(selected),
            isCorrect,
            spentMs
        );
    }

    private static BuiltAnswer BuildOpenEndedAnswer(AnswerRequest req, GameTask task, int spentMs)
    {
        var text = (req.TextAnswer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("TextAnswer is required.");

        return new BuiltAnswer(Guid.NewGuid(), Guid.Empty, Guid.Empty, null, text, null, null, spentMs);
    }

    private static BuiltAnswer BuildPuzzleAnswer(AnswerRequest req, GameTask task, int spentMs)
    {
        var ordered = req.OrderedOptionIds ?? new List<Guid>();
        var active = task.Options.Where(o => o.IsActive).OrderBy(o => o.SortOrder).ToList();
        if (ordered.Count != active.Count) throw new ArgumentException("OrderedOptionIds length mismatch.");
        if (ordered.Except(active.Select(o => o.Id)).Any()) throw new ArgumentException("OrderedOptionIds contain unknown options.");

        var correct = active.Select(o => o.Id).SequenceEqual(ordered);
        return new BuiltAnswer(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.Empty,
            null,
            null,
            JsonSerializer.Serialize(ordered),
            correct,
            spentMs
        );
    }

    private async Task<AnswerResponse> BuildIntermediateResponse(Guid attemptId, GameTask next, string nextToken, DateTime nextExpUtc, bool? lastAnswerCorrect)
    {
        var taskCounts = await GetTaskCounts(next.GameId);
        var correctAnswers = await _db.GameTaskAnswers.Where(x => x.AttemptId == attemptId && x.IsCorrect == true).CountAsync();
        var maxScore = await GetMaxScore(next.GameId);
        var score = await GetAttemptScore(attemptId);

        return new AnswerResponse(false, null, lastAnswerCorrect, score, maxScore, correctAnswers, taskCounts.Total, taskCounts.Scored, taskCounts.Neutral, 0, nextToken, nextExpUtc, ToPublicTaskDto(next));
    }

    private async Task<AnswerResponse> FinishAttempt(Guid attemptId, string reason, bool? lastAnswerCorrect)
    {
        var attempt = await _db.GameAttempts.FirstAsync(a => a.Id == attemptId);
        var taskCounts = await GetTaskCounts(attempt.GameId);
        var maxScore = await GetMaxScore(attempt.GameId);
        var correctAnswers = await _db.GameTaskAnswers.Where(x => x.AttemptId == attemptId && x.IsCorrect == true).CountAsync();
        var score = await GetAttemptScore(attemptId);

        var now = DateTime.UtcNow;
        attempt.Score = score;
        attempt.MaxScore = maxScore;
        attempt.IsPerfect = score == maxScore && maxScore > 0;
        attempt.FinishedAtUtc = now;
        attempt.TotalTimeMs = (long)Math.Max(0, (now - attempt.StartedAtUtc).TotalMilliseconds);
        await _db.SaveChangesAsync();

        return new AnswerResponse(true, reason, lastAnswerCorrect, attempt.Score, attempt.MaxScore, correctAnswers, taskCounts.Total, taskCounts.Scored, taskCounts.Neutral, attempt.TotalTimeMs, null, null, null);
    }

    private async Task<(int Total, int Scored, int Neutral)> GetTaskCounts(Guid gameId)
    {
        var counts = await _db.GameTasks
            .Where(t => t.GameId == gameId)
            .GroupBy(t => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Scored = g.Count(t => t.Type != GameTaskType.OpenEnded && t.Type != GameTaskType.Poll)
            })
            .FirstOrDefaultAsync();

        var total = counts?.Total ?? 0;
        var scored = counts?.Scored ?? 0;
        return (total, scored, Math.Max(0, total - scored));
    }

    private Task<int> GetMaxScore(Guid gameId) =>
        _db.GameTasks
            .Where(t => t.GameId == gameId && t.Type != GameTaskType.OpenEnded && t.Type != GameTaskType.Poll)
            .SumAsync(t => (int?)t.Points)
            .ContinueWith(t => t.Result ?? 0);

    private Task<int> GetAttemptScore(Guid attemptId) =>
        _db.GameTaskAnswers
            .Where(a => a.AttemptId == attemptId && a.IsCorrect == true)
            .Join(_db.GameTasks, a => a.GameTaskId, t => t.Id, (_, t) => t.Points)
            .SumAsync(x => (int?)x)
            .ContinueWith(t => t.Result ?? 0);

    private (ClaimsPrincipal principal, JwtSecurityToken jwt) ValidateQuestionTokenNoLifetime(string token)
    {
        var keyStr = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        var issuer = _cfg["Jwt:Issuer"];
        var audience = _cfg["Jwt:Audience"];

        var handler = new JwtSecurityTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyStr)),
            ValidateIssuer = !string.IsNullOrWhiteSpace(issuer),
            ValidIssuer = issuer,
            ValidateAudience = !string.IsNullOrWhiteSpace(audience),
            ValidAudience = audience,
            ValidateLifetime = false,
            ClockSkew = TimeSpan.Zero
        };

        var principal = handler.ValidateToken(token, parameters, out var validatedToken);
        if (validatedToken is not JwtSecurityToken jwt) throw new SecurityTokenException("Invalid token.");
        if (principal.FindFirstValue("typ") != "game_task") throw new SecurityTokenException("Wrong token type.");
        return (principal, jwt);
    }

    private string CreateQuestionToken(Guid attemptId, Guid gameId, Guid taskId, int timeLimitMs, out DateTime expiresAtUtc)
    {
        var keyStr = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        var issuer = _cfg["Jwt:Issuer"];
        var audience = _cfg["Jwt:Audience"];

        var now = DateTime.UtcNow;
        expiresAtUtc = now.AddMilliseconds(timeLimitMs);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var claims = new List<Claim>
        {
            new("typ", "game_task"),
            new("attemptId", attemptId.ToString()),
            new("gameId", gameId.ToString()),
            new("taskId", taskId.ToString()),
            new("issuedAtMs", nowMs.ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyStr));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: now.AddSeconds(-1),
            expires: expiresAtUtc,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
