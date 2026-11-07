using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace WikiBook;

/// <summary>
///     The in-game window. Deliberately built from IMGUI with a dynamic OS font instead of the vanilla bitmap font, so
///     text stays crisp when the game runs above 1080p. Every size is derived from one scale factor so the window
///     fits any resolution.
/// </summary>
public sealed class WikiBookGui : MonoBehaviour
{
    private const float BaseWidth = 1180f;
    private const float BaseHeight = 700f;

    private const string SearchControl = "WikiBookSearch";

    private static readonly Color32 PanelColor = new Color32(239, 227, 200, 255);
    private static readonly Color32 BorderColor = new Color32(74, 56, 38, 255);
    private static readonly Color32 RowColor = new Color32(227, 212, 180, 255);
    private static readonly Color32 RowHoverColor = new Color32(214, 194, 156, 255);
    private static readonly Color32 RowSelectedColor = new Color32(198, 174, 124, 255);
    private static readonly Color32 InputColor = new Color32(250, 244, 228, 255);
    private static readonly Color32 TextColor = new Color32(51, 37, 21, 255);
    private static readonly Color32 MutedColor = new Color32(111, 90, 62, 255);
    private static readonly Color32 AccentColor = new Color32(122, 46, 46, 255);
    private static readonly Color32 LinkColor = new Color32(47, 93, 124, 255);

    private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

    private WikiSearch _search;
    private WikiDataset _dataset;
    private Func<string> _language;
    private Func<KeyCode> _hotkey;

    private bool _open;
    private bool _focusSearch;
    private string _query = string.Empty;
    private List<SearchResult> _results = new List<SearchResult>();
    private WikiEntry _selected;
    private SearchResult _selectedResult;
    private Vector2 _resultScroll;
    private Vector2 _detailScroll;
    private EntryKind? _kindFilter;
    private readonly Dictionary<EntryKind, int> _kindCounts = new Dictionary<EntryKind, int>();
    private readonly List<EntryKind> _filterKinds = new List<EntryKind>();
    private int _totalCount;
    private float _listRowHeight;
    private float _listViewHeight;
    private float _detailContentHeight;
    private int _errorCount;
    private int _drawCount;
    private float _loggedScale = -1f;

    private float _builtScale = -1f;
    private Vector2 _guiSize;
    private Font _font;
    private int _fontSize;

    private GUIStyle _title;
    private GUIStyle _close;
    private GUIStyle _input;
    private GUIStyle _row;
    private GUIStyle _rowSelected;
    private GUIStyle _rowTitle;
    private GUIStyle _small;
    private GUIStyle _aliasSmall;
    private GUIStyle _rowMeta;
    private GUIStyle _rowAlias;
    private GUIStyle _tab;
    private GUIStyle _tabActive;
    private GUIStyle _body;
    private GUIStyle _heading;
    private GUIStyle _detailTitle;
    private GUIStyle _link;
    private GUIStyle _status;

    internal static WikiBookGui Instance { get; private set; }

    internal bool IsOpen => _open;

    internal int ResultCount => _results.Count;

    internal string SelectedId => _selected != null ? _selected.Id : null;

    internal int DrawCount => _drawCount;

    internal int ErrorCount => _errorCount;

    internal void SetKindFilter(EntryKind? kind)
    {
        if (_kindFilter == kind)
        {
            return;
        }

        _kindFilter = kind;
        Refresh();
    }

    internal void Initialize(WikiSearch search, WikiDataset dataset, Func<string> language, Func<KeyCode> hotkey)
    {
        _search = search;
        _dataset = dataset ?? WikiDataset.Empty;
        _language = language;
        _hotkey = hotkey;

        // Keep the type filter to kinds this dataset actually contains, so future quest/mechanic pages show up.
        _filterKinds.Clear();
        var order = new[] { EntryKind.Item, EntryKind.Building, EntryKind.Tech, EntryKind.Quest, EntryKind.Mechanic };
        for (var index = 0; index < order.Length; index++)
        {
            foreach (var entry in _dataset.Entries.Values)
            {
                if (entry.Kind == order[index])
                {
                    _filterKinds.Add(order[index]);
                    break;
                }
            }
        }

        Refresh();
    }

    internal void Toggle()
    {
        if (_open)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    internal void Open()
    {
        _open = true;
        _focusSearch = true;
        Refresh();
    }

    internal void Close()
    {
        _open = false;
    }

    internal void SelectEntry(string id)
    {
        if (_dataset == null)
        {
            return;
        }

        var entry = _dataset.Find(id);
        if (entry == null)
        {
            return;
        }

        _selected = entry;
        _selectedResult = FindResult(entry.Id);
        _detailScroll = Vector2.zero;
        _detailContentHeight = 0f;
    }

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        foreach (var texture in _textures.Values)
        {
            if (texture != null)
            {
                Destroy(texture);
            }
        }

        _textures.Clear();

        if (_font != null)
        {
            Destroy(_font);
            _font = null;
        }
    }

    private void Update()
    {
        try
        {
            var key = _hotkey != null ? _hotkey() : KeyCode.F2;
            if (Input.GetKeyDown(key))
            {
                Toggle();
            }

            if (_open && Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private void OnGUI()
    {
        if (!_open || _search == null)
        {
            return;
        }

        try
        {
            // The game leaves a scaled GUI.matrix behind; positions below are raw screen pixels.
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            try
            {
                Draw();
            }
            finally
            {
                GUI.matrix = previousMatrix;
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private void Draw()
    {
        _drawCount++;
        _guiSize = ResolveGuiSize();
        var scale = Mathf.Clamp(
            Mathf.Min((_guiSize.x - 40f) / BaseWidth, (_guiSize.y - 40f) / BaseHeight), 0.7f, 2.5f);
        EnsureStyles(scale);
        if (Mathf.Abs(_loggedScale - scale) > 0.001f)
        {
            _loggedScale = scale;
            if (Plugin.Log != null)
            {
                Plugin.Log.LogInfo("UI layout: screen " + Screen.width + "x" + Screen.height + ", scale " +
                                   scale.ToString("0.00") + ", window " +
                                   Mathf.RoundToInt(BaseWidth * scale) + "x" +
                                   Mathf.RoundToInt(BaseHeight * scale) + ", font " + _fontSize + "px" +
                                   WindowMetrics() + ".");
            }
        }

        // Handled before the text field is drawn so arrow keys move the selection instead of the caret.
        HandleKeys();

        var language = Language();
        // Lower depth draws above the vanilla windows that are already on screen.
        GUI.depth = -1000;
        GUI.DrawTexture(new Rect(0, 0, _guiSize.x, _guiSize.y), Texture("dim", new Color32(0, 0, 0, 150)));

        var width = BaseWidth * scale;
        var height = BaseHeight * scale;
        var left = Mathf.Round((_guiSize.x - width) / 2f);
        var top = Mathf.Round((_guiSize.y - height) / 2f);
        var pad = 22f * scale;

        GUI.DrawTexture(new Rect(left - 3, top - 3, width + 6, height + 6), Texture("border", BorderColor));
        GUI.DrawTexture(new Rect(left, top, width, height), Texture("panel", PanelColor));

        var headerTop = top + pad;
        GUI.Label(new Rect(left + pad, headerTop, width - 2 * pad - 36 * scale, 34 * scale),
            T("守墓人 Wiki 手册", "Graveyard Keeper Wiki Book"), _title);
        if (GUI.Button(new Rect(left + width - pad - 30 * scale, headerTop, 30 * scale, 30 * scale), "×", _close))
        {
            Close();
        }

        GUI.Label(new Rect(left + pad, headerTop + 34 * scale, width - 2 * pad, 20 * scale),
            T("搜索物品、建筑、任务与机制；支持别名与模糊匹配。",
                "Search items, buildings, quests and mechanics. Aliases and fuzzy matching included."), _small);

        var inputTop = headerTop + 62 * scale;
        var inputRect = new Rect(left + pad, inputTop, width - 2 * pad, 42 * scale);
        GUI.SetNextControlName(SearchControl);
        var typed = GUI.TextField(inputRect, _query ?? string.Empty, 200, _input);
        if (!string.Equals(typed, _query, StringComparison.Ordinal))
        {
            _query = typed;
            Refresh();
        }

        if (_focusSearch)
        {
            GUI.FocusControl(SearchControl);
            _focusSearch = false;
        }

        if (string.IsNullOrEmpty(_query))
        {
            GUI.Label(new Rect(inputRect.xMax - 320 * scale, inputRect.y + 11 * scale, 300 * scale, 20 * scale),
                T("例如：铁锭、金星爱情故事", "e.g. iron ingot, golden story"), _aliasSmall);
        }

        var tabTop = inputRect.yMax + 10 * scale;
        var tabHeight = 26 * scale;
        DrawFilters(new Rect(left + pad, tabTop, width - 2 * pad, tabHeight), scale);

        var contentTop = tabTop + tabHeight + 10 * scale;
        var footerHeight = 18 * scale;
        var bottom = top + height - pad - footerHeight;
        var gap = 16 * scale;
        var leftWidth = (width - 2 * pad - gap) * 0.43f;
        var listRect = new Rect(left + pad, contentTop, leftWidth, bottom - contentTop);
        var detailRect = new Rect(listRect.xMax + gap, contentTop,
            width - 2 * pad - leftWidth - gap, bottom - contentTop);

        DrawResults(listRect, language, scale);
        DrawDetail(detailRect, language, scale);

        GUI.Label(new Rect(left + pad, bottom + 6 * scale, width - 2 * pad, footerHeight),
            T("数据 " + _dataset.DataVersion + " · " + _dataset.Entries.Count + " 条 · 按 " + HotkeyName() + " 关闭",
                "Data " + _dataset.DataVersion + " · " + _dataset.Entries.Count + " entries · press " + HotkeyName() +
                " to close"), _status);
    }

    private void DrawFilters(Rect area, float scale)
    {
        var gap = 6 * scale;
        var tabs = _filterKinds.Count + 1;
        var width = (area.width - gap * (tabs - 1)) / tabs;
        TabButton(new Rect(area.x, area.y, width, area.height), null, T("全部", "All"));

        for (var index = 0; index < _filterKinds.Count; index++)
        {
            var kind = _filterKinds[index];
            var x = area.x + (index + 1) * (width + gap);
            TabButton(new Rect(x, area.y, width, area.height), kind, KindShortLabel(kind));
        }
    }

    private string KindShortLabel(EntryKind kind)
    {
        switch (kind)
        {
            case EntryKind.Item:
                return T("物品", "Items");
            case EntryKind.Building:
                return T("建筑", "Buildings");
            case EntryKind.Tech:
                return T("科技", "Techs");
            case EntryKind.Quest:
                return T("任务", "Quests");
            default:
                return T("机制", "Mechanics");
        }
    }

    private void TabButton(Rect rect, EntryKind? kind, string label)
    {
        var count = 0;
        if (kind.HasValue)
        {
            _kindCounts.TryGetValue(kind.Value, out count);
        }
        else
        {
            count = _totalCount;
        }

        var active = _kindFilter == kind;
        if (GUI.Button(rect, label + " (" + count + ")", active ? _tabActive : _tab) && !active)
        {
            _kindFilter = kind;
            Refresh();
        }
    }

    private void DrawResults(Rect area, string language, float scale)
    {
        var headerHeight = 22 * scale;
        var searching = !string.IsNullOrEmpty(_query) && _query.Trim().Length > 0;
        var countText = searching
            ? T("搜索结果（" + _results.Count + "）", "Results (" + _results.Count + ")")
            : T("全部条目（" + _results.Count + "）", "All entries (" + _results.Count + ")");
        GUI.Label(new Rect(area.x, area.y, area.width, headerHeight), countText, _heading);

        var view = new Rect(area.x, area.y + headerHeight, area.width, area.height - headerHeight);
        // Derive the row from the actual font sizes: a fixed 60px row clip the second line at small scales.
        var rowPad = 7f * scale;
        var titleLine = Mathf.Ceil(_rowTitle.fontSize * 1.45f);
        var metaLine = Mathf.Ceil(_rowMeta.fontSize * 1.55f);
        var rowHeight = rowPad + titleLine + 2f * scale + metaLine + rowPad;
        var contentWidth = view.width - 16 * scale;
        var contentHeight = Mathf.Max(view.height, _results.Count * rowHeight);
        _resultScroll = GUI.BeginScrollView(view, _resultScroll,
            new Rect(0, 0, contentWidth, contentHeight), false, true);

        if (_results.Count == 0)
        {
            GUI.Label(new Rect(8 * scale, 8 * scale, contentWidth - 16 * scale, 60 * scale),
                _kindFilter.HasValue
                    ? T("当前类型下没有匹配的条目。选择“全部”查看其它类型。",
                        "No matching entry in this type. Choose “All” to see the other types.")
                    : T("没有匹配的条目。试试更短的关键词，或输入任务里的说法。",
                        "No matching entry. Try a shorter query, or the wording used by the quest."), _body);
        }

        // Browse mode lists all 1,146 entries; drawing every row would rebuild a thousand buttons per frame.
        _listRowHeight = rowHeight;
        _listViewHeight = view.height;
        var firstVisible = Mathf.Max(0, Mathf.FloorToInt(_resultScroll.y / rowHeight) - 1);
        var lastVisible = Mathf.Min(_results.Count - 1,
            Mathf.CeilToInt((_resultScroll.y + view.height) / rowHeight) + 1);

        for (var index = firstVisible; index <= lastVisible; index++)
        {
            var result = _results[index];
            var rowRect = new Rect(0, index * rowHeight, contentWidth, rowHeight - 4 * scale);
            var selected = _selected != null && string.Equals(_selected.Id, result.Entry.Id, StringComparison.Ordinal);
            if (GUI.Button(rowRect, GUIContent.none, selected ? _rowSelected : _row))
            {
                Select(result);
            }

            var textX = rowRect.x + 12 * scale;
            var textWidth = rowRect.width - 24 * scale;
            GUI.Label(new Rect(textX, rowRect.y + rowPad, textWidth, titleLine),
                Ellipsize(_rowTitle, result.Entry.NameFor(language), textWidth), _rowTitle);

            var meta = KindLabel(result.Entry.Kind);
            if (!string.IsNullOrEmpty(result.Entry.Quality))
            {
                meta += " · " + QualityLabel(result.Entry.Quality);
            }

            var note = result.Redirected ? result.AliasNote : result.RelationNote;
            var metaTop = rowRect.y + rowPad + titleLine + 2f * scale;
            if (!string.IsNullOrEmpty(note))
            {
                GUI.Label(new Rect(textX, metaTop, textWidth, metaLine),
                    Ellipsize(_rowAlias, meta + " · " + note, textWidth), _rowAlias);
            }
            else
            {
                GUI.Label(new Rect(textX, metaTop, textWidth, metaLine), Ellipsize(_rowMeta, meta, textWidth),
                    _rowMeta);
            }
        }

        GUI.EndScrollView();
    }

    private void DrawDetail(Rect area, string language, float scale)
    {
        var view = new Rect(area.x, area.y, area.width, area.height);
        var contentWidth = view.width - 18 * scale;
        // The content height is measured on the previous frame; a fixed height cut the last section off.
        var contentHeight = Mathf.Max(view.height, _detailContentHeight + 12 * scale);
        _detailScroll = GUI.BeginScrollView(view, _detailScroll,
            new Rect(0, 0, contentWidth, contentHeight), false, true);

        if (_selected == null)
        {
            GUI.Label(new Rect(0, 0, contentWidth, 60 * scale),
                T("从左侧选择一个条目查看配方、解锁方式和相关链接。",
                    "Pick an entry on the left to see its recipe, unlock path and cross-links."), _body);
            _detailContentHeight = 0;
            GUI.EndScrollView();
            return;
        }

        var x = 0f;
        var width = contentWidth;
        var y = 0f;
        var gap = 6 * scale;

        y = Text(x, y, width, _selected.NameFor(language), _detailTitle, gap);
        var meta = KindLabel(_selected.Kind);
        if (!string.IsNullOrEmpty(_selected.Quality))
        {
            meta += " · " + QualityLabel(_selected.Quality);
        }

        if (!string.IsNullOrEmpty(_selected.VariantOf))
        {
            meta += " · " + T("品质变体", "quality variant");
        }

        y = Text(x, y, width, meta, _small, gap);

        if (_selectedResult != null && _selectedResult.Redirected)
        {
            y = Text(x, y, width, _selectedResult.AliasNote, _aliasSmall, gap);
            y = Text(x, y, width,
                T("搜索已按别名重定向到本条目。", "Search redirected to this entry through an alias."), _small, gap);
        }
        else if (_selectedResult != null && !string.IsNullOrEmpty(_selectedResult.RelationNote))
        {
            y = Text(x, y, width, _selectedResult.RelationNote, _aliasSmall, gap);
            y = Text(x, y, width,
                T("搜索结果来自配方材料：有配方消耗本条目。", "Listed because a recipe consumes this entry."), _small,
                gap);
        }

        if (!string.IsNullOrEmpty(_selected.Source))
        {
            y = Text(x, y, width, T("来源：", "Source: ") + _selected.Source, _small, gap);
        }

        y += 8 * scale;
        y = Text(x, y, width, T("说明", "Description"), _heading, 2 * scale);
        var description = _selected.Descriptions.Get(language);
        y = Text(x, y, width,
            string.IsNullOrEmpty(description) ? T("数据集没有记录说明。", "No description in the dataset.") : description,
            _body, gap);
        y += 8 * scale;

        if (_selected.HasQualities)
        {
            y = Text(x, y, width, T("品质", "Quality"), _heading, 2 * scale);
            y = Text(x, y, width,
                T("同一配方会按品质产出其中一个，每个品质在游戏里是独立物品。",
                    "One recipe produces one of these grades; every grade is a separate item in the game."),
                _small, gap);
            for (var index = 0; index < _selected.Qualities.Count; index++)
            {
                var quality = _selected.Qualities[index];
                y = Text(x, y, width, QualityLabel(quality.Grade) + " · " + quality.Names.Get(language), _body,
                    2 * scale);
            }

            y += 8 * scale;
        }

        y = Text(x, y, width, T("配方", "Recipe"), _heading, 2 * scale);
        if (_selected.HasRecipe)
        {
            y = Text(x, y, width, T("输入", "Inputs"), _small, 2 * scale);
            for (var index = 0; index < _selected.Recipe.Inputs.Count; index++)
            {
                var ingredient = _selected.Recipe.Inputs[index];
                y = IngredientLink(x, y, width, ingredient, language, scale);
            }

            y = Text(x, y, width, T("产出", "Outputs"), _small, 2 * scale);
            for (var index = 0; index < _selected.Recipe.Outputs.Count; index++)
            {
                var ingredient = _selected.Recipe.Outputs[index];
                y = IngredientLink(x, y, width, ingredient, language, scale);
            }

            if (!string.IsNullOrEmpty(_selected.Recipe.StationId))
            {
                y = Link(x, y, width, T("制作台：", "Station: ") + _dataset.NameOf(_selected.Recipe.StationId, language),
                    _selected.Recipe.StationId, scale);
            }

            if (_selected.Recipe.Seconds > 0)
            {
                y = Text(x, y, width,
                    T("耗时：" + _selected.Recipe.Seconds + " 秒", "Time: " + _selected.Recipe.Seconds + " s"), _small,
                    gap);
            }
        }
        else
        {
            y = Text(x, y, width,
                T("没有记录配方；这个条目通过采集、掉落或任务获得。",
                    "No recipe recorded; this entry is obtained by gathering, loot or a quest."), _body, gap);
        }

        // The other direction of the recipe graph: everything this entry is an ingredient of, including quest items.
        var uses = _dataset.Uses(_selected.Id);
        if (uses.Count > 0)
        {
            // Read in the player's language order, not in id order.
            uses = new List<RecipeUse>(uses);
            uses.Sort((left, right) => string.CompareOrdinal(left.Entry.NameFor(language),
                right.Entry.NameFor(language)));
            y += 8 * scale;
            y = Text(x, y, width, T("用于制作（" + uses.Count + "）", "Used in (" + uses.Count + ")"), _heading,
                2 * scale);
            y = Text(x, y, width,
                T("下面这些条目的配方会消耗本条目。", "These recipes consume this entry."), _small, gap);
            for (var index = 0; index < uses.Count; index++)
            {
                var use = uses[index];
                var label = use.Entry.NameFor(language) +
                            T("（消耗 " + use.Amount + "）", " (uses " + use.Amount + ")");
                y = Link(x, y, width, label, use.Entry.Id, scale);
            }
        }

        y += 8 * scale;
        y = Text(x, y, width, T("解锁", "Unlock"), _heading, 2 * scale);
        var hasUnlock = false;
        if (!string.IsNullOrEmpty(_selected.UnlockTechId))
        {
            hasUnlock = true;
            y = Link(x, y, width, T("科技：", "Tech: ") + _dataset.NameOf(_selected.UnlockTechId, language),
                _selected.UnlockTechId, scale);
        }

        if (!string.IsNullOrEmpty(_selected.UnlockQuestId))
        {
            hasUnlock = true;
            y = Link(x, y, width, T("任务：", "Quest: ") + _dataset.NameOf(_selected.UnlockQuestId, language),
                _selected.UnlockQuestId, scale);
        }

        if (!hasUnlock)
        {
            y = Text(x, y, width, T("没有记录解锁条件。", "No unlock recorded."), _body, gap);
        }

        if (!string.IsNullOrEmpty(_selected.VariantOf) && _dataset.Find(_selected.VariantOf) != null)
        {
            y += 8 * scale;
            y = Text(x, y, width, T("所属条目", "Base entry"), _heading, 2 * scale);
            y = Link(x, y, width, _dataset.NameOf(_selected.VariantOf, language), _selected.VariantOf, scale);
        }

        var variants = _dataset.VariantsOf(_selected.Id);
        if (variants.Count > 0)
        {
            y += 8 * scale;
            y = Text(x, y, width, T("品质变体", "Quality variants"), _heading, 2 * scale);
            y = Text(x, y, width,
                T("同一配方的每个品质都是独立物品，制作时按品质分别查询。",
                    "Every quality from one recipe is a separate item; check each grade separately."), _small, gap);
            for (var index = 0; index < variants.Count; index++)
            {
                y = Link(x, y, width, variants[index].NameFor(language), variants[index].Id, scale);
            }
        }

        var related = RelatedEntries(_selected);
        if (related.Count > 0)
        {
            y += 8 * scale;
            y = Text(x, y, width, T("相关", "Related"), _heading, 2 * scale);
            for (var index = 0; index < related.Count; index++)
            {
                y = Link(x, y, width, related[index].NameFor(language), related[index].Id, scale);
            }
        }

        _detailContentHeight = y;
        GUI.EndScrollView();
    }

    private float IngredientLink(float x, float y, float width, Ingredient ingredient, string language, float scale)
    {
        var name = _dataset.NameOf(ingredient.Id, language);
        return Link(x, y, width, ingredient.Amount + " × " + name, ingredient.Id, scale);
    }

    private float Link(float x, float y, float width, string label, string targetId, float scale)
    {
        if (string.IsNullOrEmpty(targetId) || _dataset.Find(targetId) == null)
        {
            return Text(x, y, width, label, _small, 2 * scale);
        }

        var content = new GUIContent(label);
        var size = _link.CalcSize(content);
        var rect = new Rect(x, y, Mathf.Min(width, Mathf.Max(size.x, 40 * scale)), Mathf.Max(22 * scale, size.y));
        if (GUI.Button(rect, content, _link))
        {
            SelectEntry(targetId);
        }

        return rect.yMax + 2 * scale;
    }

    /// <summary>
    ///     Truncates to the widest prefix that fits plus an ellipsis. Unity 2021 has no
    ///     <c>TextClipping.Ellipsis</c>, and plain clipping cuts glyphs in half.
    /// </summary>
    private static string Ellipsize(GUIStyle style, string text, float width)
    {
        if (string.IsNullOrEmpty(text) || style == null || width <= 0f)
        {
            return text ?? string.Empty;
        }

        if (style.CalcSize(new GUIContent(text)).x <= width)
        {
            return text;
        }

        const string suffix = "…";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var candidate = text.Substring(0, middle) + suffix;
            if (style.CalcSize(new GUIContent(candidate)).x <= width)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low <= 0 ? suffix : text.Substring(0, low) + suffix;
    }

    private float Text(float x, float y, float width, string text, GUIStyle style, float gap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return y;
        }

        var height = style.CalcHeight(new GUIContent(text), width);
        GUI.Label(new Rect(x, y, width, height), text, style);
        return y + height + gap;
    }

    private void HandleKeys()
    {
        var current = Event.current;
        if (current == null || current.type != EventType.KeyDown)
        {
            return;
        }

        switch (current.keyCode)
        {
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                if (!IsSelectionVisible() && _results.Count > 0)
                {
                    Select(_results[0]);
                }

                current.Use();
                break;
            case KeyCode.DownArrow:
                MoveSelection(1);
                current.Use();
                break;
            case KeyCode.UpArrow:
                MoveSelection(-1);
                current.Use();
                break;
            case KeyCode.PageDown:
                MoveSelection(PageStep());
                current.Use();
                break;
            case KeyCode.PageUp:
                MoveSelection(-PageStep());
                current.Use();
                break;
        }
    }

    internal void MoveSelection(int delta)
    {
        if (_results.Count == 0)
        {
            return;
        }

        var index = IndexOfSelected();
        if (index < 0)
        {
            index = delta >= 0 ? 0 : _results.Count - 1;
        }
        else
        {
            index = Mathf.Clamp(index + delta, 0, _results.Count - 1);
        }

        Select(_results[index]);

        // Keep the selected row inside the scroll view; the metrics come from the last DrawResults pass.
        if (_listRowHeight <= 0 || _listViewHeight <= 0)
        {
            return;
        }

        var top = index * _listRowHeight;
        if (top < _resultScroll.y)
        {
            _resultScroll.y = top;
        }
        else if (top + _listRowHeight > _resultScroll.y + _listViewHeight)
        {
            _resultScroll.y = top + _listRowHeight - _listViewHeight;
        }

        _resultScroll.y = Mathf.Clamp(_resultScroll.y, 0f,
            Mathf.Max(0f, _results.Count * _listRowHeight - _listViewHeight));
    }

    private int PageStep()
    {
        return _listRowHeight > 0 ? Mathf.Max(1, Mathf.FloorToInt(_listViewHeight / _listRowHeight) - 1) : 1;
    }

    private int IndexOfSelected()
    {
        if (_selected == null)
        {
            return -1;
        }

        for (var index = 0; index < _results.Count; index++)
        {
            if (string.Equals(_results[index].Entry.Id, _selected.Id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private bool IsSelectionVisible()
    {
        return IndexOfSelected() >= 0;
    }

    private void Refresh()
    {
        if (_search == null)
        {
            return;
        }

        var language = Language();
        var searching = !string.IsNullOrEmpty(_query) && _query.Trim().Length > 0;
        var pool = new List<SearchResult>();
        if (searching)
        {
            // Ask for more than the list shows so a type filter still has enough rows to fill it.
            pool = _search.Search(_query, language, 400);
        }
        else
        {
            foreach (var entry in _search.All(language))
            {
                pool.Add(new SearchResult
                {
                    Entry = entry,
                    Score = 0,
                    SortKey = entry.NameFor(language)
                });
            }
        }

        _totalCount = pool.Count;
        _kindCounts.Clear();
        for (var index = 0; index < pool.Count; index++)
        {
            var kind = pool[index].Entry.Kind;
            int count;
            _kindCounts.TryGetValue(kind, out count);
            _kindCounts[kind] = count + 1;
        }

        var results = new List<SearchResult>();
        for (var index = 0; index < pool.Count; index++)
        {
            if (_kindFilter.HasValue && pool[index].Entry.Kind != _kindFilter.Value)
            {
                continue;
            }

            results.Add(pool[index]);
        }

        if (searching && results.Count > 60)
        {
            results.RemoveRange(60, results.Count - 60);
        }

        _results = results;
        _selected = results.Count > 0 ? results[0].Entry : null;
        _selectedResult = results.Count > 0 ? results[0] : null;
        _resultScroll = Vector2.zero;
        _detailScroll = Vector2.zero;
        _detailContentHeight = 0f;
    }

    private void Select(SearchResult result)
    {
        if (result == null)
        {
            return;
        }

        _selected = result.Entry;
        _selectedResult = result;
        _detailScroll = Vector2.zero;
        _detailContentHeight = 0f;
    }

    private SearchResult FindResult(string id)
    {
        for (var index = 0; index < _results.Count; index++)
        {
            if (string.Equals(_results[index].Entry.Id, id, StringComparison.Ordinal))
            {
                return _results[index];
            }
        }

        return null;
    }

    private List<WikiEntry> RelatedEntries(WikiEntry entry)
    {
        var related = new List<WikiEntry>();
        for (var index = 0; index < entry.RelatedIds.Count; index++)
        {
            var candidate = _dataset.Find(entry.RelatedIds[index]);
            if (candidate != null && !string.Equals(candidate.Id, entry.Id, StringComparison.Ordinal))
            {
                related.Add(candidate);
            }
        }

        return related;
    }

    private void ReportError(Exception exception)
    {
        _errorCount++;
        if (_errorCount > 3 || Plugin.Log == null)
        {
            return;
        }

        Plugin.Log.LogWarning("Wiki Book UI error: " + exception);
    }

    private string Language()
    {
        return _language != null ? _language() : WikiDataset.DefaultLanguage;
    }

    private string HotkeyName()
    {
        return _hotkey != null ? _hotkey().ToString() : "F2";
    }

    private string T(string chinese, string english)
    {
        return string.Equals(Language(), "en", StringComparison.OrdinalIgnoreCase) ? english : chinese;
    }

    /// <summary>
    ///     IMGUI coordinates are the ones Unity reports in <see cref="Screen"/>. Dividing a DPI-scaled client rect
    ///     into them made the window and every font smaller than the render area on high-DPI displays.
    /// </summary>
    private static Vector2 ResolveGuiSize()
    {
        return new Vector2(Screen.width, Screen.height);
    }

    private static string WindowMetrics()
    {
        if (Application.platform != RuntimePlatform.WindowsPlayer)
        {
            return string.Empty;
        }

        try
        {
            var window = GetActiveWindow();
            ClientRect rect;
            if (window != IntPtr.Zero && GetClientRect(window, out rect))
            {
                return ", client " + rect.Right + "x" + rect.Bottom + ", dpi " + GetDpiForWindow(window);
            }
        }
        catch (Exception)
        {
            // Diagnostics only.
        }

        return string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out ClientRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static string KindLabel(EntryKind kind)
    {
        switch (kind)
        {
            case EntryKind.Item:
                return "物品 / Item";
            case EntryKind.Building:
                return "建筑 / Building";
            case EntryKind.Tech:
                return "科技 / Tech";
            case EntryKind.Quest:
                return "任务 / Quest";
            default:
                return "机制 / Mechanic";
        }
    }

    private static string QualityLabel(string quality)
    {
        switch ((quality ?? string.Empty).ToLowerInvariant())
        {
            case "bronze":
                return "铜星 / Bronze";
            case "silver":
                return "银星 / Silver";
            case "gold":
                return "金星 / Gold";
            default:
                return quality;
        }
    }

    private void EnsureStyles(float scale)
    {
        if (_builtScale >= 0 && Mathf.Abs(_builtScale - scale) < 0.001f)
        {
            return;
        }

        _builtScale = scale;
        CreateFont(Mathf.RoundToInt(16 * scale));

        var titleSize = Mathf.RoundToInt(24 * scale);
        var detailTitleSize = Mathf.RoundToInt(21 * scale);
        var headingSize = Mathf.RoundToInt(16 * scale);
        var bodySize = Mathf.RoundToInt(14.5f * scale);
        var smallSize = Mathf.RoundToInt(12.5f * scale);
        var inputSize = Mathf.RoundToInt(16 * scale);

        _title = Label(titleSize, TextColor, TextAnchor.MiddleLeft, true);
        _close = ButtonStyle(titleSize, TextColor, "close", RowColor, RowHoverColor, RowSelectedColor,
            new RectOffset(Mathf.RoundToInt(6 * scale), Mathf.RoundToInt(6 * scale), 0, 0), TextAnchor.MiddleCenter);
        _input = new GUIStyle(GUI.skin.textField)
        {
            font = _font,
            fontSize = inputSize,
            richText = false,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(Mathf.RoundToInt(12 * scale), Mathf.RoundToInt(12 * scale),
                Mathf.RoundToInt(8 * scale), Mathf.RoundToInt(8 * scale))
        };
        _input.normal.background = Texture("input", InputColor);
        _input.focused.background = Texture("inputFocus", new Color32(255, 252, 240, 255));
        _input.hover.background = _input.focused.background;
        _input.normal.textColor = TextColor;
        _input.focused.textColor = TextColor;
        _input.hover.textColor = TextColor;

        _row = ButtonStyle(smallSize, TextColor, "row", RowColor, RowHoverColor, RowSelectedColor,
            new RectOffset(Mathf.RoundToInt(8 * scale), Mathf.RoundToInt(8 * scale), 0, 0), TextAnchor.MiddleLeft);
        _rowSelected = ButtonStyle(smallSize, TextColor, "rowSel", RowSelectedColor, RowSelectedColor, RowSelectedColor,
            new RectOffset(Mathf.RoundToInt(8 * scale), Mathf.RoundToInt(8 * scale), 0, 0), TextAnchor.MiddleLeft);
        _rowTitle = Label(Mathf.RoundToInt(16 * scale), TextColor, TextAnchor.MiddleLeft, true);
        _small = Label(smallSize, MutedColor, TextAnchor.UpperLeft, false);
        _aliasSmall = Label(smallSize, AccentColor, TextAnchor.UpperLeft, false);
        _rowMeta = Label(smallSize, MutedColor, TextAnchor.UpperLeft, false);
        _rowAlias = Label(smallSize, AccentColor, TextAnchor.UpperLeft, false);
        var tabPadding = new RectOffset(Mathf.RoundToInt(4 * scale), Mathf.RoundToInt(4 * scale), 0, 0);
        _tab = ButtonStyle(smallSize, MutedColor, "tab", RowColor, RowHoverColor, RowSelectedColor, tabPadding,
            TextAnchor.MiddleCenter);
        _tabActive = ButtonStyle(smallSize, TextColor, "tabSel", RowSelectedColor, RowSelectedColor, RowSelectedColor,
            tabPadding, TextAnchor.MiddleCenter);
        _rowTitle.wordWrap = false;
        _rowMeta.wordWrap = false;
        _rowAlias.wordWrap = false;
        _body = Label(bodySize, TextColor, TextAnchor.UpperLeft, true);
        _heading = Label(headingSize, AccentColor, TextAnchor.MiddleLeft, true);
        _detailTitle = Label(detailTitleSize, TextColor, TextAnchor.UpperLeft, true);
        _status = Label(smallSize, MutedColor, TextAnchor.MiddleLeft, false);
        _link = ButtonStyle(bodySize, LinkColor, "link", new Color32(0, 0, 0, 0), new Color32(0, 0, 0, 0),
            new Color32(0, 0, 0, 0), new RectOffset(0, 0, 0, 0), TextAnchor.MiddleLeft);
        _link.hover.textColor = new Color32(32, 74, 108, 255);
    }

    private void CreateFont(int size)
    {
        if (_font != null && _fontSize == size)
        {
            return;
        }

        if (_font != null)
        {
            Destroy(_font);
            _font = null;
        }

        _fontSize = size;
        try
        {
            // Dynamic OS fonts keep CJK glyphs sharp at any resolution; the vanilla bitmap font would pixelate.
            _font = Font.CreateDynamicFontFromOSFont(new[]
            {
                "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial Unicode MS",
                "Segoe UI", "Arial"
            }, size);
        }
        catch (Exception)
        {
            _font = null;
        }
    }

    private GUIStyle Label(int size, Color32 color, TextAnchor anchor, bool richText)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            font = _font,
            fontSize = size,
            alignment = anchor,
            wordWrap = true,
            richText = richText,
            clipping = TextClipping.Clip,
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(0, 0, 0, 0)
        };
        style.normal.textColor = color;
        style.hover.textColor = color;
        style.active.textColor = color;
        return style;
    }

    private GUIStyle ButtonStyle(int size, Color32 color, string key, Color32 normal, Color32 hover, Color32 active,
        RectOffset padding, TextAnchor anchor)
    {
        var style = new GUIStyle(GUI.skin.button)
        {
            font = _font,
            fontSize = size,
            richText = true,
            alignment = anchor,
            clipping = TextClipping.Clip,
            padding = padding,
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0)
        };
        style.normal.background = Texture(key + ".n", normal);
        style.hover.background = Texture(key + ".h", hover);
        style.active.background = Texture(key + ".a", active);
        style.focused.background = style.hover.background;
        style.normal.textColor = color;
        style.hover.textColor = color;
        style.active.textColor = color;
        style.focused.textColor = color;
        return style;
    }

    private Texture2D Texture(string key, Color32 color)
    {
        Texture2D texture;
        if (_textures.TryGetValue(key, out texture) && texture != null)
        {
            return texture;
        }

        texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            name = "WikiBook " + key
        };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        _textures[key] = texture;
        return texture;
    }
}
