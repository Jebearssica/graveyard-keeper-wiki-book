# Changelog

All notable player-visible changes are listed here. The version matches `<BepInExPluginVersion>` in `WikiBook.csproj`.

## 0.3.0

- Fixed 57 missing items and 40 missing recipes: graded items whose name lives on the base locale key (`汉堡`,
  `烤南瓜`, `一碗南瓜汤`, fish, lentils, prayers, beer, wine, juice) are now extracted, and cooking crafts that output
  the ungraded id are matched to their graded pages.
- Recipe detail pages gain a **Used in** section: everything this entry is an ingredient of, including quest items,
  with the amount consumed and cross-links.
- Searching a material by its exact name (`南瓜`) now also lists the recipes that consume it, marked with
  `材料：南瓜`; direct name matches still rank first.
- Ambiguous generated aliases (several items are all named `烤串`) are dropped instead of resolving to a random entry.
- Layout: the window now sizes against the real render area instead of a DPI-divided client rect (text was needlessly
  small on high-DPI displays), result rows derive their height from the font so the second line is never clipped,
  long names end in an ellipsis, and the detail pane measures its content instead of using a fixed height.
- Dataset bumped to `1.2.0`: 1,203 entries, 516 recipes, 289 tech unlocks.
- In-game verification: `Debug.SelfTest` passes 16/16 checks (adds `汉堡` and pumpkin usage), no plugin warnings or
  errors.

### Known issues

- Clicking a cross-link inside the detail pane can close the game: the selection is applied during the same `OnGUI`
  pass that is still laying out the previous entry. Documented in `README.md`; fix pending.

## 0.2.0

- Result list can be filtered by type (items, buildings, tech, and future quest/mechanic pages); tab counts follow the
  current query.
- Keyboard navigation: `↑`/`↓` and `Page Up`/`Page Down` move through the results and keep the selected row visible;
  `Enter` opens the first result when nothing is selected.
- Browse mode only draws the rows in view, so the 1,146-entry list stays responsive.
- Quest wording `金星爱情故事` now resolves to the story item with a visible redirect note, as required by the spec.
  Dataset bumped to `1.1.0`; the alias is also part of the extractor's curated table.
- In-game verification: `Debug.SelfTest` passes 14/14 checks, including 180 window draw passes without UI errors.

## 0.1.0

- Initial bootstrap of the in-game wiki book.
- Dataset generated from an installed copy of the game (Steam build 22583570): 1,146 items/objects/techs with recipes,
  tech unlocks and quality grade groups, in Simplified Chinese and English.
- Search with alias resolution, quality-prefixed aliases and fuzzy ranking.
- Vanilla-palette window with a scalable dynamic font, result list, detail page, recipe/unlock cross-links.
- Offline extraction pipeline (`ci/extract`) and 21 out-of-game core checks.
- Out-of-game core test harness and CI stub build.
- Optional `Debug.SelfTest` in-game checks.
