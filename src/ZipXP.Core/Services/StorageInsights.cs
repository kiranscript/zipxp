namespace ZipXP.Core.Services;

public sealed record StorageItem(string Path, string Name, bool Directory, long? Logical, long? Allocated, bool Compressed, string Notes);
public sealed record StoragePage(IReadOnlyList<StorageItem> Rows, int TotalRows);

/// <summary>Sort the complete matching set before applying the desktop's response bound.</summary>
public static class StorageInsights
{
    public static StoragePage CreatePage(IEnumerable<StorageItem> items, string sort)
    {
        var matches = items.ToArray();
        var ordered = sort switch
        {
            "name" => matches.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "logical" => matches.OrderByDescending(i => i.Logical ?? -1),
            "allocated" => matches.OrderByDescending(i => i.Allocated ?? -1),
            _ => throw new ArgumentException("Choose a supported storage sort order.")
        };
        return new(ordered.ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Take(500).ToArray(), matches.Length);
    }
}
