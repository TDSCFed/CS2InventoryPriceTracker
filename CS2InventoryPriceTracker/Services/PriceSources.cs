namespace CS2InventoryPriceTracker.Services;

/// <summary>Ids and display names of the markets prices come from.</summary>
public static class PriceSources
{
    public const string Steam = "steam";
    public const string Skinport = "skinport";
    public const string CsgoMarket = "csgomarket";

    /// <summary>Not a market: the cheapest price across all markets.</summary>
    public const string Lowest = "lowest";

    /// <summary>Order in which markets are listed on item cards.</summary>
    public static readonly IReadOnlyList<string> All = [Steam, Skinport, CsgoMarket];

    public static string ShortName(string source) => source switch
    {
        Steam => "Steam",
        Skinport => "Skinport",
        CsgoMarket => "Market.CSGO",
        Lowest => "Lowest",
        _ => source
    };
}
