using System.Text.Json;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.Tests;

/// <summary>Reading prices from the Steam Market, Skinport and Market.CSGO answers.</summary>
public class PriceParsingTests
{
    [Theory]
    [InlineData("0,03€", 0.03)]
    [InlineData("1,23€", 1.23)]
    [InlineData("1.234,56€", 1234.56)]
    [InlineData("12.345,00€", 12345.00)]
    [InlineData("5,--€", 5.00)]
    [InlineData("0,30 €", 0.30)]
    public void Steam_euro_prices_are_parsed(string text, double expected)
    {
        Assert.Equal(expected, SteamMarketPriceProvider.ParseEuro(text)!.Value, 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("€")]
    public void Missing_steam_prices_are_null(string? text)
    {
        Assert.Null(SteamMarketPriceProvider.ParseEuro(text));
    }

    [Fact]
    public void Skinport_items_are_read_with_null_prices_kept_empty()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "market_hash_name": "AK-47 | Redline (Field-Tested)", "currency": "EUR", "min_price": 12.34, "median_price": 13.5, "quantity": 120 },
              { "market_hash_name": "Sticker | Nothing Listed", "min_price": null, "median_price": 0.5, "quantity": 0 },
              { "market_hash_name": "Zero", "min_price": 0, "median_price": null, "quantity": 0 },
              { "currency": "EUR", "min_price": 1 }
            ]
            """);

        var prices = SkinportPriceProvider.Parse(doc.RootElement);

        Assert.Equal(3, prices.Count);
        var ak = prices["AK-47 | Redline (Field-Tested)"];
        Assert.Equal(12.34, ak.LowestPrice);
        Assert.Equal(13.5, ak.MedianPrice);
        Assert.Equal(120, ak.Volume);
        Assert.Null(prices["Sticker | Nothing Listed"].LowestPrice);
        Assert.Equal(0.5, prices["Sticker | Nothing Listed"].MedianPrice);
        Assert.Null(prices["Zero"].LowestPrice);
    }

    [Fact]
    public void Skinport_rejects_an_unexpected_answer()
    {
        using var doc = JsonDocument.Parse("""{ "errors": [ { "id": "rate_limit" } ] }""");
        Assert.Throws<InvalidDataException>(() => SkinportPriceProvider.Parse(doc.RootElement));
    }

    [Fact]
    public void Market_csgo_prices_are_read_from_strings()
    {
        using var doc = JsonDocument.Parse("""
            {
              "success": true, "currency": "EUR",
              "items": [
                { "market_hash_name": "AK-47 | Redline (Field-Tested)", "price": "11.95", "volume": "37" },
                { "market_hash_name": "Operation Breakout Weapon Case", "price": 2.5, "volume": 1000 }
              ]
            }
            """);

        var prices = CsgoMarketPriceProvider.Parse(doc.RootElement);

        Assert.Equal(11.95, prices["AK-47 | Redline (Field-Tested)"].LowestPrice);
        Assert.Equal(37, prices["AK-47 | Redline (Field-Tested)"].Volume);
        Assert.Equal(2.5, prices["Operation Breakout Weapon Case"].LowestPrice);
    }

    [Fact]
    public void Market_csgo_rejects_an_unexpected_answer()
    {
        using var doc = JsonDocument.Parse("""{ "success": false, "error": "bad currency" }""");
        Assert.Throws<InvalidDataException>(() => CsgoMarketPriceProvider.Parse(doc.RootElement));
    }

    [Fact]
    public async Task Steam_price_requests_report_listed_and_unlisted_items()
    {
        var handler = new FakeHttpHandler()
            .Add("market_hash_name=AK-47", """{ "success": true, "lowest_price": "12,34€", "volume": "1,234", "median_price": "12,00€" }""")
            .Add("market_hash_name=Nothing", """{ "success": false }""", System.Net.HttpStatusCode.InternalServerError);
        var provider = new SteamMarketPriceProvider(new HttpClient(handler));
        var quotes = new List<Models.PriceQuote>();

        await provider.FetchAsync(["AK-47 | Redline (Field-Tested)"], q => { quotes.Add(q); return Task.CompletedTask; }, CancellationToken.None);
        await provider.FetchAsync(["Nothing Listed"], q => { quotes.Add(q); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(12.34, quotes[0].LowestPrice);
        Assert.Equal(12.00, quotes[0].MedianPrice);
        Assert.Equal(1234, quotes[0].Volume);
        Assert.Contains("currency=3", handler.Requests[0]);
        Assert.Null(quotes[1].LowestPrice);
        Assert.Null(quotes[1].MedianPrice);
    }

    [Fact]
    public void Best_price_is_the_cheapest_listing_or_the_median()
    {
        Assert.Equal(1.0, new Models.PriceEntry { LowestPrice = 1.0, MedianPrice = 2.0 }.BestPrice);
        Assert.Equal(2.0, new Models.PriceEntry { MedianPrice = 2.0 }.BestPrice);
        Assert.Null(new Models.PriceEntry().BestPrice);
    }
}
