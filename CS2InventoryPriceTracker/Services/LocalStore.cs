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
    }

    /// <summary>All cached prices of one market, by market hash name.</summary>
    public async Task<Dictionary<string, PriceEntry>> GetPricesAsync(string source)
    {
        var db = await GetDbAsync();
        var rows = await db.Table<PriceEntry>().Where(p => p.Source == source).ToListAsync();
        return rows.ToDictionary(p => p.MarketHashName);
    }

    public async Task SavePriceAsync(PriceEntry price)
    {
        var db = await GetDbAsync();
        await db.InsertOrReplaceAsync(price);
    }
}
