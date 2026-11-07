#!/usr/bin/env python3
"""Build the shipped wiki dataset from an installed Graveyard Keeper.

Developer tool only: it is never compiled into the plugin and never shipped. It reads the game's
`resources.assets` (GameBalance + the localization tables), converts them into the versioned
dataset schema and writes `data/wiki-zh-Hans.json`.

Usage:
    python ci/extract/extract.py "<game install>" data/wiki-zh-Hans.json

Requirements: `pip install -r ci/extract/requirements.txt`

The asset stores MonoBehaviours without type trees, so the type trees are generated from the
shipped managed assemblies. Two adjustments are needed on top of the generator output:

* Unity aligns strings to 4 bytes; the generator only marks the header string, so every `string`
  node gets the alignment flag.
* The generator types a `List<T>` field as `T`; those nodes must read as vectors, otherwise a
  `List<string>` is read as a single string. A node whose first child is an `Array` whose element
  type is not `char` is therefore rewritten to `vector`.
* `m_Enabled` is a byte followed by alignment padding.

Everything else is read straight from the asset, so ids, recipes and names match the game.
"""

import json
import os
import sys

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TypeTreeHelper
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
from UnityPy.helpers.TypeTreeNode import TypeTreeNode

SCHEMA_VERSION = 1
GAME_ASSEMBLY = "Assembly-CSharp"
LOCALE_ASSEMBLY = "Assembly-CSharp-firstpass"
DEFAULT_LANGUAGE = "zh-Hans"
LANGUAGES = [("zh_cn", "zh-Hans"), ("en", "en")]
PRIMITIVES = {
    "string", "int", "uint", "float", "double", "bool", "char",
    "UInt8", "SInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "SInt64",
}

GRADE_BY_QUALITY = {1: "bronze", 2: "silver", 3: "gold"}
GRADE_PREFIX = {"bronze": "铜星", "silver": "银星", "gold": "金星"}
GRADE_PREFIX_EN = {"bronze": "Bronze", "silver": "Silver", "gold": "Gold"}

# Resources the game stores without a localization key because they are never shown as loose items
# (they live in a stockpile object). Used only when a craft needs them, so recipes stay complete.
HIDDEN_ITEM_NAMES = {
    "1h_ore_metal": {"zh-Hans": "铁矿石", "en": "Iron ore"},
    "ore_metal": {"zh-Hans": "铁矿石", "en": "Iron ore"},
}

# Vocabulary players use that the game does not: the wiki resolves it and shows the real name.
CURATED_ALIASES = {
    "item.wood": ["木头", "木材", "木料"],
    "item.stone": ["石块", "石材"],
    "item.ingot_metal": ["铁块"],
    "item.flitch": ["木板", "木料板"],
    # Quest text calls the gold story "a love story"; the item itself is never named that way.
    "item.story": ["爱情故事", "金星爱情故事", "Love story", "Gold love story"],
}


def curated_aliases(entry_id, existing=None):
    """Merge the curated aliases into an entry's generated aliases without duplicates."""
    result = list(existing or [])
    seen = set(result)
    for alias in CURATED_ALIASES.get(entry_id, []):
        if alias and alias not in seen:
            result.append(alias)
            seen.add(alias)
    return result


def fix_nodes(nodes):
    """Turn generator output into a type tree UnityPy can read (see the module docstring)."""
    for i, node in enumerate(nodes):
        nxt = nodes[i + 1] if i + 1 < len(nodes) else None
        if nxt and nxt["m_Level"] == node["m_Level"] + 1 and nxt["m_Type"] == "Array":
            array = nxt
            end = i + 2
            while end < len(nodes) and nodes[end]["m_Level"] > array["m_Level"]:
                end += 1
            child_level = array["m_Level"] + 1
            children = [n for n in nodes[i + 1:end] if n["m_Level"] == child_level]
            if len(children) >= 2:
                data = children[1]
                is_char = node["m_Type"] == "string" and data["m_Type"] == "char"
                if node["m_Type"] in PRIMITIVES and not is_char:
                    node["m_Type"] = "vector"
        if node["m_Name"] == "m_Enabled" and node["m_Type"] in ("UInt8", "SInt8", "bool"):
            node["m_MetaFlag"] |= 0x4000
    return nodes


def read_typetree(env, generator, assembly, class_name, object_name):
    nodes = fix_nodes(json.loads(generator.get_nodes_as_json(assembly, class_name)))
    root = TypeTreeNode.from_list(nodes)
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            data = obj.read(check_read=False)
            name = getattr(data, "m_Name", "")
        except Exception:
            continue
        if name != object_name:
            continue
        obj.reset()
        return TypeTreeHelper.read_typetree(
            root, obj.reader, as_dict=True, byte_size=None, check_read=False,
            assetsfile=obj.assets_file,
        )
    raise SystemExit(f"{class_name} asset '{object_name}' not found")


LOCALE_OBJECTS = {"zh_cn": "lng_zh_cn", "en": "lng_en"}


def read_locale(env, object_name):
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            data = obj.read(check_read=False)
            name = getattr(data, "m_Name", "")
        except Exception:
            continue
        if name != object_name:
            continue
        raw = obj.get_raw_data()
        pos = 12  # m_GameObject
        pos = (pos + 4) & ~3  # m_Enabled (byte) + padding
        pos += 12  # m_Script

        def read_string(p):
            length = int.from_bytes(raw[p:p + 4], "little", signed=True)
            p += 4
            text = raw[p:p + length].decode("utf-8")
            return text, (p + length + 3) & ~3

        def read_list(p):
            count = int.from_bytes(raw[p:p + 4], "little", signed=True)
            p += 4
            values = []
            for _ in range(count):
                text, p = read_string(p)
                values.append(text)
            return values, p

        _, pos = read_string(pos)  # m_Name
        _, pos = read_string(pos)  # id
        keys, pos = read_list(pos)
        texts, pos = read_list(pos)
        return dict(zip(keys, texts))
    raise SystemExit(f"locale asset '{object_name}' not found")


def localized(names):
    result = {}
    for key, value in names.items():
        if value:
            result[key] = value
    return result


def is_pseudo_item(item):
    """Pseudo items are UI/engine placeholders (the hand, insert buttons), not wiki subjects."""
    raw_id = item["id"]
    return (
        item.get("type") in (10, 100000)
        or raw_id.startswith("_")
        or "pseudo" in raw_id.lower()
    )


def main():
    if len(sys.argv) < 3:
        raise SystemExit('usage: extract.py "<game install>" <out.json>')
    game_dir = sys.argv[1]
    out_path = sys.argv[2]
    data_dir = os.path.join(game_dir, "Graveyard Keeper_Data")
    assets = os.path.join(data_dir, "resources.assets")
    managed = os.path.join(data_dir, "Managed")

    TypeTreeHelper.read_typetree_boost = None  # type: ignore[assignment]
    print("loading", assets)
    env = UnityPy.load(assets)
    generator = TypeTreeGenerator("2020.3.17f1")
    generator.load_local_dll_folder(managed)

    print("reading GameBalance")
    balance = read_typetree(env, generator, GAME_ASSEMBLY, "GameBalance", "game_data")
    locales = {}
    for game_code, _ in LANGUAGES:
        print("reading locale", game_code)
        locales[game_code] = read_locale(env, LOCALE_OBJECTS[game_code])

    build(balance, locales, out_path)


def build(balance, locales, out_path):
    zh = locales["zh_cn"]
    en = locales["en"]

    def name_of(raw_id):
        return {
            "zh-Hans": zh.get(raw_id) or "",
            "en": en.get(raw_id) or "",
        }

    def name_of_item(raw_id):
        """Names for a quality-graded item id.

        Many graded items share one locale key on the base id (``meal:burger`` covers
        ``meal:burger:1``..``meal:burger:3``); without this fallback those entries were skipped.
        """
        names = name_of(raw_id)
        if names["zh-Hans"] or names["en"]:
            return names
        if ":" in raw_id:
            base = raw_id.rsplit(":", 1)[0]
            if base != raw_id:
                base_names = name_of(base)
                if base_names["zh-Hans"] or base_names["en"]:
                    return base_names
        return names

    items = balance["items_data"]
    objects = balance["objs_data"]
    techs = balance["techs_data"]
    crafts = balance["craft_data"]
    object_crafts = balance["craft_obj_data"]

    craft_by_id = {craft["id"]: craft for craft in crafts}
    tech_for_craft = {}
    for tech in techs:
        for craft_id in tech.get("crafts") or []:
            tech_for_craft.setdefault(craft_id, tech["id"])

    entries = {}
    raw_to_entry = {}
    group_members = {}

    def add_entry(entry):
        if not entry.get("names"):
            return False
        entries[entry["id"]] = entry
        return True

    # Quality grades are separate item ids ("story:1", "story:2", "story:3"). One wiki page per
    # base id lists every grade, so "how to make X" always covers all of them.
    groups = {}
    for item in items:
        raw_id = item["id"]
        if item.get("quality_type") == 1 and ":" in raw_id:
            base = raw_id.rsplit(":", 1)[0]
            groups.setdefault(base, []).append(item)
            raw_to_entry[raw_id] = "item." + base

    for item in items:
        raw_id = item["id"]
        if item.get("quality_type") == 1 and ":" in raw_id:
            continue
        entry_id = "item." + raw_id
        raw_to_entry[raw_id] = entry_id
        names = name_of(raw_id)
        if not (names["zh-Hans"] or names["en"]) and raw_id in HIDDEN_ITEM_NAMES:
            names = dict(HIDDEN_ITEM_NAMES[raw_id])
        if raw_id == "unknown" or not (names["zh-Hans"] or names["en"]):
            continue
        if is_pseudo_item(item):
            continue
        add_entry({
            "id": entry_id,
            "kind": "item",
            "names": localized(names),
            "aliases": curated_aliases(entry_id),
            "description": localized({"zh-Hans": zh.get(raw_id + "_d") or "",
                                      "en": en.get(raw_id + "_d") or ""}),
            "icon": item.get("icon") or None,
            "rawId": raw_id,
        })

    for base, members in groups.items():
        entry_id = "item." + base
        named_members = [member for member in members if any(name_of_item(member["id"]).values())]
        if not named_members:
            continue
        named_members.sort(key=lambda member: member.get("quality") or 0)
        leader = named_members[-1]
        names = name_of_item(leader["id"])
        qualities = []
        aliases = []
        seen = set()
        description = {}
        for member in named_members:
            grade = GRADE_BY_QUALITY.get(int(member.get("quality") or 0))
            if not grade:
                continue
            member_names = name_of_item(member["id"])
            qualities.append({
                "grade": grade,
                "names": localized(member_names),
                "icon": member.get("icon") or None,
            })
            for language, text in (("zh-Hans", member_names["zh-Hans"]),
                                   ("en", member_names["en"])):
                if text and text not in seen:
                    aliases.append(text)
                    seen.add(text)
            prefixed_zh = GRADE_PREFIX[grade] + member_names["zh-Hans"] if member_names["zh-Hans"] else ""
            prefixed_en = GRADE_PREFIX_EN[grade] + " " + member_names["en"] if member_names["en"] else ""
            for text in (prefixed_zh, prefixed_en):
                if text and text not in seen:
                    aliases.append(text)
                    seen.add(text)
            for suffix, target in (("_d", description),):
                if not target:
                    target["zh-Hans"] = zh.get(member["id"] + suffix) or ""
                    target["en"] = en.get(member["id"] + suffix) or ""
        aliases = curated_aliases(entry_id, aliases)
        aliases = [a for a in aliases if a and a not in set(names.values())]
        raw_to_entry[base] = entry_id
        group_members[entry_id] = [member["id"] for member in members]
        add_entry({
            "id": entry_id,
            "kind": "item",
            "names": localized(names),
            "aliases": aliases,
            "description": localized(description),
            "icon": leader.get("icon") or None,
            "qualities": qualities,
            "rawId": base,
        })

    for tech in techs:
        raw_id = tech["id"]
        if not (zh.get(raw_id) or en.get(raw_id)):
            continue
        add_entry({
            "id": "tech." + raw_id,
            "kind": "tech",
            "names": localized(name_of(raw_id)),
            "aliases": curated_aliases("tech." + raw_id),
            "description": localized({"zh-Hans": zh.get("d_" + raw_id) or "",
                                      "en": en.get("d_" + raw_id) or ""}),
            "rawId": raw_id,
        })

    object_ids = {obj["id"] for obj in objects}
    skip_prefixes = ("npc_", "blockage_")
    for obj in objects:
        raw_id = obj["id"]
        if raw_id.startswith(skip_prefixes) or raw_id.endswith("_clone"):
            continue
        if raw_id not in object_ids or not (zh.get(raw_id) or en.get(raw_id)):
            continue
        add_entry({
            "id": "building." + raw_id,
            "kind": "building",
            "names": localized(name_of(raw_id)),
            "aliases": curated_aliases("building." + raw_id),
            "rawId": raw_id,
        })

    def resolve_item(raw_id):
        """Map a game item id (including a quality variant) to a dataset entry id."""
        if raw_id in raw_to_entry:
            entry_id = raw_to_entry[raw_id]
            return entry_id if entry_id in entries else None
        return None

    def ingredient(rows, mode="sum"):
        merged = {}
        order = []
        for row in rows or []:
            entry_id = resolve_item(row["id"])
            if not entry_id:
                continue
            amount = int(row.get("value") or 1)
            if amount <= 0:
                continue
            if entry_id not in merged:
                merged[entry_id] = amount
                order.append(entry_id)
            elif mode == "sum":
                merged[entry_id] += amount
            else:
                merged[entry_id] = max(merged[entry_id], amount)
        return [{"id": entry_id, "amount": merged[entry_id]} for entry_id in order]

    def station_of(craft):
        for candidate in craft.get("craft_in") or []:
            entry_id = "building." + candidate
            if entry_id in entries:
                return entry_id
        return None

    def seconds_of(craft):
        expression = craft.get("craft_time") or {}
        value = expression.get("_simpified_float") or 0.0
        return int(round(value)) if value > 0 else 0

    def add_related(entry, ids):
        related = list(entry.get("related") or [])
        for related_id in ids:
            if related_id and related_id not in related:
                related.append(related_id)
        if related:
            entry["related"] = related

    def apply_recipe(entry, craft):
        inputs = ingredient(craft.get("needs")) + ingredient(craft.get("needs_from_wgo"))
        outputs = ingredient(craft.get("output"), mode="max")
        station = station_of(craft)
        tech_id = tech_for_craft.get(craft["id"])
        unlock = "tech." + tech_id if tech_id and "tech." + tech_id in entries else None

        add_related(entry, [station, unlock])
        if unlock and not entry.get("unlockTech"):
            entry["unlockTech"] = unlock

        # A craft that only turns the item into itself and needs nothing is a gathering step, not a
        # recipe; the station link above is the useful part.
        meaningful = bool(inputs) or any(output["id"] != entry["id"] for output in outputs)
        if not meaningful:
            return False

        recipe = {"inputs": inputs, "outputs": outputs, "station": station, "seconds": seconds_of(craft)}
        if not recipe["station"]:
            recipe.pop("station")
        if not recipe["seconds"]:
            recipe.pop("seconds")
        entry["recipe"] = recipe
        add_related(entry, [output["id"] for output in outputs if output["id"] != entry["id"]])
        return True

    # Item recipes: the first visible, non-automatic craft that produces the item.
    crafts_by_output = {}
    for craft in crafts:
        if craft.get("hidden"):
            continue
        for row in craft.get("output") or []:
            crafts_by_output.setdefault(row["id"], []).append(craft)

    def craft_rank(craft, preferred):
        return (
            0 if craft["id"] == preferred else 1,
            0 if craft["id"].startswith(preferred) else 1,
            0 if craft.get("craft_type") == 0 else 1,
            0 if craft.get("needs") else 1,
            len(craft.get("needs") or []),
            craft["id"],
        )

    for entry in entries.values():
        if entry["kind"] != "item":
            continue
        raw_id = entry.get("rawId")
        candidates = []
        for member_id in group_members.get(entry["id"], [raw_id]):
            candidates.extend(crafts_by_output.get(member_id, []))
            # Cooking crafts output the ungraded id ("meal:baked_pumpkin") while the items are graded
            # ("meal:baked_pumpkin:1"), so the base id has to be looked up as well.
            base_id = member_id.rsplit(":", 1)[0]
            if base_id != member_id:
                candidates.extend(crafts_by_output.get(base_id, []))
        candidates.sort(key=lambda craft: craft_rank(craft, raw_id))
        for craft in candidates:
            if apply_recipe(entry, craft):
                break

    # Building recipes: "<station>:p:<object>[_place]" is the build craft for that object.
    for craft in object_crafts:
        raw_id = craft["id"]
        if ":p:" not in raw_id or craft.get("hidden"):
            continue
        target = raw_id.split(":p:", 1)[1]
        candidates = [target]
        if target.endswith("_place"):
            candidates.append(target[:-len("_place")])
        for candidate in candidates:
            entry = entries.get("building." + candidate)
            if not entry:
                continue
            if not entry.get("recipe") and apply_recipe(entry, craft):
                break

    # Several game items can share one localized name (the kebab variants are all "烤串"), so their
    # generated grade aliases collide. An alias that would resolve to more than one entry is dropped
    # instead of silently pointing at whichever entry was indexed first.
    alias_owners = {}
    for entry in entries.values():
        for alias in entry.get("aliases") or []:
            alias_owners.setdefault(alias.strip().casefold(), set()).add(entry["id"])
    ambiguous_aliases = {alias for alias, owners in alias_owners.items() if len(owners) > 1}

    # Related ids are added after every entry exists, so links can point forward.
    for entry in entries.values():
        related = []
        for related_id in entry.get("related") or []:
            if related_id in entries and related_id != entry["id"]:
                related.append(related_id)
        if related:
            entry["related"] = related
        else:
            entry.pop("related", None)
        if not entry.get("description"):
            entry.pop("description", None)
        # An alias identical to a localized name adds nothing and produces no redirect note.
        names = set((entry.get("names") or {}).values())
        aliases = [alias for alias in entry.get("aliases") or []
                   if alias and alias not in names and alias.strip().casefold() not in ambiguous_aliases]
        if aliases:
            entry["aliases"] = aliases
        else:
            entry.pop("aliases", None)
        if not entry.get("icon"):
            entry.pop("icon", None)
        entry.pop("rawId", None)

    dataset = {
        "schemaVersion": SCHEMA_VERSION,
        "dataVersion": "1.2.0",
        "gameVersion": "steam-22583570",
        "source": "generated by ci/extract/extract.py from resources.assets (GameBalance + locale tables)",
        "entries": sorted(entries.values(), key=lambda e: e["id"]),
        "redirects": [],
    }

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(dataset, handle, ensure_ascii=False, indent=1, sort_keys=False)
        handle.write("\n")

    kinds = {}
    for entry in entries.values():
        kinds[entry["kind"]] = kinds.get(entry["kind"], 0) + 1
    recipes = sum(1 for entry in entries.values() if entry.get("recipe"))
    unlocks = sum(1 for entry in entries.values() if entry.get("unlockTech"))
    print(f"entries: {len(entries)} ({kinds}), recipes: {recipes}, unlocks: {unlocks}")
    print("written", out_path, os.path.getsize(out_path), "bytes")


if __name__ == "__main__":
    main()
