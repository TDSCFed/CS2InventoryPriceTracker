using System.Globalization;
using System.Net;
using System.Text.Json;
using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.Services;

/// <summary>
/// A market with one endpoint that returns the prices of every CS2 item at once.
/// The whole list is downloaded once per refresh and every requested item is answered from it.
/// </summary>
public abstract class BulkPriceProvider : IPriceProvider
{
    /// <summary>
    /// Own client on the managed handler: it decompresses Brotli, which some markets require, on every platform.
    /// </summary>
    protected static readonly HttpClient Http = CreateClient();

    public abstract string Source { get; }
    public abstract string DisplayName { get; }
    public virtual TimeSpan MaxAge => TimeSpan.FromMinutes(30);
    public TimeSpan TimePerItem => TimeSpan.Zero;

    /// <summary>Downloads the full price list, by market hash name.</summary>
    protected internal abstract Task<Dictionary<string, PriceQuote>> DownloadAsync(CancellationToken ct);

    public async Task FetchAsync(IReadOnlyList<string> marketHashNames, Func<PriceQuote, Task> onQuote, CancellationToken ct)
    {
        Dictionary<string, PriceQuote> prices;
        try
        {
            prices = await DownloadAsync(ct);
            SteamDiagnostics.Write($"{DisplayName}: {prices.Count} prices downloaded");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Offline or the market changed its API: keep the cached prices, try again on the next refresh.
            SteamDiagnostics.Write($"{DisplayName}: price download failed: {e.GetType().Name}: {e.Message}");
            return;
        }

        foreach (var name in marketHashNames)
        {
            ct.ThrowIfCancellationRequested();
            await onQuote(prices.TryGetValue(name, out var quote) ? quote : new PriceQuote(name, null, null, null));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CS2InventoryPriceTracker/1.0");
        return client;
    }

    /// <summary>Reads a price that may be sent as a number or as a string ("1.23").</summary>
    internal static double? ReadPrice(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        double? result = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
        return result is > 0 ? result : null;
    }

    internal static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null
        };
    }
}

/// <summary>
/// Skinport public API: every CS2 item in one request, in euros. Skinport caches it for 5 minutes,
/// allows only a few requests per 5 minutes and requires Brotli compression.
/// </summary>
public class SkinportPriceProvider : BulkPriceProvider
{
    private const string Url = "https://api.skinport.com/v1/items?app_id=730&currency=EUR&tradable=0";

    public override string Source => PriceSources.Skinport;
    public override string DisplayName => "Skinport";

    protected internal override async Task<Dictionary<string, PriceQuote>> DownloadAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.AcceptEncoding.ParseAdd("br");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    /// <summary>Reads Skinport's answer: an array of items with market_hash_name, min_price, median_price, quantity.</summary>
    internal static Dictionary<string, PriceQuote> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unexpected answer from Skinport.");

        var prices = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("market_hash_name", out var nameElement) || nameElement.GetString() is not { } name)
                continue;

            // min_price is the cheapest listing (null when nothing is listed).
            prices[name] = new PriceQuote(name, ReadPrice(item, "min_price"), ReadPrice(item, "median_price"), ReadInt(item, "quantity"));
        }
        return prices;
    }
}

/// <summary>Market.CSGO public price list: the cheapest listing of every CS2 item, in euros.</summary>
public class CsgoMarketPriceProvider : BulkPriceProvider
{
    private const string Url = "https://market.csgo.com/api/v2/prices/EUR.json";

    public override string Source => PriceSources.CsgoMarket;
    public override string DisplayName => "Market.CSGO";

    protected internal override async Task<Dictionary<string, PriceQuote>> DownloadAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    /// <summary>Reads Market.CSGO's answer: { "items": [ { market_hash_name, price, volume } ] }.</summary>
    internal static Dictionary<string, PriceQuote> Parse(JsonElement root)
    {
        var prices = new Dictionary<string, PriceQuote>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unexpected answer from Market.CSGO.");

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("market_hash_name", out var nameElement) || nameElement.GetString() is not { } name)
                continue;

            prices[name] = new PriceQuote(name, ReadPrice(item, "price"), null, ReadInt(item, "volume"));
        }
        return prices;
    }
}
