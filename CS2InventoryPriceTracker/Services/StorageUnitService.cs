using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.Services;

/// <summary>Reads storage unit contents from CS2, names the items and saves them with the inventory.</summary>
public class StorageUnitService(SteamSessionService session, ItemCatalog catalog, LocalStore store, SettingsService settings)
{
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>True when the saved Steam login belongs to the account whose inventory is shown.</summary>
    public async Task<bool> CanLoadAsync()
        => await session.HasLoginAsync() && settings.LoginSteamId is { } id && id == settings.SteamId;

    /// <summary>Downloads every storage unit and replaces the stored contents. Returns the number of storage units.</summary>
    public async Task<int> RefreshAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await _busy.WaitAsync(0, ct))
            throw new SteamException("Storage units are already being loaded.");

        try
        {
            if (settings.LoginSteamId is { } loginId && settings.SteamId is { } shownId && loginId != shownId)
                throw new SteamException("The Steam login is for a different account than the inventory shown. Log in with the same account.");

            var caskets = await session.LoadStorageUnitsAsync(progress, ct);
            progress?.Report("Naming items…");

            var units = new List<StorageUnit>();
            var entries = new Dictionary<string, InventoryEntry>();
            foreach (var casket in caskets)
            {
                var containerId = casket.Casket.Id.ToString();
                units.Add(new StorageUnit
                {
                    Id = containerId,
                    Name = casket.Casket.CustomName,
                    ItemCount = casket.Items.Count,
                    UpdatedAt = DateTime.UtcNow
                });

                foreach (var item in casket.Items)
                {
                    var resolved = await catalog.ResolveAsync(item);
                    var key = InventoryEntry.MakeKey(containerId, resolved.Marketable, resolved.MarketHashName, resolved.Name, item.CustomName);
                    if (entries.TryGetValue(key, out var existing))
                    {
                        existing.Quantity++;
                        continue;
                    }

                    entries[key] = new InventoryEntry
                    {
                        Key = key,
                        MarketHashName = resolved.MarketHashName,
                        Name = resolved.Name,
                        CustomName = item.CustomName,
                        Type = resolved.Type,
                        RarityColor = resolved.RarityColor,
                        IconPath = resolved.IconPath,
                        Quantity = 1,
                        Marketable = resolved.Marketable,
                        ContainerId = containerId
                    };
                }
            }

            await store.ReplaceStorageUnitsAsync(units, entries.Values);
            settings.StorageUpdatedAt = DateTime.UtcNow;
            return units.Count;
        }
        finally
        {
            _busy.Release();
        }
    }
}
