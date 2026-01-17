using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KuSaFeBackend.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace KuSaFeBackend.Tests;

public class QuizPlayControllerTests
{
    private const string JwtKey = "TEST_TEST_TEST_TEST_TEST_TEST_TEST_TEST_32+";
    private const string JwtIssuer = "kusafe-tests";
    private const string JwtAudience = "kusafe-tests";

    [Fact]
    public async Task Start_Returns401_WhenNoBearerToken()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var resp = await client.PostAsync($"/v1/quizzes/{Guid.NewGuid()}/start", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Start_Returns400_WhenQuizHasNoQuestions()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        // авторизуемся, иначе будет 401 и не дойдём до BadRequest
        var userId = Guid.NewGuid();
        await app.SeedAsync(db =>
        {
            db.Users.Add(new User
            {
                Id = userId,
                Email = "u@test.com",
                DisplayName = "U",
                CreatedAtUtc = DateTime.UtcNow,
                IsAdmin = false,
                PasswordHash = "x" // неважно для этого теста
            });
            return Task.CompletedTask;
        });

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken(userId));

        var resp = await client.PostAsync($"/v1/quizzes/{Guid.NewGuid()}/start", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Start_Succeeds_ReturnsFirstQuestion_AndCreatesAttempt()
    {
        await using var app = new TestAppFactory();
        var (userId, quizId, q1Id, q2Id, q1CorrectOptId, q1InactiveOptId) = await SeedQuiz2Q(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken(userId));

        var resp = await client.PostAsync($"/v1/quizzes/{quizId}/start", null);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<StartResponse>();
        Assert.NotNull(body);

        Assert.NotEqual(Guid.Empty, body!.AttemptId);
        Assert.False(string.IsNullOrWhiteSpace(body.QuestionToken));

        // должен прийти первый по Order (q1)
        Assert.Equal(q1Id, body.Question.Id);
        Assert.Equal(1, body.Question.Order);

        // options: только активные, порядок не проверяем (там shuffle)
        var optionIds = body.Question.Options.Select(o => o.Id).ToHashSet();
        Assert.Contains(q1CorrectOptId, optionIds);
        Assert.DoesNotContain(q1InactiveOptId, optionIds);

        // токен содержит claims
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.QuestionToken);
        Assert.Equal("quiz_question", jwt.Claims.First(c => c.Type == "typ").Value);
        Assert.Equal(body.AttemptId.ToString(), jwt.Claims.First(c => c.Type == "attemptId").Value);
        Assert.Equal(quizId.ToString(), jwt.Claims.First(c => c.Type == "quizId").Value);
        Assert.Equal(q1Id.ToString(), jwt.Claims.First(c => c.Type == "questionId").Value);

        // попытка реально создана в БД
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempt = await db.QuizAttempts.FindAsync(body.AttemptId);
        Assert.NotNull(attempt);
        Assert.Equal(userId, attempt!.UserId);
        Assert.Equal(quizId, attempt.QuizId);
        Assert.True(attempt.MaxScore > 0);
    }

    [Fact]
    public async Task Answer_ProgressesToNextQuestion_AndScores()
    {
        await using var app = new TestAppFactory();
        var (userId, quizId, q1Id, q2Id, q1CorrectOptId, _) = await SeedQuiz2Q(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken(userId));

        // start
        var start = await client.PostAsync($"/v1/quizzes/{quizId}/start", null);
        start.EnsureSuccessStatusCode();
        var s = await start.Content.ReadFromJsonAsync<StartResponse>();
        Assert.NotNull(s);

        // answer q1 correctly
        var a1Resp = await client.PostAsJsonAsync($"/v1/quizzes/{quizId}/answer", new
        {
            attemptId = s!.AttemptId,
            questionToken = s.QuestionToken,
            selectedOptionId = q1CorrectOptId
        });

        a1Resp.EnsureSuccessStatusCode();
        var a1 = await a1Resp.Content.ReadFromJsonAsync<AnswerResponse>();
        Assert.NotNull(a1);

        Assert.False(a1!.Finished);
        Assert.NotNull(a1.NextQuestionToken);
        Assert.NotNull(a1.NextQuestion);

        Assert.Equal(1, a1.CorrectAnswers);
        Assert.Equal(2, a1.TotalQuestions);
        Assert.True(a1.Score > 0);
        Assert.Equal(a1.MaxScore, a1.Score + (a1.MaxScore - a1.Score)); // просто sanity

        Assert.Equal(q2Id, a1.NextQuestion!.Id);
        Assert.Equal(2, a1.NextQuestion.Order);

        // answer q2 correctly (берём correctOptionId из DTO, раз он публичный)
        var q2Correct = a1.NextQuestion.CorrectOptionId ?? throw new Exception("q2 CorrectOptionId missing in DTO");

        var a2Resp = await client.PostAsJsonAsync($"/v1/quizzes/{quizId}/answer", new
        {
            attemptId = s.AttemptId,
            questionToken = a1.NextQuestionToken,
            selectedOptionId = q2Correct
        });

        a2Resp.EnsureSuccessStatusCode();
        var a2 = await a2Resp.Content.ReadFromJsonAsync<AnswerResponse>();
        Assert.NotNull(a2);

        Assert.True(a2!.Finished);
        Assert.Equal("Completed", a2.Reason);
        Assert.Equal(a2.MaxScore, a2.Score);
        Assert.Equal(2, a2.CorrectAnswers);

        // проверим, что попытка в БД стала perfect
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempt = await db.QuizAttempts.FindAsync(s.AttemptId);
        Assert.NotNull(attempt);
        Assert.True(attempt!.IsPerfect);
        Assert.NotNull(attempt.FinishedAtUtc);
    }

    [Fact]
    public async Task Answer_Returns400_WhenSelectedOptionNotInQuestion()
    {
        await using var app = new TestAppFactory();
        var (userId, quizId, _, _, _, _) = await SeedQuiz2Q(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken(userId));

        var start = await client.PostAsync($"/v1/quizzes/{quizId}/start", null);
        start.EnsureSuccessStatusCode();
        var s = await start.Content.ReadFromJsonAsync<StartResponse>();

        var resp = await client.PostAsJsonAsync($"/v1/quizzes/{quizId}/answer", new
        {
            attemptId = s!.AttemptId,
            questionToken = s.QuestionToken,
            selectedOptionId = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Answer_Returns400_WhenNotCurrentQuestion_ReusingOldToken()
    {
        await using var app = new TestAppFactory();
        var (userId, quizId, _, _, q1CorrectOptId, _) = await SeedQuiz2Q(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken(userId));

        var start = await client.PostAsync($"/v1/quizzes/{quizId}/start", null);
        start.EnsureSuccessStatusCode();
        var s = await start.Content.ReadFromJsonAsync<StartResponse>();

        // ответили q1
        var a1 = await client.PostAsJsonAsync($"/v1/quizzes/{quizId}/answer", new
        {
            attemptId = s!.AttemptId,
            questionToken = s.QuestionToken,
            selectedOptionId = q1CorrectOptId
        });
        a1.EnsureSuccessStatusCode();

        // пробуем снова ответить, используя СТАРЫЙ токен q1
        var again = await client.PostAsJsonAsync($"/v1/quizzes/{quizId}/answer", new
        {
            attemptId = s.AttemptId,
            questionToken = s.QuestionToken,
            selectedOptionId = q1CorrectOptId
        });

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Answer_Returns401_WhenQuestionTokenInvalid()
    {
        await using var app = new TestAppFactory();
        var (userId, quizId, _, _, _, _) = await SeedQuiz2Q(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken(userId));

        var resp = await client.PostAsJsonAsync($"/v1/quizzes/{quizId}/answer", new
        {
            attemptId = Guid.NewGuid(),
            questionToken = "not-a-jwt",
            selectedOptionId = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Answer_FinishesWithTimeout_WhenTokenExpired()
    {
        await using var app = new TestAppFactory();
        var (userId, quizId, q1Id, _, _, _) = await SeedQuiz2Q(app);

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken(userId));

        // start создаёт attempt
        var start = await client.PostAsync($"/v1/quizzes/{quizId}/start", null);
        start.EnsureSuccessStatusCode();
        var s = await start.Content.ReadFromJsonAsync<StartResponse>();
        Assert.NotNull(s);

        // делаем корректно подписанный, но уже истёкший questionToken
        var expiredToken = CreateExpiredQuestionToken(
            attemptId: s!.AttemptId,
            quizId: quizId,
            questionId: q1Id
        );

        var resp = await client.PostAsJsonAsync($"/v1/quizzes/{quizId}/answer", new
        {
            attemptId = s.AttemptId,
            questionToken = expiredToken,
            selectedOptionId = Guid.NewGuid() // не важно, timeout раньше
        });

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<AnswerResponse>();
        Assert.NotNull(body);

        Assert.True(body!.Finished);
        Assert.Equal("Timeout", body.Reason);
    }

    [Fact]
    public async Task Leaderboard_ReturnsOnlyPerfect_SortedByTime_AndRespectsLimitClamp()
    {
        await using var app = new TestAppFactory();

        var quizId = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();

        await app.SeedAsync(db =>
        {
            db.Quizzes.Add(new Quiz
            {
                Id = quizId,
                Title = "Q",
                Description = null,
                DescriptionFormat = (DescriptionFormat)0,
                CreatedAtUtc = DateTime.UtcNow,
                ThemeColor = null
            });

            db.Users.AddRange(
                new User { Id = u1, Email = "a@a.com", DisplayName = "A", CreatedAtUtc = DateTime.UtcNow, IsAdmin = false, PasswordHash = "x" },
                new User { Id = u2, Email = "b@b.com", DisplayName = "B", CreatedAtUtc = DateTime.UtcNow, IsAdmin = false, PasswordHash = "x" },
                new User { Id = u3, Email = "c@c.com", DisplayName = "C", CreatedAtUtc = DateTime.UtcNow, IsAdmin = false, PasswordHash = "x" }
            );

            // only perfect должны попасть
            db.QuizAttempts.AddRange(
                new QuizAttempt
                {
                    Id = Guid.NewGuid(),
                    UserId = u1,
                    QuizId = quizId,
                    StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    FinishedAtUtc = DateTime.UtcNow.AddMinutes(-9),
                    TotalTimeMs = 2000,
                    Score = 10,
                    MaxScore = 10,
                    IsPerfect = true
                },
                new QuizAttempt
                {
                    Id = Guid.NewGuid(),
                    UserId = u2,
                    QuizId = quizId,
                    StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    FinishedAtUtc = DateTime.UtcNow.AddMinutes(-8),
                    TotalTimeMs = 1000,
                    Score = 10,
                    MaxScore = 10,
                    IsPerfect = true
                },
                new QuizAttempt
                {
                    Id = Guid.NewGuid(),
                    UserId = u3,
                    QuizId = quizId,
                    StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    FinishedAtUtc = DateTime.UtcNow.AddMinutes(-7),
                    TotalTimeMs = 500,
                    Score = 5,
                    MaxScore = 10,
                    IsPerfect = false
                }
            );

            return Task.CompletedTask;
        });

        var client = app.CreateClient();

        // limit=0 -> clamp до 1
        var items1 = await client.GetFromJsonAsync<List<LeaderboardItem>>($"/v1/quizzes/{quizId}/leaderboard?limit=0");
        Assert.NotNull(items1);
        Assert.Single(items1!);
        Assert.Equal(u2, items1[0].UserId); // самый быстрый perfect

        // limit=50 -> оба perfect, отсортированы по TotalTimeMs
        var items2 = await client.GetFromJsonAsync<List<LeaderboardItem>>($"/v1/quizzes/{quizId}/leaderboard?limit=50");
        Assert.NotNull(items2);
        Assert.Equal(2, items2!.Count);
        Assert.Equal(u2, items2[0].UserId);
        Assert.Equal(u1, items2[1].UserId);
    }

    // ------------------ helpers ------------------

    private static string CreateAccessToken(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString())
        };

        var jwt = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static string CreateExpiredQuestionToken(Guid attemptId, Guid quizId, Guid questionId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new("typ", "quiz_question"),
            new("attemptId", attemptId.ToString()),
            new("quizId", quizId.ToString()),
            new("questionId", questionId.ToString()),
            new("issuedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString())
        };

        // expires в прошлом
        var jwt = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            notBefore: now.AddSeconds(-2),
            expires: now.AddSeconds(-1),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task<(Guid userId, Guid quizId, Guid q1Id, Guid q2Id, Guid q1CorrectOptId, Guid q1InactiveOptId)>
        SeedQuiz2Q(TestAppFactory app)
    {
        var userId = Guid.NewGuid();
        var quizId = Guid.NewGuid();
        var q1Id = Guid.NewGuid();
        var q2Id = Guid.NewGuid();

        var q1Correct = Guid.NewGuid();
        var q1Wrong = Guid.NewGuid();
        var q1Inactive = Guid.NewGuid();

        var q2Correct = Guid.NewGuid();
        var q2Wrong = Guid.NewGuid();

        await app.SeedAsync(db =>
        {
            db.Users.Add(new User
            {
                Id = userId,
                Email = "player@test.com",
                DisplayName = "Player",
                CreatedAtUtc = DateTime.UtcNow,
                IsAdmin = false,
                PasswordHash = "x"
            });

            db.Quizzes.Add(new Quiz
            {
                Id = quizId,
                Title = "Quiz",
                Description = null,
                DescriptionFormat = (DescriptionFormat)0,
                CreatedAtUtc = DateTime.UtcNow,
                ThemeColor = null
            });

            db.Questions.AddRange(
                new Question
                {
                    Id = q1Id,
                    QuizId = quizId,
                    Order = 1,
                    Text = "Q1",
                    Points = 10,
                    TimeLimitMs = 60_000,
                    CorrectOptionId = q1Correct,
                    Options = new List<AnswerOption>
                    {
                        new AnswerOption { Id = q1Correct, QuestionId = q1Id, Text = "A", IsActive = true },
                        new AnswerOption { Id = q1Wrong,   QuestionId = q1Id, Text = "B", IsActive = true },
                        new AnswerOption { Id = q1Inactive,QuestionId = q1Id, Text = "C", IsActive = false },
                    }
                },
                new Question
                {
                    Id = q2Id,
                    QuizId = quizId,
                    Order = 2,
                    Text = "Q2",
                    Points = 5,
                    TimeLimitMs = 60_000,
                    CorrectOptionId = q2Correct,
                    Options = new List<AnswerOption>
                    {
                        new AnswerOption { Id = q2Correct, QuestionId = q2Id, Text = "A2", IsActive = true },
                        new AnswerOption { Id = q2Wrong,   QuestionId = q2Id, Text = "B2", IsActive = true },
                    }
                }
            );

            return Task.CompletedTask;
        });

        return (userId, quizId, q1Id, q2Id, q1Correct, q1Inactive);
    }

    // DTO для JSON
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

    public record StartResponse(
        Guid AttemptId,
        string QuestionToken,
        DateTime QuestionExpiresAtUtc,
        PublicQuestionDto Question
    );

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
}
