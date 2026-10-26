using CS2InventoryPriceTracker.Models;
using SQLite;

namespace CS2InventoryPriceTracker.Services;

/// <summary>Local storage (SQLite) for the last downloaded inventory and the price cache.</summary>
public class LocalStore
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private SQLiteAsyncConnection? _db;

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db is not null)
            return _db;

        await _initLock.WaitAsync();
        try
        {
            if (_db is null)
            {
                var path = Path.Combine(FileSystem.AppDataDirectory, "inventory.db3");
                var db = new SQLiteAsyncConnection(path);
                await db.CreateTableAsync<InventoryEntry>();
                await db.CreateTableAsync<PriceEntry>();
                await db.CreateTableAsync<StorageUnit>();
                await db.CreateTableAsync<ValueSnapshot>();
                await db.CreateTableAsync<PriceHistoryEntry>();
                await db.CreateTableAsync<ItemDetail>();
                _db = db;
            }
            return _db;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<List<InventoryEntry>> GetInventoryAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<InventoryEntry>().ToListAsync();
    }

    /// <summary>Replaces the items of the main inventory (items inside storage units are kept).</summary>
    public async Task ReplaceMainInventoryAsync(IEnumerable<InventoryEntry> entries)
    {
        var db = await GetDbAsync();
        var list = entries.ToList();
        await db.RunInTransactionAsync(conn =>
        {
            conn.Execute("DELETE FROM inventory_items WHERE ContainerId IS NULL");
            foreach (var entry in list)
                conn.InsertOrReplace(entry);
        });
    }

    /// <summary>Replaces all storage units and the items inside them.</summary>
    public async Task ReplaceStorageUnitsAsync(IEnumerable<StorageUnit> units, IEnumerable<InventoryEntry> contents)
    {
        var db = await GetDbAsync();
        var unitList = units.ToList();
        var contentList = contents.ToList();
        await db.RunInTransactionAsync(conn =>
        {
            conn.Execute("DELETE FROM inventory_items WHERE ContainerId IS NOT NULL");
            conn.DeleteAll<StorageUnit>();
            foreach (var unit in unitList)
                conn.InsertOrReplace(unit);
            foreach (var entry in contentList)
                conn.InsertOrReplace(entry);
        });
    }

    public async Task<List<StorageUnit>> GetStorageUnitsAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<StorageUnit>().ToListAsync();
    }

    /// <summary>Removes every stored item and storage unit (used when switching to another Steam account).</summary>
    public async Task ClearInventoryAsync()
    {
        var db = await GetDbAsync();
        await db.DeleteAllAsync<InventoryEntry>();
        await db.DeleteAllAsync<StorageUnit>();
        await db.DeleteAllAsync<ItemDetail>();
    }

    /// <summary>All cached prices of one market, by market hash name.</summary>
    public async Task<Dictionary<string, PriceEntry>> GetPricesAsync(string source)
    {
        var db = await GetDbAsync();
        var rows = await db.Table<PriceEntry>().Where(p => p.Source == source).ToListAsync();
        return rows.ToDictionary(p => p.MarketHashName);
    }

    /// <summary>Cached prices of every market, grouped by market hash name.</summary>
    public async Task<Dictionary<string, List<PriceEntry>>> GetAllPricesAsync()
    {
        var db = await GetDbAsync();
        var rows = await db.Table<PriceEntry>().ToListAsync();
        return rows.GroupBy(p => p.MarketHashName).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Saves a price, and today's point of its price history.</summary>
    public async Task SavePriceAsync(PriceEntry price)
    {
        var db = await GetDbAsync();
        await db.InsertOrReplaceAsync(price);

        if (price.BestPrice is { } best)
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            await db.InsertOrReplaceAsync(new PriceHistoryEntry
            {
                Key = PriceHistoryEntry.MakeKey(price.Source, price.MarketHashName, date),
                Source = price.Source,
                MarketHashName = price.MarketHashName,
                Date = date,
                Price = best
            });
        }
    }

    /// <summary>Daily prices of one item on every market, oldest first.</summary>
    public async Task<List<PriceHistoryEntry>> GetPriceHistoryAsync(string marketHashName)
    {
        var db = await GetDbAsync();
        var rows = await db.Table<PriceHistoryEntry>().Where(p => p.MarketHashName == marketHashName).ToListAsync();
        return rows.OrderBy(p => p.Date, StringComparer.Ordinal).ToList();
    }

    public async Task SaveSnapshotsAsync(IEnumerable<ValueSnapshot> snapshots)
    {
        var db = await GetDbAsync();
        var list = snapshots.ToList();
        await db.RunInTransactionAsync(conn =>
        {
            foreach (var snapshot in list)
                conn.InsertOrReplace(snapshot);
        });
    }

    /// <summary>Replaces every item detail read from the Game Coordinator.</summary>
    public async Task ReplaceItemDetailsAsync(IEnumerable<ItemDetail> details)
    {
        var db = await GetDbAsync();
        var list = details.ToList();
        await db.RunInTransactionAsync(conn =>
        {
            conn.DeleteAll<ItemDetail>();
            foreach (var detail in list)
                conn.InsertOrReplace(detail);
        });
    }

    /// <summary>Details of every copy of one inventory line.</summary>
    public async Task<List<ItemDetail>> GetItemDetailsAsync(string entryKey)
    {
        var db = await GetDbAsync();
        return await db.Table<ItemDetail>().Where(d => d.EntryKey == entryKey).ToListAsync();
    }

    /// <summary>Every snapshot of one day.</summary>
    public async Task<List<ValueSnapshot>> GetSnapshotsForDateAsync(string date)
    {
        var db = await GetDbAsync();
        return await db.Table<ValueSnapshot>().Where(s => s.Date == date).ToListAsync();
    }

    /// <summary>Daily values of one scope (the account or a storage unit), oldest first.</summary>
    public async Task<List<ValueSnapshot>> GetSnapshotsAsync(string scope)
    {
        var db = await GetDbAsync();
        var rows = await db.Table<ValueSnapshot>().Where(s => s.Scope == scope).ToListAsync();
        return rows.OrderBy(s => s.Date, StringComparer.Ordinal).ToList();
    }
}
