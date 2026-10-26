using CS2InventoryPriceTracker.ViewModels;

namespace CS2InventoryPriceTracker.Views;

public partial class HistoryPage : ContentPage
{
    private readonly HistoryViewModel _viewModel;

    public HistoryPage(HistoryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        ChartView.Drawable = viewModel.Chart;
        viewModel.ChartChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            // Labels follow the light/dark theme.
            viewModel.Chart.TextColor = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#A0A0A8")
                : Color.FromArgb("#6E6E78");
            ChartView.Invalidate();
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }
}
