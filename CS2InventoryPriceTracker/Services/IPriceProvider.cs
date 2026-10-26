using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.Services;

/// <summary>
/// A market the app reads prices from (Steam Market, Skinport, Market.CSGO...). Prices are always in euros.
/// </summary>
public interface IPriceProvider
{
    /// <summary>Short id stored with each cached price, see <see cref="PriceSources"/>.</summary>
    string Source { get; }

    /// <summary>Name shown to the user, e.g. "Steam Market".</summary>
    string DisplayName { get; }

    /// <summary>How long a cached price is considered fresh.</summary>
    TimeSpan MaxAge { get; }

    /// <summary>Roughly how long one item takes (for the progress estimate); zero for markets that send everything at once.</summary>
    TimeSpan TimePerItem { get; }

    /// <summary>
    /// Fetches prices for the given items and reports each one through <paramref name="onQuote"/> as soon as it is known,
    /// so slow markets still show progress. Markets with a bulk endpoint report everything at once.
    /// Items the market doesn't sell are reported with empty prices.
    /// </summary>
    Task FetchAsync(IReadOnlyList<string> marketHashNames, Func<PriceQuote, Task> onQuote, CancellationToken ct);
}
