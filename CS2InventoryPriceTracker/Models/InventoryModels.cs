using SQLite;

namespace CS2InventoryPriceTracker.Models;

/// <summary>
/// One line of the inventory: all copies of the same item grouped together.
/// Marketable items are grouped by market hash name (the name the Steam Market uses);
/// items that cannot be sold (storage units, medals, coins...) are grouped by name and custom name.
/// </summary>
[Table("inventory_items")]
public class InventoryEntry
{
    [PrimaryKey]
    public string Key { get; set; } = "";

    /// <summary>Steam Market name, e.g. "AK-47 | Redline (Field-Tested)".</summary>
    public string MarketHashName { get; set; } = "";

    /// <summary>Display name shown in the Steam inventory.</summary>
    public string Name { get; set; } = "";

    /// <summary>Name tag text, if the item was renamed.</summary>
    public string? CustomName { get; set; }

    /// <summary>Item type line, e.g. "Classified Rifle".</summary>
    public string? Type { get; set; }

    /// <summary>Rarity color as hex without '#', e.g. "d32ce6".</summary>
    public string? RarityColor { get; set; }

    public string? IconPath { get; set; }

    public int Quantity { get; set; }

    public bool Marketable { get; set; }

    public bool IsStorageUnit { get; set; }

    /// <summary>Where the item is: null for the main inventory, or the asset id of the storage unit holding it.</summary>
    public string? ContainerId { get; set; }

    [Ignore]
    public string? IconUrl => string.IsNullOrEmpty(IconPath)
        ? null
        : IconPath.StartsWith("http", StringComparison.Ordinal)
            ? IconPath
            : $"https://community.akamai.steamstatic.com/economy/image/{IconPath}/128fx128f";

    public static string MakeKey(string? containerId, bool marketable, string marketHashName, string name, string? customName)
        => marketable
            ? $"{containerId}|m|{marketHashName}"
            : $"{containerId}|n|{name}|{customName}";
}

/// <summary>Last known price of an item on one market, cached so the app opens instantly and offline.</summary>
[Table("prices")]
public class PriceEntry
{
    [PrimaryKey]
    public string Key { get; set; } = "";

    /// <summary>Market id, e.g. "steam".</summary>
    public string Source { get; set; } = "";

    public string MarketHashName { get; set; } = "";

    /// <summary>Cheapest current listing, in euros.</summary>
    public double? LowestPrice { get; set; }

    /// <summary>Median sale price of the last 24 hours, in euros.</summary>
    public double? MedianPrice { get; set; }

    /// <summary>Number of sales in the last 24 hours.</summary>
    public int? Volume { get; set; }

    /// <summary>True when the market answered but has no listings for the item.</summary>
    public bool NotListed { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>The price used for totals: the cheapest listing, or the median when there is none.</summary>
    [Ignore]
    public double? BestPrice => LowestPrice ?? MedianPrice;

    public static string MakeKey(string source, string marketHashName) => $"{source}|{marketHashName}";
}

/// <summary>A storage unit whose contents were loaded from the CS2 Game Coordinator.</summary>
[Table("storage_units")]
public class StorageUnit
{
    /// <summary>Item id of the storage unit (same as its asset id in the Steam inventory).</summary>
    [PrimaryKey]
    public string Id { get; set; } = "";

    /// <summary>Name given to the storage unit in game.</summary>
    public string? Name { get; set; }

    public int ItemCount { get; set; }

    public DateTime UpdatedAt { get; set; }

    [Ignore]
    public string DisplayName => string.IsNullOrEmpty(Name) ? "Storage Unit" : Name;
}

/// <summary>A price returned by a market.</summary>
public record PriceQuote(string MarketHashName, double? LowestPrice, double? MedianPrice, int? Volume);
