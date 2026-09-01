using DeskBox.Models;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    internal async Task ApplyAgentWidgetLayoutAsync(
        IReadOnlyCollection<WidgetConfig> configs)
    {
        foreach (WidgetConfig config in configs)
        {
            IDesktopWidgetWindow? window = GetLoadedDesktopWindows()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Config.Id, config.Id, StringComparison.Ordinal));
            if (window is null)
            {
                continue;
            }

            window.RestoreBoundsForCurrentTopology();
            if (window is WidgetWindowBase widgetWindow)
            {
                widgetWindow.ApplyAgentCollapsedState(config.IsCollapsed);
            }
        }

        await _settingsService.SaveAsync(notifySubscribers: false);
    }
}
