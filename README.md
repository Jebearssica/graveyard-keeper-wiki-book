# Graveyard Keeper Wiki Book

An in-game, searchable wiki book for **Graveyard Keeper 1**: press one key, type what you remember, and get the
item/building/tech page with its recipe, unlock path and cross-links.

> A screenshot/GIF of the in-game window will be added after a manual play session. The plugin builds, its data
> pipeline is verified, and the in-game self-test passes 16/16 checks, including the window draw path; see
> [Limitations](#limitations).

## Features

- Tolerates non-exact queries: full-width input, punctuation, English typos and partial Chinese names are normalized
  before ranking.
- Explicit alias table, so player wording that differs from the game name still resolves with a visible note
  (`木头` → `原木`, `铁块` → `铁锭`, quest wording `金星爱情故事` → `精彩的故事`).
- Filter the list by type (items, buildings, tech, and future quest/mechanic pages); the counts follow the current
  query. The browse list only draws visible rows, so all 1,203 entries stay responsive.
- Detail pages answer the likely intent: description, recipe inputs/outputs/station, unlock tech, quality grades,
  **used in** (every recipe that consumes the entry, including quest items), related entries and clickable cross-links.
- Searching a material by name (`南瓜`, `iron ingot`) also lists the recipes that consume it, marked `材料：南瓜`, so
  "what can I cook with this" is one query away.
- Quality grades from one recipe share one page with a `qualities` list, so "how to make a gold X" covers every grade.
- Dataset generated from the game itself: 1,203 entries (748 items, 291 objects/buildings, 164 tech nodes), 516
  recipes and 289 tech unlocks, in Simplified Chinese and English.
- Vanilla-palette window with a dynamic OS font instead of the pixelated vanilla bitmap font; all sizes scale with the
  resolution.
- Missing or invalid dataset degrades to an empty book with a logged warning instead of throwing.

## Install

1. Install [BepInEx 5.4.23.x (x64)](https://github.com/BepInEx/BepInEx/releases) into the game folder.
2. Copy `WikiBook.dll` to `BepInEx\plugins\WikiBook\`.
3. Launch the game and press `F2`.

## Usage

| Action | Key |
|---|---|
| Open / close the book | `F2` (configurable) |
| Search | type in the search field, press `Enter` to open the first result |
| Move through results | `↑` / `↓`, `Page Up` / `Page Down` |
| Filter by type | click a tab under the search field |
| Close | `Esc` or the `×` button |
| Follow a link | click a recipe ingredient, station, tech or related entry |

With an empty query the left column lists every entry, so the book also works as a browse reference.

## Configuration

The config file is created at `BepInEx\config\gk.wikibook.cfg`.

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | `OpenKey` | `F2` | Unity key name that toggles the book. |
| General | `Language` | `auto` | `auto` follows the game language; otherwise `zh-Hans` or `en`. |
| Debug | `SelfTest` | `false` | Runs one-shot search/UI checks shortly after the game starts and logs `[SELFTEST] PASS/FAIL`. |

## Building from source

Prerequisites: .NET SDK 8 (the SDK, not the runtime) and an extracted BepInEx 5 core for the no-game build.

```powershell
# With the game in the default Steam library: builds and deploys to BepInEx\plugins\WikiBook.
dotnet build -c Release

# Game in another library.
dotnet build -c Release -p:GameDir="<game install>"

# No game installed (what CI does): game assembly comes from the stub in ci/refs.
dotnet build -c Release -p:UseGameRefs=false -p:DeployToGame=false -p:BepInExCore="<extracted BepInEx>\core"

# Out-of-game checks for the dataset, alias and ranking core.
dotnet run --project tests/WikiBook.Tests/WikiBook.Tests.csproj -c Release
```

`UseGameRefs` is auto-detected from `<GameDir>\Graveyard Keeper_Data\Managed\Assembly-CSharp.dll`; `DeployToGame` is
auto-detected from `<GameDir>\BepInEx\plugins`. Both can be overridden.

## Dataset

`data/wiki-zh-Hans.json` is embedded into the assembly and is generated, not hand-written:

```powershell
pip install -r ci/extract/requirements.txt
python ci/extract/extract.py "<game install>" data/wiki-zh-Hans.json
```

The extractor reads the game's `resources.assets` (GameBalance plus the `lng_zh_cn`/`lng_en` tables) offline and emits
the versioned schema. See [ci/extract/README.md](ci/extract/README.md) for the mapping rules, the curated additions and
what cannot be extracted. Rerun the core tests after regenerating; they validate the schema, ids, recipes, unlocks and
ranking.

## Limitations

- **Known issue (unresolved):** clicking a cross-link inside the detail pane (for example `一碗兵豆` from the `兵豆`
  page) can close the game. The click is applied while the same `OnGUI` pass is still walking the page, so the
  selection changes mid-layout and the remaining draws use a stale entry. The fix is to defer selection changes to
  the start of the next frame and to consume the click; the main-menu hit test also needs checking because the book
  does not block clicks from reaching the window underneath.
- The in-game self-test passes (16/16, including 180 window draw passes without UI errors), but the window has not
  been verified by hand in a play session yet: the screenshot/GIF and interactive mouse/keyboard checks are still
  pending.
- HUD tasks/quests cannot be extracted offline: the game builds them from save data (`KnownNPC.TaskState`) and formats
  the text at runtime, and flow quests have no player-visible names. Items, objects/buildings and tech nodes are
  covered.
- Most techs have no description in the game data; those pages show name and unlock path rather than invented text.
- Two hidden resources that recipes consume have no localization key in the build; the extractor supplies their
  established names (Iron ore).
- The window is IMGUI-based rather than a `BaseGUI` window and does not reuse vanilla sprites yet. Text is rendered with
  a dynamic font, which is the part that matters for high-resolution legibility.
- There is no in-game button in the vanilla UI; the book opens from a hotkey.
- Only Simplified Chinese and English are shipped. The loader falls back between languages instead of showing an empty
  page.

---

# 守墓人 Wiki 手册

一个游戏内可搜索的《守墓人 1》Wiki 手册：按一个键，输入你记得的词，就能看到物品/建筑/科技页面，以及配方、解锁
条件和相关链接。

> 游戏内窗口的截图/GIF 会在人工试玩后补充。插件可以构建、数据流水线已验证，实机自检（包含窗口绘制）已在游戏内
> 16/16 通过，详见 [限制](#限制)。

## 功能

- 容忍非精确输入：全角字符、标点、英文拼写错误和部分中文名都会先归一化再排序。
- 显式别名表：玩家常用说法与游戏名不同时也能命中，并显示说明（`木头` → `原木`、`铁块` → `铁锭`、任务用语
  `金星爱情故事` → `精彩的故事`）。
- 结果可按类型筛选（物品、建筑、科技，以及未来的任务/机制页），标签计数跟随当前查询；浏览列表只绘制可见行，所以
  1,203 条条目也能保持流畅。
- 详情页回答真实意图：说明、配方输入/产出/制作台、解锁科技、品质等级、**用于制作**（所有消耗本条目的配方，包含
  任务道具）、相关条目和可点击的交叉链接。
- 搜索材料名（`南瓜`、`铁锭`）时会同时列出消耗它的配方，并标注 `材料：南瓜`，一次查询就能回答“这个能做什么”。
- 同一配方的品质等级合并为一页，用 `qualities` 列出各等级，所以“怎么做金星 X”会覆盖全部品质。
- 数据直接从游戏生成：1,203 条（748 物品、291 建筑/物件、164 科技节点）、516 个配方、289 个科技解锁，包含简体
  中文与英文。
- 原版配色的窗口，使用动态系统字体而不是原版点阵字体；所有尺寸随分辨率缩放。
- 数据集缺失或损坏时记录警告并降级为空手册，不会抛出异常。

## 安装

1. 把 [BepInEx 5.4.23.x（x64）](https://github.com/BepInEx/BepInEx/releases) 安装到游戏目录。
2. 把 `WikiBook.dll` 复制到 `BepInEx\plugins\WikiBook\`。
3. 启动游戏，按 `F2`。

## 使用

| 操作 | 按键 |
|---|---|
| 打开/关闭手册 | `F2`（可配置） |
| 搜索 | 在搜索框输入，按 `Enter` 打开第一条 |
| 在结果中移动 | `↑` / `↓`、`Page Up` / `Page Down` |
| 按类型筛选 | 点击搜索框下方的标签 |
| 关闭 | `Esc` 或 `×` 按钮 |
| 跳转链接 | 点击配方材料、制作台、科技或相关条目 |

搜索框为空时左侧会列出全部条目，也可以当浏览手册使用。

## 配置

配置文件位于 `BepInEx\config\gk.wikibook.cfg`。

| 分组 | 键 | 默认值 | 说明 |
|---|---|---|---|
| General | `OpenKey` | `F2` | 开关手册的 Unity 按键名。 |
| General | `Language` | `auto` | `auto` 跟随游戏语言；也可写 `zh-Hans` 或 `en`。 |
| Debug | `SelfTest` | `false` | 游戏启动后运行一次搜索/界面自检，并输出 `[SELFTEST] PASS/FAIL`。 |

## 从源码构建

需要 .NET SDK 8（是 SDK，不是运行时）；无游戏构建还需要解压好的 BepInEx 5 core。

```powershell
# 游戏在默认 Steam 库：构建并部署到 BepInEx\plugins\WikiBook。
dotnet build -c Release

# 游戏在其它库。
dotnet build -c Release -p:GameDir="<game install>"

# 没有游戏（CI 的做法）：游戏程序集来自 ci/refs 的声明桩。
dotnet build -c Release -p:UseGameRefs=false -p:DeployToGame=false -p:BepInExCore="<extracted BepInEx>\core"

# 不开游戏，验证数据集、别名与排序核心。
dotnet run --project tests/WikiBook.Tests/WikiBook.Tests.csproj -c Release
```

`UseGameRefs` 会根据 `<GameDir>\Graveyard Keeper_Data\Managed\Assembly-CSharp.dll` 自动判断；`DeployToGame`
会根据 `<GameDir>\BepInEx\plugins` 自动判断，两者都可以手动覆盖。

## 数据集

`data/wiki-zh-Hans.json` 会内嵌进程序集，由脚本生成而不是手写：

```powershell
pip install -r ci/extract/requirements.txt
python ci/extract/extract.py "<game install>" data/wiki-zh-Hans.json
```

提取器离线读取游戏的 `resources.assets`（GameBalance 与 `lng_zh_cn`/`lng_en` 语言表），输出带版本的数据集。映射
规则、人工补充内容以及无法提取的部分见 [ci/extract/README.md](ci/extract/README.md)。重新生成后请运行核心测试，
它会校验 schema、id、配方、解锁和排序。

## 限制

- **已知问题（未解决）**：点击详情页里的交叉链接（例如在“兵豆”页面点击“一碗兵豆”）会让游戏直接退出。原因是点击在
  同一次 `OnGUI` 绘制过程中就改掉了当前条目，后续布局仍按旧条目继续绘制；修复方向是把选择推迟到下一帧开始时应用，
  并消费掉这次点击。另外手册窗口没有拦截点击，底层界面的按钮（主菜单也有按钮）仍可能收到同一次点击，需要一并核查。
- 实机自检已 16/16 通过（包含 180 次窗口绘制且无 UI 异常），但还没有人工试玩验证窗口：截图/GIF 以及鼠标/键盘
  交互检查仍待完成。
- 游戏 HUD 里的任务无法离线提取：它们由存档数据（`KnownNPC.TaskState`）生成、文本在运行时拼装，流程任务也没有
  玩家可见的名字。物品、物件/建筑和科技节点已经覆盖。
- 大多数科技在游戏数据里没有说明；这些页面只显示名称和解锁路径，不编造内容。
- 有两个配方会用到的隐藏资源在游戏里没有本地化键，提取器为它们补上了通用名称（铁矿石）。
- 窗口基于 IMGUI，还不是 `BaseGUI` 窗口，也尚未复用原版精灵；文字使用动态字体，这是高分辨率可读性的关键部分。
- 原版 UI 里还没有入口按钮，手册通过快捷键打开。
- 目前只发布简体中文和英文内容。加载器会在语言之间回退，不会显示空白页。
