using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KuSaFeBackend.Contracts;
using KuSaFeBackend.Models;

namespace KuSaFeBackend.Services;

/// <summary>
/// Calls AI features. Implementations: <see cref="RemoteAiAssistantService"/> (Python microservice)
/// and <see cref="DeterministicAiAssistantService"/> (offline fallback for E2E).
/// </summary>
public interface IAiAssistantService
{
    Task<string> RewriteAsync(string field, string mode, string text, CancellationToken cancellationToken);
    Task<AiSuggestOptionResponse> SuggestOptionAsync(AiSuggestOptionRequest request, CancellationToken cancellationToken);
    Task<AiSuggestTaskResponse> SuggestTaskAsync(AiSuggestTaskRequest request, CancellationToken cancellationToken);
    Task<string> ExplainAnswerAsync(Game game, GameTask task, GameTaskAnswer answer, CancellationToken cancellationToken);
}

/// <summary>
/// Thin proxy to the Python `ai-assistant-service` microservice.
/// All prompt-building, JSON repair and Ollama interaction is owned by the Python service.
/// This class only maps domain models to the wire DTOs the microservice expects.
/// </summary>
public class RemoteAiAssistantService : IAiAssistantService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public RemoteAiAssistantService(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> RewriteAsync(string field, string mode, string text, CancellationToken cancellationToken)
    {
        var body = new RewriteRequestDto(field, mode, text);
        var response = await PostAsync<RewriteRequestDto, RewriteResponseDto>("/v1/rewrite", body, cancellationToken);
        return response.Text ?? string.Empty;
    }

    public async Task<AiSuggestOptionResponse> SuggestOptionAsync(AiSuggestOptionRequest request, CancellationToken cancellationToken)
    {
        var body = new SuggestOptionRequestDto(
            Game: new GameSnapshotDto(request.Game.Title, request.Game.Description),
            Task: new TaskSnapshotDto(
                Text: request.Task.Text,
                Type: (int)request.Task.Type,
                Options: request.Task.Options ?? new List<string>()
            )
        );

        var response = await PostAsync<SuggestOptionRequestDto, SuggestOptionResponseDto>(
            "/v1/suggest-option", body, cancellationToken);

        if (string.IsNullOrWhiteSpace(response.Text))
            throw new InvalidOperationException("Не удалось разобрать ответ AI для нового варианта.");

        return new AiSuggestOptionResponse(response.Text!);
    }

    public async Task<AiSuggestTaskResponse> SuggestTaskAsync(AiSuggestTaskRequest request, CancellationToken cancellationToken)
    {
        var body = new SuggestTaskRequestDto(
            Game: new GameSnapshotDto(request.Game.Title, request.Game.Description),
            Tasks: (request.Tasks ?? new List<GameTaskUpsertRequest>())
                .Select(t => new TaskSummaryDto((int)t.Type, t.Text))
                .ToList()
        );

        var response = await PostAsync<SuggestTaskRequestDto, SuggestTaskResponseDto>(
            "/v1/suggest-task", body, cancellationToken);

        if (response is null || string.IsNullOrWhiteSpace(response.Text))
            throw new InvalidOperationException("Не удалось разобрать ответ AI для новой задачи.");

        return new AiSuggestTaskResponse(
            (GameTaskType)response.Type,
            response.Text!,
            response.Points,
            response.TimeLimitMs,
            response.Options ?? new List<string>(),
            response.CorrectOptionIndexes ?? new List<int>()
        );
    }

    public async Task<string> ExplainAnswerAsync(Game game, GameTask task, GameTaskAnswer answer, CancellationToken cancellationToken)
    {
        var body = new ExplainAnswerRequestDto(
            Game: ToFullGameDto(game),
            TaskId: task.Id,
            Answer: new AnswerDto(
                SelectedOptionId: answer.SelectedOptionId,
                TextAnswer: answer.TextAnswer,
                SubmittedOrder: answer.SubmittedOrder
            )
        );

        var response = await PostAsync<ExplainAnswerRequestDto, ExplainAnswerResponseDto>(
            "/v1/explain-answer", body, cancellationToken);
        return response.Explanation ?? string.Empty;
    }

    private static FullGameDto ToFullGameDto(Game game) => new(
        Title: game.Title,
        Description: game.Description,
        Tasks: game.Tasks
            .OrderBy(t => t.Order)
            .Select(t => new FullTaskDto(
                Id: t.Id,
                Order: t.Order,
                Type: (int)t.Type,
                Text: t.Text,
                CorrectOptionId: t.CorrectOptionId,
                OpenEndedAcceptedAnswer: t.OpenEndedAcceptedAnswer,
                Options: t.Options
                    .OrderBy(o => o.SortOrder)
                    .Select(o => new FullOptionDto(o.Id, o.Text, o.IsActive, o.SortOrder, o.IsCorrect))
                    .ToList()
            ))
            .ToList()
    );

    private async Task<TResp> PostAsync<TReq, TResp>(string path, TReq body, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(path, body, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var parsed = await response.Content.ReadFromJsonAsync<TResp>(JsonOptions, cancellationToken);
        return parsed ?? throw new InvalidOperationException($"Empty response from ai-assistant-service at {path}.");
    }

    // ---- Wire DTOs ----
    private record RewriteRequestDto(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("text")] string Text);

    private record RewriteResponseDto([property: JsonPropertyName("text")] string? Text);

    private record GameSnapshotDto(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description);

    private record TaskSnapshotDto(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("options")] List<string> Options);

    private record SuggestOptionRequestDto(
        [property: JsonPropertyName("game")] GameSnapshotDto Game,
        [property: JsonPropertyName("task")] TaskSnapshotDto Task);

    private record SuggestOptionResponseDto([property: JsonPropertyName("text")] string? Text);

    private record TaskSummaryDto(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("text")] string Text);

    private record SuggestTaskRequestDto(
        [property: JsonPropertyName("game")] GameSnapshotDto Game,
        [property: JsonPropertyName("tasks")] List<TaskSummaryDto> Tasks);

    private record SuggestTaskResponseDto(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("points")] int Points,
        [property: JsonPropertyName("timeLimitMs")] int TimeLimitMs,
        [property: JsonPropertyName("options")] List<string>? Options,
        [property: JsonPropertyName("correctOptionIndexes")] List<int>? CorrectOptionIndexes);

    private record FullOptionDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("isActive")] bool IsActive,
        [property: JsonPropertyName("sortOrder")] int SortOrder,
        [property: JsonPropertyName("isCorrect")] bool IsCorrect);

    private record FullTaskDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("order")] int Order,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("correctOptionId")] Guid? CorrectOptionId,
        [property: JsonPropertyName("openEndedAcceptedAnswer")] string? OpenEndedAcceptedAnswer,
        [property: JsonPropertyName("options")] List<FullOptionDto> Options);

    private record FullGameDto(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("tasks")] List<FullTaskDto> Tasks);

    private record AnswerDto(
        [property: JsonPropertyName("selectedOptionId")] Guid? SelectedOptionId,
        [property: JsonPropertyName("textAnswer")] string? TextAnswer,
        [property: JsonPropertyName("submittedOrder")] string? SubmittedOrder);

    private record ExplainAnswerRequestDto(
        [property: JsonPropertyName("game")] FullGameDto Game,
        [property: JsonPropertyName("taskId")] Guid TaskId,
        [property: JsonPropertyName("answer")] AnswerDto Answer);

    private record ExplainAnswerResponseDto([property: JsonPropertyName("explanation")] string? Explanation);
}

/// <summary>
/// Offline fallback used in E2E tests and CI when the Python microservice is unavailable.
/// Kept identical to the previous behaviour to keep existing tests green.
/// </summary>
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
