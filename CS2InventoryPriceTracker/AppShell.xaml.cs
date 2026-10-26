namespace CS2InventoryPriceTracker;

public partial class AppShell : Shell
{
    public const string ItemDetailsRoute = "item";

    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(ItemDetailsRoute, typeof(Views.ItemDetailsPage));
    }
}
