using KuSaFeBackend.Models;

namespace KuSaFeBackend.Contracts;

public record EditorOptionDto(Guid Id, string Text, bool IsActive, int SortOrder, bool IsCorrect);

public record GameTaskEditorDto(
    Guid Id,
    GameTaskType Type,
    int Order,
    string Text,
    int Points,
    int TimeLimitMs,
    Guid? CorrectOptionId,
    List<EditorOptionDto> Options
);

public record GameEditorDto(
    Guid Id,
    string Title,
    string? Description,
    DescriptionFormat DescriptionFormat,
    string? ThemeColor,
    bool IsPrivate,
    int? MaxAttemptsPerUser,
    DateTime? AvailableFromUtc,
    DateTime? AvailableUntilUtc,
    GameStatus Status,
    DateTime? LastModeratedAtUtc,
    string? ModerationDecision,
    int ModerationYesVotes,
    int ModerationNoVotes,
    Guid OwnerUserId,
    string OwnerDisplayName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    List<GameTaskEditorDto> Tasks
);

public record GameListItemDto(
    Guid Id,
    string Title,
    string? Description,
    DescriptionFormat DescriptionFormat,
    int TasksCount,
    int AttemptsCount,
    double AverageRating,
    string? ThemeColor,
    bool IsPrivate,
    int? MaxAttemptsPerUser,
    DateTime? AvailableFromUtc,
    DateTime? AvailableUntilUtc,
    GameStatus Status,
    DateTime? LastModeratedAtUtc,
    string? ModerationDecision,
    int ModerationYesVotes,
    int ModerationNoVotes,
    string OwnerDisplayName,
    bool CanEdit
);

public record TaskTypeCountDto(GameTaskType Type, int Count);

public record GameDetailsDto(
    Guid Id,
    string Title,
    string? Description,
    DescriptionFormat DescriptionFormat,
    DateTime CreatedAtUtc,
    string? ThemeColor,
    bool IsPrivate,
    int? MaxAttemptsPerUser,
    DateTime? AvailableFromUtc,
    DateTime? AvailableUntilUtc,
    GameStatus Status,
    DateTime? LastModeratedAtUtc,
    string? ModerationDecision,
    int ModerationYesVotes,
    int ModerationNoVotes,
    string OwnerDisplayName,
    int TasksCount,
    List<TaskTypeCountDto> TaskTypeCounts,
    bool CanEdit
);

public record GameUpsertRequest(
    string Title,
    string? Description,
    DescriptionFormat DescriptionFormat,
    string? ThemeColor,
    bool IsPrivate = false,
    int? MaxAttemptsPerUser = null,
    DateTime? AvailableFromUtc = null,
    DateTime? AvailableUntilUtc = null
);

public record GameTaskUpsertRequest(
    GameTaskType Type,
    int Order,
    string Text,
    int Points,
    int TimeLimitMs,
    List<string>? Options,
    int? CorrectOptionIndex,
    List<int>? CorrectOptionIndexes = null
);

public record OwnerGameStatsTaskItemDto(
    Guid TaskId,
    string Text,
    GameTaskType Type,
    int Attempts,
    int CorrectAnswers,
    int IncorrectAnswers,
    int NeutralAnswers,
    int TotalAnswers,
    double AccuracyRate,
    List<string> RecentOpenAnswers,
    List<PollOptionStatsDto> PollOptions
);

public record PollOptionStatsDto(Guid OptionId, string Text, int Votes);

public record OpenAnswerItemDto(string Text);

public record OpenAnswersPageDto(
    List<OpenAnswerItemDto> Items,
    int Total,
    int Skip,
    int Take,
    bool HasMore
);

public record OwnerGameStatsDto(
    Guid GameId,
    int AttemptsCount,
    double AverageScore,
    double AverageTimeMs,
    double PerfectRate,
    List<OwnerGameStatsTaskItemDto> Tasks
);

public record PageDto<T>(List<T> Items, int Total, int Skip, int Take, bool HasMore);

public record GameAttemptListItemDto(
    Guid AttemptId,
    string DisplayName,
    long TotalTimeMs,
    DateTime FinishedAtUtc,
    int Score,
    int MaxScore,
    int CorrectAnswers,
    int ScoredTasks,
    int NeutralTasks
);

public record AttemptReviewOptionDto(Guid Id, string Text);

public record AttemptReviewItemDto(
    Guid AnswerId,
    Guid TaskId,
    GameTaskType Type,
    int Order,
    string TaskText,
    int Points,
    int TimeSpentMs,
    bool? IsCorrect,
    List<AttemptReviewOptionDto> Options,
    List<Guid> SelectedOptionIds,
    List<string> SelectedOptionTexts,
    string? TextAnswer,
    List<Guid> SubmittedOrderOptionIds,
    List<string> SubmittedOrderTexts,
    List<Guid> CorrectOptionIds,
    List<string> CorrectOptionTexts
);

public record AttemptReviewDto(
    Guid AttemptId,
    Guid GameId,
    string GameTitle,
    int Score,
    int MaxScore,
    long TotalTimeMs,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    List<AttemptReviewItemDto> Items
);

public record AiExplainAnswerResponse(string Explanation);

public record ReviewDto(
    Guid Id,
    Guid? GameId,
    string? GameTitle,
    string DisplayName,
    int Rating,
    string Text,
    DateTime CreatedAtUtc,
    bool CanDelete
);

public record ReviewCreateRequest(int Rating, string Text);

public record AiRewriteRequest(string Field, string Mode, string Text);
public record AiSuggestOptionRequest(GameTaskUpsertRequest Task, GameUpsertRequest Game);
public record AiSuggestTaskRequest(GameUpsertRequest Game, List<GameTaskUpsertRequest> Tasks);
public record AiSuggestOptionResponse(string Text);
public record AiSuggestTaskResponse(
    GameTaskType Type,
    string Text,
    int Points,
    int TimeLimitMs,
    List<string> Options,
    List<int> CorrectOptionIndexes
);
