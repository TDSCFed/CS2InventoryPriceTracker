using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.ViewModels;

/// <summary>An inventory line shown in the Inventory page.</summary>
public partial class ItemCard : ObservableObject
{
    public ItemCard(InventoryEntry entry, PriceEntry? price, string? location)
    {
        Entry = entry;
        Location = location;
        _price = price;
    }

    public InventoryEntry Entry { get; }

    /// <summary>Name of the storage unit holding the item, or null for the main inventory.</summary>
    public string? Location { get; }

    /// <summary>For a storage unit in the main inventory: the id of its loaded contents.</summary>
    public string? StorageUnitId { get; set; }

    public string Title => string.IsNullOrEmpty(Entry.CustomName) ? Entry.Name : $"“{Entry.CustomName}”";

    /// <summary>For a storage unit: "12 items · 34,56 €" once its contents are loaded.</summary>
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPrice), nameof(TotalValue), nameof(PriceText), nameof(TotalText), nameof(HasTotal))]
    private PriceEntry? _price;

    /// <summary>Price of one copy in euros, if known.</summary>
    public double? UnitPrice => Entry.Marketable ? Price?.BestPrice : null;

    /// <summary>Price of all copies in euros, if known.</summary>
    public double? TotalValue => UnitPrice * Entry.Quantity;

    public string PriceText
    {
        get
        {
            if (Entry.IsStorageUnit)
                return "";
            if (!Entry.Marketable)
                return "Not marketable";
            if (Price is null)
                return "Loading price…";
            if (Price.NotListed)
                return "No listings";
            return FormatEuro(UnitPrice);
        }
    }

    public bool HasTotal => HasQuantity && TotalValue is not null;

    public string TotalText => HasTotal ? $"Total {FormatEuro(TotalValue)}" : "";

    private static readonly CultureInfo Portugal = CultureInfo.GetCultureInfo("pt-PT");

    public static string FormatEuro(double? value)
        => value is null ? "—" : value.Value.ToString("#,##0.00 €", Portugal);
}
