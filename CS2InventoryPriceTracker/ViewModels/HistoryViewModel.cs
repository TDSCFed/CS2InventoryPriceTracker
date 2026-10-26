using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CS2InventoryPriceTracker.Controls;
using CS2InventoryPriceTracker.Models;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

public record RangeChoice(string Label, int? Days);

public record ScopeChoice(string Label, string Scope);

/// <summary>Value of the account (or of one storage unit) over time, from the daily snapshots.</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly SettingsService _settings;
    private List<ValueSnapshot> _snapshots = new();
    private bool _loading;

    public HistoryViewModel(LocalStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;

        RangeChoices =
        [
            new RangeChoice("7 days", 7),
            new RangeChoice("30 days", 30),
            new RangeChoice("90 days", 90),
            new RangeChoice("All", null)
        ];
        MarketChoices =
        [
            new PriceSourceChoice("Steam Market", PriceSources.Steam),
            new PriceSourceChoice("Skinport", PriceSources.Skinport),
            new PriceSourceChoice("Market.CSGO", PriceSources.CsgoMarket),
            new PriceSourceChoice("Cheapest market", PriceSources.Lowest)
        ];
        _selectedRange = RangeChoices[1];
        _selectedMarket = MarketChoices[0];
        _selectedScope = new ScopeChoice("Whole account", ValueSnapshot.AccountScope);
        ScopeChoices.Add(_selectedScope);
    }

    public IReadOnlyList<RangeChoice> RangeChoices { get; }

    public IReadOnlyList<PriceSourceChoice> MarketChoices { get; }

    public ObservableCollection<ScopeChoice> ScopeChoices { get; } = new();

    public LineChartDrawable Chart { get; } = new() { EmptyText = "No history yet. A point is saved each day the app updates prices." };

    /// <summary>Raised when the chart must be redrawn.</summary>
    public event EventHandler? ChartChanged;

    [ObservableProperty]
    private RangeChoice _selectedRange;

    [ObservableProperty]
    private PriceSourceChoice _selectedMarket;

    [ObservableProperty]
    private ScopeChoice _selectedScope;

    [ObservableProperty]
    private string _currentValueText = "—";

    [ObservableProperty]
    private string _changeText = "";

    [ObservableProperty]
    private Color _changeColor = Colors.Gray;

    [ObservableProperty]
    private string _summaryText = "";

    partial void OnSelectedRangeChanged(RangeChoice value)
    {
        if (value is not null)
            UpdateChart();
    }

    partial void OnSelectedMarketChanged(PriceSourceChoice value)
    {
        if (value is not null)
            UpdateChart();
    }

    partial void OnSelectedScopeChanged(ScopeChoice value)
    {
        if (value is not null && !_loading)
            _ = LoadSnapshotsAsync();
    }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            SelectedMarket = MarketChoices.FirstOrDefault(m => m.Id == _settings.PriceSource) ?? MarketChoices[0];

            // Storage units, numbered like in the Inventory tab when they have no name.
            var selected = SelectedScope?.Scope ?? ValueSnapshot.AccountScope;
            var units = (await _store.GetStorageUnitsAsync())
                .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(u => u.ItemCount)
                .ToList();
            var unnamed = 0;
            ScopeChoices.Clear();
            ScopeChoices.Add(new ScopeChoice("Whole account", ValueSnapshot.AccountScope));
            foreach (var unit in units)
            {
                var name = string.IsNullOrEmpty(unit.Name) ? $"Storage Unit #{++unnamed}" : unit.Name;
                ScopeChoices.Add(new ScopeChoice($"📦 {name}", unit.Id));
            }
            SelectedScope = ScopeChoices.FirstOrDefault(s => s.Scope == selected) ?? ScopeChoices[0];
        }
        finally
        {
            _loading = false;
        }

        await LoadSnapshotsAsync();
    }

    private async Task LoadSnapshotsAsync()
    {
        _snapshots = await _store.GetSnapshotsAsync(SelectedScope?.Scope ?? ValueSnapshot.AccountScope);
        UpdateChart();
    }

    private void UpdateChart()
    {
        var source = SelectedMarket?.Id ?? PriceSources.Steam;
        var points = _snapshots
            .Select(s => (Date: DateTime.ParseExact(s.Date, "yyyy-MM-dd", null), Value: s.ValueFor(source)))
            .Where(p => p.Value is not null)
            .Select(p => new ChartPoint(p.Date, p.Value!.Value))
            .ToList();

        if (SelectedRange?.Days is { } days)
        {
            var from = DateTime.Today.AddDays(-days);
            points = points.Where(p => p.Date >= from).ToList();
        }

        Chart.Points = points;
        ChartChanged?.Invoke(this, EventArgs.Empty);

        if (points.Count == 0)
        {
            CurrentValueText = "—";
            ChangeText = "";
            SummaryText = "";
            return;
        }

        var first = points[0];
        var last = points[^1];
        CurrentValueText = ItemCard.FormatEuro(last.Value);

        var change = last.Value - first.Value;
        var percent = first.Value > 0 ? change / first.Value * 100 : 0;
        var sign = change >= 0 ? "+" : "−";
        ChangeText = points.Count > 1
            ? $"{sign}{ItemCard.FormatEuro(Math.Abs(change))} ({sign}{Math.Abs(percent):0.0}%) since {first.Date:dd/MM}"
            : "First day recorded";
        ChangeColor = points.Count < 2 || Math.Abs(change) < 0.005
            ? Colors.Gray
            : change > 0 ? Color.FromArgb("#4CC38A") : Color.FromArgb("#E5484D");

        SummaryText = $"High {ItemCard.FormatEuro(points.Max(p => p.Value))} · Low {ItemCard.FormatEuro(points.Min(p => p.Value))} · {points.Count} days";
    }
}
