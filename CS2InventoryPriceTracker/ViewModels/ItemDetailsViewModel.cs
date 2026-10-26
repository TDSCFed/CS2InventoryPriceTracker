using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2InventoryPriceTracker.Controls;
using CS2InventoryPriceTracker.Models;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

/// <summary>The price of an item on one market, with a link to it.</summary>
public record MarketPriceRow(string Market, string PriceText, string DetailText, string Url, bool IsSelected);

/// <summary>One copy of an item with its Game Coordinator details.</summary>
public record CopyRow(string Title, string Details);

/// <summary>Details of one inventory line: prices on every market, price history and the details of each copy.</summary>
public partial class ItemDetailsViewModel : ObservableObject, IQueryAttributable
{
    private readonly LocalStore _store;
    private readonly SettingsService _settings;
    private List<PriceHistoryEntry> _history = new();

    public ItemDetailsViewModel(LocalStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;
        MarketChoices =
        [
            new PriceSourceChoice("Steam Market", PriceSources.Steam),
            new PriceSourceChoice("Skinport", PriceSources.Skinport),
            new PriceSourceChoice("Market.CSGO", PriceSources.CsgoMarket)
        ];
        _selectedMarket = MarketChoices[0];
    }

    public LineChartDrawable Chart { get; } = new() { EmptyText = "No price history yet" };

    /// <summary>Raised when the chart must be redrawn.</summary>
    public event EventHandler? ChartChanged;

    public IReadOnlyList<PriceSourceChoice> MarketChoices { get; }

    public ObservableCollection<MarketPriceRow> Markets { get; } = new();

    public ObservableCollection<CopyRow> Copies { get; } = new();

    [ObservableProperty]
    private ItemCard? _card;

    [ObservableProperty]
    private PriceSourceChoice _selectedMarket;

    [ObservableProperty]
    private string _historyText = "";

    [ObservableProperty]
    private bool _hasCopies;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("card", out var value) && value is ItemCard card)
            Card = card;
    }

    partial void OnSelectedMarketChanged(PriceSourceChoice value)
    {
        if (value is not null)
            UpdateChart();
    }

    public async Task LoadAsync()
    {
        if (Card is not { } card)
            return;

        var source = _settings.PriceSource;
        SelectedMarket = MarketChoices.FirstOrDefault(m => m.Id == source) ?? MarketChoices[0];

        // Prices on every market.
        Markets.Clear();
        if (card.Entry.Marketable)
        {
            var name = card.Entry.MarketHashName;
            foreach (var market in PriceSources.All)
            {
                var price = card.PriceFor(market);
                var priceText = price is null ? "Loading…" : price.BestPrice is null ? "No listings" : ItemCard.FormatEuro(price.BestPrice);
                var details = new List<string>();
                if (price?.MedianPrice is { } median && price.LowestPrice is not null)
                    details.Add($"median {ItemCard.FormatEuro(median)}");
                if (price?.Volume is { } volume)
                    details.Add(market == PriceSources.Steam ? $"{volume} sold in 24 h" : $"{volume} listed");
                if (price is not null)
                    details.Add($"updated {price.UpdatedAt.ToLocalTime():dd/MM HH:mm}");

                Markets.Add(new MarketPriceRow(
                    PriceSources.ShortName(market),
                    priceText,
                    string.Join(" · ", details),
                    MarketUrl(market, name),
                    source == market || (source == PriceSources.Lowest && card.Price?.Source == market)));
            }

            _history = await _store.GetPriceHistoryAsync(name);
        }
        else
        {
            _history = new();
        }
        UpdateChart();

        // Every copy, with float, pattern, StatTrak and stickers when CS2 sent them.
        Copies.Clear();
        var copies = await _store.GetItemDetailsAsync(card.Entry.Key);
        var index = 0;
        foreach (var copy in copies.OrderBy(c => c.Float ?? 2))
        {
            index++;
            var lines = new List<string>();
            if (copy.Float is { } f)
                lines.Add($"Float {f.ToString("0.00000000", CultureInfo.InvariantCulture)}");
            if (copy.Seed is { } seed)
                lines.Add($"Pattern {seed}");
            if (copy.Kills is { } kills)
                lines.Add($"StatTrak™ {kills} kills");
            if (!string.IsNullOrEmpty(copy.Stickers))
                lines.Add(copy.Stickers);

            var title = !string.IsNullOrEmpty(copy.CustomName) ? $"“{copy.CustomName}”" : $"Copy {index}";
            Copies.Add(new CopyRow(title, string.Join("\n", lines)));
        }
        HasCopies = Copies.Count > 0;
    }

    private void UpdateChart()
    {
        var source = SelectedMarket?.Id ?? PriceSources.Steam;
        var points = _history
            .Where(h => h.Source == source)
            .Select(h => new ChartPoint(DateTime.ParseExact(h.Date, "yyyy-MM-dd", null), h.Price))
            .ToList();

        Chart.Points = points;
        ChartChanged?.Invoke(this, EventArgs.Empty);

        HistoryText = points.Count switch
        {
            0 => "",
            1 => $"Since {points[0].Date:dd/MM}",
            _ => $"Since {points[0].Date:dd/MM} · high {ItemCard.FormatEuro(points.Max(p => p.Value))} · low {ItemCard.FormatEuro(points.Min(p => p.Value))}"
        };
    }

    [RelayCommand]
    private Task OpenMarketAsync(MarketPriceRow row) => Launcher.Default.OpenAsync(row.Url);

    private static string MarketUrl(string market, string name)
    {
        var escaped = Uri.EscapeDataString(name);
        return market switch
        {
            PriceSources.Steam => $"https://steamcommunity.com/market/listings/730/{escaped}",
            PriceSources.Skinport => $"https://skinport.com/market?search={escaped}",
            PriceSources.CsgoMarket => $"https://market.csgo.com/en/?search={escaped}",
            _ => ""
        };
    }
}
