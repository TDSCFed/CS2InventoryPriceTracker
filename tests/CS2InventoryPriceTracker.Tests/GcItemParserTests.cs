using System.Text;
using CS2InventoryPriceTracker.Services;
using SteamKit2.GC.CSGO.Internal;

namespace CS2InventoryPriceTracker.Tests;

/// <summary>Decoding items sent by the CS2 Game Coordinator (storage unit contents).</summary>
public class GcItemParserTests
{
    private static CSOEconItemAttribute Uint(uint def, uint value) => new() { def_index = def, value = value };

    private static CSOEconItemAttribute Float(uint def, float value) =>
        new() { def_index = def, value_bytes = BitConverter.GetBytes(value) };

    /// <summary>String attributes are a small protobuf message with the text in field 1.</summary>
    private static CSOEconItemAttribute Text(uint def, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new CSOEconItemAttribute { def_index = def, value_bytes = [0x0A, (byte)bytes.Length, .. bytes] };
    }

    private static CSOEconItem Item(uint def, uint quality, params CSOEconItemAttribute[] attributes)
    {
        var item = new CSOEconItem { id = 123456789012, def_index = def, quality = quality };
        item.attribute.AddRange(attributes);
        return item;
    }

    [Fact]
    public void Skin_with_wear_pattern_stattrak_and_stickers()
    {
        var parsed = GcItemParser.Parse(Item(7, 9,
            Float(GcItemParser.AttrPaintIndex, 282),
            Float(GcItemParser.AttrPaintWear, 0.1234f),
            Float(GcItemParser.AttrPaintSeed, 661),
            Uint(GcItemParser.AttrKillEater, 1337),
            Uint(GcItemParser.AttrStickerSlot0, 100),
            Uint(GcItemParser.AttrStickerSlot0 + 4, 48)));

        Assert.Equal(123456789012UL, parsed.Id);
        Assert.Equal(7u, parsed.DefIndex);
        Assert.Equal(9u, parsed.Quality);
        Assert.Equal(282, parsed.PaintIndex);
        Assert.Equal(0.1234f, parsed.PaintWear!.Value, 4);
        Assert.Equal(661, parsed.PaintSeed);
        Assert.True(parsed.HasStatTrak);
        Assert.Equal(1337u, parsed.KillCount);
        Assert.Equal(new uint[] { 100, 48 }, parsed.Stickers);
        Assert.Null(parsed.CasketId);
    }

    [Fact]
    public void Float_attributes_stored_in_the_value_field_are_read_too()
    {
        var parsed = GcItemParser.Parse(Item(7, 4,
            Uint(GcItemParser.AttrPaintIndex, BitConverter.SingleToUInt32Bits(282f)),
            Uint(GcItemParser.AttrPaintWear, BitConverter.SingleToUInt32Bits(0.5f))));

        Assert.Equal(282, parsed.PaintIndex);
        Assert.Equal(0.5f, parsed.PaintWear);
    }

    [Fact]
    public void Item_inside_a_storage_unit_points_to_it()
    {
        const ulong casketId = 0x0000_0007_1234_5678;
        var parsed = GcItemParser.Parse(Item(1210, 4,
            Uint(GcItemParser.AttrCasketLow, 0x1234_5678),
            Uint(GcItemParser.AttrCasketHigh, 0x0000_0007)));

        Assert.Equal(casketId, parsed.CasketId);
    }

    [Fact]
    public void Storage_unit_name_and_item_count()
    {
        var parsed = GcItemParser.Parse(Item(ItemCatalog.StorageUnitDefIndex, 4,
            Text(GcItemParser.AttrCustomName, "Cases ✓"),
            Uint(GcItemParser.AttrItemsCount, 746)));

        Assert.Equal("Cases ✓", parsed.CustomName);
        Assert.Equal(746, parsed.CasketItemCount);
    }

    [Fact]
    public void Custom_name_field_wins_over_the_attribute()
    {
        var item = Item(7, 4, Text(GcItemParser.AttrCustomName, "From attribute"));
        item.custom_name = "Name tag";

        Assert.Equal("Name tag", GcItemParser.Parse(item).CustomName);
    }

    [Fact]
    public void Trade_hold_uses_the_latest_date()
    {
        var until = new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero);
        var parsed = GcItemParser.Parse(Item(7, 4,
            Uint(GcItemParser.AttrTradableAfter, (uint)until.AddDays(-2).ToUnixTimeSeconds()),
            Uint(GcItemParser.AttrTradeProtectedUntil, (uint)until.ToUnixTimeSeconds())));

        Assert.Equal(until.UtcDateTime, parsed.TradableAfter);
    }

    [Fact]
    public void Music_kit_graffiti_and_charm_ids()
    {
        Assert.Equal(3u, GcItemParser.Parse(Item(1314, 9, Uint(GcItemParser.AttrMusicId, 3))).MusicId);

        var graffiti = GcItemParser.Parse(Item(1348, 4,
            Uint(GcItemParser.AttrStickerSlot0, 1697), Uint(GcItemParser.AttrSprayTint, 3)));
        Assert.Equal(1697u, graffiti.StickerKit);
        Assert.Equal(3u, graffiti.GraffitiTint);

        Assert.Equal(1u, GcItemParser.Parse(Item(1355, 4, Uint(GcItemParser.AttrCharmSlot0, 1))).CharmId);
    }

    [Fact]
    public async Task Parsed_items_get_their_market_names()
    {
        var parsed = GcItemParser.Parse(Item(507, 3,
            Float(GcItemParser.AttrPaintIndex, 38), Float(GcItemParser.AttrPaintWear, 0.012f)));

        var resolved = await TestData.Catalog().ResolveAsync(parsed);

        Assert.Equal("★ Karambit | Fade (Factory New)", resolved.MarketHashName);
    }
}
