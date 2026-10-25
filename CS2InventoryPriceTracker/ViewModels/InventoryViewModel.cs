using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2InventoryPriceTracker.Models;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

public record SortChoice(string Label, Func<IEnumerable<ItemCard>, IEnumerable<ItemCard>> Apply);

/// <summary>Shows the CS2 inventory with the price of each item and the total value.</summary>
public partial class InventoryViewModel : ObservableObject
{
    /// <summary>The inventory is downloaded again automatically when it is older than this.</summary>
    private static readonly TimeSpan AutoRefreshAge = TimeSpan.FromMinutes(30);

    private readonly SettingsService _settings;
    private readonly LocalStore _store;
    private readonly SteamInventoryService _steam;
    private readonly PriceUpdater _prices;

    private List<ItemCard> _all = new();
    private Dictionary<string, List<ItemCard>> _byHashName = new();
    private string? _loadedSteamId;

    public InventoryViewModel(SettingsService settings, LocalStore store, SteamInventoryService steam, PriceUpdater prices)
    {
        _settings = settings;
        _store = store;
        _steam = steam;
        _prices = prices;

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

        _prices.PriceUpdated += OnPriceUpdated;
        _prices.ProgressChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateStatus);
    }

    public IReadOnlyList<SortChoice> SortChoices { get; }

    public ObservableCollection<ItemCard> Items { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private SortChoice _selectedSort;

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
    private string? _errorText;

    [ObservableProperty]
    private bool _isRefreshing;

    partial void OnSearchTextChanged(string value) => ApplyView();

    partial void OnShowUnmarketableChanged(bool value) => ApplyView();

    partial void OnSelectedSortChanged(SortChoice value)
    {
        if (value is not null)
            ApplyView();
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
            await RefreshAsync();
        else
            StartPriceUpdates(force: false);
    }

    private async Task ShowStoredAsync()
    {
        var entries = await _store.GetInventoryAsync();
        var prices = await _store.GetPricesAsync(_prices.Primary.Source);

        _all = entries
            .Select(e => new ItemCard(e, e.Marketable && prices.TryGetValue(e.MarketHashName, out var p) ? p : null))
            .ToList();
        _byHashName = _all
            .Where(c => c.Entry.Marketable)
            .GroupBy(c => c.Entry.MarketHashName)
            .ToDictionary(g => g.Key, g => g.ToList());

        ApplyView();
        UpdateTotals();
        UpdateStatus();
    }

    /// <summary>Downloads the inventory from Steam (pull to refresh).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
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

    /// <summary>Fetches every price again, ignoring the cache.</summary>
    [RelayCommand]
    private void RefreshPrices() => StartPriceUpdates(force: true);

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

    private void UpdateTotals()
    {
        var total = _all.Sum(c => c.TotalValue ?? 0);
        TotalValueText = ItemCard.FormatEuro(total);

        var copies = _all.Sum(c => c.Entry.Quantity);
        var marketable = _all.Where(c => c.Entry.Marketable).ToList();
        var priced = marketable.Count(c => c.Price is not null);
        SummaryText = marketable.Count == priced
            ? $"{copies} items · {_all.Count} different"
            : $"{copies} items · {priced} of {marketable.Count} prices loaded";
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
            StatusText = $"Inventory updated {updated.ToLocalTime():dd/MM HH:mm} · prices from {_prices.Primary.DisplayName}";
        }
        else
        {
            StatusText = "";
        }
    }

    /// <summary>Applies the search text, the marketable filter and the sort order.</summary>
    private void ApplyView()
    {
        IEnumerable<ItemCard> view = _all;

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
