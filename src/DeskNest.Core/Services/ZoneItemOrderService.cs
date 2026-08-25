using DeskNest.Core.Models;

namespace DeskNest.Core.Services;

public static class ZoneItemOrderService
{
    public static bool Move(
        IList<DesktopItem> source,
        IList<DesktopItem> target,
        Guid itemId,
        int targetIndex)
    {
        var sourceIndex = IndexOf(source, itemId);
        if (sourceIndex < 0)
        {
            return false;
        }

        var item = source[sourceIndex];
        source.RemoveAt(sourceIndex);
        if (ReferenceEquals(source, target) && sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, target.Count);
        target.Insert(targetIndex, item);
        return true;
    }

    private static int IndexOf(IList<DesktopItem> items, Guid itemId)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Id == itemId)
            {
                return index;
            }
        }

        return -1;
    }
}
