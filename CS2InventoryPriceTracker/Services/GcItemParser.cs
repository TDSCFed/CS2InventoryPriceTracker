using SteamKit2.GC.CSGO.Internal;

namespace CS2InventoryPriceTracker.Services;

/// <summary>
/// Turns an item from the CS2 Game Coordinator (<see cref="CSOEconItem"/>) into a <see cref="GcItem"/>,
/// decoding the attributes the app uses. Attribute ids come from items_game.txt.
/// </summary>
public static class GcItemParser
{
    public const uint AttrPaintIndex = 6;
    public const uint AttrCustomName = 111;
    public const uint AttrPaintWear = 8;
    public const uint AttrKillEater = 80;
    public const uint AttrStickerSlot0 = 113;
    public const uint AttrStickerSlot5 = 133;
    public const uint AttrPaintSeed = 7;
    public const uint AttrTradableAfter = 75;
    public const uint AttrTradeProtectedUntil = 312;
    public const uint AttrMusicId = 166;
    public const uint AttrSprayTint = 233;
    public const uint AttrItemsCount = 270;
    public const uint AttrCasketLow = 272;
    public const uint AttrCasketHigh = 273;
    public const uint AttrCharmSlot0 = 299;

    public static GcItem Parse(CSOEconItem item)
    {
        uint? casketLow = null, casketHigh = null;
        int? paintIndex = null, itemsCount = null;
        float? wear = null;
        uint? sticker = null, music = null, charm = null, tint = null;
        var customName = string.IsNullOrEmpty(item.custom_name) ? null : item.custom_name;
        var statTrak = false;
        int? seed = null;
        uint tradableAfter = 0;
        uint? kills = null;
        var stickers = new List<uint>();

        foreach (var attribute in item.attribute)
        {
            // Sticker slots 0-5: ids 113, 117, ..., 133.
            if (attribute.def_index is >= AttrStickerSlot0 and <= AttrStickerSlot5
                && (attribute.def_index - AttrStickerSlot0) % 4 == 0
                && AsUInt(attribute) is var kit and > 0)
                stickers.Add(kit);

            switch (attribute.def_index)
            {
                case AttrPaintSeed: seed = (int)Math.Floor(AsFloat(attribute)); break;
                case AttrTradableAfter or AttrTradeProtectedUntil: tradableAfter = Math.Max(tradableAfter, AsUInt(attribute)); break;
                case AttrPaintIndex: paintIndex = (int)Math.Floor(AsFloat(attribute)); break;
                case AttrCustomName: customName ??= AsString(attribute); break;
                case AttrPaintWear: wear = AsFloat(attribute); break;
                case AttrKillEater: statTrak = true; kills = AsUInt(attribute); break;
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
            customName,
            paintIndex,
            wear,
            sticker,
            music,
            charm,
            tint,
            statTrak,
            casketId,
            itemsCount,
            seed,
            kills,
            stickers,
            tradableAfter > 0 ? DateTimeOffset.FromUnixTimeSeconds(tradableAfter).UtcDateTime : null);
    }

    /// <summary>
    /// String attributes (like the name given to a storage unit) are stored as a small protobuf message
    /// with the text in field 1; plain UTF-8 is accepted too.
    /// </summary>
    internal static string? AsString(CSOEconItemAttribute attribute)
    {
        var bytes = attribute.value_bytes;
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            if (bytes[0] == 0x0A)
            {
                // Field 1, length-delimited: read the varint length, then the text.
                var length = 0;
                var shift = 0;
                var index = 1;
                while (index < bytes.Length)
                {
                    var b = bytes[index++];
                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0)
                        break;
                    shift += 7;
                }

                if (length > 0 && index + length <= bytes.Length)
                    return System.Text.Encoding.UTF8.GetString(bytes, index, length);
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes).Trim('\0', ' ');
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    internal static uint AsUInt(CSOEconItemAttribute attribute)
        => attribute.value_bytes is { Length: >= 4 } bytes ? BitConverter.ToUInt32(bytes, 0) : attribute.value;

    internal static float AsFloat(CSOEconItemAttribute attribute)
        => attribute.value_bytes is { Length: >= 4 } bytes
            ? BitConverter.ToSingle(bytes, 0)
            : BitConverter.UInt32BitsToSingle(attribute.value);
}
