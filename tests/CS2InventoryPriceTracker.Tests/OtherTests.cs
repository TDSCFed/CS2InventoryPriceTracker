using CS2InventoryPriceTracker.Models;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.Tests;

public class ItemCategoriesTests
{
    [Theory]
    [InlineData("Rifle", "Rifle")]
    [InlineData("Shotgun", "Heavy")]
    [InlineData("Machinegun", "Heavy")]
    [InlineData("Knife", "Knife")]
    [InlineData("Pass", "Other")]
    [InlineData(null, "Other")]
    [InlineData("Something new", "Other")]
    public void Steam_types_map_to_app_categories(string? steamType, string expected)
    {
        Assert.Equal(expected, ItemCategories.FromSteamType(steamType));
    }

    [Fact]
    public void Knives_come_before_cases_and_covert_before_mil_spec()
    {
        Assert.True(ItemCategories.CategoryRank("Knife") < ItemCategories.CategoryRank("Container"));
        Assert.True(ItemCategories.RarityRank("Covert") < ItemCategories.RarityRank("Mil-Spec Grade"));
        Assert.Equal(ItemCategories.RarityOrder.Length, ItemCategories.RarityRank("Unknown rarity"));
    }
}

public class ModelTests
{
    [Fact]
    public void Inventory_keys_keep_locations_and_name_tags_apart()
    {
        var inventory = InventoryEntry.MakeKey(null, true, "AK-47 | Redline (Field-Tested)", "AK-47 | Redline", null);
        var unit = InventoryEntry.MakeKey("42", true, "AK-47 | Redline (Field-Tested)", "AK-47 | Redline", null);
        var coinA = InventoryEntry.MakeKey(null, false, "Coin", "Coin", "A");
        var coinB = InventoryEntry.MakeKey(null, false, "Coin", "Coin", "B");

        Assert.NotEqual(inventory, unit);
        Assert.NotEqual(coinA, coinB);
        Assert.Equal(inventory, InventoryEntry.MakeKey(null, true, "AK-47 | Redline (Field-Tested)", "other name", "tag"));
    }

    [Fact]
    public void Icons_are_built_from_steam_image_paths()
    {
        Assert.Equal("https://community.akamai.steamstatic.com/economy/image/abc/128fx128f",
            new InventoryEntry { IconPath = "abc" }.IconUrl);
        Assert.Equal("https://cdn.example/x.png", new InventoryEntry { IconPath = "https://cdn.example/x.png" }.IconUrl);
        Assert.Null(new InventoryEntry().IconUrl);
    }

    [Fact]
    public void Snapshot_values_by_market()
    {
        var snapshot = new ValueSnapshot();
        snapshot.SetValue(PriceSources.Skinport, 12.5);
        snapshot.SetValue(PriceSources.Lowest, 10);

        Assert.Equal(12.5, snapshot.ValueFor(PriceSources.Skinport));
        Assert.Equal(10, snapshot.ValueFor(PriceSources.Lowest));
        Assert.Null(snapshot.ValueFor(PriceSources.Steam));
    }
}
