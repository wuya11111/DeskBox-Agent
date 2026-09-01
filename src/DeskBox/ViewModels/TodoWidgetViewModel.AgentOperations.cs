using DeskBox.Models;

namespace DeskBox.ViewModels;

public sealed partial class TodoWidgetViewModel
{
    public async Task<bool> ReorderItemAsync(string itemId, int targetIndex)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null || targetIndex < 0 || targetIndex >= Items.Count)
        {
            return false;
        }

        int currentIndex = Items.IndexOf(item);
        if (currentIndex < 0 || currentIndex == targetIndex)
        {
            return true;
        }

        Items.Move(currentIndex, targetIndex);
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }
}
