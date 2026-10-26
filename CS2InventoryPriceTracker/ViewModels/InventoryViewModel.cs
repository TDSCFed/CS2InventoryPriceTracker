using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2InventoryPriceTracker.Models;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

public record SortChoice(string Label, Func<IEnumerable<ItemCard>, IEnumerable<ItemCard>> Apply);

/// <summary>Where items are: everything, only the main inventory, or one storage unit.</summary>
public record LocationChoice(string Label, bool Everything, string? ContainerId);

/// <summary>Shows the CS2 inventory (and storage unit contents) with the price of each item and the total value.</summary>
public partial class InventoryViewModel : ObservableObject
{
    /// <summary>The inventory is downloaded again automatically when it is older than this.</summary>
    private static readonly TimeSpan AutoRefreshAge = TimeSpan.FromMinutes(30);

    /// <summary>Storage units are read again automatically when older than this (it briefly starts CS2 on the account).</summary>
    private static readonly TimeSpan StorageAutoRefreshAge = TimeSpan.FromHours(6);

    private readonly SettingsService _settings;
    private readonly LocalStore _store;
    private readonly SteamInventoryService _steam;
    private readonly PriceUpdater _prices;
    private readonly StorageUnitService _storage;

    private List<ItemCard> _all = new();
    private List<StorageUnit> _units = new();
    private Dictionary<string, List<ItemCard>> _byHashName = new();
    private string? _loadedSteamId;
    private string _priceSource = PriceSources.Steam;

    private string PriceSourceName => _priceSource == PriceSources.Lowest
        ? "the cheapest market"
        : _prices.Markets.FirstOrDefault(m => m.Provider.Source == _priceSource)?.Provider.DisplayName ?? _priceSource;
    private bool _pricesWereRunning;

    public InventoryViewModel(SettingsService settings, LocalStore store, SteamInventoryService steam,
        PriceUpdater prices, StorageUnitService storage)
    {
        _settings = settings;
        _store = store;
        _steam = steam;
        _prices = prices;
        _storage = storage;

        SortChoices =
        [
            new SortChoice("Value (high to low)", items => items
                .OrderByDescending(i => i.TotalValue ?? -1)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)),
            new SortChoice("Value (low to high)", items => items
                .OrderBy(i => i.TotalValue is null)
                .ThenBy(i => i.TotalValue)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)),
            new SortChoice("Unit price (high to low)", items => items
                .OrderByDescending(i => i.UnitPrice ?? -1)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)),
            new SortChoice("Name A-Z", items => items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)),
            new SortChoice("Quantity", items => items
                .OrderByDescending(i => i.Entry.Quantity)
                .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase))
        ];
        _selectedSort = SortChoices[0];
        _selectedLocation = MainInventory;
        LocationChoices.Add(MainInventory);
        LocationChoices.Add(Everything);

        _prices.PriceUpdated += OnPriceUpdated;
        _prices.ProgressChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateStatus();
            // Prices arrive one by one; sort again when a run ends so value sorting is right.
            var running = _prices.IsRunning;
            if (_pricesWereRunning && !running)
                ApplyView();
            if (!running)
                _ = RecordSnapshotsAsync();
            _pricesWereRunning = running;
        });
    }

    private static readonly LocationChoice Everything = new("Everything", true, null);
    private static readonly LocationChoice MainInventory = new("Inventory only", false, null);

    public IReadOnlyList<SortChoice> SortChoices { get; }

    public ObservableCollection<LocationChoice> LocationChoices { get; } = new();

    public ObservableCollection<ItemCard> Items { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private SortChoice _selectedSort;

    [ObservableProperty]
    private LocationChoice _selectedLocation;

    [ObservableProperty]
    private bool _showUnmarketable = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAccount))]
    private bool _hasAccount;

    public bool NeedsAccount => !HasAccount;

    [ObservableProperty]
    private string? _accountName;

    [ObservableProperty]
    private string _totalValueText = "—";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _storageStatusText;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadStorageUnitsCommand))]
    private bool _isLoadingStorage;

    partial void OnSearchTextChanged(string value) => ApplyView();

    partial void OnShowUnmarketableChanged(bool value) => ApplyView();

    partial void OnSelectedSortChanged(SortChoice value)
    {
        if (value is not null)
            ApplyView();
    }

    partial void OnSelectedLocationChanged(LocationChoice value)
    {
        if (value is not null)
        {
            UpdateTotals();
            ApplyView();
        }
    }

    /// <summary>Shows the stored inventory right away, then downloads a fresh copy if it is missing or old.</summary>
    public async Task LoadAsync()
    {
        var steamId = _settings.SteamId;
        HasAccount = steamId is not null;
        AccountName = _settings.PersonaName ?? steamId;
        if (steamId is null)
        {
            _all = new();
            _units = new();
            ApplyView();
            return;
        }

        if (_settings.PriceSource != _priceSource && _all.Count > 0)
        {
            _priceSource = _settings.PriceSource;
            foreach (var card in _all)
                card.PriceSource = _priceSource;
            UpdateTotals();
            ApplyView();
            UpdateStatus();
        }

        var accountChanged = _loadedSteamId != steamId;
        if (accountChanged || _all.Count == 0)
        {
            await ShowStoredAsync();
            _loadedSteamId = steamId;
        }

        var updatedAt = _settings.InventoryUpdatedAt;
        if (updatedAt is null || DateTime.UtcNow - updatedAt > AutoRefreshAge)
            await RefreshInventoryAsync();
        else
            StartPriceUpdates(force: false);

        var storageUpdatedAt = _settings.StorageUpdatedAt;
        if ((storageUpdatedAt is null || DateTime.UtcNow - storageUpdatedAt > StorageAutoRefreshAge)
            && await _storage.CanLoadAsync())
            _ = LoadStorageUnitsAsync();
    }

    private async Task ShowStoredAsync()
    {
        var entries = await _store.GetInventoryAsync();
        var prices = await _store.GetAllPricesAsync();
        _priceSource = _settings.PriceSource;
        _units = (await _store.GetStorageUnitsAsync())
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(u => u.ItemCount)
            .ToList();

        // Storage units without a name are told apart by a number.
        var unnamed = 0;
        var unitNames = _units.ToDictionary(
            u => u.Id,
            u => string.IsNullOrEmpty(u.Name) ? $"Storage Unit #{++unnamed}" : u.Name!);

        _all = entries
            .Select(e => new ItemCard(
                e,
                e.Marketable && prices.TryGetValue(e.MarketHashName, out var p) ? p : null,
                e.ContainerId is not null && unitNames.TryGetValue(e.ContainerId, out var name) ? name : null,
                _priceSource))
            .ToList();
        _byHashName = _all
            .Where(c => c.Entry.Marketable)
            .GroupBy(c => c.Entry.MarketHashName)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Storage units in the inventory are matched to their loaded contents by name.
        var matched = new HashSet<string>();
        foreach (var card in _all.Where(c => c.Entry.IsStorageUnit && c.Entry.ContainerId is null))
        {
            card.StorageUnitId = _units.FirstOrDefault(u => !matched.Contains(u.Id) && (u.Name ?? "") == (card.Entry.CustomName ?? ""))?.Id;
            if (card.StorageUnitId is not null)
                matched.Add(card.StorageUnitId);
        }

        // Storage units read from CS2 that the public inventory didn't list still get a card.
        foreach (var unit in _units.Where(u => !matched.Contains(u.Id)))
        {
            _all.Add(new ItemCard(new InventoryEntry
            {
                Key = $"storage|{unit.Id}",
                MarketHashName = "Storage Unit",
                Name = "Storage Unit",
                CustomName = unitNames[unit.Id],
                Type = "Storage Unit",
                IconPath = unit.IconPath,
                Quantity = 1,
                IsStorageUnit = true
            }, null, null, _priceSource) { StorageUnitId = unit.Id });
        }

        var selectedId = SelectedLocation?.ContainerId;
        var selectedEverything = SelectedLocation?.Everything ?? false;
        LocationChoices.Clear();
        LocationChoices.Add(MainInventory);
        LocationChoices.Add(Everything);
        foreach (var unit in _units)
            LocationChoices.Add(new LocationChoice($"📦 {unitNames[unit.Id]} ({unit.ItemCount})", false, unit.Id));
        SelectedLocation = LocationChoices.FirstOrDefault(l => l.Everything == selectedEverything && l.ContainerId == selectedId)
                           ?? MainInventory;

        UpdateTotals();
        ApplyView();
        UpdateStatus();
    }

    /// <summary>Pull to refresh: downloads the inventory, then the storage units when logged in.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshInventoryAsync();
        if (await _storage.CanLoadAsync())
            _ = LoadStorageUnitsAsync();
    }

    private async Task RefreshInventoryAsync()
    {
        var steamId = _settings.SteamId;
        if (steamId is null)
        {
            IsRefreshing = false;
            return;
        }

        IsRefreshing = true;
        ErrorText = null;
        try
        {
            var entries = await _steam.GetInventoryAsync(steamId);
            await _store.ReplaceMainInventoryAsync(entries);
            _settings.InventoryUpdatedAt = DateTime.UtcNow;
            await ShowStoredAsync();
            StartPriceUpdates(force: false);
        }
        catch (SteamException e)
        {
            ErrorText = e.Message;
        }
        catch (Exception)
        {
            ErrorText = "Something went wrong while loading the inventory. Pull down to try again.";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private bool CanLoadStorageUnits() => !IsLoadingStorage;

    /// <summary>Reads what is inside the storage units (needs the Steam login).</summary>
    [RelayCommand(CanExecute = nameof(CanLoadStorageUnits))]
    private async Task LoadStorageUnitsAsync()
    {
        if (IsLoadingStorage)
            return;

        IsLoadingStorage = true;
        ErrorText = null;
        var progress = new Progress<string>(text => StorageStatusText = text);
        try
        {
            var count = await Task.Run(() => _storage.RefreshAsync(progress));
            await ShowStoredAsync();
            StartPriceUpdates(force: false);
            StorageStatusText = count == 0 ? "No storage units found." : null;
        }
        catch (SteamException e)
        {
            StorageStatusText = null;
            ErrorText = e.Message;
        }
        catch (Exception e)
        {
            SteamDiagnostics.Write($"Storage unit error: {e}");
            StorageStatusText = null;
            ErrorText = "Couldn't read the storage units. Try again later.";
        }
        finally
        {
            IsLoadingStorage = false;
        }
    }

    /// <summary>Fetches every price again, ignoring the cache.</summary>
    [RelayCommand]
    private void RefreshPrices() => StartPriceUpdates(force: true);

    /// <summary>Tapping a storage unit shows what is inside it.</summary>
    [RelayCommand]
    private async Task OpenItemAsync(ItemCard card)
    {
        if (!card.Entry.IsStorageUnit)
        {
            await Shell.Current.GoToAsync(AppShell.ItemDetailsRoute, new Dictionary<string, object> { ["card"] = card });
            return;
        }

        if (card.StorageUnitId is { } id)
        {
            SelectedLocation = LocationChoices.FirstOrDefault(l => l.ContainerId == id) ?? SelectedLocation;
            return;
        }

        if (await _storage.CanLoadAsync())
            await LoadStorageUnitsAsync();
        else
            await GoToAccountAsync();
    }

    private void StartPriceUpdates(bool force)
    {
        var names = _all.Where(c => c.Entry.Marketable).Select(c => c.Entry.MarketHashName).ToList();
        _ = _prices.StartAsync(names, force);
    }

    private void OnPriceUpdated(object? sender, PriceEntry price)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_byHashName.TryGetValue(price.MarketHashName, out var cards))
                return;

            foreach (var card in cards)
                card.SetPrice(price);
            UpdateTotals();
        });
    }

    /// <summary>
    /// Saves today's value of the account and of each storage unit on every market, for the History tab.
    /// A market still refreshing keeps the value saved earlier today, so half-loaded prices are never recorded.
    /// </summary>
    private async Task RecordSnapshotsAsync()
    {
        if (!HasAccount || _all.Count == 0)
            return;

        try
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            var existing = (await _store.GetSnapshotsForDateAsync(date)).ToDictionary(s => s.Scope);
            var running = _prices.Markets.Where(m => m.IsRunning).Select(m => m.Provider.Source).ToHashSet();
            var sources = PriceSources.All.Append(PriceSources.Lowest).ToList();

            var scopes = new List<(string Scope, List<ItemCard> Cards)> { (ValueSnapshot.AccountScope, _all) };
            scopes.AddRange(_units.Select(u => (u.Id, _all.Where(c => c.Entry.ContainerId == u.Id).ToList())));

            var snapshots = new List<ValueSnapshot>();
            foreach (var (scope, cards) in scopes)
            {
                var snapshot = existing.GetValueOrDefault(scope) ?? new ValueSnapshot
                {
                    Key = ValueSnapshot.MakeKey(date, scope),
                    Date = date,
                    Scope = scope
                };

                foreach (var source in sources)
                {
                    var stillLoading = source == PriceSources.Lowest ? running.Count > 0 : running.Contains(source);
                    if (stillLoading)
                        continue;

                    var values = cards.Select(c => c.ValueFor(source)).Where(v => v is not null).ToList();
                    snapshot.SetValue(source, values.Count > 0 ? values.Sum() : null);
                }

                snapshot.UpdatedAt = DateTime.UtcNow;
                snapshots.Add(snapshot);
            }

            await _store.SaveSnapshotsAsync(snapshots);
        }
        catch (Exception e)
        {
            SteamDiagnostics.Write($"Couldn't save the value history: {e.Message}");
        }
    }

    private IEnumerable<ItemCard> InSelectedLocation()
    {
        var location = SelectedLocation ?? Everything;
        return location.Everything ? _all : _all.Where(c => c.Entry.ContainerId == location.ContainerId);
    }

    private void UpdateTotals()
    {
        // Value of each storage unit, shown on its card in the main inventory.
        var byContainer = _all
            .Where(c => c.Entry.ContainerId is not null)
            .GroupBy(c => c.Entry.ContainerId!)
            .ToDictionary(g => g.Key, g => (
                Count: g.Sum(c => c.Entry.Quantity),
                Value: g.Sum(c => c.TotalValue ?? 0),
                Marketable: g.Any(c => c.Entry.Marketable)));
        foreach (var card in _all.Where(c => c.StorageUnitId is not null))
        {
            var found = byContainer.TryGetValue(card.StorageUnitId!, out var info);
            card.StorageSummary = found ? $"{info.Count} items" : "empty";
            card.HasMarketableContents = found && info.Marketable;
            card.ContentsValue = found && info.Marketable ? info.Value : null;
        }

        // In "Everything" the items inside storage units are listed themselves, so the storage unit cards
        // are left out of the total; elsewhere a storage unit counts with the value of its contents.
        var cards = InSelectedLocation().ToList();
        var everything = SelectedLocation?.Everything ?? false;
        TotalValueText = ItemCard.FormatEuro(cards
            .Where(c => !(everything && c.Entry.IsStorageUnit))
            .Sum(c => c.TotalValue ?? 0));

        var copies = cards.Sum(c => c.Entry.Quantity);
        var marketable = cards.Where(c => c.Entry.Marketable).ToList();
        var priced = marketable.Count(c => c.HasPrice);
        SummaryText = marketable.Count == priced
            ? $"{copies} items · {cards.Count} different"
            : $"{copies} items · {priced} of {marketable.Count} prices loaded";
    }

    private void UpdateStatus()
    {
        var running = _prices.Markets.Where(m => m.IsRunning && m.Total > 0).ToList();
        if (running.Count > 0)
        {
            StatusText = "Updating prices: " + string.Join(" · ", running.Select(m =>
            {
                var text = $"{m.Provider.DisplayName} {m.Done}/{m.Total}";
                if (m.TimeLeft > TimeSpan.Zero)
                    text += $" (~{Math.Max(1, (int)Math.Ceiling(m.TimeLeft.TotalMinutes))} min left)";
                return text;
            }));
        }
        else if (_settings.InventoryUpdatedAt is { } updated)
        {
            var text = $"Inventory updated {updated.ToLocalTime():dd/MM HH:mm}";
            if (_settings.StorageUpdatedAt is { } storage)
                text += $" · storage units {storage.ToLocalTime():dd/MM HH:mm}";
            StatusText = text + $" · values from {PriceSourceName}";
        }
        else
        {
            StatusText = "";
        }
    }

    /// <summary>Applies the location, search text, marketable filter and sort order.</summary>
    private void ApplyView()
    {
        var view = InSelectedLocation();

        if (!ShowUnmarketable)
            view = view.Where(c => c.IsSellable);

        var query = SearchText?.Trim();
        if (!string.IsNullOrEmpty(query))
            view = view.Where(c =>
                c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                c.Entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                (c.Entry.Type?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false));

        view = (SelectedSort ?? SortChoices[0]).Apply(view);

        Items.Clear();
        foreach (var card in view)
            Items.Add(card);
    }

    [RelayCommand]
    private Task GoToAccountAsync() => Shell.Current.GoToAsync("//account");
}
