using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

/// <summary>
/// Line-delimited JSON request sent through the local agent pipe.
/// </summary>
public sealed record AgentRequest(
    string Id,
    string Method,
    JsonElement Parameters);

public sealed record AgentError(
    string Code,
    string Message);

public sealed record AgentResponse(
    string Id,
    bool Ok,
    JsonElement? Result = null,
    AgentError? Error = null);

public sealed record AgentPingResult(string Message);

public sealed record AgentCapability(
    string Method,
    bool Mutating,
    bool ConfirmationRequired,
    string Description);

public sealed record AgentOrganizationApplyResult(
    string HistoryId,
    int MovedItemCount,
    int CreatedWidgetCount,
    int RetainedItemCount);

public sealed record AgentUndoResult(string HistoryId, bool Undone);

public sealed record AgentOperationHistorySummary(
    string Id,
    DateTime TimestampUtc,
    string WidgetId,
    string WidgetName,
    string ActionType,
    string TransferMode,
    bool CanUndo,
    bool IsUndone,
    bool IsFailed,
    int ItemCount,
    string[] Items);

public sealed record AgentOperationHistoryResult(AgentOperationHistorySummary[] Entries);

public sealed record AgentUndoPreview(string HistoryId, bool CanUndo, string WidgetName, string ActionType, int ItemCount, string[] Items);

public sealed record AgentAppStatus(
    string Version,
    int ProcessId,
    string PipeName,
    int WidgetCount,
    int VisibleWidgetCount,
    int TodoWidgetCount,
    bool IsReady);

public sealed record AgentWidgetSummary(
    string Id,
    string Name,
    string Kind,
    bool IsVisible,
    bool IsDisabled,
    string? MappedFolderPath);

public sealed record AgentTodoSummary(
    string WidgetId,
    string WidgetName,
    string Id,
    string Title,
    bool IsCompleted,
    bool IsImportant,
    DateTimeOffset? DueDate,
    string? ColorMarker,
    DateTimeOffset UpdatedAt);

public sealed record AgentDesktopTargetSummary(
    string Id,
    string Name,
    string DirectoryPath,
    bool CreatesWidget,
    int ItemCount,
    long TotalSize,
    string[] Items);

public sealed record AgentDesktopPreview(
    string PlanId,
    string DesktopPath,
    int EligibleItemCount,
    long TotalTransferSize,
    AgentDesktopTargetSummary[] Targets,
    string[] ExcludedItems);

public sealed record AgentDesktopItemSummary(
    string Path,
    string Name,
    string Extension,
    long Size,
    string Category,
    string? Subtype,
    bool IsEligible,
    string? ExclusionReason);

public sealed record AgentDesktopScanResult(
    string DesktopPath,
    AgentDesktopItemSummary[] Items);

public sealed record AgentShellSystemEntryResult(
    string WidgetId,
    string SystemId,
    string ShortcutPath,
    string DisplayName,
    bool DesktopIconHidden);

public sealed record AgentWidgetItemSummary(
    string WidgetId,
    string Name,
    string Path,
    string Type,
    bool IsShortcut,
    string? ShortcutTarget,
    string? Arguments,
    long Size,
    DateTime ModifiedAt);

public sealed record AgentWidgetItemsResult(
    string WidgetId,
    string WidgetName,
    string FolderPath,
    AgentWidgetItemSummary[] Items);

public sealed record AgentWidgetMutationResult(
    string OperationId,
    int AffectedCount,
    string[] Paths);

public sealed record AgentMoveWidgetItemsResult(
    string OperationId,
    string SourceWidgetId,
    string TargetWidgetId,
    int MovedCount,
    string[] DestinationPaths);

public sealed record AgentDuplicateGroup(
    string Key,
    string KeptPath,
    string[] DuplicatePaths);

public sealed record AgentDeduplicatePreview(
    string PlanId,
    int DuplicateCount,
    AgentDuplicateGroup[] Groups);

public sealed record AgentDeduplicateApplyResult(
    string HistoryId,
    int RemovedCount,
    string[] QuarantinePaths);

public sealed record AgentWidgetLayoutEntry(
    string WidgetId,
    string Name,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsCollapsed,
    bool IsPositionLocked,
    bool IsSizeLocked,
    bool IsVisible);

public sealed record AgentWidgetLayoutResult(AgentWidgetLayoutEntry[] Widgets);

public sealed record AgentWidgetLayoutPreview(
    string PlanId,
    AgentWidgetLayoutEntry[] Changes);

public sealed record AgentWidgetLayoutApplyResult(
    string OperationId,
    int UpdatedCount);

public sealed record AgentTodoMutationResult(string WidgetId, string ItemId, string Operation, AgentTodoSummary Item);

public sealed record AgentTodoBatchResult(string WidgetId, string Operation, int AffectedCount, string[] ItemIds);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
[JsonSerializable(typeof(AgentError))]
[JsonSerializable(typeof(AgentPingResult))]
[JsonSerializable(typeof(AgentCapability[]), TypeInfoPropertyName = "Capabilities")]
[JsonSerializable(typeof(AgentOrganizationApplyResult))]
[JsonSerializable(typeof(AgentUndoResult))]
[JsonSerializable(typeof(AgentOperationHistoryResult))]
[JsonSerializable(typeof(AgentUndoPreview))]
[JsonSerializable(typeof(AgentAppStatus))]
[JsonSerializable(typeof(AgentWidgetSummary[]), TypeInfoPropertyName = "WidgetSummaries")]
[JsonSerializable(typeof(AgentTodoSummary[]), TypeInfoPropertyName = "TodoSummaries")]
[JsonSerializable(typeof(AgentDesktopPreview))]
[JsonSerializable(typeof(AgentDesktopTargetSummary[]))]
[JsonSerializable(typeof(AgentDesktopItemSummary[]), TypeInfoPropertyName = "DesktopItems")]
[JsonSerializable(typeof(AgentDesktopScanResult))]
[JsonSerializable(typeof(AgentShellSystemEntryResult))]
[JsonSerializable(typeof(AgentWidgetItemsResult))]
[JsonSerializable(typeof(AgentWidgetItemSummary[]), TypeInfoPropertyName = "WidgetItems")]
[JsonSerializable(typeof(AgentWidgetMutationResult))]
[JsonSerializable(typeof(AgentMoveWidgetItemsResult))]
[JsonSerializable(typeof(AgentDuplicateGroup[]), TypeInfoPropertyName = "DuplicateGroups")]
[JsonSerializable(typeof(AgentDeduplicatePreview))]
[JsonSerializable(typeof(AgentDeduplicateApplyResult))]
[JsonSerializable(typeof(AgentWidgetLayoutResult))]
[JsonSerializable(typeof(AgentWidgetLayoutPreview))]
[JsonSerializable(typeof(AgentWidgetLayoutApplyResult))]
[JsonSerializable(typeof(AgentTodoMutationResult))]
[JsonSerializable(typeof(AgentTodoBatchResult))]
internal partial class AgentJsonContext : JsonSerializerContext
{
}
