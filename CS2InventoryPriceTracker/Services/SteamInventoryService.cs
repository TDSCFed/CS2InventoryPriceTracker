using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CS2InventoryPriceTracker.Models;

namespace CS2InventoryPriceTracker.Services;

/// <summary>A Steam profile found from what the user typed.</summary>
public record SteamProfile(string SteamId, string? PersonaName, string? AvatarUrl, bool IsPublic);

/// <summary>An error with a message that can be shown to the user as is.</summary>
public class SteamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads Steam profiles and CS2 inventories from the public Steam Community endpoints (no login needed).</summary>
public partial class SteamInventoryService(HttpClient http)
{
    private const int Cs2AppId = 730;
    private const int Cs2ContextId = 2;
    private const int PageSize = 1000;

    /// <summary>
    /// Finds a profile from a SteamID64, a profile link (…/profiles/7656… or …/id/name) or a custom URL name.
    /// </summary>
    public async Task<SteamProfile> ResolveProfileAsync(string input, CancellationToken ct = default)
    {
        var text = input.Trim().TrimEnd('/');
        if (text.Length == 0)
            throw new SteamException("Enter your SteamID64, profile link or custom URL name.");

        string path;
        var profilesMatch = ProfilesRegex().Match(text);
        var idMatch = CustomUrlRegex().Match(text);
        if (SteamId64Regex().IsMatch(text))
            path = $"profiles/{text}";
        else if (profilesMatch.Success)
            path = $"profiles/{profilesMatch.Groups[1].Value}";
        else if (idMatch.Success)
            path = $"id/{Uri.EscapeDataString(idMatch.Groups[1].Value)}";
        else if (!text.Contains('/'))
            path = $"id/{Uri.EscapeDataString(text)}";
        else
            throw new SteamException("That doesn't look like a Steam profile link.");

        string xml;
        try
        {
            xml = await http.GetStringAsync($"https://steamcommunity.com/{path}/?xml=1", ct);
        }
        catch (HttpRequestException e)
        {
            throw new SteamException("Couldn't reach Steam. Check your internet connection.", e);
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception e)
        {
            throw new SteamException("Steam returned an unexpected answer. Try again later.", e);
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName == "response" || root.Element("steamID64") is null)
            throw new SteamException("Steam profile not found.");

        var steamId = root.Element("steamID64")!.Value.Trim();
        var name = root.Element("steamID")?.Value.Trim();
        var avatar = root.Element("avatarMedium")?.Value.Trim();
        var isPublic = string.Equals(root.Element("privacyState")?.Value.Trim(), "public", StringComparison.OrdinalIgnoreCase);
        return new SteamProfile(steamId, name, avatar, isPublic);
    }

    /// <summary>Downloads the whole CS2 inventory of a public profile, grouped by item.</summary>
    public async Task<List<InventoryEntry>> GetInventoryAsync(string steamId, CancellationToken ct = default)
    {
        var assets = new List<AssetDto>();
        var descriptions = new Dictionary<(string, string), DescriptionDto>();
        string? startAssetId = null;

        while (true)
        {
            var url = $"https://steamcommunity.com/inventory/{steamId}/{Cs2AppId}/{Cs2ContextId}?l=english&count={PageSize}";
            if (startAssetId is not null)
                url += $"&start_assetid={startAssetId}";

            var page = await GetPageAsync(url, ct);
            if (page is null)
                break; // Empty inventory.

            assets.AddRange(page.Assets ?? []);
            foreach (var d in page.Descriptions ?? [])
                descriptions[(d.ClassId, d.InstanceId)] = d;

            if (page.MoreItems != 1 || string.IsNullOrEmpty(page.LastAssetId))
                break;
            startAssetId = page.LastAssetId;
        }

        var entries = new Dictionary<string, InventoryEntry>();
        foreach (var asset in assets)
        {
            if (!descriptions.TryGetValue((asset.ClassId, asset.InstanceId), out var d))
                continue;

            var marketable = d.Marketable == 1;
            var customName = ParseNameTag(d.FraudWarnings);
            var key = InventoryEntry.MakeKey(null, marketable, d.MarketHashName ?? d.Name ?? "", d.Name ?? "", customName);
            var amount = int.TryParse(asset.Amount, out var a) ? Math.Max(a, 1) : 1;

            if (entries.TryGetValue(key, out var existing))
            {
                existing.Quantity += amount;
                continue;
            }

            entries[key] = new InventoryEntry
            {
                Key = key,
                MarketHashName = d.MarketHashName ?? d.Name ?? "",
                Name = d.Name ?? d.MarketHashName ?? "Unknown item",
                CustomName = customName,
                Type = d.Type,
                Category = d.MarketHashName == "Storage Unit" || d.Name == "Storage Unit"
                    ? ItemCategories.StorageUnit
                    : ItemCategories.FromSteamType(d.Tags?.FirstOrDefault(t => t.Category == "Type")?.Name),
                Rarity = d.Tags?.FirstOrDefault(t => t.Category == "Rarity")?.Name,
                RarityColor = d.Tags?.FirstOrDefault(t => t.Category == "Rarity")?.Color ?? d.NameColor,
                IconPath = d.IconUrl,
                Quantity = amount,
                Marketable = marketable,
                IsStorageUnit = d.MarketHashName == "Storage Unit" || d.Name == "Storage Unit"
            };
        }

        return entries.Values.ToList();
    }

    private async Task<InventoryPage?> GetPageAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (HttpRequestException e)
        {
            throw new SteamException("Couldn't reach Steam. Check your internet connection.", e);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.Unauthorized:
                    throw new SteamException(
                        "This inventory is private. In Steam, set Profile > Edit Profile > Privacy Settings > Inventory to Public.");
                case HttpStatusCode.TooManyRequests:
                    throw new SteamException("Steam is limiting requests right now. Wait a minute and try again.");
            }

            if (!response.IsSuccessStatusCode)
                throw new SteamException($"Steam returned an error ({(int)response.StatusCode}). Try again later.");

            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json) || json == "null")
                throw new SteamException("Steam returned an empty answer. Try again in a moment.");

            InventoryPage? page;
            try
            {
                page = JsonSerializer.Deserialize<InventoryPage>(json);
            }
            catch (JsonException e)
            {
                throw new SteamException("Steam returned an unexpected answer. Try again later.", e);
            }

            if (page is null || page.Success != 1)
                throw new SteamException("Steam couldn't load this inventory. Try again later.");

            return page.TotalInventoryCount == 0 && (page.Assets is null || page.Assets.Count == 0) ? null : page;
        }
    }

    /// <summary>Renamed items carry a warning like "Name Tag: ''My AK''".</summary>
    private static string? ParseNameTag(List<string>? warnings)
    {
        var warning = warnings?.FirstOrDefault(w => w.StartsWith("Name Tag:", StringComparison.Ordinal));
        if (warning is null)
            return null;

        var match = NameTagRegex().Match(warning);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^7656\d{13}$")]
    private static partial Regex SteamId64Regex();

    [GeneratedRegex(@"steamcommunity\.com/profiles/(7656\d{13})", RegexOptions.IgnoreCase)]
    private static partial Regex ProfilesRegex();

    [GeneratedRegex(@"steamcommunity\.com/id/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CustomUrlRegex();

    [GeneratedRegex(@"''(.*)''")]
    private static partial Regex NameTagRegex();

    // JSON returned by https://steamcommunity.com/inventory/{steamid}/730/2

    private sealed class InventoryPage
    {
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
        [JsonPropertyName("descriptions")] public List<DescriptionDto>? Descriptions { get; set; }
        [JsonPropertyName("more_items")] public int MoreItems { get; set; }
        [JsonPropertyName("last_assetid")] public string? LastAssetId { get; set; }
        [JsonPropertyName("total_inventory_count")] public int TotalInventoryCount { get; set; }
        [JsonPropertyName("success")] public int Success { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("assetid")] public string AssetId { get; set; } = "";
        [JsonPropertyName("classid")] public string ClassId { get; set; } = "";
        [JsonPropertyName("instanceid")] public string InstanceId { get; set; } = "";
        [JsonPropertyName("amount")] public string? Amount { get; set; }
    }

    private sealed class DescriptionDto
    {
        [JsonPropertyName("classid")] public string ClassId { get; set; } = "";
        [JsonPropertyName("instanceid")] public string InstanceId { get; set; } = "";
        [JsonPropertyName("market_hash_name")] public string? MarketHashName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
        [JsonPropertyName("name_color")] public string? NameColor { get; set; }
        [JsonPropertyName("marketable")] public int Marketable { get; set; }
        [JsonPropertyName("fraudwarnings")] public List<string>? FraudWarnings { get; set; }
        [JsonPropertyName("tags")] public List<TagDto>? Tags { get; set; }
    }

    private sealed class TagDto
    {
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("localized_tag_name")] public string? Name { get; set; }
        [JsonPropertyName("color")] public string? Color { get; set; }
    }
}
