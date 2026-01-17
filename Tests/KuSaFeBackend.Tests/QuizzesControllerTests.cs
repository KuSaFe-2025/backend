using System.Net;
using System.Net.Http.Json;
using KuSaFeBackend.Models;
using Xunit;

namespace KuSaFeBackend.Tests;

public class QuizzesControllerTests
{
    [Fact]
    public async Task GetAll_ReturnsEmptyList_WhenNoQuizzes()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var resp = await client.GetAsync("/v1/quizzes");
        resp.EnsureSuccessStatusCode();

        var items = await resp.Content.ReadFromJsonAsync<List<QuizListItem>>();
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetAll_ReturnsQuizzes_OrderedByCreatedAtDesc()
    {
        await using var app = new TestAppFactory();

        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();

        await app.SeedAsync(db =>
        {
            db.Quizzes.AddRange(
                new Quiz
                {
                    Id = olderId,
                    Title = "Old",
                    Description = null,
                    DescriptionFormat = (DescriptionFormat)0,
                    CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ThemeColor = "#111111",
                },
                new Quiz
                {
                    Id = newerId,
                    Title = "New",
                    Description = "desc",
                    DescriptionFormat = (DescriptionFormat)0,
                    CreatedAtUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    ThemeColor = "#222222",
                }
            );

            return Task.CompletedTask;
        });

        var client = app.CreateClient();
        var items = await client.GetFromJsonAsync<List<QuizListItem>>("/v1/quizzes");

        Assert.NotNull(items);
        Assert.Equal(2, items!.Count);

        // сортировка OrderByDescending(CreatedAtUtc)
        Assert.Equal(newerId, items[0].Id);
        Assert.Equal(olderId, items[1].Id);
    }

    [Fact]
    public async Task GetOne_Returns404_WhenMissing()
    {
        await using var app = new TestAppFactory();
        var client = app.CreateClient();

        var resp = await client.GetAsync($"/v1/quizzes/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetOne_ReturnsMeta_WhenExists()
    {
        await using var app = new TestAppFactory();
        var quizId = Guid.NewGuid();

        await app.SeedAsync(db =>
        {
            db.Quizzes.Add(new Quiz
            {
                Id = quizId,
                Title = "Quiz",
                Description = "D",
                DescriptionFormat = (DescriptionFormat)0,
                CreatedAtUtc = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc),
                ThemeColor = null,
            });

            return Task.CompletedTask;
        });

        var client = app.CreateClient();
        var resp = await client.GetAsync($"/v1/quizzes/{quizId}");
        resp.EnsureSuccessStatusCode();

        var obj = await resp.Content.ReadFromJsonAsync<QuizMeta>();
        Assert.NotNull(obj);
        Assert.Equal(quizId, obj!.Id);
        Assert.Equal("Quiz", obj.Title);

        // пока без вопросов — ожидаем 0
        Assert.Equal(0, obj.QuestionsCount);
    }

    // DTO для десериализации ответа (под JSON)
    public record QuizListItem(
        Guid Id,
        string Title,
        string? Description,
        DescriptionFormat DescriptionFormat,
        int QuestionsCount,
        string? ThemeColor
    );

    public record QuizMeta(
        Guid Id,
        string Title,
        string? Description,
        DescriptionFormat DescriptionFormat,
        DateTime CreatedAtUtc,
        int QuestionsCount,
        string? ThemeColor
    );
}
