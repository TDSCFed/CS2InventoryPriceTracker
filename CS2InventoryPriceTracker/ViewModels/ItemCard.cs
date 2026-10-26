using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CS2InventoryPriceTracker.Models;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

/// <summary>An inventory line shown in the Inventory page.</summary>
public partial class ItemCard : ObservableObject
{
    public ItemCard(InventoryEntry entry, IEnumerable<PriceEntry>? prices, string? location, string priceSource)
    {
        Entry = entry;
        Location = location;
        _lockedCount = entry.LockedCount;
        _lockedUntil = entry.LockedUntil;
        _priceSource = priceSource;
        foreach (var price in prices ?? [])
            _prices[price.Source] = price;
    }

    public InventoryEntry Entry { get; }

    /// <summary>Name of the storage unit holding the item, or null for the main inventory.</summary>
    public string? Location { get; }

    /// <summary>For a storage unit in the main inventory: the id of its loaded contents.</summary>
    public string? StorageUnitId { get; set; }

    public string Title => string.IsNullOrEmpty(Entry.CustomName) ? Entry.Name : $"“{Entry.CustomName}”";

    /// <summary>For a storage unit: value of everything inside it, once its contents are loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPrice), nameof(TotalValue), nameof(PriceText), nameof(HasTotal), nameof(TotalText))]
    private double? _contentsValue;

    /// <summary>For a storage unit: true when at least one item inside it can be sold.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSellable))]
    private bool _hasMarketableContents;

    /// <summary>Can be sold: a marketable item, or a storage unit with marketable items inside.</summary>
    public bool IsSellable => Entry.Marketable || (Entry.IsStorageUnit && HasMarketableContents);

    /// <summary>Copies still trade protected and when the last one unlocks (UTC; null when unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockText), nameof(HasLock))]
    private int _lockedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockText), nameof(HasLock))]
    private DateTime? _lockedUntil;

    /// <summary>True while at least one copy can't be traded or sold.</summary>
    public bool HasLock => LockedCount > 0 && (LockedUntil is null || LockedUntil > DateTime.UtcNow);

    /// <summary>E.g. "Trade locked until 02/11 08:00" or "2 of 5 trade locked until 02/11 08:00".</summary>
    public string LockText
    {
        get
        {
            if (!HasLock)
                return "";
            var who = Entry.Quantity > 1 ? $"{Math.Min(LockedCount, Entry.Quantity)} of {Entry.Quantity} trade locked" : "Trade locked";
            return LockedUntil is { } until ? $"🔒 {who} until {until.ToLocalTime():dd/MM HH:mm}" : $"🔒 {who}";
        }
    }

    /// <summary>Doppler phases of the copies, e.g. "💎 Ruby" or "💎 Phase 2 ×2 · Sapphire" (from CS2, with the Steam login).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhase))]
    private string? _phaseText;

    public bool HasPhase => !string.IsNullOrEmpty(PhaseText);

    /// <summary>Builds <see cref="PhaseText"/> from the phase of each copy, rarest phases first.</summary>
    public void SetPhases(IEnumerable<string> phases)
    {
        string[] order = ["Ruby", "Sapphire", "Black Pearl", "Emerald", "Phase 2", "Phase 4", "Phase 1", "Phase 3"];
        var parts = phases
            .GroupBy(p => p)
            .OrderBy(g => Array.IndexOf(order, g.Key) is var i && i >= 0 ? i : order.Length)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
            .ToList();
        PhaseText = parts.Count > 0 ? "💎 " + string.Join(" · ", parts) : null;
    }

    /// <summary>For a storage unit: true when it is close to the 1000 item limit.</summary>
    [ObservableProperty]
    private bool _isAlmostFull;

    /// <summary>For a storage unit: "12 items" once its contents are loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string? _storageSummary;

    public string Subtitle
    {
        get
        {
            if (Entry.IsStorageUnit)
                return StorageSummary is null
                    ? "Storage unit · log in to Steam to see what's inside"
                    : $"Storage unit · {StorageSummary} · tap to open";

            var text = Entry.Type ?? "";
            if (!string.IsNullOrEmpty(Entry.CustomName))
                text = string.IsNullOrEmpty(text) ? Entry.Name : $"{Entry.Name} · {text}";
            if (Location is not null)
                text = string.IsNullOrEmpty(text) ? $"In {Location}" : $"In {Location} · {text}";
            return text;
        }
    }

    public string? IconUrl => Entry.IconUrl;

    public string QuantityText => Entry.Quantity > 1 ? $"×{Entry.Quantity}" : "";

    public bool HasQuantity => Entry.Quantity > 1;

    public Color RarityColor
    {
        get
        {
            if (!string.IsNullOrEmpty(Entry.RarityColor) && Color.TryParse("#" + Entry.RarityColor, out var color))
                return color;
            return Colors.Gray;
        }
    }

    private readonly Dictionary<string, PriceEntry> _prices = new();
    private string _priceSource;

    /// <summary>Market used for this card's value (see <see cref="PriceSources"/>), or "lowest" for the cheapest market.</summary>
    public string PriceSource
    {
        get => _priceSource;
        set
        {
            if (_priceSource == value)
                return;
            _priceSource = value;
            RaisePriceChanged();
        }
    }

    /// <summary>Saves a new price from one market.</summary>
    public void SetPrice(PriceEntry price)
    {
        _prices[price.Source] = price;
        RaisePriceChanged();
    }

    /// <summary>The price used for the value: the selected market's, or the cheapest one for "lowest".</summary>
    public PriceEntry? Price => PriceFor(_priceSource);

    /// <summary>The price on one market, or the cheapest one for "lowest".</summary>
    public PriceEntry? PriceFor(string source)
    {
        if (source != PriceSources.Lowest)
            return _prices.GetValueOrDefault(source);

        return _prices.Values
            .Where(p => p.BestPrice is not null)
            .MinBy(p => p.BestPrice)
            ?? _prices.Values.FirstOrDefault();
    }

    /// <summary>Value of all copies on one market (items only; storage units count through their contents).</summary>
    public double? ValueFor(string source)
        => Entry.Marketable && !Entry.IsStorageUnit ? PriceFor(source)?.BestPrice * Entry.Quantity : null;

    /// <summary>True once the selected market (any market for "lowest") answered for this item.</summary>
    public bool HasPrice => _priceSource == PriceSources.Lowest ? _prices.Count > 0 : _prices.ContainsKey(_priceSource);

    /// <summary>Price of one copy in euros, if known.</summary>
    public double? UnitPrice => Entry.IsStorageUnit ? ContentsValue : Entry.Marketable ? Price?.BestPrice : null;

    /// <summary>Price of all copies in euros, if known.</summary>
    public double? TotalValue => Entry.IsStorageUnit ? ContentsValue : UnitPrice * Entry.Quantity;

    public string PriceText
    {
        get
        {
            if (Entry.IsStorageUnit)
                return HasMarketableContents ? FormatEuro(ContentsValue) : "";
            if (!Entry.Marketable)
                return "Not marketable";
            var price = Price;
            if (price is null)
                return "Loading price…";
            if (price.BestPrice is null)
                return "No listings";
            return FormatEuro(UnitPrice);
        }
    }

    /// <summary>Prices on every market, e.g. "Steam 1,23 € · Skinport 0,98 €".</summary>
    public string MarketPricesText
    {
        get
        {
            if (!Entry.Marketable || _prices.Count == 0)
                return "";

            var parts = PriceSources.All
                .Where(_prices.ContainsKey)
                .Select(source =>
                {
                    var best = _prices[source].BestPrice;
                    var text = $"{PriceSources.ShortName(source)} {(best is null ? "—" : FormatEuro(best))}";
                    return source == Price?.Source && _priceSource == PriceSources.Lowest ? text + " ✓" : text;
                });
            return string.Join(" · ", parts);
        }
    }

    public bool HasMarketPrices => MarketPricesText.Length > 0;

    public bool HasTotal => HasQuantity && TotalValue is not null;

    public string TotalText => HasTotal ? $"Total {FormatEuro(TotalValue)}" : "";

    private void RaisePriceChanged()
    {
        OnPropertyChanged(nameof(Price));
        OnPropertyChanged(nameof(HasPrice));
        OnPropertyChanged(nameof(UnitPrice));
        OnPropertyChanged(nameof(TotalValue));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(MarketPricesText));
        OnPropertyChanged(nameof(HasMarketPrices));
        OnPropertyChanged(nameof(HasTotal));
        OnPropertyChanged(nameof(TotalText));
    }

    private static readonly CultureInfo Portugal = CultureInfo.GetCultureInfo("pt-PT");

    public static string FormatEuro(double? value)
        => value is null ? "—" : value.Value.ToString("#,##0.00 €", Portugal);
}
