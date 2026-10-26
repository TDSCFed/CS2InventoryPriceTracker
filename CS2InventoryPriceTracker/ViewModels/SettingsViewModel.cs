using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

/// <summary>A market the user can pick for values and totals.</summary>
public record PriceSourceChoice(string Label, string Id);

/// <summary>Chooses the Steam account whose inventory is shown, and manages the Steam login used for storage units.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SteamInventoryService _steam;
    private readonly SteamSessionService _session;
    private readonly LocalStore _store;
    private readonly PriceUpdater _prices;

    public SettingsViewModel(SettingsService settings, SteamInventoryService steam, SteamSessionService session,
        LocalStore store, PriceUpdater prices)
    {
        _settings = settings;
        _steam = steam;
        _session = session;
        _store = store;
        _prices = prices;
        Load();
    }

    [ObservableProperty]
    private string _profileInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccount))]
    private string? _steamId;

    [ObservableProperty]
    private string? _personaName;

    [ObservableProperty]
    private string? _avatarUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(LoginCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    public bool HasAccount => SteamId is not null;

    public IReadOnlyList<PriceSourceChoice> PriceSourceChoices { get; } =
    [
        new("Steam Market", PriceSources.Steam),
        new("Skinport", PriceSources.Skinport),
        new("Market.CSGO", PriceSources.CsgoMarket),
        new("Cheapest market", PriceSources.Lowest)
    ];

    [ObservableProperty]
    private PriceSourceChoice? _selectedPriceSource;

    partial void OnSelectedPriceSourceChanged(PriceSourceChoice? value)
    {
        if (value is not null)
            _settings.PriceSource = value.Id;
    }

    // Steam login (storage units)

    [ObservableProperty]
    private string _loginAccountName = "";

    [ObservableProperty]
    private string _loginPassword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedOut))]
    private bool _isLoggedIn;

    public bool IsLoggedOut => !IsLoggedIn;

    [ObservableProperty]
    private string? _loggedInText;

    [ObservableProperty]
    private string? _loginMessage;

    /// <summary>True once something was logged while talking to Steam.</summary>
    [ObservableProperty]
    private bool _canCopyLog;

    public void Load()
    {
        SelectedPriceSource = PriceSourceChoices.FirstOrDefault(c => c.Id == _settings.PriceSource) ?? PriceSourceChoices[0];
        SteamId = _settings.SteamId;
        PersonaName = _settings.PersonaName;
        AvatarUrl = _settings.AvatarUrl;
        if (string.IsNullOrEmpty(ProfileInput))
            ProfileInput = SteamId ?? "";
        CanCopyLog = SteamDiagnostics.HasLines;
        _ = LoadLoginStateAsync();
    }

    private async Task LoadLoginStateAsync()
    {
        IsLoggedIn = await _session.HasLoginAsync();
        LoggedInText = IsLoggedIn ? $"Logged in as {_settings.LoginAccountName}" : null;
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var profile = await _steam.ResolveProfileAsync(ProfileInput);
            await SetAccountAsync(profile);
            ProfileInput = profile.SteamId;

            Message = profile.IsPublic
                ? "Account saved. Open the Inventory tab to see your items."
                : "Account saved, but this profile is not public. Make your profile and inventory public in Steam's privacy settings.";
        }
        catch (SteamException e)
        {
            Message = e.Message;
        }
        catch (Exception)
        {
            Message = "Something went wrong. Check your internet connection and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetAccountAsync(SteamProfile profile)
    {
        if (profile.SteamId != _settings.SteamId)
        {
            _prices.Stop();
            await _store.ClearInventoryAsync();
        }

        _settings.SetAccount(profile.SteamId, profile.PersonaName, profile.AvatarUrl);
        Load();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(LoginAccountName) || string.IsNullOrEmpty(LoginPassword))
        {
            LoginMessage = "Enter your Steam account name and password.";
            return;
        }

        var page = Shell.Current?.CurrentPage;
        if (page is null)
            return;

        IsBusy = true;
        LoginMessage = "Logging in…";
        try
        {
            var authenticator = new DialogAuthenticator(page, text => MainThread.BeginInvokeOnMainThread(() => LoginMessage = text));
            var steamId = await Task.Run(() => _session.LoginAsync(LoginAccountName, LoginPassword, authenticator));
            LoginPassword = "";

            // The inventory shown follows the logged-in account.
            if (steamId != 0 && steamId.ToString() != _settings.SteamId)
            {
                var profile = await _steam.ResolveProfileAsync(steamId.ToString());
                await SetAccountAsync(profile);
                ProfileInput = profile.SteamId;
            }

            await LoadLoginStateAsync();
            LoginMessage = "Logged in. Your storage units will load in the Inventory tab.";
        }
        catch (OperationCanceledException)
        {
            LoginMessage = "Login cancelled.";
        }
        catch (SteamException e)
        {
            LoginMessage = e.Message;
        }
        catch (Exception e)
        {
            SteamDiagnostics.Write($"Login error: {e}");
            LoginMessage = "Couldn't log in to Steam. Check your internet connection and try again.";
        }
        finally
        {
            IsBusy = false;
            CanCopyLog = SteamDiagnostics.HasLines;
        }
    }

    /// <summary>Copies the Steam connection log, to share it when something fails.</summary>
    [RelayCommand]
    private async Task CopyLogAsync()
    {
        await Clipboard.Default.SetTextAsync(SteamDiagnostics.GetText());
        LoginMessage = "Steam log copied. Paste it in a message to share it.";
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        _session.Logout();
        await LoadLoginStateAsync();
        LoginMessage = "Logged out. Storage units already loaded stay visible.";
    }
}
