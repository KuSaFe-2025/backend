using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using KuSaFeBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace KuSaFeBackend.Services;

public record GameModerationResult(bool Approved, int YesVotes, int NoVotes, string Decision);

public interface IGameModerationService
{
    Task<GameModerationResult> ModerateAsync(Game game, CancellationToken cancellationToken);
}

public class OllamaGameModerationService : IGameModerationService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;

    public OllamaGameModerationService(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public async Task<GameModerationResult> ModerateAsync(Game game, CancellationToken cancellationToken)
    {
        var votes = Math.Max(1, int.TryParse(_cfg["Moderation:Votes"], out var configuredVotes) ? configuredVotes : 5);
        var model = _cfg["Moderation:Model"] ?? "llama3.1:8b";
        var prompt = BuildPrompt(game);
        var yes = 0;
        var no = 0;

        for (var i = 0; i < votes; i++)
        {
            var response = await AskOllama(model, prompt, cancellationToken);
            if (response.Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase))
                yes++;
            else
                no++;
        }

        var approved = yes > no;
        var decision = approved
            ? $"Approved by local AI moderation ({yes}/{votes} YES)."
            : $"Rejected by local AI moderation ({no}/{votes} NO).";

        return new GameModerationResult(approved, yes, no, decision);
    }

    private async Task<string> AskOllama(string model, string prompt, CancellationToken cancellationToken)
    {
        var resp = await _http.PostAsJsonAsync("/api/generate", new OllamaGenerateRequest(model, prompt, false), cancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: cancellationToken);
        return body?.Response ?? "NO";
    }

    private static string BuildPrompt(Game game)
    {
        var content = new StringBuilder();
        content.AppendLine("You are moderating a user-created educational game.");
        content.AppendLine("Return exactly YES if this content is safe for a public educational platform.");
        content.AppendLine("Return exactly NO if it contains prohibited, hateful, sexual, violent, illegal, or otherwise unsafe content.");
        content.AppendLine();
        content.AppendLine($"Title: {game.Title}");
        content.AppendLine($"Description: {game.Description}");
        content.AppendLine("Tasks:");

        foreach (var task in game.Tasks.OrderBy(t => t.Order))
        {
            content.AppendLine($"- Type: {task.Type}; Text: {task.Text}");
            var options = task.Options.Where(o => o.IsActive).OrderBy(o => o.SortOrder).Select(o => o.Text);
            content.AppendLine($"  Options: {string.Join("; ", options)}");
        }

        return content.ToString();
    }

    private record OllamaGenerateRequest(string Model, string Prompt, bool Stream);

    private record OllamaGenerateResponse([property: JsonPropertyName("response")] string Response);
}

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
            ? new GameModerationResult(false, 1, 4, "Rejected by deterministic E2E moderation (4/5 NO).")
            : new GameModerationResult(true, 4, 1, "Approved by deterministic E2E moderation (4/5 YES)."));
    }
}
