"""
Builds CS2InventoryPriceTracker/Resources/Raw/items.json: a compact lookup from CS2 game data ids
(item definition index, paint kit, sticker kit, music kit, charm, graffiti tint) to item names, rarity colors and icons.

Items inside storage units come from the CS2 Game Coordinator with only these ids, so the app needs this table
to show their names and look up their market prices.

Source: https://github.com/ByMykel/CSGO-API (English files).

Usage:
    python tools/generate_item_data.py                 # downloads the source files
    python tools/generate_item_data.py <folder>        # uses already downloaded *.json files from <folder>
"""

import json
import os
import sys
import urllib.request

SOURCE = "https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/{}.json"
STEAM_INF = "https://raw.githubusercontent.com/SteamDatabase/GameTracking-CS2/master/game/csgo/steam.inf"
# Fallback when steam.inf can't be read. The Game Coordinator rejects a ClientHello without a client version.
DEFAULT_CLIENT_VERSION = 2000935
OUTPUT = os.path.join(os.path.dirname(__file__), "..", "CS2InventoryPriceTracker", "Resources", "Raw", "items.json")
STEAM_IMAGE_PREFIX = "https://community.akamai.steamstatic.com/economy/image/"


def load(name, folder):
    if folder:
        with open(os.path.join(folder, name + ".json"), encoding="utf-8") as f:
            return json.load(f)
    with urllib.request.urlopen(SOURCE.format(name), timeout=120) as response:
        return json.load(response)


def client_version(folder):
    """CS2 client version (ClientVersion in steam.inf), sent in the Game Coordinator hello."""
    try:
        if folder and os.path.exists(os.path.join(folder, "steam.inf")):
            with open(os.path.join(folder, "steam.inf"), encoding="utf-8") as f:
                text = f.read()
        else:
            with urllib.request.urlopen(STEAM_INF, timeout=60) as response:
                text = response.read().decode("utf-8")
        for line in text.splitlines():
            if line.startswith("ClientVersion="):
                return int(line.split("=", 1)[1].strip())
    except Exception as e:  # noqa: BLE001 - any failure falls back to the default
        print(f"Couldn't read steam.inf ({e}); using {DEFAULT_CLIENT_VERSION}")
    return DEFAULT_CLIENT_VERSION


def image(url):
    """Steam economy images are stored without their common prefix to keep the file small."""
    if not url:
        return ""
    return url[len(STEAM_IMAGE_PREFIX):] if url.startswith(STEAM_IMAGE_PREFIX) else url


def color(item):
    rarity = item.get("rarity") or {}
    return (rarity.get("color") or "").lstrip("#")


def rarity_name(item):
    return (item.get("rarity") or {}).get("name") or ""


# Sniper rifles are listed with the rifles in the source data.
SNIPER_IDS = {9, 11, 38, 40}  # AWP, G3SG1, SCAR-20, SSG 08
WEAPON_CATEGORIES = {"Knives": "Knife", "Gloves": "Gloves", "Pistols": "Pistol", "SMGs": "SMG", "Heavy": "Heavy",
                     "Rifles": "Rifle", "Equipment": "Other"}


def weapon_category(category, weapon_id):
    """App category of a weapon (same names as the Steam inventory "Type" tag, simplified)."""
    if category == "Rifles" and weapon_id in SNIPER_IDS:
        return "Sniper Rifle"
    return WEAPON_CATEGORIES.get(category, "Other")


def strip_star(name):
    return name[2:] if name.startswith("★ ") else name


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else None
    out = {"version": 1, "clientVersion": client_version(folder), "defs": {}, "skins": {}, "stickers": {}, "patches": {},
           "graffiti": {}, "music": {}, "charms": {}, "phases": {}}

    # Rows are [name, rarity color, image, ...]; see each table below for the extra columns.

    # Plain items by definition index: [name, color, image, kind, category, rarity].
    # Kind: k = knife, g = gloves (both get the ★ prefix), w = weapon, m = marketable item, i = unmarketable item.
    for w in load("base_weapons", folder):
        category = (w.get("category") or {}).get("name")
        kind = "k" if category == "Knives" else "g" if category == "Gloves" else "w"
        out["defs"][str(w["def_index"])] = [w["name"], "", image(w.get("image")), kind,
                                            weapon_category(category, w["def_index"]), ""]

    def_categories = {"crates": "Container", "keys": "Key", "collectibles": "Collectible", "agents": "Agent", "tools": "Tool"}
    for name in ("crates", "keys", "collectibles", "agents", "tools"):
        for item in load(name, folder):
            if item.get("def_index") is None:
                continue
            # m = sold on the market, i = cannot be sold (most coins, pins, tools...).
            marketable = bool(item.get("market_hash_name")) and item.get("marketable", True)
            display = item.get("market_hash_name") or item["name"]
            out["defs"].setdefault(str(item["def_index"]), [display, color(item), image(item.get("image")),
                                                            "m" if marketable else "i", def_categories[name], rarity_name(item)])

    # Weapon skins by "<definition index>_<paint kit>": [name, color, image, category, rarity].
    # Vanilla knives have no paint kit and use "_0".
    for skin in load("skins", folder):
        weapon_id = (skin.get("weapon") or {}).get("weapon_id")
        if weapon_id is None:
            continue
        paint = skin.get("paint_index") or "0"
        # Doppler and Gamma Doppler phases (Ruby, Sapphire, Phase 1...) have their own paint kit:
        # phases[<paint kit>] = [phase name].
        if skin.get("phase"):
            out["phases"][str(paint)] = [skin["phase"]]
        category = weapon_category((skin.get("category") or {}).get("name"), weapon_id)
        out["skins"][f"{weapon_id}_{paint}"] = [strip_star(skin["name"]), color(skin), image(skin.get("image")),
                                                category, rarity_name(skin)]

    # Stickers, patches, charms, graffiti and music kits: [name, color, image, rarity].

    for name, key in (("stickers", "stickers"), ("patches", "patches"), ("keychains", "charms")):
        for item in load(name, folder):
            out[key][str(item["def_index"])] = [item.get("market_hash_name") or item["name"], color(item),
                                                image(item.get("image")), rarity_name(item)]

    # Graffiti by "<sticker kit>" or "<sticker kit>_<tint id>" (ids look like "graffiti-1697_3").
    for item in load("graffiti", folder):
        suffix = item["id"].split("-", 1)[-1]
        out["graffiti"][suffix] = [item.get("market_hash_name") or item["name"], color(item), image(item.get("image")),
                                   rarity_name(item)]

    # Music kits by id; the StatTrak variants (ids ending in "_st") are built by the app from the quality.
    for item in load("music_kits", folder):
        kit_id = item["id"].split("-", 1)[-1]
        if kit_id.endswith("_st"):
            continue
        out["music"][kit_id] = [item.get("market_hash_name") or item["name"], color(item), image(item.get("image")),
                                rarity_name(item)]

    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
    with open(OUTPUT, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"), sort_keys=True)

    counts = ", ".join(f"{k}: {len(v)}" for k, v in out.items() if isinstance(v, dict))
    counts += f", client version {out['clientVersion']}"
    print(f"Wrote {os.path.normpath(OUTPUT)} ({os.path.getsize(OUTPUT) // 1024} KB; {counts})")


if __name__ == "__main__":
    main()
