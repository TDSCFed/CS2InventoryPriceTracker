using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;

namespace CS2InventoryPriceTracker.Services;

/// <summary>The contents of one storage unit, as read from the CS2 Game Coordinator.</summary>
public record CasketContents(GcItem Casket, IReadOnlyList<GcItem> Items);

/// <summary>
/// Steam login and the CS2 Game Coordinator, used to read what is inside storage units
/// (the public Steam inventory does not show it).
/// Only the Steam refresh token is kept (in the platform secure storage); the password is never stored.
/// </summary>
public class SteamSessionService(SettingsService settings)
{
    private const string RefreshTokenKey = "steam_refresh_token";
    private const string GuardDataKey = "steam_guard_data";

    /// <summary>True when a Steam login is saved on this phone.</summary>
    public async Task<bool> HasLoginAsync()
    {
        try
        {
            return !string.IsNullOrEmpty(await SecureStorage.Default.GetAsync(RefreshTokenKey))
                   && settings.LoginAccountName is not null;
        }
        catch
        {
            return false; // Secure storage unavailable or reset.
        }
    }

    /// <summary>
    /// Logs in with an account name and password. Steam Guard (approval in the Steam app or a code)
    /// is handled by <paramref name="authenticator"/>. Returns the SteamID64 of the account.
    /// </summary>
    public async Task<ulong> LoginAsync(string accountName, string password, IAuthenticator authenticator, CancellationToken ct = default)
    {
        await using var connection = await SteamConnection.OpenAsync(ct);

        string? guardData = null;
        try
        {
            guardData = await SecureStorage.Default.GetAsync(GuardDataKey);
        }
        catch
        {
            // Ignore: Steam just asks for Steam Guard again.
        }

        AuthPollResult result;
        try
        {
            connection.Stage = "checking the password";
            SteamDiagnostics.Write("Starting credentials auth session");
            var session = await connection.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = accountName.Trim(),
                Password = password,
                IsPersistentSession = true,
                GuardData = guardData,
                DeviceFriendlyName = "CS2 Inventory (Android)",
                Authenticator = authenticator
            });
            connection.Stage = "waiting for Steam Guard";
            SteamDiagnostics.Write("Auth session started, polling for result");
            result = await session.PollingWaitForResultAsync(ct);
            SteamDiagnostics.Write("Auth session approved");
        }
        catch (AuthenticationException e)
        {
            SteamDiagnostics.Write($"Authentication failed: {e.Result}");
            throw new SteamException(e.Result switch
            {
                EResult.InvalidPassword => "Wrong account name or password.",
                EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle =>
                    "Too many login attempts. Wait a while and try again.",
                EResult.FileNotFound or EResult.Expired => "The Steam Guard request expired. Try again.",
                _ => $"Steam login failed ({e.Result})."
            }, e);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            SteamDiagnostics.Write($"Timed out while {connection.Stage}");
            throw new SteamException("Steam didn't answer in time. Try again.");
        }
        catch (Exception e) when (e is not SteamException and not OperationCanceledException)
        {
            SteamDiagnostics.Write($"Error while {connection.Stage}: {e.GetType().Name}: {e.Message}");
            throw new SteamException($"Steam login failed while {connection.Stage} ({e.GetType().Name}). Try again.", e);
        }

        connection.Stage = "logging on";
        var steamId = await connection.LogOnAsync(result.AccountName, result.RefreshToken, ct);

        await SecureStorage.Default.SetAsync(RefreshTokenKey, result.RefreshToken);
        if (result.NewGuardData is not null)
            await SecureStorage.Default.SetAsync(GuardDataKey, result.NewGuardData);
        settings.LoginAccountName = result.AccountName;
        settings.LoginSteamId = steamId.ToString();
        return steamId;
    }

    /// <summary>Forgets the saved login.</summary>
    public void Logout()
    {
        SecureStorage.Default.Remove(RefreshTokenKey);
        settings.LoginAccountName = null;
        settings.LoginSteamId = null;
    }

    /// <summary>
    /// Connects to the CS2 Game Coordinator with the saved login and reads every storage unit.
    /// Takes from a few seconds to about a minute, depending on how many storage units there are.
    /// </summary>
    public async Task<List<CasketContents>> LoadStorageUnitsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string? refreshToken = null;
        try
        {
            refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
        }
        catch
        {
            // Treated as logged out below.
        }

        var accountName = settings.LoginAccountName;
        if (string.IsNullOrEmpty(refreshToken) || accountName is null)
            throw new SteamException("Log in to Steam in the Account tab to see what is inside your storage units.");

        progress?.Report("Connecting to Steam…");
        await using var connection = await SteamConnection.OpenAsync(ct);
        connection.Stage = "logging on";
        try
        {
            await connection.LogOnAsync(accountName, refreshToken, ct);
        }
        catch (SteamLoginExpiredException)
        {
            Logout();
            throw new SteamException("Your Steam login expired. Log in again in the Account tab.");
        }

        progress?.Report("Connecting to CS2…");
        connection.Stage = "connecting to CS2";
        var inventory = await connection.StartGameCoordinatorAsync(ct);

        var caskets = inventory.Values
            .Where(i => i.DefIndex == ItemCatalog.StorageUnitDefIndex)
            .ToList();

        for (var i = 0; i < caskets.Count; i++)
        {
            var casket = caskets[i];
            progress?.Report($"Opening storage unit {i + 1} of {caskets.Count}: {casket.CustomName ?? "Storage Unit"}…");
            if (casket.CasketItemCount is null or 0)
                continue;
            connection.Stage = "opening storage units";
            await connection.LoadCasketAsync(casket.Id, ct);
        }

        // Storage unit contents arrive as extra items that point to their storage unit.
        var all = connection.Items.Values.ToList();
        return caskets
            .Select(c => new CasketContents(c, all.Where(i => i.CasketId == c.Id).ToList()))
            .ToList();
    }
}

/// <summary>Thrown when the saved refresh token is no longer accepted.</summary>
public class SteamLoginExpiredException() : Exception("Steam login expired.");

/// <summary>
/// One connection to Steam: the client, its callback loop, the logon and the CS2 Game Coordinator messages.
/// Disposing it leaves CS2 and disconnects.
/// </summary>
internal sealed class SteamConnection : IAsyncDisposable
{
    private const uint Cs2AppId = 730;

    private const uint AttrPaintIndex = 6;
    private const uint AttrPaintWear = 8;
    private const uint AttrKillEater = 80;
    private const uint AttrStickerSlot0 = 113;
    private const uint AttrMusicId = 166;
    private const uint AttrSprayTint = 233;
    private const uint AttrItemsCount = 270;
    private const uint AttrCasketLow = 272;
    private const uint AttrCasketHigh = 273;
    private const uint AttrCharmSlot0 = 299;

    /// <summary>Shared-object type id of items in the Game Coordinator cache.</summary>
    private const int SoTypeEconItem = 1;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _loop = new();
    private readonly SteamUser _user;
    private readonly SteamGameCoordinator _gc;
    private readonly CallbackManager _callbacks;
    private readonly List<IDisposable> _subscriptions = new();

    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<SteamUser.LoggedOnCallback>? _loggedOn;
    private TaskCompletionSource? _welcome;
    private TaskCompletionSource? _casketLoaded;
    private ulong _casketLoading;
    private bool _playingBlocked;

    /// <summary>Items received from the Game Coordinator, by item id.</summary>
    private readonly Dictionary<ulong, GcItem> _items = new();
    private readonly object _itemsLock = new();

    /// <summary>
    /// WebSocket connections use port 443, which works on mobile data and most Wi-Fi networks
    /// (the TCP ports Steam also offers are often blocked). Mobile networks also need more than the default 5 s.
    /// On Android a plain <see cref="HttpClient"/> uses the native Android handler, which cannot open WebSockets,
    /// so every SteamKit2 client is built on the managed <see cref="SocketsHttpHandler"/> instead.
    /// </summary>
    private static readonly SteamConfiguration Configuration = SteamConfiguration.Create(builder => builder
        .WithProtocolTypes(ProtocolTypes.WebSocket)
        .WithConnectionTimeout(TimeSpan.FromSeconds(20))
        .WithHttpClientFactory(_ =>
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20)
            });
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CS2InventoryPriceTracker/1.0");
            return client;
        }));

    /// <summary>What the connection is doing, used to explain a lost connection.</summary>
    public string Stage { get; set; } = "connecting";

    private SteamConnection()
    {
        Client = new SteamClient(Configuration);
        _callbacks = new CallbackManager(Client);
        _user = Client.GetHandler<SteamUser>()!;
        _gc = Client.GetHandler<SteamGameCoordinator>()!;

        _subscriptions.Add(_callbacks.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            SteamDiagnostics.Write("Connected to Steam");
            _connected.TrySetResult();
        }));
        _subscriptions.Add(_callbacks.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            SteamDiagnostics.Write($"Disconnected while {Stage} (user initiated: {cb.UserInitiated})");
            var error = new SteamException(_connected.Task.IsCompletedSuccessfully
                ? $"Lost the connection to Steam while {Stage}. Try again."
                : "Couldn't connect to Steam. Check your internet connection and try again.");
            _connected.TrySetException(error);
            _loggedOn?.TrySetException(error);
            _welcome?.TrySetException(error);
            _casketLoaded?.TrySetException(error);
        }));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => _loggedOn?.TrySetResult(cb)));
        _subscriptions.Add(_callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(cb => _playingBlocked = cb.PlayingBlocked));
        _subscriptions.Add(_callbacks.Subscribe<SteamGameCoordinator.MessageCallback>(OnGcMessage));
    }

    public SteamClient Client { get; }

    /// <summary>Connects to Steam, trying up to five servers (some only listen on ports that mobile networks block).</summary>
    public static async Task<SteamConnection> OpenAsync(CancellationToken ct)
    {
        SteamDiagnostics.Start();
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            SteamDiagnostics.Write($"Connecting (attempt {attempt})");
            var connection = new SteamConnection();
            _ = Task.Run(connection.RunCallbacksAsync);
            try
            {
                connection.Client.Connect();
                await connection._connected.Task.WaitAsync(Timeout, ct);
                return connection;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await connection.DisposeAsync();
                throw;
            }
            catch (Exception e)
            {
                SteamDiagnostics.Write($"Connection attempt {attempt} failed: {e.GetType().Name}: {e.Message}");
                lastError = e;
                await connection.DisposeAsync();
            }
        }

        throw lastError as SteamException
              ?? new SteamException("Couldn't connect to Steam. Check your internet connection and try again.", lastError);
    }

    private async Task RunCallbacksAsync()
    {
        while (!_loop.IsCancellationRequested)
        {
            try
            {
                await _callbacks.RunWaitCallbackAsync(_loop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A failing handler must not stop the loop.
            }
        }
    }

    /// <summary>Logs on with a refresh token and returns the SteamID64.</summary>
    public async Task<ulong> LogOnAsync(string accountName, string refreshToken, CancellationToken ct)
    {
        _loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = accountName,
            AccessToken = refreshToken,
            ShouldRememberPassword = true,
            // A fixed id per install, so this login doesn't replace another session of the same account.
            LoginID = 0x43533249, // "CS2I"
            MachineName = "CS2 Inventory app"
        });

        SteamUser.LoggedOnCallback result;
        try
        {
            result = await _loggedOn.Task.WaitAsync(Timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new SteamException("Steam didn't answer the login. Try again.");
        }

        SteamDiagnostics.Write($"Logon result: {result.Result} / {result.ExtendedResult}");
        switch (result.Result)
        {
            case EResult.OK:
                return (result.ClientSteamID ?? Client.SteamID)?.ConvertToUInt64() ?? 0;
            case EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.InvalidSignature
                or EResult.Revoked or EResult.AccountLogonDenied:
                throw new SteamLoginExpiredException();
            case EResult.ServiceUnavailable or EResult.TryAnotherCM or EResult.Busy:
                throw new SteamException("Steam is unavailable right now. Try again later.");
            default:
                throw new SteamException($"Steam login failed ({result.Result}).");
        }
    }

    /// <summary>Starts CS2 (without a game window) and waits for the Game Coordinator to send the inventory.</summary>
    public async Task<IReadOnlyDictionary<ulong, GcItem>> StartGameCoordinatorAsync(CancellationToken ct)
    {
        SetGamesPlayed(Cs2AppId);

        _welcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var attempt = 0; attempt < 6 && !_welcome.Task.IsCompleted; attempt++)
        {
            if (_playingBlocked)
                throw new SteamException("CS2 is open on another device. Close it there and try again.");

            var hello = new ClientGCMsgProtobuf<CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            _gc.Send(hello, Cs2AppId);

            try
            {
                await _welcome.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                // Ask again: the Game Coordinator often ignores the first hello.
            }
        }

        if (!_welcome.Task.IsCompletedSuccessfully)
        {
            if (_playingBlocked)
                throw new SteamException("CS2 is open on another device. Close it there and try again.");
            throw new SteamException("The CS2 Game Coordinator didn't answer. Try again in a few minutes.");
        }

        lock (_itemsLock)
            return new Dictionary<ulong, GcItem>(_items);
    }

    /// <summary>Asks the Game Coordinator for the contents of one storage unit and waits until they arrived.</summary>
    public async Task LoadCasketAsync(ulong casketId, CancellationToken ct)
    {
        _casketLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _casketLoading = casketId;

        var request = new ClientGCMsgProtobuf<CMsgCasketItem>((uint)EGCItemMsg.k_EMsgGCCasketItemLoadContents);
        request.Body.casket_item_id = casketId;
        request.Body.item_item_id = casketId;
        _gc.Send(request, Cs2AppId);

        try
        {
            await _casketLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        catch (TimeoutException)
        {
            // Keep whatever arrived; the Game Coordinator sometimes skips the "done" notification.
        }

        // Items can arrive just after the notification.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }

    /// <summary>Snapshot of every item received so far (inventory and loaded storage units).</summary>
    public IReadOnlyDictionary<ulong, GcItem> Items
    {
        get
        {
            lock (_itemsLock)
                return new Dictionary<ulong, GcItem>(_items);
        }
    }

    private void OnGcMessage(SteamGameCoordinator.MessageCallback callback)
    {
        if (callback.AppID != Cs2AppId)
            return;

        switch (callback.EMsg)
        {
            case (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome:
            {
                var welcome = new ClientGCMsgProtobuf<CMsgClientWelcome>(callback.Message);
                foreach (var cache in welcome.Body.outofdate_subscribed_caches)
                    foreach (var type in cache.objects.Where(o => o.type_id == SoTypeEconItem))
                        foreach (var data in type.object_data)
                            AddItem(data);
                _welcome?.TrySetResult();
                break;
            }

            case (uint)ESOMsg.k_ESOMsg_CacheSubscribed:
            {
                var cache = new ClientGCMsgProtobuf<CMsgSOCacheSubscribed>(callback.Message);
                foreach (var type in cache.Body.objects.Where(o => o.type_id == SoTypeEconItem))
                    foreach (var data in type.object_data)
                        AddItem(data);
                break;
            }

            case (uint)ESOMsg.k_ESOMsg_Create:
            case (uint)ESOMsg.k_ESOMsg_Update:
            {
                var single = new ClientGCMsgProtobuf<CMsgSOSingleObject>(callback.Message);
                if (single.Body.type_id == SoTypeEconItem)
                    AddItem(single.Body.object_data);
                break;
            }

            case (uint)ESOMsg.k_ESOMsg_UpdateMultiple:
            {
                var multiple = new ClientGCMsgProtobuf<CMsgSOMultipleObjects>(callback.Message);
                foreach (var obj in multiple.Body.objects_modified.Where(o => o.type_id == SoTypeEconItem))
                    AddItem(obj.object_data);
                break;
            }

            case (uint)EGCItemMsg.k_EMsgGCItemCustomizationNotification:
            {
                var notification = new ClientGCMsgProtobuf<CMsgGCItemCustomizationNotification>(callback.Message);
                if (notification.Body.request == (uint)EGCItemCustomizationNotification.k_EGCItemCustomizationNotification_CasketContents
                    && (notification.Body.item_id.Count == 0 || notification.Body.item_id.Contains(_casketLoading)))
                    _casketLoaded?.TrySetResult();
                break;
            }
        }
    }

    private void AddItem(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return;

        CSOEconItem item;
        try
        {
            using var stream = new MemoryStream(data);
            item = ProtoBuf.Serializer.Deserialize<CSOEconItem>(stream);
        }
        catch
        {
            return;
        }

        var parsed = Parse(item);
        lock (_itemsLock)
            _items[parsed.Id] = parsed;
    }

    private static GcItem Parse(CSOEconItem item)
    {
        uint? casketLow = null, casketHigh = null;
        int? paintIndex = null, itemsCount = null;
        float? wear = null;
        uint? sticker = null, music = null, charm = null, tint = null;
        var statTrak = false;

        foreach (var attribute in item.attribute)
        {
            switch (attribute.def_index)
            {
                case AttrPaintIndex: paintIndex = (int)Math.Floor(AsFloat(attribute)); break;
                case AttrPaintWear: wear = AsFloat(attribute); break;
                case AttrKillEater: statTrak = true; break;
                case AttrStickerSlot0: sticker = AsUInt(attribute); break;
                case AttrMusicId: music = AsUInt(attribute); break;
                case AttrSprayTint: tint = AsUInt(attribute); break;
                case AttrItemsCount: itemsCount = (int)AsUInt(attribute); break;
                case AttrCasketLow: casketLow = AsUInt(attribute); break;
                case AttrCasketHigh: casketHigh = AsUInt(attribute); break;
                case AttrCharmSlot0: charm = AsUInt(attribute); break;
            }
        }

        ulong? casketId = casketLow is not null && casketHigh is not null
            ? ((ulong)casketHigh.Value << 32) | casketLow.Value
            : null;

        return new GcItem(
            item.id,
            item.def_index,
            item.quality,
            string.IsNullOrEmpty(item.custom_name) ? null : item.custom_name,
            paintIndex,
            wear,
            sticker,
            music,
            charm,
            tint,
            statTrak,
            casketId,
            itemsCount);
    }

    private static uint AsUInt(CSOEconItemAttribute attribute)
        => attribute.value_bytes is { Length: >= 4 } bytes ? BitConverter.ToUInt32(bytes, 0) : attribute.value;

    private static float AsFloat(CSOEconItemAttribute attribute)
        => attribute.value_bytes is { Length: >= 4 } bytes
            ? BitConverter.ToSingle(bytes, 0)
            : BitConverter.UInt32BitsToSingle(attribute.value);

    private void SetGamesPlayed(params uint[] appIds)
    {
        var message = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        foreach (var appId in appIds)
            message.Body.games_played.Add(new SteamKit2.Internal.CMsgClientGamesPlayed.GamePlayed { game_id = appId });
        Client.Send(message);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client.IsConnected)
            {
                SetGamesPlayed(); // Leave CS2 so the account doesn't show as in-game.
                await Task.Delay(500);
                Client.Disconnect();
            }
        }
        catch
        {
            // Already disconnected.
        }

        _loop.Cancel();
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
    }
}
