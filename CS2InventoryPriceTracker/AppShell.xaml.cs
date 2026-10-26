namespace CS2InventoryPriceTracker;

public partial class AppShell : Shell
{
    public const string ItemDetailsRoute = "item";
    public const string MarketCompareRoute = "compare";

    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(ItemDetailsRoute, typeof(Views.ItemDetailsPage));
        Routing.RegisterRoute(MarketCompareRoute, typeof(Views.MarketComparePage));
    }
}
