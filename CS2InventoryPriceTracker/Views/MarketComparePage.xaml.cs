using CS2InventoryPriceTracker.ViewModels;

namespace CS2InventoryPriceTracker.Views;

public partial class MarketComparePage : ContentPage
{
    private readonly MarketCompareViewModel _viewModel;

    public MarketComparePage(MarketCompareViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }
}
