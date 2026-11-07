# Dataset extraction

Generates the shipped `data/wiki-zh-Hans.json` from an installed Graveyard Keeper. Developer tool only: it is not part
of the plugin build and is never shipped.

## Usage

```powershell
pip install -r ci/extract/requirements.txt
python ci/extract/extract.py "<game install>" data/wiki-zh-Hans.json
dotnet run --project tests/WikiBook.Tests/WikiBook.Tests.csproj -c Release
```

The extractor reads `<game>\Graveyard Keeper_Data\resources.assets`; it does not launch the game.

## What it reads

| Asset | Contents |
|---|---|
| `game_data` (`GameBalance`) | `items_data`, `objs_data`, `craft_data`, `craft_obj_data`, `techs_data` |
| `Locales/lng_zh_cn` | Simplified Chinese names, descriptions and task text |
| `Locales/lng_en` | English names and descriptions |

The assets store MonoBehaviours without type trees, so the trees are generated from the shipped managed assemblies.
Two adjustments are applied to the generator output (see `fix_nodes` in `extract.py`):

- every `string` node and `m_Enabled` gets the Unity alignment flag;
- `List<T>` nodes are typed as `T` by the generator and must be read as vectors.

Without those fixes the reader silently misaligns and reports enormous bogus list counts.

## Output rules

- **Ids**: `item.<game id>`, `building.<game id>`, `tech.<game id>`. The prefix keeps the one known collision
  (`barrel` exists as both item and object) apart.
- **Names**: `GJL.L(id)` for items, objects and techs; tech descriptions use `d_<id>`, item descriptions `<id>_d`,
  which the game leaves empty for most items.
- **Quality grades**: the game stores each grade as its own item id (`story:1`, `story:2`, `story:3`). One page is
  emitted per base id with a `qualities` array (grade, names, icon). Grade names and prefixed forms
  (`金星<name>`, `Gold <name>`, ...) become aliases and are also indexed, so "how to make a gold X" resolves.
  Many graded items only have a locale key on the base id (`meal:burger` for `meal:burger:1..3`); the extractor falls
  back to it, otherwise those entries are silently lost. Generated aliases that several entries share (all kebab
  variants are `烤串`) are dropped rather than resolving to an arbitrary entry.
- **Cooking recipes**: the craft may output the ungraded id (`meal:baked_pumpkin`) while the item is graded
  (`meal:baked_pumpkin:1`), so both ids are looked up when attaching a recipe.
- **Recipes**: crafts with `hidden == 0`. The craft whose id matches the item is preferred, then its variants, then
  the craft with the fewest inputs. Inputs and outputs are mapped to dataset ids; unresolvable pseudo ids are dropped.
  Crafts that only turn an item into itself and need nothing are gathering steps, not recipes, and are skipped.
- **Building recipes**: object craft ids of the form `<station>:p:<object>[_place]` in `craft_obj_data`.
- **Unlocks**: `<tech>.crafts` entries matched back to the craft id, giving `unlockTech` and a related link.
- **Curated additions**: a handful of player synonyms (`木头` → `item.wood`), quest wording the item never uses
  (`金星爱情故事` → `item.story`) and display names for two resources the game stores without localization keys but
  crafts require (`1h_ore_metal` → Iron ore).
- **Excluded**: pseudo items (`unknown`, the hand, insert buttons), NPC and barrier objects, and `_clone` duplicates.

## Not extracted

- **Tasks/quests shown in the HUD** are not in the balance asset. The game builds them from save data
  (`KnownNPC.TaskState`) and formats the text at runtime (`task_<id>` localization keys), so they cannot be enumerated
  offline. Flow quests (`QuestDefinition`) have ids but no player-visible names.
- **Work nodes / mining spots** are tech-tree entries, included as `tech`, but their per-level work data lives in level
  assets and is not converted.
- **Icons/images** are keys only (`icon`); the plugin draws no game art yet.

Keep the output curated: rerun the extractor after a game update, then run the core tests. A dataset from a different
game build is a bug — update `gameVersion`/`dataVersion` in the output header.
