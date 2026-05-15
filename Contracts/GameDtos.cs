using KuSaFeBackend.Models;

namespace KuSaFeBackend.Contracts;

public record EditorOptionDto(Guid Id, string Text, bool IsActive, int SortOrder);

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
    string? ThemeColor,
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
    string? ThemeColor
);

public record GameTaskUpsertRequest(
    GameTaskType Type,
    int Order,
    string Text,
    int Points,
    int TimeLimitMs,
    List<string>? Options,
    int? CorrectOptionIndex
);

public record OwnerGameStatsTaskItemDto(
    Guid TaskId,
    string Text,
    GameTaskType Type,
    int Attempts,
    int CorrectAnswers,
    int TotalAnswers,
    List<string> RecentOpenAnswers,
    List<PollOptionStatsDto> PollOptions
);

public record PollOptionStatsDto(Guid OptionId, string Text, int Votes);

public record OwnerGameStatsDto(
    Guid GameId,
    int AttemptsCount,
    double AverageScore,
    double AverageTimeMs,
    double PerfectRate,
    List<OwnerGameStatsTaskItemDto> Tasks
);
