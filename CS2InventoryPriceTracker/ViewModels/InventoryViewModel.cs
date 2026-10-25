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
        _selectedLocation = Everything;
        LocationChoices.Add(Everything);

        _prices.PriceUpdated += OnPriceUpdated;
        _prices.ProgressChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateStatus);
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
            ApplyView();
            UpdateTotals();
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
        var prices = await _store.GetPricesAsync(_prices.Primary.Source);
        _units = (await _store.GetStorageUnitsAsync())
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var unitNames = _units.ToDictionary(u => u.Id, u => u.DisplayName);

        _all = entries
            .Select(e => new ItemCard(
                e,
                e.Marketable && prices.TryGetValue(e.MarketHashName, out var p) ? p : null,
                e.ContainerId is not null && unitNames.TryGetValue(e.ContainerId, out var name) ? name : null))
            .ToList();
        _byHashName = _all
            .Where(c => c.Entry.Marketable)
            .GroupBy(c => c.Entry.MarketHashName)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Storage units in the inventory are matched to their loaded contents by name.
        foreach (var card in _all.Where(c => c.Entry.IsStorageUnit && c.Entry.ContainerId is null))
            card.StorageUnitId = _units.FirstOrDefault(u => (u.Name ?? "") == (card.Entry.CustomName ?? ""))?.Id;

        var selectedId = SelectedLocation?.ContainerId;
        var selectedEverything = SelectedLocation?.Everything ?? true;
        LocationChoices.Clear();
        LocationChoices.Add(Everything);
        if (_units.Count > 0)
        {
            LocationChoices.Add(MainInventory);
            foreach (var unit in _units)
                LocationChoices.Add(new LocationChoice($"📦 {unit.DisplayName} ({unit.ItemCount})", false, unit.Id));
        }
        SelectedLocation = LocationChoices.FirstOrDefault(l => l.Everything == selectedEverything && l.ContainerId == selectedId)
                           ?? Everything;

        ApplyView();
        UpdateTotals();
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
        catch (Exception)
        {
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
            return;

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
                card.Price = price;
            UpdateTotals();
        });
    }

    private IEnumerable<ItemCard> InSelectedLocation()
    {
        var location = SelectedLocation ?? Everything;
        return location.Everything ? _all : _all.Where(c => c.Entry.ContainerId == location.ContainerId);
    }

    private void UpdateTotals()
    {
        var cards = InSelectedLocation().ToList();
        TotalValueText = ItemCard.FormatEuro(cards.Sum(c => c.TotalValue ?? 0));

        var copies = cards.Sum(c => c.Entry.Quantity);
        var marketable = cards.Where(c => c.Entry.Marketable).ToList();
        var priced = marketable.Count(c => c.Price is not null);
        SummaryText = marketable.Count == priced
            ? $"{copies} items · {cards.Count} different"
            : $"{copies} items · {priced} of {marketable.Count} prices loaded";

        // Value of each storage unit, shown on its card in the main inventory.
        var byContainer = _all
            .Where(c => c.Entry.ContainerId is not null)
            .GroupBy(c => c.Entry.ContainerId!)
            .ToDictionary(g => g.Key, g => (Count: g.Sum(c => c.Entry.Quantity), Value: g.Sum(c => c.TotalValue ?? 0)));
        foreach (var card in _all.Where(c => c.StorageUnitId is not null))
        {
            card.StorageSummary = byContainer.TryGetValue(card.StorageUnitId!, out var info)
                ? $"{info.Count} items · {ItemCard.FormatEuro(info.Value)}"
                : "empty";
        }
    }

    private void UpdateStatus()
    {
        if (_prices.IsRunning && _prices.Total > 0)
        {
            var left = _prices.Total - _prices.Done;
            // About 3.5 seconds per Steam Market request.
            var minutes = Math.Max(1, (int)Math.Ceiling(left * 3.5 / 60));
            StatusText = $"Updating {_prices.Primary.DisplayName} prices: {_prices.Done}/{_prices.Total} (~{minutes} min left)";
        }
        else if (_settings.InventoryUpdatedAt is { } updated)
        {
            var text = $"Inventory updated {updated.ToLocalTime():dd/MM HH:mm}";
            if (_settings.StorageUpdatedAt is { } storage)
                text += $" · storage units {storage.ToLocalTime():dd/MM HH:mm}";
            StatusText = text + $" · prices from {_prices.Primary.DisplayName}";
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
            view = view.Where(c => c.Entry.Marketable);

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
