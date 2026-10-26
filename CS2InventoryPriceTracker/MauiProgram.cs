using System.Net;
using Microsoft.Extensions.Logging;
using CS2InventoryPriceTracker.Services;
using CS2InventoryPriceTracker.ViewModels;
using CS2InventoryPriceTracker.Views;

namespace CS2InventoryPriceTracker;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // One HttpClient for every Steam request. Steam answers some endpoints with gzip.
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 14) CS2InventoryPriceTracker/1.0");
        builder.Services.AddSingleton(http);

        // Services
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<LocalStore>();
        builder.Services.AddSingleton<SteamInventoryService>();
        // Markets, in the order they are shown on item cards.
        builder.Services.AddSingleton<IPriceProvider, SteamMarketPriceProvider>();
        builder.Services.AddSingleton<IPriceProvider, SkinportPriceProvider>();
        builder.Services.AddSingleton<IPriceProvider, CsgoMarketPriceProvider>();
        builder.Services.AddSingleton<PriceUpdater>();
        builder.Services.AddSingleton<ItemCatalog>();
        builder.Services.AddSingleton<SteamSessionService>();
        builder.Services.AddSingleton<StorageUnitService>();

        // View models
        builder.Services.AddTransient<InventoryViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<HistoryViewModel>();
        builder.Services.AddTransient<ItemDetailsViewModel>();

        // Pages
        builder.Services.AddTransient<InventoryPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<ItemDetailsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
