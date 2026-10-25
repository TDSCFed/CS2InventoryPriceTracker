using SteamKit2;

namespace CS2InventoryPriceTracker.Services;

/// <summary>
/// Keeps the last lines of what happened while talking to Steam (including SteamKit2's own debug output),
/// so a failed login can be copied from the Account tab and shared to diagnose it.
/// Never contains the password or login tokens: SteamKit2 does not log them, and neither does the app.
/// </summary>
public static class SteamDiagnostics
{
    private const int MaxLines = 400;
    private static readonly LinkedList<string> Lines = new();
    private static readonly object Lock = new();
    private static bool _listening;

    /// <summary>Starts capturing SteamKit2's debug output (once per app run).</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_listening)
                return;
            _listening = true;
        }

        DebugLog.AddListener((category, message) => Write($"[{category}] {message}"));
        DebugLog.Enabled = true;
    }

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        System.Diagnostics.Debug.WriteLine(text);
        lock (Lock)
        {
            Lines.AddLast(text);
            while (Lines.Count > MaxLines)
                Lines.RemoveFirst();
        }
    }

    public static bool HasLines
    {
        get
        {
            lock (Lock)
                return Lines.Count > 0;
        }
    }

    /// <summary>The captured log, with device and app details at the top.</summary>
    public static string GetText()
    {
        lock (Lock)
        {
            var header = $"CS2 Inventory {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString}) · " +
                         $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString} · {DeviceInfo.Current.Model} · " +
                         $"network: {string.Join(",", Connectivity.Current.ConnectionProfiles)}";
            return header + Environment.NewLine + string.Join(Environment.NewLine, Lines);
        }
    }
}
