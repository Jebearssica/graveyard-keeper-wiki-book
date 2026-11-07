# Copilot instructions — Graveyard Keeper Wiki Book

## What this repository is

An in-game, searchable wiki book for **Graveyard Keeper 1**, shipped as a BepInEx 5 / HarmonyX plugin (C#, `net481`,
Unity 2021.3.x). The book opens from inside the game and answers "what is this, how do I make it, what unlocks it"
for items, buildings, quests, and mechanics.

`AGENTS_INIT.MD` is the authoritative product spec (Simplified Chinese). Read it before changing scope or design, and
keep this file aligned with it.

**Repository status:** implemented and launched inside the game. `Debug.SelfTest` passes all 16 checks (schema,
dataset, references, exact search, alias redirects, the quest-wording alias, graded cooking items, material usage,
fuzzy search, recipe links, quality grades, empty query, type filter, keyboard selection, window toggle, window draw)
with no plugin warnings or errors. The window has not been verified by hand in a play session yet; screenshots/GIF,
interactive checks and the vanilla UI entry remain open.

### Product invariants (from the spec)

- Search must tolerate non-exact queries. Quest text often names an item differently from the item itself: quest
  wording such as "金星爱情故事" must resolve to the real item with a visible redirect/alias note. Build an explicit
  alias table; fuzzy matching alone will not cover this.
- A result must answer the likely intent, not just show a name:
  - item/building: display name, recipe, and the tech or task that unlocks the recipe;
  - quest/mechanic: what it is and how it is completed or used.
- UI follows the vanilla art style. Text must stay crisp at high resolution; the installed `ShowMeMoar` mod is the
  reference for scalable text.
- Images are optional. Text-first is acceptable when asset extraction would dominate the work.
- Content/layout references: [`graveyardkeeperdb.wiki`](https://graveyardkeeperdb.wiki/zh-Hans) for the search and
  text layout only (its content is sparse), and the [Fandom wiki](https://graveyardkeeper.fandom.com/wiki/Graveyard_Keeper_Wiki)
  for content depth.
- Minimise game launches. Batch verification into as few launches as possible (see *Tests and verification*).
- The repository is public: no absolute paths, usernames, machine names, or save/log contents in source, data, docs,
  configs, screenshots, commits, or build output. Treat the spec's game-install path as a placeholder — never copy a
  real install path or username into another file.
- Do not commit the game installation, decompiled sources, raw extraction dumps, or ripped art. Commit only the
  curated dataset the mod ships; prefer referencing vanilla sprites at runtime, and use original assets otherwise.

## Build

Target `net481`, `<LangVersion>latest</LangVersion>`, `Nullable=disable`, and a single root-level `<ModName>.csproj`
(working name `WikiBook`) next to the `.cs` files. Mirror the reference csproj: `BepInEx.PluginInfoProps`,
`Microsoft.NETFramework.ReferenceAssemblies`, and `Private=false` references to BepInEx and game assemblies.

Commands the scaffold must support:

```powershell
# Real install in the default Steam location; deploys to <game>\BepInEx\plugins\<ModName>\
dotnet build -c Release

# Game in a non-default Steam library (common; pass it explicitly)
dotnet build -c Release -p:GameDir="<game install>"

# No game installed (CI): game assemblies come from declaration-only stubs in ci/refs
dotnet build -c Release -p:UseGameRefs=false -p:DeployToGame=false -p:BepInExCore="<extracted BepInEx>\core"
```

- `UseGameRefs` is true when `<GameDir>\Graveyard Keeper_Data\Managed\Assembly-CSharp.dll` exists; `DeployToGame` is
  true when `<GameDir>\BepInEx\plugins` exists. The properties above override auto-detection.
- Build with the modern .NET SDK (CI uses 8.0.x); `net481` is the target framework, not the SDK. The
  `Microsoft.NETFramework.ReferenceAssemblies` package keeps it buildable without the .NET Framework targeting pack.
- Both flavours compile the same sources. Never put `#if` around gameplay logic and never ship a CI-only build.
- BepInEx (`BepInEx.dll`, `0Harmony.dll`) and game assemblies are referenced with `<Private>false</Private>`.
- No linter or formatter is configured. `dotnet build` (warnings included) is the compile gate.

## Tests and verification

There is no test framework and the plugin cannot run outside the game. The reference mods verify through an in-game
`SelfTest` gated by a `Debug.SelfTest` config entry:

```powershell
# 1. dotnet build -c Release
# 2. set Debug.SelfTest = true in <game>\BepInEx\config\<plugin-guid>.cfg
# 3. launch the game once (the checks run a few seconds after startup; a save is not required)
Select-String -Path "<game install>\BepInEx\LogOutput.log" -Pattern '\[SELFTEST\]'
```

- "Run a single test" means run one scenario in `SelfTest.cs` and read its `PASS`/`FAIL` line; there is no per-test
  CLI.
- Every non-trivial feature gets a self-test/demo step that drives game APIs, not the mouse. Extend the existing
  harness instead of asking for manual play-testing; collect open questions and answer them in one launch.
- Keep the wiki data model, normalization, alias resolution, and ranking free of Unity types so they can be exercised
  by a plain console harness without launching the game. Do not put search or extraction logic in `Patches.cs`.
- `dotnet run --project tests/WikiBook.Tests -c Release` compiles `Json.cs`/`WikiData.cs`/`WikiEntry.cs`/`WikiSearch.cs`
  for net8.0 and runs the dataset, alias, grade, recipe and reference checks. CI runs it before the stub build.
- CI also runs the stub build and package checks; gameplay is verified locally.

## Architecture

### Plugin layers

| Layer | File | Responsibility |
|---|---|---|
| Entry point | `Plugin.cs` | `BaseUnityPlugin`; binds config, exposes the static `Log`, loads the dataset, builds the search index, applies `Harmony.CreateAndPatchAll` |
| Patch surface | `Patches.cs` | Every Harmony patch, one small `static` method each, named `<Type>_<Method>_<Prefix or Postfix>`; no reflection or UI plumbing |
| Game access | `GameAccess.cs` | Cached `FieldInfo`/`MethodInfo` for non-public game members; the only file allowed to use reflection |
| Wiki model/loader | `WikiData.cs`, `WikiEntry.cs` | Plain C# records, schema version, dataset load and validation |
| Search | `WikiSearch.cs` | Normalization, alias resolution, fuzzy ranking |
| UI | `WikiBookGui.cs` + UI helpers | Vanilla-styled window, list/detail views, input handling, deferred layout |
| Verification | `SelfTest.cs` (+ optional `Demo.cs`) | Scripted in-game checks, disabled by default, excluded from release unless enabled |
| Extraction (dev only) | `tools/` or `ci/extract/` | Reads the installed game and emits the committed dataset; never compiled into the plugin |

Runtime flow: `Plugin.Awake` loads `data/wiki-*.json` (embedded resource or file next to the DLL), builds the index,
installs patches, and exposes the book's entry point. A patch on `MainGame.Update` ticks the UI and self-test. Opening
the book builds the window; a query goes through `WikiSearch`; selecting a result opens the detail view with recipe
and unlock links.

### Content pipeline

1. Extract offline on a machine with the game installed: `python ci/extract/extract.py "<game install>" data/wiki-zh-Hans.json`.
   The extractor reads `Graveyard Keeper_Data\resources.assets` (the `game_data` `GameBalance` asset and the
   `Locales/lng_zh_cn`, `Locales/lng_en` tables) with UnityPy plus type trees generated from the shipped managed
   assemblies. It does not launch the game and never commits a raw dump. See `ci/extract/README.md`.
2. Normalize into a versioned schema. At minimum: stable id, kind, per-language names, aliases/redirects, description,
   icon key, recipe inputs/outputs/amounts, unlock (tech id and quest id), cross-links, and `qualities` for grades.
3. Commit the curated dataset under `data/` with `schemaVersion` and the game build it was generated from
   (`gameVersion: steam-22583570` today).
4. Ship it as an embedded resource or a file next to the DLL. The runtime must not need `GameDir`; a missing dataset
   must log and degrade, not throw.
5. Regenerate when the game version changes and bump `schemaVersion`/`dataVersion`. A dataset from a different game
   version is a bug, not a warning.

Game-domain facts that shape the data model:

- Quality grades are separate item ids produced by one recipe (`story:1`, `story:2`, `story:3`). The dataset keeps one
  page per base id with a `qualities` list (grade, names, icon), indexes every grade name, and generates
  quality-prefixed aliases (`金星<name>`, `Gold <name>`), so "how to make X" covers every grade. Many graded items
  only localize the base id (`meal:burger`), and cooking crafts output that ungraded id: both need the fallback or the
  entry/recipe is silently lost.
- The recipe graph is bidirectional at runtime: `WikiDataset.Uses(id)` indexes every recipe that consumes an entry
  (built lazily), which powers the detail page's "Used in" list and the search expansion for a material name.
- Recipes, tech-tree nodes (`TechTree`/`TechDefinition`), and quests (`QuestDefinition`) reference each other by id;
  resolve ids to localized names for display and keep the ids for cross-links.
- Displayed names depend on the player's language (`GameSettings._cur_lng`, `Localize`/`GetLocalizedName`). Store names
  per language, search the language the player sees, and fall back to another language rather than showing an empty
  page.
- Vanilla UI is built from `BaseGUI`/`GUIElements`; deferred layout goes through `GJTimer.AddTimer`, because vanilla
  windows lay out before injected content exists.

### UI

- Reuse vanilla sprites through `UI2DSprite`/`GUIElements` where possible; otherwise ship original art in the vanilla
  palette.
- Do not render the book with the vanilla bitmap fonts. Follow `ShowMeMoar`'s high-resolution text approach so the book
  stays legible at large resolutions.
- Keep the book usable with the existing mouse/keyboard input; do not add a new input system.

## Key conventions

### Game API access

- Game assemblies are **not** publicized. Only members public in the shipped `Assembly-CSharp.dll` may be referenced
  directly; `protected`/`private` members go through cached `FieldInfo`/`MethodInfo` in `GameAccess.cs`.
- Verify member names and signatures against the decompiled assembly before patching or modifying stubs. Do not guess
  API names.
- A missing reflected member is logged once and then ignored, so an unfamiliar game version degrades instead of
  crashing.
- Pass optional arguments explicitly; stubs may not carry the real defaults.
- Harmony attributes that only use `nameof(...)` do not appear in IL. Add those members to `StubModel.ExtraMembers` in
  `ci/stubgen` or the stub build breaks.
- Regenerate stubs whenever a new game API is used, and commit the result:

```powershell
dotnet build -c Release
dotnet run --project ci/stubgen -c Release -- bin/Release/<ModName>.dll ci/refs "<game install>\Graveyard Keeper_Data\Managed"
```

### Privacy and release hygiene

- Keep these csproj properties; they strip local paths from the shipped assembly:

```xml
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
<PathMap>$(MSBuildProjectDirectory)=/_/</PathMap>
<Deterministic>true</Deterministic>
<GenerateDependencyFile>false</GenerateDependencyFile>
```

- `.gitignore` must exclude `bin/`, `obj/`, `.vs/`, `.idea/`, `dist/`, `.vscode/`, `*.user`, and `*.suo`.
- A release contains only `<ModName>.dll`, `README.md`, and `LICENSE` — no `.pdb`, `.xml`, logs, saves, or datasets
  that embed machine paths.
- Before considering a change done, scan the built DLL for `[A-Za-z]:\\Users\\`, `[A-Za-z]:\\a\\`, and `\\Users\\`
  (the reference CI does this).
- Never commit a `BepInEx` output folder, `LogOutput.log`, save files, or screenshots showing a local path.

### Naming, versions, docs

- `AssemblyName` and root namespace are PascalCase; the deployed plugin folder uses the same name. Plugin GUID:
  `gk.<modname>` lowercase, no spaces (for example `gk.wikibook`).
- Version lives in `<BepInExPluginVersion>` in the `.csproj` (consumed by `BepInEx.PluginInfoProps` →
  `MyPluginInfo`); bump it together with `CHANGELOG.md` for every user-visible change.
- Config entries live under `General` / `Debug` with bilingual descriptions (English first, 中文 second). The
  `SelfTest` toggle belongs under `Debug`.
- `README.md` is bilingual (English first, 中文 second) with Install / Usage / Configuration / Building from source /
  Limitations sections, and opens with a screenshot or GIF of the book. Player-visible changes go in `CHANGELOG.md`.
- Code comments and XML docs are English and explain game-engine quirks, not what the code says.

### Code style

- File-scoped namespaces, `internal static` helpers, `var`, Allman braces, 4 spaces, ~120 columns.
- Null-check every game object/component at patch entry and return the vanilla result instead of throwing; a patch
  that cannot act must `return true`.
- Wrap `Tick()` bodies and background callbacks in `try/catch` and log a warning — an exception inside
  `MainGame.Update` spams the console every frame.
- Keep each Harmony patch small; put multi-step behavior in a feature class and call it from the patch.

## Reference implementations

[`Jebearssica/graveyard-keeper-move-buildings`](https://github.com/Jebearssica/graveyard-keeper-move-buildings) and
[`Jebearssica/graveyard-keeper-queue-quality-fix`](https://github.com/Jebearssica/graveyard-keeper-queue-quality-fix)
(same author, same stack) are the closest working examples: csproj layout, `ci/refs` + `ci/stubgen`, `SelfTest`,
`GameAccess`, vanilla UI work, and the release workflow. Follow them unless the wiki-book requirement gives a reason
not to.

The repository form is also modelled on
[`Jebearssica/graveyard-keeper-mod-manager`](https://github.com/Jebearssica/graveyard-keeper-mod-manager).
