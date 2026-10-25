namespace CS2InventoryPriceTracker.Services;

/// <summary>Small user settings, kept in the platform preferences.</summary>
public class SettingsService
{
    private const string SteamIdKey = "steam_id";
    private const string PersonaNameKey = "persona_name";
    private const string AvatarUrlKey = "avatar_url";
    private const string InventoryUpdatedKey = "inventory_updated";

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

    /// <summary>Raised when the selected account changes.</summary>
    public event EventHandler? AccountChanged;

    public void SetAccount(string steamId, string? personaName, string? avatarUrl)
    {
        SteamId = steamId;
        PersonaName = personaName;
        AvatarUrl = avatarUrl;
        InventoryUpdatedAt = null;
        AccountChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
