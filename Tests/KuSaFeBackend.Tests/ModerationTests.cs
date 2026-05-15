using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using KuSaFeBackend.Models;
using KuSaFeBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KuSaFeBackend.Tests;

public class ModerationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SubmitForVerification_ApprovesGame_WhenModerationMajorityIsYes()
    {
        await using var app = CreateApp(new GameModerationResult(true, 4, 1, "Approved in tests."));
        var (client, _) = await CreateAuthedClient(app, "approve@test.com");

        var gameId = await CreateGameWithTask(client);

        var resp = await client.PostAsync($"/v1/my/games/{gameId}/submit-for-verification", null);
        resp.EnsureSuccessStatusCode();

        await app.AssertGameAsync(gameId, game =>
        {
            Assert.Equal(GameStatus.Verified, game.Status);
            Assert.Equal(4, game.ModerationYesVotes);
            Assert.Equal(1, game.ModerationNoVotes);
            Assert.NotNull(game.LastModeratedAtUtc);
            Assert.Equal("Approved in tests.", game.ModerationDecision);
        });
    }

    [Fact]
    public async Task SubmitForVerification_RejectsGame_WhenModerationMajorityIsNo()
    {
        await using var app = CreateApp(new GameModerationResult(false, 2, 3, "Rejected in tests."));
        var (client, _) = await CreateAuthedClient(app, "reject@test.com");

        var gameId = await CreateGameWithTask(client);

        var resp = await client.PostAsync($"/v1/my/games/{gameId}/submit-for-verification", null);
        resp.EnsureSuccessStatusCode();

        await app.AssertGameAsync(gameId, game =>
        {
            Assert.Equal(GameStatus.Rejected, game.Status);
            Assert.Equal(2, game.ModerationYesVotes);
            Assert.Equal(3, game.ModerationNoVotes);
            Assert.NotNull(game.LastModeratedAtUtc);
            Assert.Equal("Rejected in tests.", game.ModerationDecision);
        });
    }

    [Fact]
    public async Task SubmitForVerification_Fails_WhenGameHasNoTasks()
    {
        await using var app = CreateApp(new GameModerationResult(true, 5, 0, "unused"));
        var (client, _) = await CreateAuthedClient(app, "empty@test.com");

        var create = await client.PostAsJsonAsync("/v1/my/games", new
        {
            title = "Empty game",
            description = "No tasks yet",
            descriptionFormat = 1,
            themeColor = "#7C3AED"
        }, JsonOpts);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreatedGame>(JsonOpts);

        var resp = await client.PostAsync($"/v1/my/games/{created!.Id}/submit-for-verification", null);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static TestAppFactory CreateApp(GameModerationResult result) =>
        new(services =>
        {
            services.RemoveAll<IGameModerationService>();
            services.AddSingleton<IGameModerationService>(new FakeModerationService(result));
            services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });

    private static async Task<(HttpClient Client, Guid UserId)> CreateAuthedClient(TestAppFactory app, string email)
    {
        var userId = Guid.NewGuid();
        await app.SeedAsync(db =>
        {
            db.Users.Add(new User
            {
                Id = userId,
                Email = email,
                DisplayName = email.Split('@')[0],
                PasswordHash = "test",
                IsAdmin = false,
                CreatedAtUtc = DateTime.UtcNow
            });
            return Task.CompletedTask;
        });

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", userId.ToString());
        return (client, userId);
    }

    private static async Task<Guid> CreateGameWithTask(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/v1/my/games", new
        {
            title = "Moderated game",
            description = "Safe educational content",
            descriptionFormat = 1,
            themeColor = "#7C3AED"
        }, JsonOpts);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreatedGame>(JsonOpts);

        var task = await client.PostAsJsonAsync($"/v1/my/games/{created!.Id}/tasks", new
        {
            type = 0,
            order = 0,
            text = "What is 2 + 2?",
            points = 100,
            timeLimitMs = 60000,
            options = new[] { "4", "5" },
            correctOptionIndex = 0
        }, JsonOpts);
        task.EnsureSuccessStatusCode();

        return created.Id;
    }

    private record CreatedGame(Guid Id);

    private sealed class FakeModerationService : IGameModerationService
    {
        private readonly GameModerationResult _result;

        public FakeModerationService(GameModerationResult result) => _result = result;

        public Task<GameModerationResult> ModerateAsync(Game game, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }
}

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-UserId", out var userId))
            return Task.FromResult(AuthenticateResult.Fail("Missing test user id."));

        var isAdmin = Request.Headers.TryGetValue("X-Test-IsAdmin", out var adminHeader)
            && string.Equals(adminHeader.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("sub", userId.ToString()),
            new Claim("displayName", "Test User"),
            new Claim("isAdmin", isAdmin ? "true" : "false")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public static class TestAppFactoryAssertions
{
    public static async Task AssertGameAsync(this TestAppFactory app, Guid gameId, Action<Game> assert)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var game = await db.Games.FirstAsync(g => g.Id == gameId);
        assert(game);
    }
}
