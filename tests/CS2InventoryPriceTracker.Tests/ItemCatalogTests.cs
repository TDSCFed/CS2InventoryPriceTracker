using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.Tests;

/// <summary>
/// Names of storage unit items, built from Game Coordinator ids with the real items.json.
/// These must match the Steam Market names exactly, or the items get no price.
/// </summary>
public class ItemCatalogTests
{
    private const uint Normal = 4, Unusual = 3, StatTrak = 9, Souvenir = 12;

    private static GcItem Item(uint def, uint quality = Normal, int? paint = null, float? wear = null,
        uint? sticker = null, uint? music = null, uint? charm = null, uint? tint = null, string? name = null) =>
        new(1, def, quality, name, paint, wear, sticker, music, charm, tint, false, null, null);

    private static Task<ResolvedItem> Resolve(GcItem item) => TestData.Catalog().ResolveAsync(item);

    [Fact]
    public async Task Weapon_skin_gets_its_wear()
    {
        var item = await Resolve(Item(7, paint: 282, wear: 0.25f));

        Assert.Equal("AK-47 | Redline (Field-Tested)", item.MarketHashName);
        Assert.True(item.Marketable);
        Assert.Equal("Rifle", item.Category);
        Assert.Equal("Classified", item.Rarity);
        Assert.Equal("Classified Rifle", item.Type);
        Assert.NotNull(item.IconPath);
    }

    [Fact]
    public async Task StatTrak_and_souvenir_prefixes()
    {
        Assert.Equal("StatTrak™ AK-47 | Redline (Minimal Wear)",
            (await Resolve(Item(7, StatTrak, 282, 0.10f))).MarketHashName);
        Assert.Equal("Souvenir AWP | Dragon Lore (Battle-Scarred)",
            (await Resolve(Item(9, Souvenir, 344, 0.70f))).MarketHashName);
    }

    [Fact]
    public async Task Knives_and_gloves_get_the_star()
    {
        Assert.Equal("★ Karambit | Fade (Factory New)", (await Resolve(Item(507, Unusual, 38, 0.01f))).MarketHashName);
        Assert.Equal("★ StatTrak™ Karambit | Fade (Factory New)", (await Resolve(Item(507, StatTrak, 38, 0.01f))).MarketHashName);
        Assert.Equal("★ Sport Gloves | Pandora's Box (Well-Worn)", (await Resolve(Item(5030, Unusual, 10037, 0.40f))).MarketHashName);

        var vanilla = await Resolve(Item(507, Unusual));
        Assert.Equal("★ Karambit", vanilla.MarketHashName);
        Assert.Equal("Knife", vanilla.Category);
    }

    [Fact]
    public async Task Stickers_music_kits_and_charms()
    {
        Assert.Equal("Sticker | Shooter", (await Resolve(Item(1209, sticker: 1))).MarketHashName);
        Assert.Equal("Sticker", (await Resolve(Item(1209, sticker: 1))).Category);
        Assert.Equal("StatTrak™ Music Kit | Daniel Sadowski, Crimson Assault",
            (await Resolve(Item(1314, StatTrak, music: 3))).MarketHashName);
        Assert.Equal("Charm | Lil' Ava", (await Resolve(Item(1355, charm: 1))).MarketHashName);
    }

    [Fact]
    public async Task Containers_and_storage_units()
    {
        var gift = await Resolve(Item(1210));
        Assert.Equal("Gift Package", gift.MarketHashName);
        Assert.True(gift.Marketable);
        Assert.Equal("Container", gift.Category);

        var unit = await Resolve(Item(ItemCatalog.StorageUnitDefIndex));
        Assert.Equal("Storage Unit", unit.Name);
        Assert.False(unit.Marketable);
    }

    [Fact]
    public async Task Stock_weapon_without_skin_is_not_marketable()
    {
        var ak = await Resolve(Item(7));
        Assert.Equal("AK-47", ak.Name);
        Assert.False(ak.Marketable);
    }

    [Fact]
    public async Task Unknown_items_do_not_crash()
    {
        var item = await Resolve(Item(999999));
        Assert.Equal("Unknown item #999999", item.Name);
        Assert.False(item.Marketable);
    }

    [Theory]
    [InlineData(0.00f, "Factory New")]
    [InlineData(0.0699f, "Factory New")]
    [InlineData(0.07f, "Minimal Wear")]
    [InlineData(0.15f, "Field-Tested")]
    [InlineData(0.38f, "Well-Worn")]
    [InlineData(0.45f, "Battle-Scarred")]
    [InlineData(0.99f, "Battle-Scarred")]
    public void Wear_names_follow_the_market_ranges(float wear, string expected)
    {
        Assert.Equal(expected, ItemCatalog.WearName(wear));
    }

    [Fact]
    public async Task Sticker_names_for_applied_stickers()
    {
        Assert.Equal("Sticker | Shooter", await TestData.Catalog().StickerNameAsync(1));
        Assert.Null(await TestData.Catalog().StickerNameAsync(uint.MaxValue));
    }

    [Fact]
    public async Task Item_table_is_complete()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "items.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("clientVersion").GetUInt32() > 2_000_000, "CS2 client version missing");
        Assert.True(root.GetProperty("skins").EnumerateObject().Count() > 1500, "too few skins");
        Assert.True(root.GetProperty("stickers").EnumerateObject().Count() > 5000, "too few stickers");
        Assert.True(root.GetProperty("defs").EnumerateObject().Count() > 500, "too few items");

        foreach (var skin in root.GetProperty("skins").EnumerateObject())
        {
            var row = skin.Value;
            Assert.False(string.IsNullOrWhiteSpace(row[0].GetString()), $"skin {skin.Name} has no name");
            Assert.True(row.GetArrayLength() >= 5, $"skin {skin.Name} has no category/rarity");
        }
    }
}
