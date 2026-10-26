namespace CS2InventoryPriceTracker.Services;

/// <summary>Small user settings, kept in the platform preferences.</summary>
public class SettingsService
{
    private const string SteamIdKey = "steam_id";
    private const string PersonaNameKey = "persona_name";
    private const string AvatarUrlKey = "avatar_url";
    private const string InventoryUpdatedKey = "inventory_updated";
    private const string StorageUpdatedKey = "storage_updated";
    private const string LoginAccountNameKey = "login_account_name";
    private const string LoginSteamIdKey = "login_steam_id";

    /// <summary>SteamID64 of the account whose inventory is shown, e.g. "76561198000000000".</summary>
    public string? SteamId
    {
        get => NullIfEmpty(Preferences.Get(SteamIdKey, ""));
        set => Preferences.Set(SteamIdKey, value ?? "");
    }

    public string? PersonaName
    {
        get => NullIfEmpty(Preferences.Get(PersonaNameKey, ""));
        set => Preferences.Set(PersonaNameKey, value ?? "");
    }

    public string? AvatarUrl
    {
        get => NullIfEmpty(Preferences.Get(AvatarUrlKey, ""));
        set => Preferences.Set(AvatarUrlKey, value ?? "");
    }

    /// <summary>When the inventory was last downloaded from Steam (UTC).</summary>
    public DateTime? InventoryUpdatedAt
    {
        get
        {
            var ticks = Preferences.Get(InventoryUpdatedKey, 0L);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Preferences.Set(InventoryUpdatedKey, value?.Ticks ?? 0L);
    }

    /// <summary>When the storage unit contents were last read from CS2 (UTC).</summary>
    public DateTime? StorageUpdatedAt
    {
        get
        {
            var ticks = Preferences.Get(StorageUpdatedKey, 0L);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Preferences.Set(StorageUpdatedKey, value?.Ticks ?? 0L);
    }

    /// <summary>Steam account name of the saved login (the login token itself is in secure storage).</summary>
    public string? LoginAccountName
    {
        get => NullIfEmpty(Preferences.Get(LoginAccountNameKey, ""));
        set => Preferences.Set(LoginAccountNameKey, value ?? "");
    }

    /// <summary>SteamID64 of the saved login.</summary>
    public string? LoginSteamId
    {
        get => NullIfEmpty(Preferences.Get(LoginSteamIdKey, ""));
        set => Preferences.Set(LoginSteamIdKey, value ?? "");
    }

    /// <summary>Raised when the selected account changes.</summary>
    public event EventHandler? AccountChanged;

    public void SetAccount(string steamId, string? personaName, string? avatarUrl)
    {
        SteamId = steamId;
        PersonaName = personaName;
        AvatarUrl = avatarUrl;
        InventoryUpdatedAt = null;
        StorageUpdatedAt = null;
        AccountChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
