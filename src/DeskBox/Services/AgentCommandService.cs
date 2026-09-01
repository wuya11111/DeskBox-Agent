using System.Globalization;
using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.ViewModels;
using DeskBox.Views;

namespace DeskBox.Services;

/// <summary>
/// Executes the small, explicit command surface exposed to local AI clients.
/// Mutating file operations are always split into preview and confirmed apply.
/// </summary>
public sealed class AgentCommandService : IDisposable
{
    private static readonly AgentCapability[] Capabilities =
    [
        new("ping", false, false, "Check whether DeskBox is ready."),
        new("get_app_status", false, false, "Read application and widget counts."),
        new("list_widgets", false, false, "List configured widgets."),
        new("list_desktop_items", false, false, "List top-level desktop items. Folder items can be passed to preview_organize_desktop_to_widget."),
        new("scan_public_desktop", false, false, "Scan top-level items from the Windows Public Desktop."),
        new("list_todos", false, false, "List Todo items."),
        new("create_todo", true, false, "Create a Todo item."),
        new("update_todo", true, false, "Update a Todo title, importance, or due date."),
        new("delete_todo", true, false, "Delete a Todo item."),
        new("restore_todo", true, false, "Restore a completed Todo item."),
        new("set_todo_importance", true, false, "Set Todo importance."),
        new("set_todo_due_date", true, false, "Set or clear a Todo due date."),
        new("reorder_todo", true, false, "Reorder a Todo item."),
        new("complete_todo", true, false, "Complete a Todo item."),
        new("list_operation_history", false, false, "List recent file organization operations."),
        new("preview_undo_operation", false, false, "Preview the latest or selected undoable operation."),
        new("undo_last_operation", true, true, "Undo the latest or selected operation."),
        new("preview_organize_desktop", false, false, "Preview desktop organization without moving files."),
        new("preview_custom_organize_desktop", false, false, "Preview an AI-defined desktop classification and the new widgets it would create."),
        new("preview_organize_desktop_to_widget", false, false, "Preview moving selected user or Public Desktop files and folders into an existing File widget selected by widgetId."),
        new("preview_organize_desktop_to_widgets", false, false, "Preview one atomic organization operation targeting multiple existing File widgets."),
        new("ensure_shell_system_entry", true, true, "Create or update one idempotent Windows Shell system entry in a File widget."),
        new("create_shell_system_entry", true, true, "Create or update a Windows Shell system entry in an existing File widget and optionally hide the original desktop icon."),
        new("set_shell_system_icon_visibility", true, true, "Show or hide a Windows Shell system desktop icon."),
        new("list_widget_items", false, false, "List the files, folders, and shortcuts currently contained in a File widget."),
        new("move_widget_items", true, true, "Move items from one File widget to another."),
        new("rename_widget_item", true, true, "Rename an item inside a File widget."),
        new("remove_widget_items", true, true, "Remove items from a File widget, using the Recycle Bin by default."),
        new("preview_deduplicate_widgets", false, false, "Preview duplicate shortcuts grouped by Shell target and arguments."),
        new("apply_deduplicate_plan", true, true, "Apply a duplicate-shortcut cleanup plan with recoverable quarantine."),
        new("get_widget_layout", false, false, "Read widget positions, sizes, collapsed state, and lock state."),
        new("preview_widget_layout", false, false, "Preview widget layout changes including alignment and equal spacing."),
        new("apply_widget_layout", true, true, "Apply a previously previewed widget layout."),
        new("apply_organize_plan", true, true, "Apply a previously previewed desktop organization plan."),
        new("undo_operation", true, true, "Undo a completed desktop organization operation.")
    ];
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;
    private readonly WidgetManager _widgetManager;
    private readonly LocalizationService _localizationService;
    private readonly DesktopOrganizationCoordinator _organizationCoordinator;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Dictionary<string, DesktopOrganizationPlan> _pendingPlans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentDedupPlan> _pendingDedupPlans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentLayoutPlan> _pendingLayoutPlans = new(StringComparer.Ordinal);
    private sealed record AgentDedupPlan(string Id, IReadOnlyList<AgentDuplicateGroup> Groups);
    private sealed record AgentLayoutPlan(string Id, IReadOnlyList<AgentWidgetLayoutEntry> Changes);
    private bool _disposed;

    public AgentCommandService(
        SettingsService settingsService,
        FileService fileService,
        OrganizerService organizerService,
        WidgetManager widgetManager,
        LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _organizerService = organizerService;
        _widgetManager = widgetManager;
        _localizationService = localizationService;
        _organizationCoordinator = new DesktopOrganizationCoordinator(
            settingsService,
            fileService,
            widgetManager,
            organizerService,
            localizationService);
    }

    public async Task<AgentResponse> ExecuteAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_disposed)
        {
            return Failure(request.Id, "service_unavailable", "The DeskBox agent service is shutting down.");
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            // WidgetManager and Todo view models own UI state. Keep the command
            // boundary serialized on the WinUI dispatcher, while file scans
            // and persistence remain asynchronous inside the called services.
            return await RunOnUiThreadAsync(() => ExecuteCoreAsync(request, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return Failure(request.Id, "cancelled", "The command was cancelled.");
        }
        catch (Exception ex)
        {
            App.Log($"[Agent] Command failed method={request.Method}: {ex}");
            return Failure(request.Id, "internal_error", ex.Message);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _commandGate.Dispose();
    }

    private async Task<AgentResponse> ExecuteCoreAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string method = request.Method.Trim().ToLowerInvariant();
        return method switch
        {
            "ping" => Success(request.Id, new AgentPingResult("pong")),
            "get_capabilities" => Success(request.Id, Capabilities),
            "get_app_status" => Success(request.Id, BuildStatus()),
            "list_widgets" => Success(request.Id, BuildWidgets()),
            "list_desktop_items" => await ListDesktopItemsAsync(request, cancellationToken),
            "scan_public_desktop" => await ScanPublicDesktopAsync(request, cancellationToken),
            "list_todos" => await ListTodosAsync(request, cancellationToken),
            "create_todo" => await CreateTodoAsync(request, cancellationToken),
            "update_todo" => await UpdateTodoAsync(request, cancellationToken),
            "delete_todo" => await DeleteTodoAsync(request, cancellationToken),
            "restore_todo" => await SetTodoCompletedAsync(request, cancellationToken, false),
            "set_todo_importance" => await SetTodoImportanceAsync(request, cancellationToken),
            "set_todo_due_date" => await SetTodoDueDateAsync(request, cancellationToken),
            "reorder_todo" => await ReorderTodoAsync(request, cancellationToken),
            "complete_todo" => await CompleteTodoAsync(request, cancellationToken),
            "list_operation_history" => ListOperationHistory(request),
            "preview_undo_operation" => PreviewUndoOperation(request),
            "undo_last_operation" => await UndoLastOperationAsync(request, cancellationToken),
            "preview_organize_desktop" => await PreviewOrganizationAsync(request, cancellationToken),
            "preview_custom_organize_desktop" => await PreviewCustomOrganizationAsync(request, cancellationToken),
            "preview_organize_desktop_to_widget" => await PreviewOrganizationToWidgetAsync(request, cancellationToken),
            "preview_organize_desktop_to_widgets" => await PreviewOrganizationToWidgetsAsync(request, cancellationToken),
            "ensure_shell_system_entry" => await CreateShellSystemEntryAsync(request, cancellationToken),
            "create_shell_system_entry" => await CreateShellSystemEntryAsync(request, cancellationToken),
            "set_shell_system_icon_visibility" => SetShellSystemIconVisibility(request),
            "list_widget_items" => await ListWidgetItemsAsync(request, cancellationToken),
            "move_widget_items" => await MoveWidgetItemsAsync(request, cancellationToken),
            "rename_widget_item" => await RenameWidgetItemAsync(request, cancellationToken),
            "remove_widget_items" => await RemoveWidgetItemsAsync(request, cancellationToken),
            "preview_deduplicate_widgets" => await PreviewDeduplicateWidgetsAsync(request, cancellationToken),
            "apply_deduplicate_plan" => await ApplyDeduplicatePlanAsync(request, cancellationToken),
            "get_widget_layout" => GetWidgetLayout(request),
            "preview_widget_layout" => PreviewWidgetLayout(request),
            "apply_widget_layout" => await ApplyWidgetLayoutAsync(request, cancellationToken),
            "apply_organize_plan" => await ApplyOrganizationAsync(request, cancellationToken),
            "undo_operation" => await UndoOperationAsync(request, cancellationToken),
            _ => Failure(request.Id, "unknown_method", $"Unknown agent method '{request.Method}'.")
        };
    }

    private AgentAppStatus BuildStatus()
    {
        AppSettings settings = _settingsService.Settings;
        return new AgentAppStatus(
            Version: typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
            ProcessId: Environment.ProcessId,
            PipeName: DeskBoxDataPathService.Current.AgentPipeName,
            WidgetCount: settings.Widgets.Count,
            VisibleWidgetCount: _widgetManager.VisibleWidgetCountForAgent,
            TodoWidgetCount: settings.Widgets.Count(widget =>
                widget.WidgetKind == WidgetKind.Todo &&
                !settings.DeletedWidgetIds.Contains(widget.Id)),
            IsReady: true);
    }

    private AgentWidgetSummary[] BuildWidgets()
    {
        return _settingsService.Settings.Widgets
            .Where(widget => !_settingsService.Settings.DeletedWidgetIds.Contains(widget.Id))
            .Select(widget => new AgentWidgetSummary(
                widget.Id,
                widget.Name,
                widget.WidgetKind.ToString(),
                widget.IsVisible,
                widget.IsDisabled,
                widget.MappedFolderPath))
            .ToArray();
    }

    private async Task<AgentResponse> ListWidgetItemsAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? widgetId = RequiredString(request.Parameters, "widgetId");
        if (widgetId is null)
        {
            return Failure(request.Id, "invalid_argument", "'widgetId' is required.");
        }

        try
        {
            WidgetConfig widget = GetActiveFileWidget(widgetId);
            string folderPath = Path.GetFullPath(widget.MappedFolderPath!);
            if (!Directory.Exists(folderPath))
            {
                return Success(request.Id, new AgentWidgetItemsResult(widget.Id, widget.Name, folderPath, []));
            }

            var items = new List<AgentWidgetItemSummary>();
            var options = new EnumerationOptions { IgnoreInaccessible = true };
            foreach (string path in Directory.EnumerateFileSystemEntries(folderPath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WidgetItem item = await _fileService.CreateWidgetItemAsync(
                    path,
                    loadIcon: false,
                    loadFolderItemCount: false,
                    loadShortcutTarget: false);
                ShortcutInfo? shortcut = item.IsShortcut ? ShortcutHelper.Resolve(path) : null;
                items.Add(new AgentWidgetItemSummary(
                    widget.Id,
                    item.Name,
                    Path.GetFullPath(path),
                    item.IsFolder ? "folder" : item.IsShortcut ? "shortcut" : "file",
                    item.IsShortcut,
                    shortcut?.TargetPath,
                    shortcut?.Arguments,
                    item.FileSize,
                    item.LastModified));
            }

            return Success(request.Id, new AgentWidgetItemsResult(widget.Id, widget.Name, folderPath, items.ToArray()));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> MoveWidgetItemsAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? sourceWidgetId = RequiredString(request.Parameters, "sourceWidgetId");
        string? targetWidgetId = RequiredString(request.Parameters, "targetWidgetId");
        if (sourceWidgetId is null || targetWidgetId is null ||
            !TryGetStringArray(request.Parameters, "itemPaths", out string[] itemPaths))
        {
            return Failure(request.Id, "invalid_argument", "'sourceWidgetId', 'targetWidgetId', and a non-empty 'itemPaths' array are required.");
        }

        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false))
        {
            return Failure(request.Id, "confirmation_required", "Pass confirm=true to move items between widgets.");
        }

        try
        {
            WidgetConfig source = GetActiveFileWidget(sourceWidgetId);
            WidgetConfig target = GetActiveFileWidget(targetWidgetId);
            if (string.Equals(source.Id, target.Id, StringComparison.Ordinal))
            {
                return Failure(request.Id, "invalid_argument", "Source and target widgets must be different.");
            }

            string sourceRoot = Path.GetFullPath(source.MappedFolderPath!);
            string targetRoot = Path.GetFullPath(target.MappedFolderPath!);
            if (FileService.PathsOverlap(sourceRoot, targetRoot))
            {
                return Failure(request.Id, "invalid_argument", "Source and target widget folders overlap.");
            }

            string[] normalized = itemPaths
                .Select(path => ValidateWidgetItemPath(sourceRoot, path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            OrganizationHistoryEntry history = await _organizerService.OrganizeDropAsync(
                target,
                target.Name,
                normalized,
                move: true,
                cancellationToken: cancellationToken);
            await _widgetManager.RefreshFileWidgetAsync(source.Id);
            await _widgetManager.RefreshFileWidgetAsync(target.Id);
            return Success(request.Id, new AgentMoveWidgetItemsResult(
                history.Id,
                source.Id,
                target.Id,
                history.Items.Count,
                history.Items.Select(item => item.DestinationPath).ToArray()));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> RenameWidgetItemAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? widgetId = RequiredString(request.Parameters, "widgetId");
        string? itemPath = RequiredString(request.Parameters, "itemPath");
        string? newName = RequiredString(request.Parameters, "newName");
        if (widgetId is null || itemPath is null || newName is null)
        {
            return Failure(request.Id, "invalid_argument", "'widgetId', 'itemPath', and 'newName' are required.");
        }

        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false))
        {
            return Failure(request.Id, "confirmation_required", "Pass confirm=true to rename a widget item.");
        }

        try
        {
            WidgetConfig widget = GetActiveFileWidget(widgetId);
            string sourceRoot = Path.GetFullPath(widget.MappedFolderPath!);
            string sourcePath = ValidateWidgetItemPath(sourceRoot, itemPath);
            string safeName = FileService.SanitizeFileSystemName(newName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                return Failure(request.Id, "invalid_argument", "'newName' must contain a valid file-system name.");
            }

            string extension = Directory.Exists(sourcePath) ? string.Empty : Path.GetExtension(sourcePath);
            string destinationPath = Path.Combine(Path.GetDirectoryName(sourcePath)!,
                Directory.Exists(sourcePath) ? safeName : Path.GetFileNameWithoutExtension(safeName) + extension);
            if ((File.Exists(destinationPath) || Directory.Exists(destinationPath)) &&
                !FileService.IsCaseOnlyPathChange(sourcePath, destinationPath))
            {
                return Failure(request.Id, "invalid_argument", "The destination name already exists.");
            }

            await _fileService.RenameEntryAsync(sourcePath, destinationPath);
            OrganizationHistoryEntry history = await _organizerService.RecordAgentHistoryAsync(
                widget.Id,
                widget.Name,
                OrganizationActionType.ManagedDrop,
                move: true,
                [new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(destinationPath),
                    SourcePath = sourcePath,
                    DestinationPath = destinationPath,
                    TargetWidgetId = widget.Id,
                    TargetWidgetName = widget.Name
                }],
                [new OrganizationHistoryTarget { WidgetId = widget.Id, WidgetName = widget.Name, DirectoryPath = sourceRoot }]);
            await _widgetManager.RefreshFileWidgetAsync(widget.Id);
            cancellationToken.ThrowIfCancellationRequested();
            return Success(request.Id, new AgentWidgetMutationResult(history.Id, 1, [destinationPath]));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> RemoveWidgetItemsAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? widgetId = RequiredString(request.Parameters, "widgetId");
        if (widgetId is null || !TryGetStringArray(request.Parameters, "itemPaths", out string[] itemPaths))
        {
            return Failure(request.Id, "invalid_argument", "'widgetId' and a non-empty 'itemPaths' array are required.");
        }

        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false))
        {
            return Failure(request.Id, "confirmation_required", "Pass confirm=true to remove items. Items go to the Recycle Bin by default.");
        }

        try
        {
            WidgetConfig widget = GetActiveFileWidget(widgetId);
            string root = Path.GetFullPath(widget.MappedFolderPath!);
            string[] normalized = itemPaths.Select(path => ValidateWidgetItemPath(root, path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool recycle = OptionalBoolean(request.Parameters, "recycle") ?? true;
            var removed = new List<string>();
            foreach (string path in normalized)
            {
                if (await _fileService.DeleteEntryAsync(path, recycle))
                {
                    removed.Add(path);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            await _widgetManager.RefreshFileWidgetAsync(widget.Id);
            return Success(request.Id, new AgentWidgetMutationResult(
                Guid.NewGuid().ToString("N"), removed.Count, removed.ToArray()));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> PreviewDeduplicateWidgetsAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string keepRule = (OptionalString(request.Parameters, "keepRule") ?? "first").Trim().ToLowerInvariant();
        if (keepRule is not ("first" or "oldest" or "newest" or "shortest_path"))
        {
            return Failure(request.Id, "invalid_argument", "'keepRule' must be first, oldest, newest, or shortest_path.");
        }

        HashSet<string>? requestedIds = TryGetOptionalStringArray(request.Parameters, "widgetIds");
        var candidates = new List<(string WidgetId, string Path, string Key, DateTime Created)>();
        foreach (WidgetConfig widget in GetActiveFileWidgets())
        {
            if (requestedIds is not null && !requestedIds.Contains(widget.Id))
            {
                continue;
            }

            string? root = widget.MappedFolderPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ShortcutInfo? shortcut = ShortcutHelper.Resolve(path);
                if (shortcut is null || string.IsNullOrWhiteSpace(shortcut.TargetPath))
                {
                    continue;
                }

                string key = $"{shortcut.TargetPath.Trim().ToUpperInvariant()}\u001f{shortcut.Arguments.Trim()}";
                candidates.Add((widget.Id, Path.GetFullPath(path), key, File.GetCreationTimeUtc(path)));
            }
        }

        var groups = new List<AgentDuplicateGroup>();
        foreach (IGrouping<string, (string WidgetId, string Path, string Key, DateTime Created)> group in candidates.GroupBy(item => item.Key, StringComparer.Ordinal))
        {
            if (group.Count() < 2)
            {
                continue;
            }

            IEnumerable<(string WidgetId, string Path, string Key, DateTime Created)> ordered = keepRule switch
            {
                "oldest" => group.OrderBy(item => item.Created),
                "newest" => group.OrderByDescending(item => item.Created),
                "shortest_path" => group.OrderBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
                _ => group.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            };
            var selected = ordered.ToList();
            groups.Add(new AgentDuplicateGroup(group.Key, selected[0].Path, selected.Skip(1).Select(item => item.Path).ToArray()));
        }

        string planId = Guid.NewGuid().ToString("N");
        while (_pendingDedupPlans.Count >= 8)
        {
            _pendingDedupPlans.Remove(_pendingDedupPlans.Keys.First());
        }
        _pendingDedupPlans[planId] = new AgentDedupPlan(planId, groups);
        return Success(request.Id, new AgentDeduplicatePreview(
            planId,
            groups.Sum(group => group.DuplicatePaths.Length),
            groups.ToArray()));
    }

    private async Task<AgentResponse> ApplyDeduplicatePlanAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? planId = RequiredString(request.Parameters, "planId");
        if (planId is null)
        {
            return Failure(request.Id, "invalid_argument", "'planId' is required.");
        }
        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false))
        {
            return Failure(request.Id, "confirmation_required", "Pass confirm=true to apply the deduplication plan.");
        }
        if (!_pendingDedupPlans.Remove(planId, out AgentDedupPlan? plan))
        {
            return Failure(request.Id, "plan_not_found", "The deduplication plan is no longer available.");
        }

        string quarantineRoot = Path.Combine(DeskBoxDataPathService.Current.RecoveryDirectory, "Dedup", plan.Id);
        Directory.CreateDirectory(quarantineRoot);
        var transferPlans = new List<FileService.FileTransferPlan>();
        var sourceWidgets = new HashSet<string>(StringComparer.Ordinal);
        foreach (AgentDuplicateGroup group in plan.Groups)
        {
            foreach (string sourcePath in group.DuplicatePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(sourcePath))
                {
                    continue;
                }
                string destinationPath = FileService.GetAvailablePath(
                    Path.Combine(quarantineRoot, Path.GetFileName(sourcePath)));
                transferPlans.Add(new FileService.FileTransferPlan(sourcePath, destinationPath));
                WidgetConfig? owner = GetActiveFileWidgets().FirstOrDefault(widget =>
                    !string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(sourcePath)), Path.GetFullPath(widget.MappedFolderPath!), StringComparison.OrdinalIgnoreCase));
                if (owner is not null)
                {
                    sourceWidgets.Add(owner.Id);
                }
            }
        }

        if (transferPlans.Count == 0)
        {
            return Success(request.Id, new AgentDeduplicateApplyResult(string.Empty, 0, []));
        }

        IReadOnlyList<FileService.FileTransferResult> results = await _fileService.ExecuteTransferPlanAsync(
            transferPlans,
            move: true,
            cancellationToken: cancellationToken);
        var historyItems = results.Select(result => new OrganizationHistoryItem
        {
            Name = Path.GetFileName(result.SourcePath),
            SourcePath = result.SourcePath,
            DestinationPath = result.DestinationPath,
            TargetWidgetId = string.Empty,
            TargetWidgetName = "Deduplication quarantine"
        }).ToList();
        var targets = sourceWidgets.Select(id =>
        {
            WidgetConfig widget = GetActiveFileWidget(id);
            return new OrganizationHistoryTarget
            {
                WidgetId = id,
                WidgetName = widget.Name,
                DirectoryPath = widget.MappedFolderPath ?? string.Empty
            };
        }).ToList();
        OrganizationHistoryEntry history = await _organizerService.RecordAgentHistoryAsync(
            sourceWidgets.FirstOrDefault() ?? string.Empty,
            "Deduplication",
            OrganizationActionType.DesktopOrganization,
            move: true,
            historyItems,
            targets);
        foreach (string widgetId in sourceWidgets)
        {
            await _widgetManager.RefreshFileWidgetAsync(widgetId);
        }
        return Success(request.Id, new AgentDeduplicateApplyResult(
            history.Id,
            results.Count,
            results.Select(result => result.DestinationPath).ToArray()));
    }

    private AgentResponse GetWidgetLayout(AgentRequest request)
    {
        return Success(request.Id, new AgentWidgetLayoutResult(GetLayoutEntries().ToArray()));
    }

    private AgentResponse PreviewWidgetLayout(AgentRequest request)
    {
        try
        {
            var entries = GetLayoutEntries().ToDictionary(item => item.WidgetId, StringComparer.Ordinal);
            bool hasOperation = false;
            if (TryGetProperty(request.Parameters, "updates", out JsonElement updates) && updates.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement update in updates.EnumerateArray())
                {
                    string? id = RequiredString(update, "widgetId");
                    if (id is null || !entries.TryGetValue(id, out AgentWidgetLayoutEntry? current))
                    {
                        return Failure(request.Id, "invalid_argument", "Each layout update requires a valid 'widgetId'.");
                    }
                    entries[id] = ApplyLayoutUpdate(current, update);
                    hasOperation = true;
                }
            }

            string[] selectedIds = TryGetOptionalStringArray(request.Parameters, "widgetIds")?.ToArray()
                ?? (entries.Keys.ToArray());
            var selected = selectedIds.Where(entries.ContainsKey).Select(id => entries[id]).ToList();
            string? alignment = OptionalString(request.Parameters, "alignment")?.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(alignment))
            {
                if (selected.Count == 0 || alignment is not ("left" or "right" or "top" or "bottom" or "center_horizontal" or "center_vertical"))
                {
                    return Failure(request.Id, "invalid_argument", "Unsupported alignment or no matching widgets.");
                }
                hasOperation = true;
                ApplyAlignment(entries, selected, alignment);
            }

            if (TryGetProperty(request.Parameters, "spacing", out JsonElement spacingElement))
            {
                if (!spacingElement.TryGetDouble(out double spacing) || spacing < 0 || spacing > 10000 || selected.Count == 0)
                {
                    return Failure(request.Id, "invalid_argument", "'spacing' must be a number between 0 and 10000.");
                }
                hasOperation = true;
                ApplySpacing(entries, selected, spacing);
            }

            bool? lockPosition = OptionalBoolean(request.Parameters, "lockPosition");
            bool? lockSize = OptionalBoolean(request.Parameters, "lockSize");
            foreach (string id in selectedIds)
            {
                if (!entries.TryGetValue(id, out AgentWidgetLayoutEntry? entry)) continue;
                if (lockPosition.HasValue) { entries[id] = entry with { IsPositionLocked = lockPosition.Value }; hasOperation = true; }
                if (lockSize.HasValue) { entries[id] = entries[id] with { IsSizeLocked = lockSize.Value }; hasOperation = true; }
            }

            if (!hasOperation)
            {
                return Failure(request.Id, "invalid_argument", "Provide updates, alignment, spacing, lockPosition, or lockSize.");
            }

            string planId = Guid.NewGuid().ToString("N");
            AgentWidgetLayoutEntry[] changes = entries.Values.ToArray();
            while (_pendingLayoutPlans.Count >= 8) _pendingLayoutPlans.Remove(_pendingLayoutPlans.Keys.First());
            _pendingLayoutPlans[planId] = new AgentLayoutPlan(planId, changes);
            return Success(request.Id, new AgentWidgetLayoutPreview(planId, changes));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> ApplyWidgetLayoutAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        string? planId = RequiredString(request.Parameters, "planId");
        if (planId is null) return Failure(request.Id, "invalid_argument", "'planId' is required.");
        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false)) return Failure(request.Id, "confirmation_required", "Pass confirm=true to apply the widget layout.");
        if (!_pendingLayoutPlans.Remove(planId, out AgentLayoutPlan? plan)) return Failure(request.Id, "plan_not_found", "The widget layout plan is no longer available.");

        var configs = new List<WidgetConfig>();
        foreach (AgentWidgetLayoutEntry entry in plan.Changes)
        {
            WidgetConfig? config = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
                string.Equals(widget.Id, entry.WidgetId, StringComparison.Ordinal) &&
                !_settingsService.Settings.DeletedWidgetIds.Contains(widget.Id));
            if (config is null) continue;
            config.X = entry.X;
            config.Y = entry.Y;
            config.Width = Math.Max(SettingsService.MinWidgetWidth, entry.Width);
            config.Height = Math.Max(SettingsService.MinWidgetHeight, entry.Height);
            config.IsCollapsed = entry.IsCollapsed;
            config.IsPositionLocked = entry.IsPositionLocked;
            config.IsSizeLocked = entry.IsSizeLocked;
            configs.Add(config);
        }
        await _widgetManager.ApplyAgentWidgetLayoutAsync(configs);
        cancellationToken.ThrowIfCancellationRequested();
        return Success(request.Id, new AgentWidgetLayoutApplyResult(plan.Id, configs.Count));
    }

    private async Task<AgentResponse> ListTodosAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? requestedWidgetId = OptionalString(request.Parameters, "widgetId");
        var summaries = new List<AgentTodoSummary>();
        foreach (WidgetConfig widget in GetTodoWidgets(requestedWidgetId))
        {
            TodoWidgetData data = await new TodoWidgetStore(widget.Id).LoadAsync();
            summaries.AddRange(data.Items.Select(item => ToTodoSummary(widget, item)));
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Success(request.Id, summaries.ToArray());
    }

    private async Task<AgentResponse> CreateTodoAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? title = RequiredString(request.Parameters, "title");
        if (title is null)
        {
            return Failure(request.Id, "invalid_argument", "'title' is required.");
        }

        bool important = OptionalBoolean(request.Parameters, "important") ?? false;
        DateTimeOffset? dueDate = OptionalDate(request.Parameters, "dueDate");
        string? widgetId = OptionalString(request.Parameters, "widgetId");
        (WidgetConfig Config, TodoWidgetViewModel ViewModel)? target =
            await GetTodoViewModelAsync(
                widgetId,
                createIfMissing: true,
                cancellationToken: cancellationToken);
        if (target is null)
        {
            return Failure(request.Id, "todo_unavailable", "No usable Todo widget is available.");
        }

        TodoItemViewModel? item = await target.Value.ViewModel.AddItemAsync(title, important, dueDate);
        if (item is null)
        {
            return Failure(request.Id, "invalid_argument", "The Todo title cannot be empty.");
        }

        return Success(request.Id, ToTodoSummary(target.Value.Config, item.Item));
    }

    private async Task<AgentResponse> CompleteTodoAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
        => await SetTodoCompletedAsync(request, cancellationToken, true);

    private async Task<AgentResponse> SetTodoCompletedAsync(
        AgentRequest request,
        CancellationToken cancellationToken,
        bool completed)
    {
        string? itemId = RequiredString(request.Parameters, "itemId");
        if (itemId is null)
        {
            return Failure(request.Id, "invalid_argument", "'itemId' is required.");
        }

        string? widgetId = OptionalString(request.Parameters, "widgetId");
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            widgetId = await FindTodoWidgetIdAsync(itemId, cancellationToken);
        }

        (WidgetConfig Config, TodoWidgetViewModel ViewModel)? target =
            await GetTodoViewModelAsync(
                widgetId,
                createIfMissing: false,
                cancellationToken: cancellationToken);
        if (target is null)
        {
            return Failure(request.Id, "todo_not_found", "The Todo widget or item was not found.");
        }

        bool updated = await target.Value.ViewModel.SetCompletedAsync(itemId, completed);
        return updated
            ? Success(request.Id, ToTodoSummary(
                target.Value.Config,
                target.Value.ViewModel.Items.First(item => item.Id == itemId).Item))
            : Failure(request.Id, "todo_not_found", "The Todo item was not found.");
    }

    private async Task<AgentResponse> UpdateTodoAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        string? itemId = RequiredString(request.Parameters, "itemId");
        if (itemId is null) return Failure(request.Id, "invalid_argument", "'itemId' is required.");
        string? widgetId = OptionalString(request.Parameters, "widgetId") ?? await FindTodoWidgetIdAsync(itemId, cancellationToken);
        var target = await GetTodoViewModelAsync(widgetId, false, cancellationToken);
        if (target is null) return Failure(request.Id, "todo_not_found", "The Todo widget or item was not found.");
        bool changed = false;
        if (TryGetProperty(request.Parameters, "title", out _))
        {
            string? title = RequiredString(request.Parameters, "title");
            if (title is null || !await target.Value.ViewModel.UpdateItemTextAsync(itemId, title)) return Failure(request.Id, "invalid_argument", "'title' must be non-empty.");
            changed = true;
        }
        if (OptionalBoolean(request.Parameters, "important") is bool important)
        {
            if (!await target.Value.ViewModel.SetImportantAsync(itemId, important)) return Failure(request.Id, "todo_not_found", "The Todo item was not found.");
            changed = true;
        }
        if (TryGetProperty(request.Parameters, "dueDate", out _))
        {
            DateTimeOffset? due = ParseNullableDate(request.Parameters, "dueDate", out bool valid);
            if (!valid || !await target.Value.ViewModel.SetDueDateAsync(itemId, due)) return Failure(request.Id, "invalid_argument", "'dueDate' must be an ISO date-time string or null.");
            changed = true;
        }
        if (!changed) return Failure(request.Id, "invalid_argument", "Provide title, important, or dueDate.");
        TodoItemViewModel item = target.Value.ViewModel.Items.First(entry => entry.Id == itemId);
        return Success(request.Id, new AgentTodoMutationResult(target.Value.Config.Id, itemId, "updated", ToTodoSummary(target.Value.Config, item.Item)));
    }

    private async Task<AgentResponse> DeleteTodoAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        string? itemId = RequiredString(request.Parameters, "itemId");
        if (itemId is null) return Failure(request.Id, "invalid_argument", "'itemId' is required.");
        string? widgetId = OptionalString(request.Parameters, "widgetId") ?? await FindTodoWidgetIdAsync(itemId, cancellationToken);
        var target = await GetTodoViewModelAsync(widgetId, false, cancellationToken);
        if (target is null || !await target.Value.ViewModel.DeleteItemAsync(itemId)) return Failure(request.Id, "todo_not_found", "The Todo item was not found.");
        return Success(request.Id, new AgentTodoBatchResult(target.Value.Config.Id, "deleted", 1, [itemId]));
    }

    private async Task<AgentResponse> SetTodoImportanceAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        string? itemId = RequiredString(request.Parameters, "itemId");
        bool? important = OptionalBoolean(request.Parameters, "important");
        if (itemId is null || !important.HasValue) return Failure(request.Id, "invalid_argument", "'itemId' and boolean 'important' are required.");
        string? widgetId = OptionalString(request.Parameters, "widgetId") ?? await FindTodoWidgetIdAsync(itemId, cancellationToken);
        var target = await GetTodoViewModelAsync(widgetId, false, cancellationToken);
        if (target is null || !await target.Value.ViewModel.SetImportantAsync(itemId, important.Value)) return Failure(request.Id, "todo_not_found", "The Todo item was not found.");
        TodoItemViewModel item = target.Value.ViewModel.Items.First(entry => entry.Id == itemId);
        return Success(request.Id, new AgentTodoMutationResult(target.Value.Config.Id, itemId, "importance_changed", ToTodoSummary(target.Value.Config, item.Item)));
    }

    private async Task<AgentResponse> SetTodoDueDateAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        string? itemId = RequiredString(request.Parameters, "itemId");
        if (itemId is null || !TryGetProperty(request.Parameters, "dueDate", out _)) return Failure(request.Id, "invalid_argument", "'itemId' and 'dueDate' are required.");
        DateTimeOffset? due = ParseNullableDate(request.Parameters, "dueDate", out bool valid);
        if (!valid) return Failure(request.Id, "invalid_argument", "'dueDate' must be an ISO date-time string or null.");
        string? widgetId = OptionalString(request.Parameters, "widgetId") ?? await FindTodoWidgetIdAsync(itemId, cancellationToken);
        var target = await GetTodoViewModelAsync(widgetId, false, cancellationToken);
        if (target is null || !await target.Value.ViewModel.SetDueDateAsync(itemId, due)) return Failure(request.Id, "todo_not_found", "The Todo item was not found.");
        TodoItemViewModel item = target.Value.ViewModel.Items.First(entry => entry.Id == itemId);
        return Success(request.Id, new AgentTodoMutationResult(target.Value.Config.Id, itemId, "due_date_changed", ToTodoSummary(target.Value.Config, item.Item)));
    }

    private async Task<AgentResponse> ReorderTodoAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        string? itemId = RequiredString(request.Parameters, "itemId");
        if (itemId is null || !TryGetProperty(request.Parameters, "index", out JsonElement indexElement) || !indexElement.TryGetInt32(out int index) || index < 0)
            return Failure(request.Id, "invalid_argument", "'itemId' and a non-negative integer 'index' are required.");
        string? widgetId = OptionalString(request.Parameters, "widgetId") ?? await FindTodoWidgetIdAsync(itemId, cancellationToken);
        var target = await GetTodoViewModelAsync(widgetId, false, cancellationToken);
        if (target is null || !await target.Value.ViewModel.ReorderItemAsync(itemId, index)) return Failure(request.Id, "todo_not_found", "The Todo item was not found.");
        TodoItemViewModel item = target.Value.ViewModel.Items.First(entry => entry.Id == itemId);
        return Success(request.Id, new AgentTodoMutationResult(target.Value.Config.Id, itemId, "reordered", ToTodoSummary(target.Value.Config, item.Item)));
    }

    private async Task<AgentResponse> PreviewOrganizationAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        bool includeSlowItems = OptionalBoolean(request.Parameters, "includeSlowItems") ?? false;
        DesktopOrganizationPlan plan = await _organizationCoordinator.BuildPlanAsync(
            includeSlowItems,
            cancellationToken);
        while (_pendingPlans.Count >= 8)
        {
            string oldest = _pendingPlans.Keys.First();
            _pendingPlans.Remove(oldest);
        }

        _pendingPlans[plan.Id] = plan;
        return Success(request.Id, ToPreview(plan));
    }

    private async Task<AgentResponse> ListDesktopItemsAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        bool includeSlowItems = OptionalBoolean(request.Parameters, "includeSlowItems") ?? false;
        DesktopOrganizationScanResult scan = await _organizationCoordinator.ScanDesktopAsync(
            includeSlowItems,
            cancellationToken);
        return Success(request.Id, scan.Items.Select(item => new AgentDesktopItemSummary(
            item.SourcePath,
            item.Name,
            item.Extension,
            item.Size,
            item.CategoryId,
            item.SubtypeId,
            item.IsEligible,
            item.IsEligible ? null : item.ExclusionReason.ToString())).ToArray());
    }

    private async Task<AgentResponse> ScanPublicDesktopAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        bool includeSlowItems = OptionalBoolean(request.Parameters, "includeSlowItems") ?? false;
        DesktopOrganizationScanResult scan = await _organizationCoordinator.ScanPublicDesktopAsync(
            includeSlowItems,
            cancellationToken);
        return Success(request.Id, new AgentDesktopScanResult(
            scan.DesktopPath,
            scan.Items.Select(ToDesktopItemSummary).ToArray()));
    }

    private async Task<AgentResponse> PreviewOrganizationToWidgetAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? widgetId = RequiredString(request.Parameters, "widgetId");
        if (widgetId is null)
        {
            return Failure(request.Id, "invalid_argument", "'widgetId' is required.");
        }

        if (!TryGetStringArray(request.Parameters, "sourcePaths", out string[] sourcePaths))
        {
            return Failure(
                request.Id,
                "invalid_argument",
                "'sourcePaths' must be a non-empty array of desktop file or folder paths.");
        }

        bool includeSlowItems = OptionalBoolean(request.Parameters, "includeSlowItems") ?? false;
        try
        {
            DesktopOrganizationPlan plan = await _organizationCoordinator.BuildPlanForExistingWidgetAsync(
                widgetId,
                sourcePaths,
                includeSlowItems,
                cancellationToken);
            while (_pendingPlans.Count >= 8)
            {
                string oldest = _pendingPlans.Keys.First();
                _pendingPlans.Remove(oldest);
            }

            _pendingPlans[plan.Id] = plan;
            return Success(request.Id, ToPreview(plan));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> PreviewOrganizationToWidgetsAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetProperty(request.Parameters, "mappings", out JsonElement mappings) || mappings.ValueKind != JsonValueKind.Array)
        {
            return Failure(request.Id, "invalid_argument", "'mappings' must be a non-empty array of {widgetId, sourcePaths}.");
        }

        var selections = new List<DesktopOrganizationWidgetSelection>();
        foreach (JsonElement mapping in mappings.EnumerateArray())
        {
            string? widgetId = RequiredString(mapping, "widgetId");
            if (widgetId is null || !TryGetStringArray(mapping, "sourcePaths", out string[] paths))
            {
                return Failure(request.Id, "invalid_argument", "Each mapping requires widgetId and a non-empty sourcePaths array.");
            }
            selections.Add(new DesktopOrganizationWidgetSelection(widgetId, paths));
        }

        try
        {
            bool includeSlowItems = OptionalBoolean(request.Parameters, "includeSlowItems") ?? false;
            DesktopOrganizationPlan plan = await _organizationCoordinator.BuildPlanForExistingWidgetsAsync(selections, includeSlowItems, cancellationToken);
            while (_pendingPlans.Count >= 8) _pendingPlans.Remove(_pendingPlans.Keys.First());
            _pendingPlans[plan.Id] = plan;
            return Success(request.Id, ToPreview(plan));
        }
        catch (ArgumentException ex) { return Failure(request.Id, "invalid_argument", ex.Message); }
        catch (InvalidOperationException ex) { return Failure(request.Id, "invalid_argument", ex.Message); }
    }

    private AgentResponse SetShellSystemIconVisibility(AgentRequest request)
    {
        string? systemId = RequiredString(request.Parameters, "systemId");
        if (systemId is null) return Failure(request.Id, "invalid_argument", "'systemId' is required.");
        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false)) return Failure(request.Id, "confirmation_required", "Pass confirm=true to change desktop icon visibility.");
        bool hidden = OptionalBoolean(request.Parameters, "hidden") ?? true;
        try
        {
            bool applied = _widgetManager.SetShellSystemDesktopIconVisibility(systemId, hidden);
            return Success(request.Id, new AgentShellSystemEntryResult(string.Empty, systemId.ToLowerInvariant(), string.Empty, string.Empty, applied));
        }
        catch (ArgumentException ex) { return Failure(request.Id, "invalid_argument", ex.Message); }
    }

    private async Task<AgentResponse> CreateShellSystemEntryAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? widgetId = RequiredString(request.Parameters, "widgetId");
        if (widgetId is null)
        {
            return Failure(request.Id, "invalid_argument", "'widgetId' is required.");
        }

        string? systemId = RequiredString(request.Parameters, "systemId");
        if (systemId is null)
        {
            return Failure(request.Id, "invalid_argument", "'systemId' is required.");
        }

        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false))
        {
            return Failure(request.Id, "confirmation_required", "Pass confirm=true to create a Shell system entry and change desktop icon visibility.");
        }

        string? displayName = OptionalString(request.Parameters, "displayName");
        bool hideDesktopIcon = OptionalBoolean(request.Parameters, "hideDesktopIcon") ?? false;
        try
        {
            (string shortcutPath, string createdName, string normalizedSystemId, bool hidden) =
                await _widgetManager.CreateShellSystemEntryAsync(
                    widgetId,
                    systemId,
                    displayName,
                    hideDesktopIcon);
            cancellationToken.ThrowIfCancellationRequested();
            return Success(request.Id, new AgentShellSystemEntryResult(
                widgetId,
                normalizedSystemId,
                shortcutPath,
                createdName,
                hidden));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> PreviewCustomOrganizationAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        bool includeSlowItems = OptionalBoolean(request.Parameters, "includeSlowItems") ?? false;
        if (!TryGetProperty(request.Parameters, "groups", out JsonElement groupsElement) ||
            groupsElement.ValueKind != JsonValueKind.Array)
        {
            return Failure(request.Id, "invalid_argument", "'groups' must be an array of {name, sourcePaths} objects.");
        }

        var groups = new List<DesktopOrganizationCustomGroup>();
        foreach (JsonElement groupElement in groupsElement.EnumerateArray())
        {
            if (groupElement.ValueKind != JsonValueKind.Object ||
                !groupElement.TryGetProperty("name", out JsonElement nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                !groupElement.TryGetProperty("sourcePaths", out JsonElement pathsElement) ||
                pathsElement.ValueKind != JsonValueKind.Array)
            {
                return Failure(request.Id, "invalid_argument", "Each group requires a string 'name' and an array 'sourcePaths'.");
            }

            string? name = nameElement.GetString();
            var sourcePaths = new List<string>();
            foreach (JsonElement pathElement in pathsElement.EnumerateArray())
            {
                if (pathElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathElement.GetString()))
                {
                    return Failure(request.Id, "invalid_argument", "Every 'sourcePaths' entry must be a non-empty string.");
                }

                sourcePaths.Add(pathElement.GetString()!);
            }

            groups.Add(new DesktopOrganizationCustomGroup(name ?? string.Empty, sourcePaths));
        }

        try
        {
            DesktopOrganizationPlan plan = await _organizationCoordinator.BuildCustomPlanAsync(
                groups,
                includeSlowItems,
                cancellationToken);
            while (_pendingPlans.Count >= 8)
            {
                string oldest = _pendingPlans.Keys.First();
                _pendingPlans.Remove(oldest);
            }

            _pendingPlans[plan.Id] = plan;
            return Success(request.Id, ToPreview(plan));
        }
        catch (ArgumentException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(request.Id, "invalid_argument", ex.Message);
        }
    }

    private async Task<AgentResponse> ApplyOrganizationAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? planId = RequiredString(request.Parameters, "planId");
        if (planId is null)
        {
            return Failure(request.Id, "invalid_argument", "'planId' is required.");
        }

        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false))
        {
            return Failure(request.Id, "confirmation_required", "Pass confirm=true to apply a previewed plan.");
        }

        if (!_pendingPlans.Remove(planId, out DesktopOrganizationPlan? plan))
        {
            return Failure(request.Id, "plan_not_found", "The organization plan is no longer available.");
        }

        DesktopOrganizationExecutionResult execution = await _organizationCoordinator.ExecuteAsync(
            plan,
            cancellationToken);
        return Success(request.Id, new AgentOrganizationApplyResult(
            execution.History.Id,
            execution.History.Items.Count,
            execution.CreatedWidgets.Count,
            execution.RetainedItems.Count));
    }

    private async Task<AgentResponse> UndoOperationAsync(
        AgentRequest request,
        CancellationToken cancellationToken)
    {
        string? historyId = RequiredString(request.Parameters, "historyId");
        if (historyId is null)
        {
            return Failure(request.Id, "invalid_argument", "'historyId' is required.");
        }

        return await UndoOperationByIdAsync(request, cancellationToken, historyId);
    }

    private async Task<AgentResponse> UndoOperationByIdAsync(
        AgentRequest request,
        CancellationToken cancellationToken,
        string historyId)
    {

        OrganizationHistoryEntry? history = _settingsService.Settings.RecentOrganizationHistory
            .FirstOrDefault(entry => string.Equals(entry.Id, historyId, StringComparison.Ordinal));
        await _organizationCoordinator.UndoAsync(historyId);
        if (history is not null && history.Targets.Count == 0 && !string.IsNullOrWhiteSpace(history.WidgetId))
        {
            await _widgetManager.RefreshFileWidgetAsync(history.WidgetId);
        }
        if (history is not null)
        {
            string recoveryRoot = Path.GetFullPath(DeskBoxDataPathService.Current.RecoveryDirectory);
            foreach (string directory in history.Items.Select(item => Path.GetDirectoryName(item.DestinationPath))
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(path => path!)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (directory.StartsWith(recoveryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); } catch { }
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Success(request.Id, new AgentUndoResult(historyId, true));
    }

    private AgentResponse ListOperationHistory(AgentRequest request)
    {
        int maxCount = 20;
        if (TryGetProperty(request.Parameters, "maxCount", out JsonElement max) && max.TryGetInt32(out int parsed)) maxCount = Math.Clamp(parsed, 1, 100);
        AgentOperationHistorySummary[] entries = _organizerService.GetRecentHistory(maxCount).Select(ToHistorySummary).ToArray();
        return Success(request.Id, new AgentOperationHistoryResult(entries));
    }

    private AgentResponse PreviewUndoOperation(AgentRequest request)
    {
        string? requestedId = OptionalString(request.Parameters, "historyId");
        OrganizationHistoryEntry? entry = string.IsNullOrWhiteSpace(requestedId)
            ? _organizerService.GetLatestUndoableEntry()
            : _settingsService.Settings.RecentOrganizationHistory.FirstOrDefault(item => string.Equals(item.Id, requestedId, StringComparison.Ordinal));
        if (entry is null) return Failure(request.Id, "history_not_found", "No matching operation history entry was found.");
        return Success(request.Id, new AgentUndoPreview(entry.Id, entry.CanUndo && !entry.IsUndone && !entry.IsFailed, entry.WidgetName, entry.ActionType, entry.Items.Count, entry.Items.Select(item => item.Name).ToArray()));
    }

    private async Task<AgentResponse> UndoLastOperationAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        if (!(OptionalBoolean(request.Parameters, "confirm") ?? false)) return Failure(request.Id, "confirmation_required", "Pass confirm=true to undo the latest operation.");
        string? historyId = OptionalString(request.Parameters, "historyId") ?? _organizerService.GetLatestUndoableEntry()?.Id;
        if (string.IsNullOrWhiteSpace(historyId)) return Failure(request.Id, "history_not_found", "No undoable operation is available.");
        return await UndoOperationByIdAsync(request, cancellationToken, historyId);
    }

    private async Task<(WidgetConfig Config, TodoWidgetViewModel ViewModel)?> GetTodoViewModelAsync(
        string? widgetId,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        WidgetConfig? config = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
            widget.WidgetKind == WidgetKind.Todo &&
            !_settingsService.Settings.DeletedWidgetIds.Contains(widget.Id) &&
            (string.IsNullOrWhiteSpace(widgetId) ||
             string.Equals(widget.Id, widgetId, StringComparison.Ordinal)));
        if (config is null && createIfMissing)
        {
            ContentWidgetWindow window = await _widgetManager.CreateTodoWidgetAsync();
            config = window.Config;
        }

        if (config is { IsDisabled: true } && createIfMissing)
        {
            await _widgetManager.SetTodoEnabledAsync(enabled: true, reveal: false);
            config.IsDisabled = false;
            config.IsVisible = true;
        }

        if (config is null || config.IsDisabled)
        {
            return null;
        }

        await _widgetManager.ShowWidgetAsync(config.Id, reveal: false, autoRestoreOnReveal: false);
        if (!_widgetManager.ContentWidgets.TryGetValue(config.Id, out ContentWidgetWindow? contentWindow))
        {
            return null;
        }

        await contentWindow.ContentReadyTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (contentWindow.CurrentContent is not TodoWidgetContentAdapter adapter)
        {
            return null;
        }

        return (config, adapter.ViewModel);
    }

    private async Task<string?> FindTodoWidgetIdAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        foreach (WidgetConfig widget in GetTodoWidgets(null))
        {
            TodoWidgetData data = await new TodoWidgetStore(widget.Id).LoadAsync();
            if (data.Items.Any(item => string.Equals(item.Id, itemId, StringComparison.Ordinal)))
            {
                return widget.Id;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        return null;
    }

    private IEnumerable<WidgetConfig> GetTodoWidgets(string? widgetId)
    {
        return _settingsService.Settings.Widgets.Where(widget =>
            widget.WidgetKind == WidgetKind.Todo &&
            !widget.IsDisabled &&
            !_settingsService.Settings.DeletedWidgetIds.Contains(widget.Id) &&
            (string.IsNullOrWhiteSpace(widgetId) ||
             string.Equals(widget.Id, widgetId, StringComparison.Ordinal)));
    }

    private static AgentTodoSummary ToTodoSummary(WidgetConfig widget, TodoItem item) =>
        new(
            widget.Id,
            widget.Name,
            item.Id,
            item.Text,
            item.IsCompleted,
            item.IsImportant,
            item.DueDate,
            item.ColorMarker,
            item.UpdatedAt);

    private static AgentDesktopPreview ToPreview(DesktopOrganizationPlan plan) =>
        new(
            plan.Id,
            plan.DesktopPath,
            plan.EligibleItemCount,
            plan.TotalTransferSize,
            plan.Targets.Select(target => new AgentDesktopTargetSummary(
                target.TargetWidgetId,
                target.SuggestedDisplayName,
                target.TargetDirectoryPath,
                target.CreatesWidget,
                target.Items.Count,
                target.Items.Sum(item => item.Size),
                target.Items.Select(item => item.Name).ToArray())).ToArray(),
            plan.ExcludedItems.Select(item => item.Name).ToArray());

    private static AgentDesktopItemSummary ToDesktopItemSummary(
        DesktopOrganizationFileSnapshot item) =>
        new(
            item.SourcePath,
            item.Name,
            item.Extension,
            item.Size,
            item.CategoryId,
            item.SubtypeId,
            item.IsEligible,
            item.IsEligible ? null : item.ExclusionReason.ToString());

    private IEnumerable<WidgetConfig> GetActiveFileWidgets() =>
        _settingsService.Settings.Widgets.Where(widget =>
            widget.WidgetKind == WidgetKind.File &&
            !widget.IsDisabled &&
            !_settingsService.Settings.DeletedWidgetIds.Contains(widget.Id) &&
            !string.IsNullOrWhiteSpace(widget.MappedFolderPath));

    private WidgetConfig GetActiveFileWidget(string widgetId)
    {
        WidgetConfig? widget = GetActiveFileWidgets().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, widgetId.Trim(), StringComparison.Ordinal));
        return widget ?? throw new InvalidOperationException(
            "The widgetId must identify an active File widget with a mapped folder.");
    }

    private static string ValidateWidgetItemPath(string root, string path)
    {
        string normalized = Path.GetFullPath(path.Trim());
        if (!string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(normalized)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase) ||
            (!File.Exists(normalized) && !Directory.Exists(normalized)))
        {
            throw new InvalidOperationException($"The item is not a current top-level item in the widget: '{path}'.");
        }
        return normalized;
    }

    private AgentWidgetLayoutEntry[] GetLayoutEntries() => _settingsService.Settings.Widgets
        .Where(widget => !_settingsService.Settings.DeletedWidgetIds.Contains(widget.Id))
        .Select(widget => new AgentWidgetLayoutEntry(
            widget.Id, widget.Name, widget.X, widget.Y, widget.Width, widget.Height,
            widget.IsCollapsed, widget.IsPositionLocked, widget.IsSizeLocked, widget.IsVisible))
        .ToArray();

    private static AgentWidgetLayoutEntry ApplyLayoutUpdate(AgentWidgetLayoutEntry current, JsonElement update)
    {
        double value;
        if (update.TryGetProperty("x", out JsonElement x) && x.TryGetDouble(out value)) current = current with { X = value };
        if (update.TryGetProperty("y", out JsonElement y) && y.TryGetDouble(out value)) current = current with { Y = value };
        if (update.TryGetProperty("width", out JsonElement width) && width.TryGetDouble(out value)) current = current with { Width = value };
        if (update.TryGetProperty("height", out JsonElement height) && height.TryGetDouble(out value)) current = current with { Height = value };
        if (update.TryGetProperty("isCollapsed", out JsonElement collapsed) && (collapsed.ValueKind is JsonValueKind.True or JsonValueKind.False)) current = current with { IsCollapsed = collapsed.GetBoolean() };
        if (update.TryGetProperty("isPositionLocked", out JsonElement positionLocked) && (positionLocked.ValueKind is JsonValueKind.True or JsonValueKind.False)) current = current with { IsPositionLocked = positionLocked.GetBoolean() };
        if (update.TryGetProperty("isSizeLocked", out JsonElement sizeLocked) && (sizeLocked.ValueKind is JsonValueKind.True or JsonValueKind.False)) current = current with { IsSizeLocked = sizeLocked.GetBoolean() };
        if (!double.IsFinite(current.X) || !double.IsFinite(current.Y) || !double.IsFinite(current.Width) || !double.IsFinite(current.Height) || current.Width < SettingsService.MinWidgetWidth || current.Height < SettingsService.MinWidgetHeight)
            throw new ArgumentException("Widget layout values must be finite and meet the minimum widget size.");
        return current;
    }

    private static void ApplyAlignment(Dictionary<string, AgentWidgetLayoutEntry> entries, IReadOnlyList<AgentWidgetLayoutEntry> selected, string alignment)
    {
        double minX = selected.Min(item => item.X), maxRight = selected.Max(item => item.X + item.Width);
        double minY = selected.Min(item => item.Y), maxBottom = selected.Max(item => item.Y + item.Height);
        double centerX = selected.Average(item => item.X + item.Width / 2), centerY = selected.Average(item => item.Y + item.Height / 2);
        foreach (AgentWidgetLayoutEntry item in selected)
        {
            AgentWidgetLayoutEntry updated = alignment switch
            {
                "left" => item with { X = minX },
                "right" => item with { X = maxRight - item.Width },
                "top" => item with { Y = minY },
                "bottom" => item with { Y = maxBottom - item.Height },
                "center_horizontal" => item with { X = centerX - item.Width / 2 },
                _ => item with { Y = centerY - item.Height / 2 }
            };
            entries[item.WidgetId] = updated;
        }
    }

    private static void ApplySpacing(Dictionary<string, AgentWidgetLayoutEntry> entries, IReadOnlyList<AgentWidgetLayoutEntry> selected, double spacing)
    {
        AgentWidgetLayoutEntry[] ordered = selected.OrderBy(item => item.X).ToArray();
        if (ordered.Length < 2) return;
        double nextX = ordered[0].X + ordered[0].Width + spacing;
        for (int index = 1; index < ordered.Length; index++)
        {
            AgentWidgetLayoutEntry updated = ordered[index] with { X = nextX };
            entries[updated.WidgetId] = updated;
            nextX = updated.X + updated.Width + spacing;
        }
    }

    private static string? RequiredString(JsonElement parameters, string name)
    {
        string? value = OptionalString(parameters, name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? OptionalString(JsonElement parameters, string name)
    {
        return TryGetProperty(parameters, name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static HashSet<string>? TryGetOptionalStringArray(JsonElement parameters, string name)
    {
        if (!TryGetProperty(parameters, name, out JsonElement element)) return null;
        if (element.ValueKind != JsonValueKind.Array) throw new ArgumentException($"'{name}' must be an array of strings.");
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new ArgumentException($"Every '{name}' entry must be a non-empty string.");
            values.Add(value.GetString()!.Trim());
        }
        return values;
    }

    private static bool? OptionalBoolean(JsonElement parameters, string name)
    {
        return TryGetProperty(parameters, name, out JsonElement value) &&
               (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;
    }

    private static bool TryGetStringArray(
        JsonElement parameters,
        string name,
        out string[] values)
    {
        values = [];
        if (!TryGetProperty(parameters, name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var result = new List<string>();
        foreach (JsonElement value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                return false;
            }

            result.Add(value.GetString()!.Trim());
        }

        values = result.ToArray();
        return values.Length > 0;
    }

    private static DateTimeOffset? OptionalDate(JsonElement parameters, string name)
    {
        string? value = OptionalString(parameters, name);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? ParseNullableDate(JsonElement parameters, string name, out bool valid)
    {
        valid = false;
        if (!TryGetProperty(parameters, name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.Null) { valid = true; return null; }
        if (value.ValueKind != JsonValueKind.String) return null;
        valid = DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset parsed);
        return valid ? parsed : null;
    }

    private static AgentOperationHistorySummary ToHistorySummary(OrganizationHistoryEntry entry) =>
        new(entry.Id, entry.TimestampUtc, entry.WidgetId, entry.WidgetName, entry.ActionType, entry.TransferMode,
            entry.CanUndo, entry.IsUndone, entry.IsFailed, entry.Items.Count, entry.Items.Select(item => item.Name).ToArray());

    private static bool TryGetProperty(JsonElement parameters, string name, out JsonElement value)
    {
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static AgentResponse Success<T>(string requestId, T result)
    {
        JsonElement json = result switch
        {
            AgentPingResult ping => JsonSerializer.SerializeToElement(ping, AgentJsonContext.Default.AgentPingResult),
            AgentCapability[] capabilities => JsonSerializer.SerializeToElement(capabilities, AgentJsonContext.Default.Capabilities),
            AgentAppStatus status => JsonSerializer.SerializeToElement(status, AgentJsonContext.Default.AgentAppStatus),
            AgentWidgetSummary[] widgets => JsonSerializer.SerializeToElement(widgets, AgentJsonContext.Default.WidgetSummaries),
            AgentDesktopItemSummary[] desktopItems => JsonSerializer.SerializeToElement(desktopItems, AgentJsonContext.Default.DesktopItems),
            AgentDesktopScanResult desktopScan => JsonSerializer.SerializeToElement(desktopScan, AgentJsonContext.Default.AgentDesktopScanResult),
            AgentTodoSummary[] todos => JsonSerializer.SerializeToElement(todos, AgentJsonContext.Default.TodoSummaries),
            AgentDesktopPreview preview => JsonSerializer.SerializeToElement(preview, AgentJsonContext.Default.AgentDesktopPreview),
            AgentOrganizationApplyResult applied => JsonSerializer.SerializeToElement(applied, AgentJsonContext.Default.AgentOrganizationApplyResult),
            AgentUndoResult undone => JsonSerializer.SerializeToElement(undone, AgentJsonContext.Default.AgentUndoResult),
            AgentOperationHistoryResult history => JsonSerializer.SerializeToElement(history, AgentJsonContext.Default.AgentOperationHistoryResult),
            AgentUndoPreview undoPreview => JsonSerializer.SerializeToElement(undoPreview, AgentJsonContext.Default.AgentUndoPreview),
            AgentShellSystemEntryResult shellEntry => JsonSerializer.SerializeToElement(shellEntry, AgentJsonContext.Default.AgentShellSystemEntryResult),
            AgentWidgetItemsResult widgetItems => JsonSerializer.SerializeToElement(widgetItems, AgentJsonContext.Default.AgentWidgetItemsResult),
            AgentWidgetMutationResult mutation => JsonSerializer.SerializeToElement(mutation, AgentJsonContext.Default.AgentWidgetMutationResult),
            AgentMoveWidgetItemsResult movedItems => JsonSerializer.SerializeToElement(movedItems, AgentJsonContext.Default.AgentMoveWidgetItemsResult),
            AgentDeduplicatePreview dedupPreview => JsonSerializer.SerializeToElement(dedupPreview, AgentJsonContext.Default.AgentDeduplicatePreview),
            AgentDeduplicateApplyResult dedupApplied => JsonSerializer.SerializeToElement(dedupApplied, AgentJsonContext.Default.AgentDeduplicateApplyResult),
            AgentWidgetLayoutResult layout => JsonSerializer.SerializeToElement(layout, AgentJsonContext.Default.AgentWidgetLayoutResult),
            AgentWidgetLayoutPreview layoutPreview => JsonSerializer.SerializeToElement(layoutPreview, AgentJsonContext.Default.AgentWidgetLayoutPreview),
            AgentWidgetLayoutApplyResult layoutApplied => JsonSerializer.SerializeToElement(layoutApplied, AgentJsonContext.Default.AgentWidgetLayoutApplyResult),
            AgentTodoMutationResult todoMutation => JsonSerializer.SerializeToElement(todoMutation, AgentJsonContext.Default.AgentTodoMutationResult),
            AgentTodoBatchResult todoBatch => JsonSerializer.SerializeToElement(todoBatch, AgentJsonContext.Default.AgentTodoBatchResult),
            _ => throw new InvalidOperationException($"Unsupported agent result type '{result?.GetType().Name ?? "null"}'.")
        };
        return new AgentResponse(requestId, true, json);
    }

    private static AgentResponse Failure(string requestId, string code, string message) =>
        new(requestId, false, Error: new AgentError(code, message));

    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = App.UiDispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("Unable to dispatch the agent command to the UI thread."));
        }

        return completion.Task;
    }
}
