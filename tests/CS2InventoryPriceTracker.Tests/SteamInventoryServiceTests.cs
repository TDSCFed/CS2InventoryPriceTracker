using System.Net;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.Tests;

/// <summary>Reading Steam profiles and the public CS2 inventory (no network: answers come from a fake handler).</summary>
public class SteamInventoryServiceTests
{
    private const string SteamId = "76561198000000001";

    private static SteamInventoryService Service(FakeHttpHandler handler) => new(new HttpClient(handler));

    private const string Inventory = """
        {
          "success": 1, "total_inventory_count": 7, "more_items": 0,
          "assets": [
            { "assetid": "1", "classid": "10", "instanceid": "0", "amount": "1" },
            { "assetid": "2", "classid": "10", "instanceid": "0", "amount": "1" },
            { "assetid": "3", "classid": "20", "instanceid": "0", "amount": "1" },
            { "assetid": "4", "classid": "30", "instanceid": "0", "amount": "1" },
            { "assetid": "5", "classid": "40", "instanceid": "0", "amount": "1" },
            { "assetid": "6", "classid": "50", "instanceid": "0", "amount": "1" },
            { "assetid": "7", "classid": "60", "instanceid": "0", "amount": "1" }
          ],
          "descriptions": [
            { "classid": "10", "instanceid": "0", "market_hash_name": "AK-47 | Redline (Field-Tested)", "name": "AK-47 | Redline",
              "type": "Classified Rifle", "marketable": 1, "tradable": 1, "market_tradable_restriction": 7, "icon_url": "ak",
              "tags": [ { "category": "Type", "localized_tag_name": "Rifle" },
                        { "category": "Rarity", "localized_tag_name": "Classified", "color": "d32ce6" } ] },
            { "classid": "20", "instanceid": "0", "market_hash_name": "AK-47 | Redline (Field-Tested)", "name": "AK-47 | Redline",
              "type": "Classified Rifle", "marketable": 0, "tradable": 0, "market_tradable_restriction": 7,
              "cache_expiration": "2026-11-02T07:00:00Z",
              "tags": [ { "category": "Type", "localized_tag_name": "Rifle" } ] },
            { "classid": "30", "instanceid": "0", "market_hash_name": "AWP | Asiimov (Field-Tested)", "name": "AWP | Asiimov",
              "marketable": 1, "tradable": 1, "market_tradable_restriction": 7,
              "fraudwarnings": [ "Name Tag: ''Big Green''" ],
              "tags": [ { "category": "Type", "localized_tag_name": "Sniper Rifle" } ] },
            { "classid": "40", "instanceid": "0", "market_hash_name": "Storage Unit", "name": "Storage Unit",
              "marketable": 0, "tradable": 0, "fraudwarnings": [ "Name Tag: ''Cases''" ] },
            { "classid": "50", "instanceid": "0", "market_hash_name": "5 Year Veteran Coin", "name": "5 Year Veteran Coin",
              "marketable": 0, "tradable": 0, "tags": [ { "category": "Type", "localized_tag_name": "Collectible" } ] },
            { "classid": "60", "instanceid": "0", "market_hash_name": "Nova | Sand Dune (Field-Tested)", "name": "Nova | Sand Dune",
              "marketable": 1, "tradable": 1, "market_tradable_restriction": 7,
              "tags": [ { "category": "Type", "localized_tag_name": "Shotgun" } ] }
          ]
        }
        """;

    [Fact]
    public async Task Copies_of_the_same_item_are_grouped_and_trade_holds_counted()
    {
        var entries = await Service(new FakeHttpHandler().Add("/inventory/", Inventory)).GetInventoryAsync(SteamId);

        var ak = Assert.Single(entries, e => e.MarketHashName == "AK-47 | Redline (Field-Tested)");
        Assert.Equal(3, ak.Quantity); // two tradable copies and one under trade hold
        Assert.True(ak.Marketable, "a trade-protected copy still has a market price");
        Assert.Equal(1, ak.LockedCount);
        Assert.Equal(new DateTime(2026, 11, 2, 7, 0, 0, DateTimeKind.Utc), ak.LockedUntil!.Value.ToUniversalTime());
        Assert.Equal("Rifle", ak.Category);
        Assert.Equal("Classified", ak.Rarity);
        Assert.Equal("d32ce6", ak.RarityColor);
    }

    [Fact]
    public async Task Name_tags_storage_units_and_categories()
    {
        var entries = await Service(new FakeHttpHandler().Add("/inventory/", Inventory)).GetInventoryAsync(SteamId);

        Assert.Equal("Big Green", entries.Single(e => e.Name == "AWP | Asiimov").CustomName);
        Assert.Equal("Sniper Rifle", entries.Single(e => e.Name == "AWP | Asiimov").Category);
        Assert.Equal("Heavy", entries.Single(e => e.Name == "Nova | Sand Dune").Category);

        var unit = entries.Single(e => e.IsStorageUnit);
        Assert.Equal("Cases", unit.CustomName);
        Assert.Equal(ItemCategories.StorageUnit, unit.Category);
        Assert.Equal(0, unit.LockedCount);

        var coin = entries.Single(e => e.Name == "5 Year Veteran Coin");
        Assert.False(coin.Marketable);
        Assert.Equal(0, coin.LockedCount);
    }

    [Fact]
    public async Task Large_inventories_are_read_page_by_page()
    {
        var handler = new FakeHttpHandler()
            .Add("/inventory/", """
                { "success": 1, "total_inventory_count": 2, "more_items": 1, "last_assetid": "1",
                  "assets": [ { "assetid": "1", "classid": "10", "instanceid": "0", "amount": "1" } ],
                  "descriptions": [ { "classid": "10", "instanceid": "0", "market_hash_name": "A", "name": "A", "marketable": 1, "tradable": 1 } ] }
                """)
            .Add("start_assetid=1", """
                { "success": 1, "total_inventory_count": 2, "more_items": 0,
                  "assets": [ { "assetid": "2", "classid": "20", "instanceid": "0", "amount": "1" } ],
                  "descriptions": [ { "classid": "20", "instanceid": "0", "market_hash_name": "B", "name": "B", "marketable": 1, "tradable": 1 } ] }
                """);

        var entries = await Service(handler).GetInventoryAsync(SteamId);

        Assert.Equal(new[] { "A", "B" }, entries.Select(e => e.Name).Order().ToArray());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Private_inventory_gives_a_clear_message()
    {
        var handler = new FakeHttpHandler().Add("/inventory/", "null", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<SteamException>(() => Service(handler).GetInventoryAsync(SteamId));
        Assert.Contains("private", error.Message);
    }

    [Fact]
    public async Task Empty_inventory_has_no_items()
    {
        var handler = new FakeHttpHandler().Add("/inventory/", """{ "success": 1, "total_inventory_count": 0 }""");
        Assert.Empty(await Service(handler).GetInventoryAsync(SteamId));
    }

    [Theory]
    [InlineData("76561198000000001", "profiles/76561198000000001")]
    [InlineData("https://steamcommunity.com/profiles/76561198000000001/", "profiles/76561198000000001")]
    [InlineData("https://steamcommunity.com/id/someone", "id/someone")]
    [InlineData("someone", "id/someone")]
    public async Task Profiles_are_found_from_links_ids_and_names(string input, string expectedPath)
    {
        var handler = new FakeHttpHandler().Add("?xml=1", """
            <?xml version="1.0" encoding="UTF-8"?>
            <profile>
              <steamID64>76561198000000001</steamID64>
              <steamID><![CDATA[Fed]]></steamID>
              <privacyState>public</privacyState>
              <avatarMedium><![CDATA[https://avatars.example/a.jpg]]></avatarMedium>
            </profile>
            """);

        var profile = await Service(handler).ResolveProfileAsync(input);

        Assert.Equal("76561198000000001", profile.SteamId);
        Assert.Equal("Fed", profile.PersonaName);
        Assert.True(profile.IsPublic);
        Assert.Contains(expectedPath, handler.Requests.Single());
    }

    [Fact]
    public async Task Unknown_profile_is_reported()
    {
        var handler = new FakeHttpHandler().Add("?xml=1", """<response><error>The specified profile could not be found.</error></response>""");
        await Assert.ThrowsAsync<SteamException>(() => Service(handler).ResolveProfileAsync("nobody"));
    }
}
