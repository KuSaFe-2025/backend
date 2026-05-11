using System.Net.Http.Json;
using System.Text.Json;
using KuSaFeBackend.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace KuSaFeBackend.Tests;

public class GamesControllerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetGames_ForAnonymousUser_ReturnsOnlyVerifiedGames()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var ownerId = Guid.NewGuid();
        await app.SeedAsync(db =>
        {
            db.Users.Add(new User
            {
                Id = ownerId,
                Email = "owner@test.com",
                DisplayName = "Owner",
                PasswordHash = "x",
                IsAdmin = false
            });

            db.Games.AddRange(
                new Game
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerId,
                    Title = "Verified game",
                    Status = GameStatus.Verified,
                    ThemeColor = "#7C3AED"
                },
                new Game
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerId,
                    Title = "Draft game",
                    Status = GameStatus.Unverified,
                    ThemeColor = "#7C3AED"
                }
            );

            return Task.CompletedTask;
        });

        var resp = await client.GetAsync("/v1/games");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<List<GameListItem>>(JsonOpts);
        Assert.NotNull(body);
        Assert.Contains(body!, x => x.Title == "Verified game");
        Assert.DoesNotContain(body!, x => x.Title == "Draft game");
    }

    [Fact]
    public async Task GetGameDetails_ForAnonymousUser_HidesUnverifiedGame()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var ownerId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var verifiedId = Guid.NewGuid();

        await app.SeedAsync(db =>
        {
            db.Users.Add(new User
            {
                Id = ownerId,
                Email = "owner@test.com",
                DisplayName = "Owner",
                PasswordHash = "x",
                IsAdmin = false
            });

            db.Games.AddRange(
                new Game
                {
                    Id = draftId,
                    OwnerUserId = ownerId,
                    Title = "Draft game",
                    Status = GameStatus.Unverified,
                    ThemeColor = "#7C3AED"
                },
                new Game
                {
                    Id = verifiedId,
                    OwnerUserId = ownerId,
                    Title = "Verified game",
                    Status = GameStatus.Verified,
                    ThemeColor = "#7C3AED"
                }
            );

            return Task.CompletedTask;
        });

        var draftResp = await client.GetAsync($"/v1/games/{draftId}");
        var verifiedResp = await client.GetAsync($"/v1/games/{verifiedId}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, draftResp.StatusCode);
        verifiedResp.EnsureSuccessStatusCode();
    }

    private record GameListItem(Guid Id, string Title, string? Description, int DescriptionFormat, int TasksCount, string? ThemeColor, int Status, string OwnerDisplayName, bool CanEdit);
}
