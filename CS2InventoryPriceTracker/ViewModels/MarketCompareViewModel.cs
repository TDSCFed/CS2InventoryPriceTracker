using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2InventoryPriceTracker.Models;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

/// <summary>An item whose price differs between markets.</summary>
public class MarketGapRow(ItemCard card, string cheapMarket, double cheapPrice, string highMarket, double highPrice, int quantity)
{
    public ItemCard Card { get; } = card;
    public string Title => Card.Title;
    public string? IconUrl => Card.IconUrl;
    public Color RarityColor => Card.RarityColor;
    public int Quantity { get; } = quantity;

    public double CheapPrice { get; } = cheapPrice;
    public double HighPrice { get; } = highPrice;
    public double Gap => HighPrice - CheapPrice;
    public double Percent => CheapPrice > 0 ? Gap / CheapPrice * 100 : 0;

    /// <summary>Difference for every copy owned.</summary>
    public double TotalGap => Gap * Quantity;

    public string HighText => $"Highest: {PriceSources.ShortName(highMarket)} {ItemCard.FormatEuro(HighPrice)}";
    public string CheapText => $"Lowest: {PriceSources.ShortName(cheapMarket)} {ItemCard.FormatEuro(CheapPrice)}";
    public string GapText => $"+{ItemCard.FormatEuro(Gap)} (+{Percent:0}%)";
    public string QuantityText => Quantity > 1 ? $"×{Quantity} · +{ItemCard.FormatEuro(TotalGap)} in total" : "";
    public bool HasQuantity => Quantity > 1;
}

public record GapSortChoice(string Label, Func<IEnumerable<MarketGapRow>, IEnumerable<MarketGapRow>> Apply);

/// <summary>
/// Items whose price differs most between the Steam Market, Skinport and Market.CSGO,
/// to see where an item sells for more.
/// </summary>
public partial class MarketCompareViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly SettingsService _settings;
    private List<MarketGapRow> _all = new();

    public MarketCompareViewModel(LocalStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;
        SortChoices =
        [
            new GapSortChoice("Biggest difference (€)", rows => rows.OrderByDescending(r => r.TotalGap)),
            new GapSortChoice("Biggest difference (%)", rows => rows.OrderByDescending(r => r.Percent)),
            new GapSortChoice("Most valuable", rows => rows.OrderByDescending(r => r.HighPrice * r.Quantity))
        ];
        MinPriceChoices =
        [
            new FilterChoice("Items from 0,10 €", "0.10"),
            new FilterChoice("Items from 1 €", "1"),
            new FilterChoice("Items from 10 €", "10"),
            new FilterChoice("All items", "0")
        ];
        _selectedSort = SortChoices[0];
        _selectedMinPrice = MinPriceChoices[0];
    }

    public IReadOnlyList<GapSortChoice> SortChoices { get; }

    public IReadOnlyList<FilterChoice> MinPriceChoices { get; }

    public ObservableCollection<MarketGapRow> Rows { get; } = new();

    [ObservableProperty]
    private GapSortChoice _selectedSort;

    [ObservableProperty]
    private FilterChoice _selectedMinPrice;

    /// <summary>Compare Steam at what the seller receives (after Steam's 15% fee) instead of the listing price.</summary>
    [ObservableProperty]
    private bool _steamAfterFee;

    [ObservableProperty]
    private string _summaryText = "";

    partial void OnSelectedSortChanged(GapSortChoice value) => ApplyView();

    partial void OnSelectedMinPriceChanged(FilterChoice value) => ApplyView();

    partial void OnSteamAfterFeeChanged(bool value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        var entries = await _store.GetInventoryAsync();
        var prices = await _store.GetAllPricesAsync();

        // One row per item, whatever its location (inventory or storage units).
        var rows = new List<MarketGapRow>();
        foreach (var group in entries.Where(e => e.Marketable && !e.IsStorageUnit).GroupBy(e => e.MarketHashName))
        {
            if (!prices.TryGetValue(group.Key, out var itemPrices))
                continue;

            var quotes = itemPrices
                .Where(p => p.BestPrice is > 0)
                .Select(p => (p.Source, Price: p.Source == PriceSources.Steam && SteamAfterFee ? p.BestPrice!.Value / 1.15 : p.BestPrice!.Value))
                .ToList();
            if (quotes.Count < 2)
                continue;

            var cheap = quotes.MinBy(q => q.Price);
            var high = quotes.MaxBy(q => q.Price);
            if (high.Price - cheap.Price < 0.005)
                continue;

            var first = group.First();
            var card = new ItemCard(first, itemPrices, null, _settings.PriceSource);
            rows.Add(new MarketGapRow(card, cheap.Source, cheap.Price, high.Source, high.Price, group.Sum(e => e.Quantity)));
        }

        _all = rows;
        ApplyView();
    }

    private void ApplyView()
    {
        var min = double.TryParse(SelectedMinPrice?.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
        var view = (SelectedSort ?? SortChoices[0]).Apply(_all.Where(r => r.HighPrice >= min)).ToList();

        Rows.Clear();
        foreach (var row in view.Take(200))
            Rows.Add(row);

        SummaryText = view.Count == 0
            ? ""
            : $"{view.Count} items · selling every copy on its highest market instead of its lowest: +{ItemCard.FormatEuro(view.Sum(r => r.TotalGap))}";
    }

    [RelayCommand]
    private Task OpenAsync(MarketGapRow row)
        => Shell.Current.GoToAsync(AppShell.ItemDetailsRoute, new Dictionary<string, object> { ["card"] = row.Card });
}
