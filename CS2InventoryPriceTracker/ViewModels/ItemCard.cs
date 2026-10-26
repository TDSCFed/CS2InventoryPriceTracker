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
    public PriceEntry? Price
    {
        get
        {
            if (_priceSource != PriceSources.Lowest)
                return _prices.GetValueOrDefault(_priceSource);

            return _prices.Values
                .Where(p => p.BestPrice is not null)
                .MinBy(p => p.BestPrice)
                ?? _prices.Values.FirstOrDefault();
        }
    }

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
