using System;
using System.Collections.Generic;

namespace WikiBook;

/// <summary>What a wiki page describes. Drives the result label and the detail layout.</summary>
internal enum EntryKind
{
    Item,
    Building,
    Quest,
    Mechanic,
    Tech
}

/// <summary>
///     One localized string per language. The dataset is authored in Simplified Chinese and English today, but any
///     language key is accepted so a future extraction can add more without a schema bump.
/// </summary>
internal sealed class LocalizedText
{
    internal static readonly LocalizedText Empty = new LocalizedText(new Dictionary<string, string>());

    private readonly Dictionary<string, string> _values;

    internal LocalizedText(Dictionary<string, string> values)
    {
        _values = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal bool IsEmpty => _values.Count == 0;

    internal IEnumerable<KeyValuePair<string, string>> All => _values;

    /// <summary>Player language first, then Simplified Chinese, then English, then whatever the dataset has.</summary>
    internal string Get(string language)
    {
        if (_values.Count == 0)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(language))
        {
            if (_values.TryGetValue(language, out var exact) && !string.IsNullOrEmpty(exact))
            {
                return exact;
            }

            foreach (var pair in _values)
            {
                if (SameLanguage(pair.Key, language) && !string.IsNullOrEmpty(pair.Value))
                {
                    return pair.Value;
                }
            }
        }

        if (_values.TryGetValue("zh-Hans", out var chinese) && !string.IsNullOrEmpty(chinese))
        {
            return chinese;
        }

        if (_values.TryGetValue("en", out var english) && !string.IsNullOrEmpty(english))
        {
            return english;
        }

        foreach (var pair in _values)
        {
            if (!string.IsNullOrEmpty(pair.Value))
            {
                return pair.Value;
            }
        }

        return string.Empty;
    }

    private static bool SameLanguage(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftDash = left.IndexOf('-');
        var rightDash = right.IndexOf('-');
        var leftPrimary = leftDash < 0 ? left : left.Substring(0, leftDash);
        var rightPrimary = rightDash < 0 ? right : right.Substring(0, rightDash);
        return string.Equals(leftPrimary, rightPrimary, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One row of a recipe. Ids are entry ids, so the UI can turn every row into a cross-link.</summary>
internal sealed class Ingredient
{
    internal string Id;
    internal int Amount = 1;
}

internal sealed class Recipe
{
    internal readonly List<Ingredient> Inputs = new List<Ingredient>();
    internal readonly List<Ingredient> Outputs = new List<Ingredient>();
    internal string StationId;
    internal int Seconds;
}

/// <summary>
///     One grade of a multi-quality item. The game produces the grades from a single craft but stores each grade
///     as its own item id, so the wiki keeps one page and lists the grades here.
/// </summary>
internal sealed class QualityVariant
{
    internal string Grade;
    internal LocalizedText Names = LocalizedText.Empty;
    internal string IconKey;
}

internal sealed class WikiEntry
{
    internal string Id;
    internal EntryKind Kind = EntryKind.Item;
    internal LocalizedText Names = LocalizedText.Empty;
    internal LocalizedText Descriptions = LocalizedText.Empty;
    internal readonly List<string> Aliases = new List<string>();
    internal readonly List<string> RelatedIds = new List<string>();
    internal readonly List<QualityVariant> Qualities = new List<QualityVariant>();
    internal string IconKey;
    internal string Quality;
    internal string VariantOf;
    internal Recipe Recipe;
    internal string UnlockTechId;
    internal string UnlockQuestId;
    internal string Source;

    internal bool HasRecipe => Recipe != null && (Recipe.Inputs.Count > 0 || Recipe.Outputs.Count > 0);

    internal bool HasQualities => Qualities.Count > 0;

    internal string NameFor(string language)
    {
        var name = Names.Get(language);
        return string.IsNullOrEmpty(name) ? Id : name;
    }
}
