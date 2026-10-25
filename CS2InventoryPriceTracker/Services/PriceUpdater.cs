using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.Services;

/// <summary>
/// Refreshes cached prices in the background, one market at a time, and reports each new price as it arrives.
/// Lives for the whole app, so the work continues when the user switches tabs.
/// </summary>
public class PriceUpdater(LocalStore store, IEnumerable<IPriceProvider> providers)
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    /// <summary>The market used for totals (the first registered one, Steam Market).</summary>
    public IPriceProvider Primary { get; } = providers.First();

    public bool IsRunning { get; private set; }
    public int Total { get; private set; }
    public int Done { get; private set; }

    /// <summary>Raised (on a background thread) for every price saved.</summary>
    public event EventHandler<PriceEntry>? PriceUpdated;

    /// <summary>Raised (on a background thread) when progress or running state changes.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>
    /// Fetches prices for the given items that are missing or older than the market's max age
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

        var cached = await store.GetPricesAsync(Primary.Source);
        var now = DateTime.UtcNow;
        var todo = marketHashNames
            .Distinct()
            .Where(name => force || !cached.TryGetValue(name, out var p) || now - p.UpdatedAt > Primary.MaxAge)
            // Items never priced first, then the oldest prices.
            .OrderBy(name => cached.TryGetValue(name, out var p) ? p.UpdatedAt : DateTime.MinValue)
            .ToList();

        if (cts.IsCancellationRequested)
            return;

        Total = todo.Count;
        Done = 0;
        IsRunning = todo.Count > 0;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
        if (todo.Count == 0)
            return;

        try
        {
            await Task.Run(() => Primary.FetchAsync(todo, async quote =>
            {
                var entry = new PriceEntry
                {
                    Key = PriceEntry.MakeKey(Primary.Source, quote.MarketHashName),
                    Source = Primary.Source,
                    MarketHashName = quote.MarketHashName,
                    LowestPrice = quote.LowestPrice,
                    MedianPrice = quote.MedianPrice,
                    Volume = quote.Volume,
                    NotListed = quote.LowestPrice is null && quote.MedianPrice is null,
                    UpdatedAt = DateTime.UtcNow
                };
                await store.SavePriceAsync(entry);
                if (cts.IsCancellationRequested)
                    return;

                Done++;
                PriceUpdated?.Invoke(this, entry);
                ProgressChanged?.Invoke(this, EventArgs.Empty);
            }, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer run took over.
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    IsRunning = false;
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
