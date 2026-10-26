using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.Services;

/// <summary>Progress of one market while its prices are refreshed.</summary>
public sealed class PriceRunState(IPriceProvider provider)
{
    public IPriceProvider Provider { get; } = provider;
    public bool IsRunning { get; internal set; }
    public int Total { get; internal set; }
    public int Done { get; internal set; }

    /// <summary>Estimated time left, for markets that answer item by item.</summary>
    public TimeSpan TimeLeft => Provider.TimePerItem * Math.Max(0, Total - Done);
}

/// <summary>
/// Refreshes cached prices in the background, every market at the same time, and reports each new price as it arrives.
/// Lives for the whole app, so the work continues when the user switches tabs.
/// </summary>
public class PriceUpdater
{
    private readonly LocalStore _store;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public PriceUpdater(LocalStore store, IEnumerable<IPriceProvider> providers)
    {
        _store = store;
        Markets = providers.Select(p => new PriceRunState(p)).ToList();
    }

    /// <summary>Every market, in the order they were registered.</summary>
    public IReadOnlyList<PriceRunState> Markets { get; }

    public bool IsRunning => Markets.Any(m => m.IsRunning);

    /// <summary>Raised (on a background thread) for every price saved.</summary>
    public event EventHandler<PriceEntry>? PriceUpdated;

    /// <summary>Raised (on a background thread) when progress or running state changes.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>
    /// Fetches prices for the given items that are missing or older than each market's max age
    /// (all of them when <paramref name="force"/> is true). Replaces any run already in progress.
    /// </summary>
    public async Task StartAsync(IEnumerable<string> marketHashNames, bool force = false)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
        }

        var names = marketHashNames.Distinct().ToList();

        // Markets that send everything at once finish in seconds. Their prices then decide the order of the
        // slow markets (item by item), so the valuable items get their Steam price first.
        var bulk = Markets.Where(m => m.Provider.TimePerItem == TimeSpan.Zero).ToList();
        var slow = Markets.Except(bulk).ToList();

        await Task.WhenAll(bulk.Select(m => RunAsync(m, names, force, cts, otherPrices: null)));
        if (cts.IsCancellationRequested || slow.Count == 0)
            return;

        var all = await _store.GetAllPricesAsync();
        await Task.WhenAll(slow.Select(m => RunAsync(m, names, force, cts, BestOtherPrices(all, m.Provider.Source))));
    }

    /// <summary>Cheapest known price of each item on markets other than <paramref name="source"/>.</summary>
    private static Dictionary<string, double> BestOtherPrices(Dictionary<string, List<PriceEntry>> all, string source)
    {
        var result = new Dictionary<string, double>();
        foreach (var (name, prices) in all)
        {
            var best = prices
                .Where(p => p.Source != source && p.BestPrice is not null)
                .Select(p => p.BestPrice!.Value)
                .DefaultIfEmpty(-1)
                .Min();
            if (best >= 0)
                result[name] = best;
        }
        return result;
    }

    /// <summary>Items worth less than this elsewhere get their slow-market price refreshed less often.</summary>
    private const double CheapItemPrice = 0.10;

    /// <summary>How long the slow-market price of a cheap item stays fresh.</summary>
    private static readonly TimeSpan CheapItemMaxAge = TimeSpan.FromDays(7);

    /// <summary>Order position for items no other market prices: after items worth more than this.</summary>
    private const double UnknownItemPrice = 1.0;

    private async Task RunAsync(PriceRunState market, List<string> names, bool force, CancellationTokenSource cts,
        Dictionary<string, double>? otherPrices)
    {
        var provider = market.Provider;
        var cached = await _store.GetPricesAsync(provider.Source);
        var now = DateTime.UtcNow;

        TimeSpan MaxAge(string name) =>
            otherPrices is not null && otherPrices.TryGetValue(name, out var other) && other < CheapItemPrice
                ? CheapItemMaxAge
                : provider.MaxAge;

        var stale = names.Where(name => force || !cached.TryGetValue(name, out var p) || now - p.UpdatedAt > MaxAge(name));
        var todo = otherPrices is null
            ? stale.ToList()
            : stale
                // Most valuable first (by the other markets' price), then items never priced, then the oldest prices.
                .OrderByDescending(name => otherPrices.TryGetValue(name, out var other) ? other : UnknownItemPrice)
                .ThenBy(name => cached.TryGetValue(name, out var p) ? p.UpdatedAt : DateTime.MinValue)
                .ToList();

        if (cts.IsCancellationRequested)
            return;

        market.Total = todo.Count;
        market.Done = 0;
        market.IsRunning = todo.Count > 0;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
        if (todo.Count == 0)
            return;

        try
        {
            await Task.Run(() => provider.FetchAsync(todo, async quote =>
            {
                var entry = new PriceEntry
                {
                    Key = PriceEntry.MakeKey(provider.Source, quote.MarketHashName),
                    Source = provider.Source,
                    MarketHashName = quote.MarketHashName,
                    LowestPrice = quote.LowestPrice,
                    MedianPrice = quote.MedianPrice,
                    Volume = quote.Volume,
                    NotListed = quote.LowestPrice is null && quote.MedianPrice is null,
                    UpdatedAt = DateTime.UtcNow
                };
                await _store.SavePriceAsync(entry);
                if (cts.IsCancellationRequested)
                    return;

                market.Done++;
                PriceUpdated?.Invoke(this, entry);
                if (provider.TimePerItem > TimeSpan.Zero)
                    ProgressChanged?.Invoke(this, EventArgs.Empty);
            }, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer run took over.
        }
        catch (Exception e)
        {
            SteamDiagnostics.Write($"{provider.DisplayName}: price update failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    market.IsRunning = false;
                    ProgressChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
            _cts?.Cancel();
    }
}
