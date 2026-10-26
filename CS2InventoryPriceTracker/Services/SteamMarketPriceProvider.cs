using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.Services;

/// <summary>
/// Prices from the Steam Community Market, one item per request (in euros).
/// Steam only allows about 20 of these requests per minute, so they are spaced out and the results are cached.
/// </summary>
public class SteamMarketPriceProvider(HttpClient http) : IPriceProvider
{
    private const int EuroCurrency = 3;
    private static readonly TimeSpan DelayBetweenRequests = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(65);

    public string Source => PriceSources.Steam;
    public string DisplayName => "Steam Market";
    public TimeSpan MaxAge => TimeSpan.FromHours(12);
    public TimeSpan TimePerItem => DelayBetweenRequests;

    public async Task FetchAsync(IReadOnlyList<string> marketHashNames, Func<PriceQuote, Task> onQuote, CancellationToken ct)
    {
        var failuresInARow = 0;
        for (var i = 0; i < marketHashNames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0)
                await Task.Delay(DelayBetweenRequests, ct);

            var name = marketHashNames[i];
            var url = "https://steamcommunity.com/market/priceoverview/" +
                      $"?appid=730&currency={EuroCurrency}&market_hash_name={Uri.EscapeDataString(name)}";

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, ct);
            }
            catch (HttpRequestException)
            {
                // Offline: stop here, the remaining items are retried on the next refresh.
                return;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (++failuresInARow >= 3)
                        return;
                    await Task.Delay(RateLimitPause, ct);
                    i--; // Retry the same item.
                    continue;
                }

                failuresInARow = 0;

                // Steam answers 500 with success=false for items it has no market data for.
                var json = await response.Content.ReadAsStringAsync(ct);
                PriceOverview? overview = null;
                try
                {
                    overview = JsonSerializer.Deserialize<PriceOverview>(json);
                }
                catch (JsonException)
                {
                }

                if (overview is null && !response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.InternalServerError)
                    continue; // Unknown error: skip this item for now.

                await onQuote(new PriceQuote(
                    name,
                    ParseEuro(overview?.LowestPrice),
                    ParseEuro(overview?.MedianPrice),
                    ParseVolume(overview?.Volume)));
            }
        }
    }

    /// <summary>Parses Steam's euro format, e.g. "0,03€", "1.234,56€" or "5,--€".</summary>
    internal static double? ParseEuro(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleaned = new string(text.Replace("--", "00").Where(c => char.IsDigit(c) || c is ',' or '.').ToArray());
        if (cleaned.Length == 0)
            return null;

        if (cleaned.Contains(','))
            cleaned = cleaned.Replace(".", "").Replace(',', '.');

        return double.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static int? ParseVolume(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }

    private sealed class PriceOverview
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("lowest_price")] public string? LowestPrice { get; set; }
        [JsonPropertyName("median_price")] public string? MedianPrice { get; set; }
        [JsonPropertyName("volume")] public string? Volume { get; set; }
    }
}
