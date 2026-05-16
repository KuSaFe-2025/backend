using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;

namespace KuSaFeBackend.Services;

public interface IAiAssistantService
{
    Task<string> RewriteAsync(string field, string mode, string text, CancellationToken cancellationToken);
    Task<AiSuggestOptionResponse> SuggestOptionAsync(AiSuggestOptionRequest request, CancellationToken cancellationToken);
    Task<AiSuggestTaskResponse> SuggestTaskAsync(AiSuggestTaskRequest request, CancellationToken cancellationToken);
    Task<string> ExplainAnswerAsync(Game game, GameTask task, GameTaskAnswer answer, CancellationToken cancellationToken);
}

public class OllamaAiAssistantService : IAiAssistantService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;

    public OllamaAiAssistantService(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public async Task<string> RewriteAsync(string field, string mode, string text, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder()
            .AppendLine("Ты помогаешь автору образовательной игры KuSaFe.")
            .AppendLine("Верни только переписанный русский текст без пояснений и Markdown-оберток.")
            .AppendLine($"Поле: {field}")
            .AppendLine($"Действие: {NormalizeRewriteMode(mode)}")
            .AppendLine("Исходный текст:")
            .AppendLine(text)
            .ToString();

        return (await AskOllama(prompt, cancellationToken)).Trim();
    }

    public async Task<AiSuggestOptionResponse> SuggestOptionAsync(AiSuggestOptionRequest request, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder()
            .AppendLine("Ты придумываешь вариант ответа для образовательной игры KuSaFe.")
            .AppendLine("Верни JSON строго вида {\"text\":\"...\"}. Никаких пояснений.")
            .AppendLine("Пиши на русском языке. Не повторяй существующие варианты.")
            .AppendLine("Важно: предложи именно НЕПРАВИЛЬНЫЙ, но правдоподобный вариант ответа. Не возвращай правильный ответ.")
            .AppendLine($"Название игры: {request.Game.Title}")
            .AppendLine($"Описание игры: {request.Game.Description}")
            .AppendLine($"Текст задачи: {request.Task.Text}")
            .AppendLine($"Тип задачи: {request.Task.Type}")
            .AppendLine($"Текущие варианты: {string.Join("; ", request.Task.Options ?? new List<string>())}")
            .ToString();

        return await ParseWithRetries(
            () => AskOllama(prompt, cancellationToken),
            json => JsonSerializer.Deserialize<AiSuggestOptionResponse>(json, JsonOptions),
            result => result is not null && !string.IsNullOrWhiteSpace(result.Text)
        ) ?? throw new InvalidOperationException("Не удалось разобрать ответ AI для нового варианта.");
    }

    public async Task<AiSuggestTaskResponse> SuggestTaskAsync(AiSuggestTaskRequest request, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder()
            .AppendLine("Ты придумываешь новую задачу для образовательной игры KuSaFe.")
            .AppendLine("Верни JSON строго вида {\"type\":0,\"text\":\"...\",\"points\":100,\"timeLimitMs\":60000,\"options\":[\"...\",\"...\"],\"correctOptionIndexes\":[0]}.")
            .AppendLine("type: 0 викторина, 1 верно/неверно, 2 порядок, 3 открытый ответ, 4 опрос, 5 множественный выбор.")
            .AppendLine("Если вопрос ожидает один правильный вариант ответа, используй type 0 (викторина), а не type 5. Используй type 5 только когда нужно выбрать несколько правильных вариантов одновременно.")
            .AppendLine("Пиши на русском языке. Никаких пояснений вне JSON.")
            .AppendLine($"Название игры: {request.Game.Title}")
            .AppendLine($"Описание игры: {request.Game.Description}")
            .AppendLine("Уже существующие задачи:")
            .AppendLine(string.Join("\n", request.Tasks.Select(t => $"- {t.Type}: {t.Text}")))
            .ToString();

        return await ParseWithRetries(
            () => AskOllama(prompt, cancellationToken),
            json => JsonSerializer.Deserialize<AiSuggestTaskResponse>(json, JsonOptions),
            IsValidTaskSuggestion
        ) ?? throw new InvalidOperationException("Не удалось разобрать ответ AI для новой задачи.");
    }

    public async Task<string> ExplainAnswerAsync(Game game, GameTask task, GameTaskAnswer answer, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder()
            .AppendLine("Ты объясняешь результат прохождения образовательной игры KuSaFe.")
            .AppendLine("Ответь на русском языке кратко: 1-3 предложения.")
            .AppendLine("Объясни именно почему правильный ответ является правильным. Не оценивай пользователя и не используй Markdown.")
            .AppendLine()
            .AppendLine("Полное содержимое игры:")
            .AppendLine($"Название: {game.Title}")
            .AppendLine($"Описание: {game.Description}")
            .AppendLine("Задания:")
            .AppendLine(BuildGameSnapshot(game))
            .AppendLine()
            .AppendLine("Текущее задание:")
            .AppendLine(BuildTaskSnapshot(task))
            .AppendLine($"Ответ пользователя: {DescribeSubmittedAnswer(task, answer)}")
            .AppendLine($"Правильный ответ: {DescribeCorrectAnswer(task)}")
            .ToString();

        return (await AskOllama(prompt, cancellationToken)).Trim();
    }

    private async Task<string> AskOllama(string prompt, CancellationToken cancellationToken)
    {
        var model = _cfg["Ai:Model"] ?? _cfg["Moderation:Model"] ?? "llama3.1:8b";
        var resp = await _http.PostAsJsonAsync("/api/generate", new OllamaGenerateRequest(model, prompt, false), cancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: cancellationToken);
        return body?.Response ?? "";
    }

    private static async Task<T?> ParseWithRetries<T>(Func<Task<string>> ask, Func<string, T?> parse, Func<T?, bool> valid)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var raw = await ask();
            var json = ExtractJson(raw);
            try
            {
                var result = parse(json);
                if (valid(result)) return result;
            }
            catch (JsonException)
            {
                // Retry once with a fresh Ollama answer.
            }
        }

        return default;
    }

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static bool IsValidTaskSuggestion(AiSuggestTaskResponse? suggestion)
    {
        if (suggestion is null) return false;
        if (string.IsNullOrWhiteSpace(suggestion.Text)) return false;
        if (suggestion.Points < 0 || suggestion.TimeLimitMs <= 0) return false;
        if (!Enum.IsDefined(typeof(GameTaskType), suggestion.Type)) return false;
        if (suggestion.Type is GameTaskType.OpenEnded) return true;
        if (suggestion.Options.Count < 2) return false;
        if (suggestion.Type is GameTaskType.Quiz or GameTaskType.TrueFalse or GameTaskType.Multichoice)
            return suggestion.CorrectOptionIndexes.Count > 0;
        return true;
    }

    private static string NormalizeRewriteMode(string mode) => mode switch
    {
        "professional" => "сделать профессиональнее",
        "simple" => "упростить",
        "hard" => "усложнить",
        _ => mode
    };

    private static string BuildGameSnapshot(Game game) =>
        string.Join("\n", game.Tasks.OrderBy(t => t.Order).Select(BuildTaskSnapshot));

    private static string BuildTaskSnapshot(GameTask task)
    {
        var options = task.Options
            .Where(o => o.IsActive)
            .OrderBy(o => o.SortOrder)
            .Select(o => $"- {o.Text}{(IsCorrectOption(task, o) ? " (правильный)" : "")}");

        return new StringBuilder()
            .AppendLine($"{task.Order + 1}. Тип: {task.Type}. Текст: {task.Text}")
            .AppendLine($"Варианты:\n{string.Join("\n", options)}")
            .ToString()
            .Trim();
    }

    private static string DescribeSubmittedAnswer(GameTask task, GameTaskAnswer answer)
    {
        var optionMap = task.Options.ToDictionary(o => o.Id, o => o.Text);

        if (answer.SelectedOptionId.HasValue && optionMap.TryGetValue(answer.SelectedOptionId.Value, out var selected))
            return selected;

        if (!string.IsNullOrWhiteSpace(answer.TextAnswer))
            return answer.TextAnswer;

        var ordered = ParseGuidList(answer.SubmittedOrder);
        if (ordered.Count > 0)
            return string.Join(" -> ", ordered.Select(id => optionMap.TryGetValue(id, out var text) ? text : id.ToString()));

        return "не указан";
    }

    private static string DescribeCorrectAnswer(GameTask task)
    {
        var correctOptions = task.Options.Where(o => o.IsActive && IsCorrectOption(task, o));
        var correct = (task.Type == GameTaskType.Puzzle
                ? correctOptions.OrderBy(o => o.SortOrder)
                : correctOptions.OrderBy(o => o.Text))
            .Select(o => o.Text)
            .ToList();

        if (task.Type == GameTaskType.Puzzle && correct.Count > 0)
            return string.Join(" -> ", correct);

        if (correct.Count > 0)
            return string.Join(", ", correct);

        if (!string.IsNullOrWhiteSpace(task.OpenEndedAcceptedAnswer))
            return task.OpenEndedAcceptedAnswer;

        return task.Type == GameTaskType.Poll ? "у опроса нет правильного ответа" : "не задан";
    }

    private static bool IsCorrectOption(GameTask task, AnswerOption option) =>
        task.Type switch
        {
            GameTaskType.Quiz or GameTaskType.TrueFalse => option.Id == task.CorrectOptionId,
            GameTaskType.Multichoice => option.IsCorrect,
            GameTaskType.Puzzle => true,
            _ => false
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private record OllamaGenerateRequest(string Model, string Prompt, bool Stream);
    private record OllamaGenerateResponse([property: JsonPropertyName("response")] string Response);
}

public class DeterministicAiAssistantService : IAiAssistantService
{
    public Task<string> RewriteAsync(string field, string mode, string text, CancellationToken cancellationToken)
    {
        var prefix = mode switch
        {
            "professional" => "Профессионально: ",
            "simple" => "Проще: ",
            "hard" => "Сложнее: ",
            _ => "AI: "
        };
        return Task.FromResult(prefix + text.Trim());
    }

    public Task<AiSuggestOptionResponse> SuggestOptionAsync(AiSuggestOptionRequest request, CancellationToken cancellationToken)
    {
        var count = (request.Task.Options ?? new List<string>()).Count + 1;
        return Task.FromResult(new AiSuggestOptionResponse($"Неправильный вариант {count}"));
    }

    public Task<AiSuggestTaskResponse> SuggestTaskAsync(AiSuggestTaskRequest request, CancellationToken cancellationToken)
    {
        var number = request.Tasks.Count + 1;
        return Task.FromResult(new AiSuggestTaskResponse(
            GameTaskType.Quiz,
            $"AI-задача {number}",
            100,
            60000,
            new List<string> { "Верный ответ", "Неверный ответ" },
            new List<int> { 0 }
        ));
    }

    public Task<string> ExplainAnswerAsync(Game game, GameTask task, GameTaskAnswer answer, CancellationToken cancellationToken) =>
        Task.FromResult("Правильный ответ выбран потому, что он соответствует условию задания.");
}
