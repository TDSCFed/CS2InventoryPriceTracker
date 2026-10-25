# CS2InventoryPriceTracker

Android app (.NET MAUI) to see a Counter-Strike 2 Steam inventory and the current price of each item, in euros.

## Features

- Loads the CS2 inventory of a public Steam profile (SteamID64, profile link or custom URL name)
- Current price of every marketable item on the Steam Community Market, plus quantity and total value
- Search, sort (value, unit price, name, quantity) and hide unmarketable items
- Inventory and prices are kept on the phone, so the app opens instantly and works offline

### Planned

- Steam login, to see the items inside storage units
- Prices from other markets (Skinport, CSFloat, ...) through `IPriceProvider`

## Download

Every build of `main` is published as the **latest** release:
https://github.com/TDSCFed/CS2InventoryPriceTracker/releases/latest/download/CS2InventoryPriceTracker.apk

Every pull request gets its own pre-release, and the download link is posted as a comment on the PR.

## Setup

1. In Steam, make your profile and inventory public (Edit Profile > Privacy Settings).
2. Open `CS2InventoryPriceTracker.slnx` in Rider, select your phone (USB debugging on) or an emulator, and run.
3. In the app, open the **Account** tab and paste your Steam profile link.

Command line alternative:

```
dotnet build CS2InventoryPriceTracker/CS2InventoryPriceTracker.csproj -t:Run -f net10.0-android
```

No API keys are needed: the app uses Steam's public inventory and market price endpoints.

## Prices

The Steam Market only allows about 20 price requests per minute, so the first load of a large inventory takes a few
minutes. Prices are cached in SQLite and refreshed after 12 hours (or on demand with **Update prices** in the menu).
The price shown is the cheapest current listing, or the median sale price when there are no listings.

## Structure

- `Models/` - SQLite entities (inventory lines and cached prices)
- `Services/` - `SteamInventoryService` (profiles and inventory), `IPriceProvider` with `SteamMarketPriceProvider`,
  `PriceUpdater` (background price refresh), `LocalStore` (SQLite) and `SettingsService`
- `ViewModels/` - MVVM view models (CommunityToolkit.Mvvm)
- `Views/` - pages (XAML)
