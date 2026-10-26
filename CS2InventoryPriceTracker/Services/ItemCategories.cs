namespace CS2InventoryPriceTracker.Services;

/// <summary>Item categories and rarities used by the filters, in the order they are listed.</summary>
public static class ItemCategories
{
    public const string StorageUnit = "Storage Unit";
    public const string Other = "Other";

    /// <summary>Categories from the most to the least valuable kind of item.</summary>
    public static readonly string[] Order =
    [
        "Knife", "Gloves", "Rifle", "Sniper Rifle", "Pistol", "SMG", "Heavy", "Agent", "Sticker", "Patch", "Charm",
        "Music Kit", "Graffiti", "Container", "Key", StorageUnit, "Collectible", "Tool", Other
    ];

    /// <summary>Rarities from the rarest to the most common (weapons, then other items, then agents).</summary>
    public static readonly string[] RarityOrder =
    [
        "Contraband", "Covert", "Extraordinary", "Classified", "Exotic", "Restricted", "Remarkable", "Mil-Spec Grade",
        "High Grade", "Industrial Grade", "Base Grade", "Consumer Grade",
        "Master", "Superior", "Exceptional", "Distinguished"
    ];

    /// <summary>Maps the "Type" tag of the Steam inventory to an app category.</summary>
    public static string FromSteamType(string? type) => type switch
    {
        null or "" => Other,
        "Shotgun" or "Machinegun" => "Heavy",
        "Gift" or "Pass" or "Equipment" => Other,
        _ when Order.Contains(type) => type,
        _ => Other
    };

    public static int CategoryRank(string? category)
    {
        var index = Array.IndexOf(Order, category ?? Other);
        return index < 0 ? Order.Length : index;
    }

    public static int RarityRank(string? rarity)
    {
        var index = rarity is null ? -1 : Array.IndexOf(RarityOrder, rarity);
        return index < 0 ? RarityOrder.Length : index;
    }
}
