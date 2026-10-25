using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2InventoryPriceTracker.Services;

namespace CS2InventoryPriceTracker.ViewModels;

/// <summary>Chooses the Steam account whose inventory is shown.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SteamInventoryService _steam;
    private readonly LocalStore _store;
    private readonly PriceUpdater _prices;

    public SettingsViewModel(SettingsService settings, SteamInventoryService steam, LocalStore store, PriceUpdater prices)
    {
        _settings = settings;
        _steam = steam;
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
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    public bool HasAccount => SteamId is not null;

    public void Load()
    {
        SteamId = _settings.SteamId;
        PersonaName = _settings.PersonaName;
        AvatarUrl = _settings.AvatarUrl;
        if (string.IsNullOrEmpty(ProfileInput))
            ProfileInput = SteamId ?? "";
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var profile = await _steam.ResolveProfileAsync(ProfileInput);
            if (profile.SteamId != _settings.SteamId)
            {
                _prices.Stop();
                await _store.ClearInventoryAsync();
            }

            _settings.SetAccount(profile.SteamId, profile.PersonaName, profile.AvatarUrl);
            ProfileInput = profile.SteamId;
            Load();

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
}
