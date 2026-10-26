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

    /// <summary>Icon of the storage unit item.</summary>
    public string? IconPath { get; set; }

    public DateTime UpdatedAt { get; set; }

    [Ignore]
    public string DisplayName => string.IsNullOrEmpty(Name) ? "Storage Unit" : Name;
}

/// <summary>
/// Value of the inventory (or of one storage unit) on one day, on every market.
/// The row of the current day is replaced as prices change, so each day keeps its last value.
/// </summary>
[Table("value_snapshots")]
public class ValueSnapshot
{
    /// <summary>"yyyy-MM-dd|scope".</summary>
    [PrimaryKey]
    public string Key { get; set; } = "";

    /// <summary>Local date, "yyyy-MM-dd".</summary>
    [Indexed]
    public string Date { get; set; } = "";

    /// <summary><see cref="AccountScope"/> for the whole account, or the id of a storage unit.</summary>
    [Indexed]
    public string Scope { get; set; } = "";

    public double? Steam { get; set; }
    public double? Skinport { get; set; }
    public double? CsgoMarket { get; set; }
    public double? Lowest { get; set; }

    public DateTime UpdatedAt { get; set; }

    public const string AccountScope = "account";

    public static string MakeKey(string date, string scope) => $"{date}|{scope}";

    /// <summary>Value on one market (see PriceSources).</summary>
    public double? ValueFor(string source) => source switch
    {
        "steam" => Steam,
        "skinport" => Skinport,
        "csgomarket" => CsgoMarket,
        "lowest" => Lowest,
        _ => null
    };

    public void SetValue(string source, double? value)
    {
        switch (source)
        {
            case "steam": Steam = value; break;
            case "skinport": Skinport = value; break;
            case "csgomarket": CsgoMarket = value; break;
            case "lowest": Lowest = value; break;
        }
    }
}

/// <summary>Price of one item on one market on one day (the last price seen that day).</summary>
[Table("price_history")]
public class PriceHistoryEntry
{
    /// <summary>"source|market hash name|yyyy-MM-dd".</summary>
    [PrimaryKey]
    public string Key { get; set; } = "";

    public string Source { get; set; } = "";

    [Indexed]
    public string MarketHashName { get; set; } = "";

    /// <summary>Local date, "yyyy-MM-dd".</summary>
    public string Date { get; set; } = "";

    public double Price { get; set; }

    public static string MakeKey(string source, string name, string date) => $"{source}|{name}|{date}";
}

/// <summary>
/// Details of one copy of an item read from the CS2 Game Coordinator (float, pattern, stickers, StatTrak),
/// linked to its inventory line by <see cref="EntryKey"/>.
/// </summary>
[Table("item_details")]
public class ItemDetail
{
    /// <summary>Item id (same as the asset id in the Steam inventory).</summary>
    [PrimaryKey]
    public string Id { get; set; } = "";

    /// <summary>Key of the <see cref="InventoryEntry"/> this copy belongs to.</summary>
    [Indexed]
    public string EntryKey { get; set; } = "";

    /// <summary>Exact wear (float), e.g. 0.0712.</summary>
    public double? Float { get; set; }

    /// <summary>Pattern template (paint seed).</summary>
    public int? Seed { get; set; }

    /// <summary>StatTrak kill count.</summary>
    public int? Kills { get; set; }

    /// <summary>Applied stickers and patches, one name per line.</summary>
    public string? Stickers { get; set; }

    public string? CustomName { get; set; }
}

/// <summary>A price returned by a market.</summary>
public record PriceQuote(string MarketHashName, double? LowestPrice, double? MedianPrice, int? Volume);
