using System.Net.Http.Json;
using System.Text.Json;
using KuSaFeBackend.Models;
using KuSaFeBackend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KuSaFeBackend.Tests;

public class AnalyticsTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task OwnerStatsExport_ReturnsCsvWithAttemptAndTaskRows()
    {
        await using var app = CreateApp();
        var (ownerId, gameId) = await SeedCompletedGame(app);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", ownerId.ToString());

        var resp = await client.GetAsync($"/v1/my/games/{gameId}/stats/export.csv");
        resp.EnsureSuccessStatusCode();

        var csv = await resp.Content.ReadAsStringAsync();
        Assert.Contains("attemptId", csv);
        Assert.Contains("What is 2 + 2?", csv);
        Assert.Contains("Player", csv);
        Assert.Contains("selectedOptionText", csv);
    }

    [Fact]
    public async Task OwnerStats_ReturnsTaskBreakdownMetrics()
    {
        await using var app = CreateApp();
        var (ownerId, gameId) = await SeedCompletedGame(app);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", ownerId.ToString());

        var resp = await client.GetAsync($"/v1/my/games/{gameId}/stats");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"incorrectAnswers\"", json);
        Assert.Contains("\"neutralAnswers\"", json);
        Assert.Contains("\"accuracyRate\"", json);
    }

    [Fact]
    public async Task Leaderboard_ReturnsDateScoreAndTaskBreakdown()
    {
        await using var app = CreateApp();
        var (_, gameId) = await SeedCompletedGame(app);
        var client = app.CreateClient();

        var resp = await client.GetAsync($"/v1/games/{gameId}/leaderboard");
        resp.EnsureSuccessStatusCode();

        var items = await resp.Content.ReadFromJsonAsync<List<LeaderboardItem>>(JsonOpts);
        Assert.NotNull(items);
        var item = Assert.Single(items!);
        Assert.Equal(100, item.Score);
        Assert.Equal(100, item.MaxScore);
        Assert.Equal(1, item.CorrectAnswers);
        Assert.Equal(1, item.TotalTasks);
        Assert.True(item.FinishedAtUtc > DateTime.MinValue);
    }

    private static TestAppFactory CreateApp() =>
        new(services =>
        {
            services.RemoveAll<IGameModerationService>();
            services.AddSingleton<IGameModerationService>(new FakeModerationService());
            services.AddAuthentication("Test")
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });

    private static async Task<(Guid OwnerId, Guid GameId)> SeedCompletedGame(TestAppFactory app)
    {
        var ownerId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var gameId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var optionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await app.SeedAsync(db =>
        {
            db.Users.AddRange(
                new User
                {
                    Id = ownerId,
                    Email = "owner@analytics.test",
                    DisplayName = "Owner",
                    PasswordHash = "test",
                    IsAdmin = false,
                    CreatedAtUtc = DateTime.UtcNow
                },
                new User
                {
                    Id = playerId,
                    Email = "player@analytics.test",
                    DisplayName = "Player",
                    PasswordHash = "test",
                    IsAdmin = false,
                    CreatedAtUtc = DateTime.UtcNow
                });

            db.Games.Add(new Game
            {
                Id = gameId,
                OwnerUserId = ownerId,
                Title = "Analytics game",
                Status = GameStatus.Verified,
                ThemeColor = "#7C3AED"
            });

            db.GameTasks.Add(new GameTask
            {
                Id = taskId,
                GameId = gameId,
                Type = GameTaskType.Quiz,
                Order = 0,
                Text = "What is 2 + 2?",
                Points = 100,
                TimeLimitMs = 60000
            });

            db.AnswerOptions.Add(new AnswerOption
            {
                Id = optionId,
                GameTaskId = taskId,
                Text = "4",
                SortOrder = 0,
                IsActive = true
            });

            db.GameAttempts.Add(new GameAttempt
            {
                Id = attemptId,
                UserId = playerId,
                GameId = gameId,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                FinishedAtUtc = DateTime.UtcNow,
                TotalTimeMs = 120000,
                Score = 100,
                MaxScore = 100,
                IsPerfect = true
            });

            db.GameTaskAnswers.Add(new GameTaskAnswer
            {
                Id = Guid.NewGuid(),
                AttemptId = attemptId,
                GameTaskId = taskId,
                SelectedOptionId = optionId,
                IsCorrect = true,
                TimeSpentMs = 1500
            });

            return Task.CompletedTask;
        });

        return (ownerId, gameId);
    }

    private record LeaderboardItem(Guid UserId, string DisplayName, long TotalTimeMs, DateTime FinishedAtUtc, int Score, int MaxScore, int CorrectAnswers, int TotalTasks);

    private sealed class FakeModerationService : IGameModerationService
    {
        public Task<GameModerationResult> ModerateAsync(Game game, CancellationToken cancellationToken) =>
            Task.FromResult(new GameModerationResult(true, 5, 0, "Approved."));
    }
}
