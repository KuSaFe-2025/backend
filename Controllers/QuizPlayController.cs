using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;


namespace KuSaFeBackend.Controllers;

[ApiController]
[Route("v1/quizzes")]
public class QuizPlayController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public QuizPlayController(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    // ---------- DTOs ----------
    public record OptionDto(Guid Id, string Text);

    public record PublicQuestionDto(
        Guid Id,
        int Order,
        string Text,
        int Points,
        int TimeLimitMs,
        Guid? CorrectOptionId,
        List<OptionDto> Options
    );

    private static List<OptionDto> BuildShuffledOptions(Question q)
    {
        var list = q.Options
            .Where(o => o.IsActive)
            .Select(o => new OptionDto(o.Id, o.Text))
            .ToList();

        // Fisher–Yates
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private static PublicQuestionDto ToPublicQuestionDto(Question q) =>
        new(
            q.Id,
            q.Order,
            q.Text,
            q.Points,
            q.TimeLimitMs,
            q.CorrectOptionId,
            BuildShuffledOptions(q)
        );

    public record StartResponse(
        Guid AttemptId,
        string QuestionToken,
        DateTime QuestionExpiresAtUtc,
        PublicQuestionDto Question
    );

    public record AnswerRequest(Guid AttemptId, string QuestionToken, Guid SelectedOptionId);

    public record AnswerResponse(
        bool Finished,
        string? Reason,
        int Score,
        int MaxScore,
        int CorrectAnswers,
        int TotalQuestions,
        long TotalTimeMs,
        string? NextQuestionToken,
        DateTime? NextQuestionExpiresAtUtc,
        PublicQuestionDto? NextQuestion
    );

    public record LeaderboardItem(Guid UserId, string DisplayName, long TotalTimeMs, DateTime FinishedAtUtc);

    // ---------- Helpers ----------
    private Guid? GetUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

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

            ValidateLifetime = false, // exp проверим сами
            ClockSkew = TimeSpan.Zero
        };

        var principal = handler.ValidateToken(token, parameters, out var validatedToken);
        if (validatedToken is not JwtSecurityToken jwt) throw new SecurityTokenException("Invalid token.");

        var typ = principal.FindFirstValue("typ");
        if (typ != "quiz_question") throw new SecurityTokenException("Wrong token type.");

        return (principal, jwt);
    }

    private string CreateQuestionToken(Guid attemptId, Guid quizId, Guid questionId, int timeLimitMs, out DateTime expiresAtUtc)
    {
        var keyStr = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing");
        var issuer = _cfg["Jwt:Issuer"];
        var audience = _cfg["Jwt:Audience"];

        var now = DateTime.UtcNow;
        expiresAtUtc = now.AddMilliseconds(timeLimitMs);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var claims = new List<Claim>
        {
            new("typ", "quiz_question"),
            new("attemptId", attemptId.ToString()),
            new("quizId", quizId.ToString()),
            new("questionId", questionId.ToString()),
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


    private async Task<AnswerResponse> FinishAttempt(Guid attemptId, string reason)
    {
        var attempt = await _db.QuizAttempts.FirstAsync(a => a.Id == attemptId);

        var totalQuestions = await _db.Questions.Where(q => q.QuizId == attempt.QuizId).CountAsync();
        var maxScore = await _db.Questions.Where(q => q.QuizId == attempt.QuizId).SumAsync(q => q.Points);
        var correctAnswers = await _db.AttemptAnswers.Where(x => x.AttemptId == attemptId && x.IsCorrect).CountAsync();

        // пересчёт score чтобы не зависеть от инкрементов
        var score = await _db.AttemptAnswers
            .Where(x => x.AttemptId == attemptId && x.IsCorrect)
            .Join(_db.Questions, a => a.QuestionId, q => q.Id, (a, q) => q.Points)
            .SumAsync();

        var now = DateTime.UtcNow;
        attempt.Score = score;
        attempt.MaxScore = maxScore;
        attempt.IsPerfect = (score == maxScore) && (totalQuestions > 0);
        attempt.FinishedAtUtc = now;
        attempt.TotalTimeMs = (long)Math.Max(0, (now - attempt.StartedAtUtc).TotalMilliseconds);

        await _db.SaveChangesAsync();

        return new AnswerResponse(
            Finished: true,
            Reason: reason,
            Score: attempt.Score,
            MaxScore: attempt.MaxScore,
            CorrectAnswers: correctAnswers,
            TotalQuestions: totalQuestions,
            TotalTimeMs: attempt.TotalTimeMs,
            NextQuestionToken: null,
            NextQuestionExpiresAtUtc: null,
            NextQuestion: null
        );
    }

    // ---------- Public endpoints ----------
    // START: создать попытку и вернуть первый вопрос + questionToken
    [Authorize]
    [HttpPost("{quizId:guid}/start")]
    public async Task<IActionResult> Start(Guid quizId)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var firstQuestion = await _db.Questions
            .AsNoTracking()
            .Include(q => q.Options)
            .Where(q => q.QuizId == quizId)
            .OrderBy(q => q.Order)
            .FirstOrDefaultAsync();

        if (firstQuestion is null) return BadRequest("Quiz has no questions.");

        var maxScore = await _db.Questions.Where(q => q.QuizId == quizId).SumAsync(q => q.Points);

        var now = DateTime.UtcNow;
        var attempt = new QuizAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            QuizId = quizId,
            StartedAtUtc = now,
            FinishedAtUtc = now, // обновим при финише
            TotalTimeMs = 0,
            Score = 0,
            MaxScore = maxScore,
            IsPerfect = false
        };

        _db.QuizAttempts.Add(attempt);
        await _db.SaveChangesAsync();

        var token = CreateQuestionToken(attempt.Id, quizId, firstQuestion.Id, firstQuestion.TimeLimitMs, out var expUtc);

        return Ok(new StartResponse(
            AttemptId: attempt.Id,
            QuestionToken: token,
            QuestionExpiresAtUtc: expUtc,
            Question: ToPublicQuestionDto(firstQuestion)
        ));
    }

    // ANSWER: принять ответ, вернуть следующий вопрос + новый токен ИЛИ результаты
    [Authorize]
    [HttpPost("{quizId:guid}/answer")]
    public async Task<IActionResult> Answer(Guid quizId, [FromBody] AnswerRequest req)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var attempt = await _db.QuizAttempts.FirstOrDefaultAsync(a => a.Id == req.AttemptId && a.UserId == userId.Value);
        if (attempt is null) return NotFound("Attempt not found.");
        if (attempt.QuizId != quizId) return BadRequest("Attempt/quiz mismatch.");

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

        // exp проверяем сами
        if (jwt.ValidTo <= DateTime.UtcNow)
        {
            var finished = await FinishAttempt(req.AttemptId, "Timeout");
            return Ok(finished);
        }

        var tokenAttemptId = qp.FindFirstValue("attemptId");
        var tokenQuizId = qp.FindFirstValue("quizId");
        var tokenQuestionId = qp.FindFirstValue("questionId");

        if (tokenAttemptId != req.AttemptId.ToString()) return BadRequest("questionToken attemptId mismatch.");
        if (tokenQuizId != quizId.ToString()) return BadRequest("questionToken quizId mismatch.");
        if (!Guid.TryParse(tokenQuestionId, out var questionId)) return BadRequest("questionToken questionId invalid.");

        // Найдём ОЖИДАЕМЫЙ текущий вопрос (первый неотвеченный)
        var expected = await _db.Questions
            .AsNoTracking()
            .Include(q => q.Options)
            .Where(q => q.QuizId == quizId)
            .Where(q => !_db.AttemptAnswers.Where(a => a.AttemptId == req.AttemptId).Select(a => a.QuestionId).Contains(q.Id))
            .OrderBy(q => q.Order)
            .FirstOrDefaultAsync();

        if (expected is null)
        {
            var finished = await FinishAttempt(req.AttemptId, "AlreadyFinished");
            return Ok(finished);
        }

        if (expected.Id != questionId) return BadRequest("Not the current question.");

        // option existence
        if (!expected.Options.Any(o => o.IsActive && o.Id == req.SelectedOptionId))
            return BadRequest("SelectedOptionId is not in this question.");

        // timeSpentMs из токена
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var issuedMsStr = qp.FindFirstValue("issuedAtMs");
        _ = long.TryParse(issuedMsStr, out var issuedMs);
        var spentMs = (int)Math.Clamp(nowMs - issuedMs, 0, (long)expected.TimeLimitMs);

        var isCorrect = (req.SelectedOptionId == expected.CorrectOptionId);

        _db.AttemptAnswers.Add(new AttemptAnswer
        {
            Id = Guid.NewGuid(),
            AttemptId = req.AttemptId,
            QuestionId = expected.Id,
            SelectedOptionId = req.SelectedOptionId,
            IsCorrect = isCorrect,
            TimeSpentMs = spentMs
        });

        await _db.SaveChangesAsync();

        // Следующий вопрос
        var next = await _db.Questions
            .AsNoTracking()
            .Include(q => q.Options)
            .Where(q => q.QuizId == quizId)
            .Where(q => !_db.AttemptAnswers.Where(a => a.AttemptId == req.AttemptId).Select(a => a.QuestionId).Contains(q.Id))
            .OrderBy(q => q.Order)
            .FirstOrDefaultAsync();

        if (next is null)
        {
            var finished = await FinishAttempt(req.AttemptId, "Completed");
            return Ok(finished);
        }

        var nextToken = CreateQuestionToken(req.AttemptId, quizId, next.Id, next.TimeLimitMs, out var nextExpUtc);

        // Для удобства клиента уже посчитаем промежуточные цифры
        var totalQuestions = await _db.Questions.Where(q => q.QuizId == quizId).CountAsync();
        var correctAnswers = await _db.AttemptAnswers.Where(x => x.AttemptId == req.AttemptId && x.IsCorrect).CountAsync();
        var maxScore = await _db.Questions.Where(q => q.QuizId == quizId).SumAsync(q => q.Points);

        var score = await _db.AttemptAnswers
            .Where(x => x.AttemptId == req.AttemptId && x.IsCorrect)
            .Join(_db.Questions, a => a.QuestionId, q => q.Id, (a, q) => q.Points)
            .SumAsync();

        return Ok(new AnswerResponse(
            Finished: false,
            Reason: null,
            Score: score,
            MaxScore: maxScore,
            CorrectAnswers: correctAnswers,
            TotalQuestions: totalQuestions,
            TotalTimeMs: 0,
            NextQuestionToken: nextToken,
            NextQuestionExpiresAtUtc: nextExpUtc,
            NextQuestion: ToPublicQuestionDto(next)
        ));
    }

    // LEADERBOARD: только perfect, по времени
    [HttpGet("{quizId:guid}/leaderboard")]
    public async Task<IActionResult> Leaderboard(Guid quizId, [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var items = await _db.QuizAttempts
            .AsNoTracking()
            .Where(a => a.QuizId == quizId && a.IsPerfect)
            .OrderBy(a => a.TotalTimeMs)
            .ThenBy(a => a.FinishedAtUtc)
            .Select(a => new LeaderboardItem(
                a.UserId,
                a.User.DisplayName,
                a.TotalTimeMs,
                a.FinishedAtUtc
            ))
            .Take(limit)
            .ToListAsync();

        return Ok(items);
    }
}
