using System;
using System.Collections.Generic;
using System.Text;

namespace WikiBook;

internal sealed class SearchResult
{
    internal WikiEntry Entry;
    internal double Score;
    internal string MatchedText;
    internal string AliasNote;
    internal string RelationNote;
    internal string SortKey;

    internal bool Redirected => !string.IsNullOrEmpty(AliasNote);

    /// <summary>True when the row is listed because the query named one of its ingredients.</summary>
    internal bool RelatedByRecipe => !string.IsNullOrEmpty(RelationNote) && !Redirected;
}

/// <summary>Text normalization shared by the index and the query. Kept pure so the console harness can test it.</summary>
internal static class WikiText
{
    /// <summary>
    ///     Full-width forms become half-width, case is folded, and punctuation/whitespace is dropped. This makes
    ///     "Iron Ingot", "iron-ingot" and "ＩＲＯＮ　ＩＮＧＯＴ" the same key while leaving CJK characters intact.
    /// </summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    internal static int Levenshtein(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                var replace = previous[column - 1] + cost;
                var insert = current[column - 1] + 1;
                var delete = previous[column] + 1;
                current[column] = Math.Min(replace, Math.Min(insert, delete));
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
    }

    /// <summary>1.0 when the query is a compact in-order subsequence of the target, lower when it is spread out.</summary>
    internal static double SubsequenceScore(string query, string target)
    {
        if (query.Length < 2 || target.Length == 0)
        {
            return 0;
        }

        var queryIndex = 0;
        var lastMatch = -1;
        var gaps = 0;
        for (var index = 0; index < target.Length && queryIndex < query.Length; index++)
        {
            if (target[index] != query[queryIndex])
            {
                continue;
            }

            if (lastMatch >= 0)
            {
                gaps += index - lastMatch - 1;
            }

            lastMatch = index;
            queryIndex++;
        }

        if (queryIndex < query.Length)
        {
            return 0;
        }

        var coverage = (double)query.Length / Math.Max(query.Length, target.Length);
        return coverage / (1 + gaps * 0.15);
    }

    internal static double Similarity(string left, string right)
    {
        var longest = Math.Max(left.Length, right.Length);
        if (longest == 0)
        {
            return 1;
        }

        return 1.0 - (double)Levenshtein(left, right) / longest;
    }
}

/// <summary>
///     Ranked search over the dataset. Curated aliases and redirects resolve first (they are the only reliable way to
///     map quest wording such as "金星爱情故事" to "金星故事"); substring, subsequence and edit-distance matching cover
///     non-exact queries. No Unity types, so the console harness exercises the real ranking code.
/// </summary>
internal sealed class WikiSearch
{
    internal const int DefaultMaxResults = 20;

    /// <summary>How many "what can I make with this" rows an exact match may add.</summary>
    internal const int MaxUseResults = 40;

    private const double ExactScore = 1000;

    private readonly WikiDataset _dataset;
    private readonly List<IndexedName> _names = new List<IndexedName>();
    private readonly Dictionary<string, AliasTarget> _aliases = new Dictionary<string, AliasTarget>(StringComparer.Ordinal);

    internal WikiSearch(WikiDataset dataset)
    {
        _dataset = dataset ?? WikiDataset.Empty;
        BuildIndex();
    }

    internal WikiDataset Dataset => _dataset;

    internal IReadOnlyList<WikiEntry> All(string language)
    {
        var entries = new List<WikiEntry>(_dataset.Entries.Values);
        entries.Sort((left, right) =>
        {
            var kind = KindOrder(left.Kind).CompareTo(KindOrder(right.Kind));
            return kind != 0 ? kind : string.CompareOrdinal(left.NameFor(language), right.NameFor(language));
        });
        return entries;
    }

    internal List<SearchResult> Search(string query, string language, int maxResults = DefaultMaxResults)
    {
        var normalized = WikiText.Normalize(query);
        var best = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
        if (normalized.Length == 0)
        {
            return new List<SearchResult>();
        }

        AliasTarget direct;
        if (_aliases.TryGetValue(normalized, out direct))
        {
            Consider(best, direct.Entry, 1100, direct.Text, direct.Note, language);
        }

        foreach (var indexed in _names)
        {
            var score = ScoreName(normalized, indexed, language);
            if (score <= 0)
            {
                continue;
            }

            Consider(best, indexed.Entry, score, indexed.Text, null, language);
        }

        // Quest text often wraps the item name in extra words. If a curated alias appears inside the query, the
        // redirect is more trustworthy than fuzzy scoring, and the note explains why the entry was chosen.
        if (normalized.Length > 3)
        {
            foreach (var pair in _aliases)
            {
                if (pair.Key.Length < 2 || pair.Key.Length >= normalized.Length)
                {
                    continue;
                }

                if (normalized.IndexOf(pair.Key, StringComparison.Ordinal) >= 0)
                {
                    Consider(best, pair.Value.Entry, 700 + pair.Key.Length, pair.Value.Text, pair.Value.Note, language);
                }
            }
        }

        AddRecipeUses(best, normalized, language);

        var results = new List<SearchResult>(best.Values);
        results.Sort((left, right) =>
        {
            var score = right.Score.CompareTo(left.Score);
            if (score != 0)
            {
                return score;
            }

            // Several entries can legitimately share a display name (a building and its tech node). Prefer the
            // concrete page, then sort by name, so the order is deterministic instead of dictionary order.
            var kind = KindOrder(left.Entry.Kind).CompareTo(KindOrder(right.Entry.Kind));
            return kind != 0 ? kind : string.CompareOrdinal(left.SortKey, right.SortKey);
        });

        if (results.Count > maxResults)
        {
            results.RemoveRange(maxResults, results.Count - maxResults);
        }

        return results;
    }

    internal SearchResult Best(string query, string language)
    {
        var results = Search(query, language, 1);
        return results.Count > 0 ? results[0] : null;
    }

    /// <summary>
    ///     "What can I make with this": when the query names an entry exactly (南瓜), also list the entries whose
    ///     recipe consumes it, so dishes and quest items show up next to the material. They rank below direct name
    ///     matches and carry a visible note so they are not mistaken for name hits.
    /// </summary>
    private void AddRecipeUses(Dictionary<string, SearchResult> best, string normalized, string language)
    {
        if (normalized.Length < 2 || _dataset.Entries.Count == 0)
        {
            return;
        }

        var anchors = new List<SearchResult>();
        foreach (var result in best.Values)
        {
            if (result.Score >= ExactScore)
            {
                anchors.Add(result);
            }
        }

        if (anchors.Count == 0)
        {
            return;
        }

        // Several entries can share one name; two anchors are enough and the order stays deterministic.
        anchors.Sort((left, right) => string.CompareOrdinal(left.Entry.Id, right.Entry.Id));
        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var added = 0;
        var anchorCount = Math.Min(2, anchors.Count);
        for (var anchorIndex = 0; anchorIndex < anchorCount; anchorIndex++)
        {
            var anchor = anchors[anchorIndex].Entry;
            var uses = _dataset.Uses(anchor.Id);
            var relation = (english ? "Ingredient: " : "材料：") + anchor.NameFor(language);
            for (var index = 0; index < uses.Count && added < MaxUseResults; index++)
            {
                var use = uses[index];
                if (string.Equals(use.Entry.Id, anchor.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                Consider(best, use.Entry, 500 - anchorIndex * 25 - added * 0.5, null, null, language, relation);
                added++;
            }
        }
    }

    private void BuildIndex()
    {
        foreach (var entry in _dataset.Entries.Values)
        {
            foreach (var pair in entry.Names.All)
            {
                AddName(entry, pair.Value, pair.Key, false);
            }

            foreach (var alias in entry.Aliases)
            {
                AddName(entry, alias, null, true);
                AddAlias(alias, entry, null);
            }

            foreach (var quality in entry.Qualities)
            {
                foreach (var pair in quality.Names.All)
                {
                    AddName(entry, pair.Value, pair.Key, false);
                }
            }
        }

        foreach (var redirect in _dataset.Redirects)
        {
            var entry = _dataset.Find(redirect.To);
            if (entry != null)
            {
                AddAlias(redirect.From, entry, string.IsNullOrEmpty(redirect.Note) ? null : redirect.Note);
            }
        }
    }

    private void AddName(WikiEntry entry, string text, string language, bool isAlias)
    {
        var normalized = WikiText.Normalize(text);
        if (normalized.Length == 0)
        {
            return;
        }

        _names.Add(new IndexedName
        {
            Entry = entry,
            Text = text,
            Normalized = normalized,
            Language = language,
            IsAlias = isAlias
        });
    }

    private void AddAlias(string text, WikiEntry entry, string note)
    {
        var normalized = WikiText.Normalize(text);
        if (normalized.Length == 0)
        {
            return;
        }

        AliasTarget existing;
        if (_aliases.TryGetValue(normalized, out existing))
        {
            // A curated redirect note wins over a plain alias entry.
            if (string.IsNullOrEmpty(existing.Note) && !string.IsNullOrEmpty(note))
            {
                existing.Note = note;
            }

            return;
        }

        _aliases[normalized] = new AliasTarget { Entry = entry, Text = text, Note = note };
    }

    private void Consider(Dictionary<string, SearchResult> best, WikiEntry entry, double score, string matchedText,
        string note, string language, string relation = null)
    {
        if (entry == null || score <= 0)
        {
            return;
        }

        var display = entry.NameFor(language);
        var aliasNote = note;
        if (string.IsNullOrEmpty(aliasNote) && !string.IsNullOrEmpty(matchedText) &&
            !string.Equals(WikiText.Normalize(matchedText), WikiText.Normalize(display), StringComparison.Ordinal))
        {
            aliasNote = "“" + matchedText + "” → “" + display + "”";
        }

        SearchResult existing;
        if (best.TryGetValue(entry.Id, out existing))
        {
            if (score > existing.Score)
            {
                existing.Score = score;
                existing.MatchedText = matchedText;
                if (!string.IsNullOrEmpty(aliasNote))
                {
                    existing.AliasNote = aliasNote;
                    existing.SortKey = display;
                }
            }
            else if (Math.Abs(score - existing.Score) < 0.001 && string.IsNullOrEmpty(existing.AliasNote) &&
                     !string.IsNullOrEmpty(aliasNote))
            {
                existing.AliasNote = aliasNote;
            }

            if (string.IsNullOrEmpty(existing.RelationNote) && !string.IsNullOrEmpty(relation))
            {
                existing.RelationNote = relation;
            }

            return;
        }

        best[entry.Id] = new SearchResult
        {
            Entry = entry,
            Score = score,
            MatchedText = matchedText,
            AliasNote = aliasNote,
            RelationNote = relation,
            SortKey = display
        };
    }

    private static double ScoreName(string query, IndexedName indexed, string language)
    {
        var target = indexed.Normalized;
        double score;
        if (string.Equals(target, query, StringComparison.Ordinal))
        {
            score = 1000;
        }
        else if (target.StartsWith(query, StringComparison.Ordinal))
        {
            score = 820 - Math.Min(160, (target.Length - query.Length) * 3);
        }
        else if (target.IndexOf(query, StringComparison.Ordinal) >= 0)
        {
            score = 650 - Math.Min(140, target.Length - query.Length);
        }
        else if (query.Length >= 2 && target.Length >= 2 && query.IndexOf(target, StringComparison.Ordinal) >= 0)
        {
            score = 540;
        }
        else
        {
            var subsequence = WikiText.SubsequenceScore(query, target);
            if (subsequence >= 0.2)
            {
                score = 220 + subsequence * 320;
            }
            else
            {
                var similarity = WikiText.Similarity(query, target);
                score = similarity >= 0.55 ? similarity * 320 : 0;
            }
        }

        if (score <= 0)
        {
            return 0;
        }

        if (indexed.IsAlias)
        {
            score += 10;
        }

        if (!string.IsNullOrEmpty(language) && !string.IsNullOrEmpty(indexed.Language) &&
            string.Equals(indexed.Language, language, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score;
    }

    private static int KindOrder(EntryKind kind)
    {
        switch (kind)
        {
            case EntryKind.Item:
                return 0;
            case EntryKind.Building:
                return 1;
            case EntryKind.Tech:
                return 2;
            case EntryKind.Quest:
                return 3;
            default:
                return 4;
        }
    }

    private sealed class IndexedName
    {
        internal WikiEntry Entry;
        internal string Text;
        internal string Normalized;
        internal string Language;
        internal bool IsAlias;
    }

    private sealed class AliasTarget
    {
        internal WikiEntry Entry;
        internal string Text;
        internal string Note;
    }
}
