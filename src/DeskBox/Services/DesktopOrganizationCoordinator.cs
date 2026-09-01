using System.Runtime.InteropServices;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly WidgetManager _widgetManager;
    private readonly OrganizerService _organizerService;
    private readonly LocalizationService _localizationService;
    private readonly DesktopOrganizationScanner _scanner;
    private readonly DesktopOrganizationPlanner _planner;
    private readonly DesktopOrganizationPlacementPlanner _placementPlanner = new();
    private readonly DesktopOrganizationTransaction _transaction;

    public DesktopOrganizationCoordinator(
        SettingsService settingsService,
        FileService fileService,
        WidgetManager widgetManager,
        OrganizerService organizerService,
        LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _widgetManager = widgetManager;
        _organizerService = organizerService;
        _localizationService = localizationService;
        var classifier = new DesktopOrganizationClassifier();
        _scanner = new DesktopOrganizationScanner(classifier);
        _planner = new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver());
        _transaction = new DesktopOrganizationTransaction(settingsService, fileService);
    }

    public async Task<DesktopOrganizationPlan> BuildPlanAsync(
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default)
    {
        DesktopOrganizationScanResult scan =
            await _scanner.ScanAsync(includeSlowItems, cancellationToken);
        string root = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
        DesktopOrganizationPlan plan = _planner.CreatePlan(
            scan,
            root,
            _settingsService.Settings.Widgets,
            _settingsService.Settings.DesktopOrganizationRules,
            ResolveCategoryName);

        AssignNonOverlappingBounds(plan);
        return plan;
    }

    public Task<DesktopOrganizationScanResult> ScanDesktopAsync(
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default) =>
        _scanner.ScanAsync(includeSlowItems, cancellationToken);

    public Task<DesktopOrganizationScanResult> ScanPublicDesktopAsync(
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default) =>
        _scanner.ScanPublicAsync(includeSlowItems, cancellationToken);

    public async Task<DesktopOrganizationPlan> BuildPlanForExistingWidgetAsync(
        string widgetId,
        IReadOnlyCollection<string> sourcePaths,
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new ArgumentException("A widgetId is required.", nameof(widgetId));
        }

        WidgetConfig? widget = _settingsService.Settings.Widgets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, widgetId.Trim(), StringComparison.Ordinal) &&
            candidate.WidgetKind == WidgetKind.File &&
            !candidate.IsDisabled &&
            !_settingsService.Settings.DeletedWidgetIds.Contains(candidate.Id) &&
            !string.IsNullOrWhiteSpace(candidate.MappedFolderPath));
        if (widget is null)
        {
            throw new InvalidOperationException(
                "The widgetId must identify an active File widget with a mapped folder.");
        }

        DesktopOrganizationScanResult userScan = await _scanner.ScanAsync(
            includeSlowItems,
            cancellationToken);
        DesktopOrganizationScanResult publicScan = await _scanner.ScanPublicAsync(
            includeSlowItems,
            cancellationToken);
        DesktopOrganizationFileSnapshot[] scannedItems = userScan.Items
            .Concat(publicScan.Items)
            .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var itemsByPath = scannedItems.ToDictionary(
            item => item.SourcePath,
            StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedItems = new List<DesktopOrganizationFileSnapshot>();
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException(
                    "sourcePaths entries must be non-empty.",
                    nameof(sourcePaths));
            }

            string normalizedPath = Path.GetFullPath(sourcePath.Trim());
            if (!itemsByPath.TryGetValue(normalizedPath, out DesktopOrganizationFileSnapshot? item))
            {
                throw new InvalidOperationException(
                    $"The path is not a current top-level desktop item: '{sourcePath}'.");
            }

            bool selectableFolder = item.IsDirectory &&
                item.ExclusionReason == DesktopOrganizationExclusionReason.Folder;
            if ((!item.IsEligible && !selectableFolder) ||
                item.ExclusionReason is DesktopOrganizationExclusionReason.HiddenOrSystem or
                    DesktopOrganizationExclusionReason.ReparsePoint or
                    DesktopOrganizationExclusionReason.OfflinePlaceholder or
                    DesktopOrganizationExclusionReason.TemporaryOrDownloading or
                    DesktopOrganizationExclusionReason.Unavailable)
            {
                throw new InvalidOperationException(
                    $"The desktop item cannot be organized: '{item.Name}'.");
            }

            if (selectedPaths.Add(normalizedPath))
            {
                selectedItems.Add(selectableFolder
                    ? item with { ExclusionReason = DesktopOrganizationExclusionReason.None }
                    : item);
            }
        }

        if (selectedItems.Count == 0)
        {
            throw new ArgumentException(
                "At least one sourcePaths entry is required.",
                nameof(sourcePaths));
        }

        var targets = new List<DesktopOrganizationTargetPlan>
        {
            new()
            {
                SourceBucketId = $"widget:{widget.Id}",
                CategoryId = DesktopOrganizationCategoryIds.Other,
                TargetWidgetId = widget.Id,
                SuggestedDisplayName = widget.Name,
                TargetDirectoryPath = Path.GetFullPath(widget.MappedFolderPath!),
                CreatesWidget = false,
                Items = selectedItems
            }
        };

        return new DesktopOrganizationPlan
        {
            DesktopPath = userScan.DesktopPath,
            StorageRootPath = SettingsService.NormalizeManagedStorageRootPath(
                _settingsService.Settings.DefaultManagedStorageRootPath),
            Targets = targets,
            ExcludedItems = scannedItems
                .Where(item => !selectedPaths.Contains(item.SourcePath))
                .Select(item => item.IsEligible
                    ? item with { ExclusionReason = DesktopOrganizationExclusionReason.UserChoice }
                    : item)
                .ToList()
        };
    }

    public async Task<DesktopOrganizationPlan> BuildCustomPlanAsync(
        IReadOnlyCollection<DesktopOrganizationCustomGroup> groups,
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default)
    {
        if (groups.Count == 0 || groups.Count > DesktopOrganizationPlanner.MaxNewWidgetCount)
        {
            throw new ArgumentException(
                $"Custom organization must contain between 1 and {DesktopOrganizationPlanner.MaxNewWidgetCount} groups.",
                nameof(groups));
        }

        var normalizedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (DesktopOrganizationCustomGroup group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name) || !normalizedNames.Add(group.Name.Trim()))
            {
                throw new ArgumentException("Custom group names must be non-empty and unique.", nameof(groups));
            }
        }

        DesktopOrganizationScanResult scan = await _scanner.ScanAsync(includeSlowItems, cancellationToken);
        var itemsByPath = scan.Items.ToDictionary(item => item.SourcePath, StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedDirectories = _settingsService.Settings.Widgets
            .Where(widget => !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .Select(widget => Path.GetFullPath(widget.MappedFolderPath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string storageRoot = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
        var targets = new List<DesktopOrganizationTargetPlan>();

        foreach (DesktopOrganizationCustomGroup group in groups)
        {
            var selectedItems = new List<DesktopOrganizationFileSnapshot>();
            foreach (string sourcePath in group.SourcePaths)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    throw new ArgumentException("Custom group paths must be non-empty.", nameof(groups));
                }

                string normalizedPath = Path.GetFullPath(sourcePath.Trim());
                if (!itemsByPath.TryGetValue(normalizedPath, out DesktopOrganizationFileSnapshot? item))
                {
                    throw new InvalidOperationException(
                        $"The path is not a current top-level desktop item: '{sourcePath}'.");
                }

                if (!item.IsEligible || item.IsDirectory)
                {
                    throw new InvalidOperationException(
                        $"The desktop item cannot be organized: '{item.Name}'.");
                }

                if (!selectedPaths.Add(normalizedPath))
                {
                    throw new InvalidOperationException(
                        $"The desktop item was assigned to more than one group: '{item.Name}'.");
                }

                selectedItems.Add(item);
            }

            if (selectedItems.Count == 0)
            {
                throw new ArgumentException(
                    $"Custom group '{group.Name.Trim()}' must contain at least one desktop file.",
                    nameof(groups));
            }

            string directory = FileService.GetAvailablePath(
                Path.Combine(storageRoot, SanitizeCustomFolderName(group.Name)),
                reservedDirectories);
            targets.Add(new DesktopOrganizationTargetPlan
            {
                SourceBucketId = $"custom:{targets.Count}",
                CategoryId = "Custom",
                TargetWidgetId = Guid.NewGuid().ToString("N"),
                SuggestedDisplayName = group.Name.Trim(),
                TargetDirectoryPath = directory,
                CreatesWidget = true,
                Items = selectedItems
            });
        }

        var excluded = scan.Items
            .Where(item => !selectedPaths.Contains(item.SourcePath))
            .Select(item => selectedPaths.Contains(item.SourcePath)
                ? item
                : item with
                {
                    ExclusionReason = item.IsEligible
                        ? DesktopOrganizationExclusionReason.UserChoice
                        : item.ExclusionReason
                })
            .ToList();
        var plan = new DesktopOrganizationPlan
        {
            DesktopPath = scan.DesktopPath,
            StorageRootPath = storageRoot,
            Targets = targets,
            ExcludedItems = excluded
        };
        AssignNonOverlappingBounds(plan);
        return plan;
    }

    public async Task<DesktopOrganizationPlan> BuildPlanForExistingWidgetsAsync(
        IReadOnlyCollection<DesktopOrganizationWidgetSelection> selections,
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default)
    {
        if (selections.Count == 0)
        {
            throw new ArgumentException("At least one widget selection is required.", nameof(selections));
        }

        DesktopOrganizationScanResult userScan = await _scanner.ScanAsync(includeSlowItems, cancellationToken);
        DesktopOrganizationScanResult publicScan = await _scanner.ScanPublicAsync(includeSlowItems, cancellationToken);
        DesktopOrganizationFileSnapshot[] scannedItems = userScan.Items
            .Concat(publicScan.Items)
            .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var itemsByPath = scannedItems.ToDictionary(item => item.SourcePath, StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<DesktopOrganizationTargetPlan>();

        foreach (DesktopOrganizationWidgetSelection selection in selections)
        {
            if (string.IsNullOrWhiteSpace(selection.WidgetId) || selection.SourcePaths.Count == 0)
            {
                throw new ArgumentException("Each selection requires a widgetId and at least one source path.", nameof(selections));
            }

            WidgetConfig? widget = _settingsService.Settings.Widgets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, selection.WidgetId.Trim(), StringComparison.Ordinal) &&
                candidate.WidgetKind == WidgetKind.File && !candidate.IsDisabled &&
                !_settingsService.Settings.DeletedWidgetIds.Contains(candidate.Id) &&
                !string.IsNullOrWhiteSpace(candidate.MappedFolderPath));
            if (widget is null)
            {
                throw new InvalidOperationException($"The widgetId must identify an active File widget with a mapped folder: '{selection.WidgetId}'.");
            }

            var selectedItems = new List<DesktopOrganizationFileSnapshot>();
            foreach (string sourcePath in selection.SourcePaths)
            {
                string normalizedPath = Path.GetFullPath(sourcePath.Trim());
                if (!itemsByPath.TryGetValue(normalizedPath, out DesktopOrganizationFileSnapshot? item))
                {
                    throw new InvalidOperationException($"The path is not a current top-level desktop item: '{sourcePath}'.");
                }

                bool selectableFolder = item.IsDirectory && item.ExclusionReason == DesktopOrganizationExclusionReason.Folder;
                if ((!item.IsEligible && !selectableFolder) || item.ExclusionReason is
                    DesktopOrganizationExclusionReason.HiddenOrSystem or DesktopOrganizationExclusionReason.ReparsePoint or
                    DesktopOrganizationExclusionReason.OfflinePlaceholder or DesktopOrganizationExclusionReason.TemporaryOrDownloading or
                    DesktopOrganizationExclusionReason.Unavailable)
                {
                    throw new InvalidOperationException($"The desktop item cannot be organized: '{item.Name}'.");
                }

                if (!selectedPaths.Add(normalizedPath))
                {
                    throw new InvalidOperationException($"The desktop item was assigned to more than one widget: '{item.Name}'.");
                }

                selectedItems.Add(selectableFolder ? item with { ExclusionReason = DesktopOrganizationExclusionReason.None } : item);
            }

            targets.Add(new DesktopOrganizationTargetPlan
            {
                SourceBucketId = $"widget:{widget.Id}",
                CategoryId = DesktopOrganizationCategoryIds.Other,
                TargetWidgetId = widget.Id,
                SuggestedDisplayName = widget.Name,
                TargetDirectoryPath = Path.GetFullPath(widget.MappedFolderPath!),
                CreatesWidget = false,
                Items = selectedItems
            });
        }

        var plan = new DesktopOrganizationPlan
        {
            DesktopPath = userScan.DesktopPath,
            StorageRootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath),
            Targets = targets,
            ExcludedItems = scannedItems.Where(item => !selectedPaths.Contains(item.SourcePath)).Select(item =>
                item.IsEligible ? item with { ExclusionReason = DesktopOrganizationExclusionReason.UserChoice } : item).ToList()
        };
        AssignNonOverlappingBounds(plan);
        return plan;
    }

    /// <summary>
    /// Compiles the user's preview selections into an immutable execution
    /// plan. The scan plan is never mutated, so changing a combo box cannot
    /// leak into a later refresh or into another execution attempt.
    /// </summary>
    public DesktopOrganizationPlan CreateExecutionPlan(
        DesktopOrganizationPlan previewPlan,
        IReadOnlyCollection<DesktopOrganizationTargetSelection> selections)
    {
        var selectionByBucket = selections
            .Where(selection => !string.IsNullOrWhiteSpace(selection.SourceBucketId))
            .ToDictionary(selection => selection.SourceBucketId, StringComparer.Ordinal);
        var widgetsById = _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .ToDictionary(widget => widget.Id, StringComparer.Ordinal);
        var targetsByDestination = new Dictionary<string, DesktopOrganizationTargetPlan>(StringComparer.Ordinal);
        var retainedByChoice = new List<DesktopOrganizationFileSnapshot>();

        foreach (DesktopOrganizationTargetPlan source in previewPlan.Targets)
        {
            if (selectionByBucket.TryGetValue(source.SourceBucketId, out DesktopOrganizationTargetSelection? selection) &&
                !selection.IsSelected)
            {
                retainedByChoice.AddRange(source.Items.Select(item => item with
                {
                    ExclusionReason = DesktopOrganizationExclusionReason.UserChoice
                }));
                continue;
            }

            DesktopOrganizationTargetPlan target = source;
            bool shouldResolveExistingDestination =
                selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget ||
                !source.CreatesWidget;
            if (shouldResolveExistingDestination)
            {
                string? requestedWidgetId = selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
                    ? selection.ExistingWidgetId
                    : source.TargetWidgetId;
                if (string.IsNullOrWhiteSpace(requestedWidgetId) ||
                    !widgetsById.TryGetValue(requestedWidgetId, out WidgetConfig? widget) ||
                    string.IsNullOrWhiteSpace(widget.MappedFolderPath))
                {
                    throw new InvalidOperationException(
                        _localizationService.T("DesktopOrganization.Error.TargetUnavailable"));
                }

                target = source.CloneWith(
                    widget.Id,
                    widget.Name,
                    Path.GetFullPath(widget.MappedFolderPath),
                    createsWidget: false,
                    source.Items);
            }

            if (targetsByDestination.TryGetValue(target.TargetWidgetId, out DesktopOrganizationTargetPlan? merged))
            {
                targetsByDestination[target.TargetWidgetId] = merged.CloneWith(
                    merged.TargetWidgetId,
                    merged.SuggestedDisplayName,
                    merged.TargetDirectoryPath,
                    merged.CreatesWidget,
                    merged.Items.Concat(target.Items));
            }
            else
            {
                targetsByDestination.Add(target.TargetWidgetId, target);
            }
        }

        var executionPlan = new DesktopOrganizationPlan
        {
            Id = Guid.NewGuid().ToString("N"),
            DesktopPath = previewPlan.DesktopPath,
            StorageRootPath = previewPlan.StorageRootPath,
            Targets = targetsByDestination.Values
                .Where(target => target.Items.Count > 0)
                .ToList(),
            ExcludedItems = previewPlan.ExcludedItems
                .Concat(retainedByChoice)
                .ToList()
        };

        AssignNonOverlappingBounds(executionPlan);
        return executionPlan;
    }

    public DesktopOrganizationPlan CreatePreviewPlanWithOptionalItems(
        DesktopOrganizationPlan basePlan,
        IReadOnlyCollection<string> includedSourcePaths)
    {
        var included = includedSourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DesktopOrganizationFileSnapshot[] allItems = basePlan.Targets
            .SelectMany(target => target.Items)
            .Concat(basePlan.ExcludedItems)
            .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => item.CanOptIn && included.Contains(item.SourcePath)
                ? item with { ExclusionReason = DesktopOrganizationExclusionReason.None }
                : item)
            .ToArray();
        var scan = new DesktopOrganizationScanResult
        {
            DesktopPath = basePlan.DesktopPath,
            Items = allItems.ToList()
        };
        DesktopOrganizationPlan plan = _planner.CreatePlan(
            scan,
            basePlan.StorageRootPath,
            _settingsService.Settings.Widgets,
            _settingsService.Settings.DesktopOrganizationRules,
            ResolveCategoryName);
        AssignNonOverlappingBounds(plan);
        return plan;
    }

    public IReadOnlyList<DesktopOrganizationDestinationOption> GetDestinationOptions()
    {
        return _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .OrderBy(widget => widget.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(widget => new DesktopOrganizationDestinationOption(
                widget.Id,
                widget.Name,
                Path.GetFullPath(widget.MappedFolderPath!),
                IsDynamic: false))
            .ToList();
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(plan, progress: null, cancellationToken);
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        IProgress<DesktopOrganizationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        string[] existingTargetIds = plan.Targets
            .Where(target => !target.CreatesWidget)
            .Select(target => target.TargetWidgetId)
            .ToArray();
        _widgetManager.SetDesktopOrganizationBusy(existingTargetIds, isBusy: true);
        DesktopOrganizationExecutionResult result;
        try
        {
            result = await _transaction.ExecuteAsync(plan, progress, cancellationToken);
        }
        finally
        {
            _widgetManager.SetDesktopOrganizationBusy(existingTargetIds, isBusy: false);
        }

        var shownWidgetIds = new List<string>();
        try
        {
            foreach (WidgetConfig widget in result.CreatedWidgets)
            {
                await _widgetManager.ShowWidgetAsync(widget.Id, reveal: true, autoRestoreOnReveal: false);
                shownWidgetIds.Add(widget.Id);
            }

            foreach (DesktopOrganizationTargetPlan target in plan.Targets.Where(target => !target.CreatesWidget))
            {
                await _widgetManager.RefreshFileWidgetAsync(target.TargetWidgetId);
            }

            return result;
        }
        catch
        {
            foreach (string widgetId in shownWidgetIds)
            {
                await _widgetManager.RemoveWidgetAsync(widgetId, WidgetRemovalAction.RemoveWidgetOnly);
            }

            await _organizerService.UndoAsync(result.History.Id);
            _settingsService.Settings.DesktopOrganizationRules.RemoveAll(rule =>
                result.CreatedWidgets.Any(widget =>
                    string.Equals(widget.Id, rule.TargetWidgetId, StringComparison.Ordinal)));
            _settingsService.Settings.Widgets.RemoveAll(widget =>
                result.CreatedWidgets.Any(created =>
                    string.Equals(created.Id, widget.Id, StringComparison.Ordinal)));
            await _settingsService.SaveAsync(notifySubscribers: false);
            throw;
        }
    }

    public Task<int> RecoverPendingAsync() => _transaction.RecoverPendingAsync();

    public async Task UndoAsync(string historyId)
    {
        OrganizationHistoryEntry? history = _settingsService.Settings.RecentOrganizationHistory
            .FirstOrDefault(entry =>
                string.Equals(entry.Id, historyId, StringComparison.Ordinal));
        if (history is null)
        {
            throw new InvalidOperationException("The organization history entry no longer exists.");
        }

        await _organizerService.UndoAsync(historyId);
        foreach (OrganizationHistoryTarget target in history.Targets)
        {
            if (target.WasCreated)
            {
                await _widgetManager.RemoveWidgetAsync(
                    target.WidgetId,
                    WidgetRemovalAction.RemoveWidgetOnly);
                _settingsService.Settings.DesktopOrganizationRules.RemoveAll(rule =>
                    string.Equals(
                        rule.TargetWidgetId,
                        target.WidgetId,
                        StringComparison.Ordinal));
                TryDeleteEmptyDirectory(target.DirectoryPath);
            }
            else
            {
                await _widgetManager.RefreshFileWidgetAsync(target.WidgetId);
            }
        }

        await _settingsService.SaveAsync(notifySubscribers: false);
    }

    private string ResolveCategoryName(string categoryId)
    {
        string key = $"DesktopOrganization.Category.{categoryId}";
        string localized = _localizationService.T(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? categoryId
            : localized;
    }

    private static string SanitizeCustomFolderName(string name)
    {
        string sanitized = string.Concat(name.Trim().Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "分类" : sanitized;
    }

    private void AssignNonOverlappingBounds(DesktopOrganizationPlan plan)
    {
        if (plan.NewWidgetCount == 0)
        {
            return;
        }

        NativeRect nativeWorkArea = default;
        if (!SystemParametersInfo(SpiGetWorkArea, 0, ref nativeWorkArea, 0))
        {
            return;
        }

        double scale = Math.Max(1, GetDpiForSystem() / 96d);
        var workArea = new DesktopOrganizationRect(
            nativeWorkArea.Left,
            nativeWorkArea.Top,
            nativeWorkArea.Right - nativeWorkArea.Left,
            nativeWorkArea.Bottom - nativeWorkArea.Top);
        var occupied = _settingsService.Settings.Widgets
            .Where(widget => widget.IsVisible && !widget.IsDisabled)
            .Select(widget => new DesktopOrganizationRect(
                widget.X,
                widget.Y,
                widget.Width * scale,
                widget.Height * scale))
            .ToList();

        if (!_placementPlanner.TryAssignBounds(
                plan,
                workArea,
                occupied,
                _settingsService.Settings.DefaultWidgetWidth * scale,
                _settingsService.Settings.DefaultWidgetHeight * scale,
                DesktopOrganizationPlacementPlanner.DefaultEdgeMargin * scale,
                DesktopOrganizationPlacementPlanner.DefaultGap * scale))
        {
            throw new InvalidOperationException(
                _localizationService.T("DesktopOrganization.Error.NoLayoutSpace"));
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private const uint SpiGetWorkArea = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref NativeRect value,
        uint update);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
