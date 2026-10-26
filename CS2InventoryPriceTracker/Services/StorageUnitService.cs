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

            var inventory = await session.LoadStorageUnitsAsync(progress, ct);
            progress?.Report("Naming items…");

            var units = new List<StorageUnit>();
            var entries = new Dictionary<string, InventoryEntry>();
            var details = new List<ItemDetail>();
            foreach (var casket in inventory.Caskets)
            {
                var containerId = casket.Casket.Id.ToString();
                var casketInfo = await catalog.ResolveAsync(casket.Casket);
                units.Add(new StorageUnit
                {
                    Id = containerId,
                    Name = casket.Casket.CustomName,
                    ItemCount = casket.Items.Count,
                    IconPath = casketInfo.IconPath,
                    UpdatedAt = DateTime.UtcNow
                });

                foreach (var item in casket.Items)
                {
                    var resolved = await catalog.ResolveAsync(item);
                    var key = InventoryEntry.MakeKey(containerId, resolved.Marketable, resolved.MarketHashName, resolved.Name, item.CustomName);
                    if (await ToDetailAsync(item, key) is { } detail)
                        details.Add(detail);
                    var locked = item.TradableAfter > DateTime.UtcNow;
                    if (entries.TryGetValue(key, out var existing))
                    {
                        existing.Quantity++;
                        if (locked)
                        {
                            existing.LockedCount++;
                            if (existing.LockedUntil is null || item.TradableAfter > existing.LockedUntil)
                                existing.LockedUntil = item.TradableAfter;
                        }
                        continue;
                    }

                    entries[key] = new InventoryEntry
                    {
                        Key = key,
                        MarketHashName = resolved.MarketHashName,
                        Name = resolved.Name,
                        CustomName = item.CustomName,
                        Type = resolved.Type,
                        Category = resolved.Category,
                        Rarity = resolved.Rarity,
                        RarityColor = resolved.RarityColor,
                        IconPath = resolved.IconPath,
                        Quantity = 1,
                        Marketable = resolved.Marketable,
                        LockedCount = locked ? 1 : 0,
                        LockedUntil = locked ? item.TradableAfter : null,
                        ContainerId = containerId
                    };
                }
            }

            // Items in the main inventory get their details too; their line comes from the public inventory,
            // whose key is built the same way.
            foreach (var item in inventory.InventoryItems.Where(i => i.DefIndex != ItemCatalog.StorageUnitDefIndex))
            {
                var resolved = await catalog.ResolveAsync(item);
                var key = InventoryEntry.MakeKey(null, resolved.Marketable, resolved.MarketHashName, resolved.Name, item.CustomName);
                if (await ToDetailAsync(item, key) is { } detail)
                    details.Add(detail);
            }

            await store.ReplaceStorageUnitsAsync(units, entries.Values);
            await store.ReplaceItemDetailsAsync(details);
            settings.StorageUpdatedAt = DateTime.UtcNow;
            return units.Count;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Details worth showing for one copy (null for items that have none, like cases).</summary>
    private async Task<ItemDetail?> ToDetailAsync(GcItem item, string entryKey)
    {
        var stickerNames = new List<string>();
        foreach (var kit in item.Stickers ?? [])
        {
            // A sticker item carries its own kit in slot 0; only applied stickers are listed.
            if (item.DefIndex is 1209 or 4609)
                break;
            stickerNames.Add(await catalog.StickerNameAsync(kit) ?? $"Sticker #{kit}");
        }

        var tradableAfter = item.TradableAfter > DateTime.UtcNow ? item.TradableAfter : null;
        var phase = await catalog.PhaseAsync(item.PaintIndex);
        if (item.PaintWear is null && item.PaintSeed is null && item.KillCount is null && stickerNames.Count == 0
            && tradableAfter is null)
            return null;

        return new ItemDetail
        {
            Id = item.Id.ToString(),
            EntryKey = entryKey,
            Float = item.PaintWear,
            Seed = item.PaintSeed,
            Kills = item.KillCount is { } kills ? (int)kills : null,
            TradableAfter = tradableAfter,
            Phase = phase,
            Stickers = stickerNames.Count > 0 ? string.Join("\n", stickerNames) : null,
            CustomName = item.CustomName
        };
    }
}
