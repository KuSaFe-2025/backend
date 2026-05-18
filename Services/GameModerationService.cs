using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KuSaFeBackend.Models;

namespace KuSaFeBackend.Services;

public record GameModerationResult(bool Approved, int YesVotes, int NoVotes, string Decision);

public interface IGameModerationService
{
    Task<GameModerationResult> ModerateAsync(Game game, CancellationToken cancellationToken);
}

/// <summary>
/// Thin proxy to the Python `game-moderation-service` microservice.
/// All prompt-building, voting logic and Ollama interaction is owned by the Python service.
/// </summary>
public class RemoteGameModerationService : IGameModerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public RemoteGameModerationService(HttpClient http)
    {
        _http = http;
    }

    public async Task<GameModerationResult> ModerateAsync(Game game, CancellationToken cancellationToken)
    {
        var body = new ModerateRequestDto(ToWireGame(game));
        var response = await _http.PostAsJsonAsync("/v1/moderate", body, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var parsed = await response.Content.ReadFromJsonAsync<ModerateResponseDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Empty response from game-moderation-service.");

        return new GameModerationResult(
            parsed.Approved,
            parsed.YesVotes,
            parsed.NoVotes,
            parsed.Decision ?? "No decision provided.");
    }

    private static GameWireDto ToWireGame(Game game) => new(
        Title: game.Title,
        Description: game.Description,
        Tasks: game.Tasks
            .OrderBy(t => t.Order)
            .Select(t => new TaskWireDto(
                Order: t.Order,
                Type: (int)t.Type,
                Text: t.Text,
                Options: t.Options
                    .Where(o => o.IsActive)
                    .OrderBy(o => o.SortOrder)
                    .Select(o => new OptionWireDto(o.Text, o.IsActive, o.SortOrder))
                    .ToList()
            ))
            .ToList()
    );

    // ---- Wire DTOs ----
    private record OptionWireDto(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("isActive")] bool IsActive,
        [property: JsonPropertyName("sortOrder")] int SortOrder);

    private record TaskWireDto(
        [property: JsonPropertyName("order")] int Order,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("options")] List<OptionWireDto> Options);

    private record GameWireDto(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("tasks")] List<TaskWireDto> Tasks);

    private record ModerateRequestDto([property: JsonPropertyName("game")] GameWireDto Game);

    private record ModerateResponseDto(
        [property: JsonPropertyName("approved")] bool Approved,
        [property: JsonPropertyName("yesVotes")] int YesVotes,
        [property: JsonPropertyName("noVotes")] int NoVotes,
        [property: JsonPropertyName("decision")] string? Decision);
}

/// <summary>
/// Offline fallback used in E2E tests and CI when the Python microservice is unavailable.
/// Kept identical to the previous behaviour.
/// </summary>
public class DeterministicGameModerationService : IGameModerationService
{
    public Task<GameModerationResult> ModerateAsync(Game game, CancellationToken cancellationToken)
    {
        var text = string.Join(" ", new[]
        {
            game.Title,
            game.Description ?? "",
            string.Join(" ", game.Tasks.Select(t => t.Text)),
            string.Join(" ", game.Tasks.SelectMany(t => t.Options).Select(o => o.Text))
        });

        var rejected = text.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || text.Contains("banword", StringComparison.OrdinalIgnoreCase);

        return Task.FromResult(rejected
            ? new GameModerationResult(false, 1, 4, "Rejected by deterministic E2E moderation (4/5 NO). Reason: Content contains a blocked word.")
            : new GameModerationResult(true, 4, 1, "Approved by deterministic E2E moderation (4/5 YES)."));
    }
}
