using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.Tests;

/// <summary>
/// Checks against the real Steam and market APIs, to notice when one of them changes its format.
/// They need the internet, so they don't run with the other tests: the "API checks" workflow runs them every week
/// (or run them locally with: dotnet test --filter Category=Live).
/// </summary>
[Trait("Category", "Live")]
public class LiveApiTests
{
    /// <summary>Common items whose names the app builds itself (storage units) and that every market sells.</summary>
    private static readonly string[] CommonItems =
    [
        "AK-47 | Redline (Field-Tested)",
        "AWP | Asiimov (Field-Tested)",
        "Glock-18 | Water Elemental (Field-Tested)",
        "Revolution Case",
        "Recoil Case"
    ];

    [Fact]
    public async Task Skinport_sends_euro_prices_for_common_items()
    {
        var prices = await new SkinportPriceProvider().DownloadAsync(CancellationToken.None);

        Assert.True(prices.Count > 10_000, $"only {prices.Count} items");
        foreach (var name in CommonItems)
            Assert.True(prices.TryGetValue(name, out var p) && (p.LowestPrice ?? p.MedianPrice) > 0, $"no Skinport price for {name}");
    }

    [Fact]
    public async Task Market_csgo_sends_euro_prices_for_common_items()
    {
        var prices = await new CsgoMarketPriceProvider().DownloadAsync(CancellationToken.None);

        Assert.True(prices.Count > 10_000, $"only {prices.Count} items");
        foreach (var name in CommonItems)
            Assert.True(prices.TryGetValue(name, out var p) && p.LowestPrice > 0, $"no Market.CSGO price for {name}");
    }

    [Fact]
    public async Task Steam_market_sends_a_euro_price()
    {
        var quotes = new List<Models.PriceQuote>();
        await new SteamMarketPriceProvider(new HttpClient()).FetchAsync(
            [CommonItems[0]], q => { quotes.Add(q); return Task.CompletedTask; }, CancellationToken.None);

        var quote = Assert.Single(quotes);
        Assert.True((quote.LowestPrice ?? quote.MedianPrice) > 0, "no Steam Market price");
    }

    [Fact]
    public async Task Names_built_for_storage_units_exist_on_the_market()
    {
        var prices = await new SkinportPriceProvider().DownloadAsync(CancellationToken.None);
        var catalog = TestData.Catalog();

        // AK-47 Redline, AWP Asiimov, Karambit Fade and a StatTrak, as the Game Coordinator describes them.
        GcItem[] items =
        [
            new(1, 7, 4, null, 282, 0.25f, null, null, null, null, false, null, null),
            new(2, 9, 4, null, 279, 0.25f, null, null, null, null, false, null, null),
            new(3, 507, 3, null, 38, 0.01f, null, null, null, null, false, null, null),
            new(4, 7, 9, null, 282, 0.25f, null, null, null, null, false, null, null)
        ];

        foreach (var item in items)
        {
            var name = (await catalog.ResolveAsync(item)).MarketHashName;
            Assert.True(prices.ContainsKey(name), $"\"{name}\" is not a market name");
        }
    }
}
