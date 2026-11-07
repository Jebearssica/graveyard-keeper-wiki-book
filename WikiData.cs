using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WikiBook;

/// <summary>A curated rename: quest text or an old name that should resolve to the real entry, with a visible note.</summary>
internal sealed class Redirect
{
    internal string From;
    internal string To;
    internal string Note;
}

/// <summary>One entry whose recipe consumes an ingredient: the "what can I make with this" direction.</summary>
internal sealed class RecipeUse
{
    internal WikiEntry Entry;
    internal int Amount = 1;
}

internal sealed class WikiDataset
{
    internal const string DefaultLanguage = "zh-Hans";

    internal int SchemaVersion = WikiData.SupportedSchemaVersion;
    internal string DataVersion = "0";
    internal string GameVersion = "unknown";
    internal string Source = "unknown";
    internal readonly Dictionary<string, WikiEntry> Entries = new Dictionary<string, WikiEntry>(StringComparer.Ordinal);
    internal readonly List<Redirect> Redirects = new List<Redirect>();
    internal readonly List<string> Warnings = new List<string>();

    private Dictionary<string, List<RecipeUse>> _uses;

    internal static WikiDataset Empty => new WikiDataset();

    internal WikiEntry Find(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        WikiEntry entry;
        return Entries.TryGetValue(id, out entry) ? entry : null;
    }

    internal string NameOf(string id, string language)
    {
        var entry = Find(id);
        return entry != null ? entry.NameFor(language) : id;
    }

    /// <summary>All quality variants of an entry id (the entries whose <c>variantOf</c> points at it).</summary>
    internal List<WikiEntry> VariantsOf(string id)
    {
        var variants = new List<WikiEntry>();
        if (string.IsNullOrEmpty(id))
        {
            return variants;
        }

        foreach (var entry in Entries.Values)
        {
            if (string.Equals(entry.VariantOf, id, StringComparison.Ordinal))
            {
                variants.Add(entry);
            }
        }

        variants.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return variants;
    }

    /// <summary>The entry itself plus its quality variants: "how to make X" must cover every grade.</summary>
    internal List<WikiEntry> Group(string id)
    {
        var group = new List<WikiEntry>();
        var entry = Find(id);
        if (entry == null)
        {
            return group;
        }

        group.Add(entry);
        group.AddRange(VariantsOf(id));
        return group;
    }

    /// <summary>
    ///     Every entry whose recipe consumes this one, in stable id order. Built once on first use; the dataset is
    ///     immutable after parsing, so the index cannot go stale.
    /// </summary>
    internal List<RecipeUse> Uses(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return new List<RecipeUse>();
        }

        if (_uses == null)
        {
            BuildUses();
        }

        List<RecipeUse> uses;
        return _uses.TryGetValue(id, out uses) ? uses : new List<RecipeUse>();
    }

    private void BuildUses()
    {
        var uses = new Dictionary<string, List<RecipeUse>>(StringComparer.Ordinal);
        var ids = new List<string>(Entries.Keys);
        ids.Sort(StringComparer.Ordinal);
        for (var index = 0; index < ids.Count; index++)
        {
            var entry = Entries[ids[index]];
            var recipe = entry.Recipe;
            if (recipe == null)
            {
                continue;
            }

            for (var row = 0; row < recipe.Inputs.Count; row++)
            {
                var ingredient = recipe.Inputs[row];
                List<RecipeUse> list;
                if (!uses.TryGetValue(ingredient.Id, out list))
                {
                    list = new List<RecipeUse>();
                    uses[ingredient.Id] = list;
                }

                var existing = false;
                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].Entry.Id, entry.Id, StringComparison.Ordinal))
                    {
                        list[i].Amount = Math.Max(list[i].Amount, ingredient.Amount);
                        existing = true;
                        break;
                    }
                }

                if (!existing)
                {
                    list.Add(new RecipeUse { Entry = entry, Amount = ingredient.Amount });
                }
            }
        }

        _uses = uses;
    }
}

/// <summary>
///     Reads and validates the versioned dataset. Pure C#: no Unity or BepInEx types, so the same code runs in the
///     in-game self-test and in the out-of-game console harness.
/// </summary>
internal static class WikiData
{
    internal const int SupportedSchemaVersion = 1;

    /// <summary>Parses or returns an empty dataset, logging why. A bad dataset must degrade, never take the game down.</summary>
    internal static WikiDataset TryParse(string json, Action<string> log)
    {
        try
        {
            return Parse(json);
        }
        catch (Exception exception)
        {
            if (log != null)
            {
                log("Wiki dataset rejected: " + exception.Message);
            }

            return WikiDataset.Empty;
        }
    }

    internal static WikiDataset Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            throw new InvalidDataException("the dataset is empty");
        }

        var root = Json.Parse(json.TrimStart('\uFEFF')) as Dictionary<string, object>;
        if (root == null)
        {
            throw new InvalidDataException("the dataset root must be a JSON object");
        }

        var dataset = new WikiDataset();
        dataset.SchemaVersion = ReadInt(root, "schemaVersion", 0);
        if (dataset.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                "unsupported schemaVersion " + dataset.SchemaVersion + " (expected " + SupportedSchemaVersion + ")");
        }

        dataset.DataVersion = ReadString(root, "dataVersion", "0");
        dataset.GameVersion = ReadString(root, "gameVersion", "unknown");
        dataset.Source = ReadString(root, "source", "unknown");

        var entries = ReadArray(root, "entries", true);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = ReadEntry(entries[index], "entries[" + index + "]");
            if (dataset.Entries.ContainsKey(entry.Id))
            {
                throw new InvalidDataException("duplicate entry id '" + entry.Id + "'");
            }

            dataset.Entries.Add(entry.Id, entry);
        }

        var redirects = ReadArray(root, "redirects", false);        for (var index = 0; index < redirects.Count; index++)
        {
            var data = redirects[index] as Dictionary<string, object>;
            if (data == null)
            {
                throw new InvalidDataException("redirects[" + index + "] must be an object");
            }

            var redirect = new Redirect
            {
                From = RequireString(data, "from", "redirects[" + index + "]"),
                To = RequireString(data, "to", "redirects[" + index + "]"),
                Note = ReadString(data, "note", string.Empty)
            };
            dataset.Redirects.Add(redirect);
        }

        ValidateReferences(dataset);
        return dataset;
    }

    private static WikiEntry ReadEntry(object value, string path)
    {
        var data = value as Dictionary<string, object>;
        if (data == null)
        {
            throw new InvalidDataException(path + " must be an object");
        }

        var entry = new WikiEntry
        {
            Id = RequireString(data, "id", path),
            Kind = ParseKind(ReadString(data, "kind", "item"), path),
            IconKey = ReadString(data, "icon", null),
            Quality = ReadString(data, "quality", null),
            VariantOf = ReadString(data, "variantOf", null),
            UnlockTechId = ReadString(data, "unlockTech", null),
            UnlockQuestId = ReadString(data, "unlockQuest", null),
            Source = ReadString(data, "source", null)
        };

        if (string.IsNullOrEmpty(entry.Id))
        {
            throw new InvalidDataException(path + ".id must not be empty");
        }

        entry.Names = ReadLocalized(data, "names", path, true);
        entry.Descriptions = ReadLocalized(data, "description", path, false);
        AddStrings(data, "aliases", entry.Aliases, path);
        AddStrings(data, "related", entry.RelatedIds, path);
        ReadQualities(data, entry, path);

        var recipe = data.ContainsKey("recipe") ? data["recipe"] as Dictionary<string, object> : null;
        if (recipe != null)
        {
            entry.Recipe = new Recipe
            {
                StationId = ReadString(recipe, "station", null),
                Seconds = ReadInt(recipe, "seconds", 0)
            };
            ReadIngredients(recipe, "inputs", entry.Recipe.Inputs, path + ".recipe");
            ReadIngredients(recipe, "outputs", entry.Recipe.Outputs, path + ".recipe");
        }

        return entry;
    }

    private static void ReadQualities(Dictionary<string, object> data, WikiEntry entry, string path)
    {
        var rows = ReadArray(data, "qualities", false);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index] as Dictionary<string, object>;
            if (row == null)
            {
                throw new InvalidDataException(path + ".qualities[" + index + "] must be an object");
            }

            var rowPath = path + ".qualities[" + index + "]";
            entry.Qualities.Add(new QualityVariant
            {
                Grade = RequireString(row, "grade", rowPath),
                Names = ReadLocalized(row, "names", rowPath, true),
                IconKey = ReadString(row, "icon", null)
            });
        }
    }

    private static void ReadIngredients(Dictionary<string, object> recipe, string key, List<Ingredient> target,
        string path)
    {
        var rows = ReadArray(recipe, key, false);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index] as Dictionary<string, object>;
            if (row == null)
            {
                throw new InvalidDataException(path + "." + key + "[" + index + "] must be an object");
            }

            target.Add(new Ingredient
            {
                Id = RequireString(row, "id", path + "." + key + "[" + index + "]"),
                Amount = Math.Max(1, ReadInt(row, "amount", 1))
            });
        }
    }

    private static EntryKind ParseKind(string value, string path)
    {
        switch ((value ?? string.Empty).ToLowerInvariant())
        {
            case "item":
                return EntryKind.Item;
            case "building":
                return EntryKind.Building;
            case "quest":
                return EntryKind.Quest;
            case "mechanic":
                return EntryKind.Mechanic;
            case "tech":
                return EntryKind.Tech;
            default:
                throw new InvalidDataException(path + ".kind '" + value + "' is not a known entry kind");
        }
    }

    private static LocalizedText ReadLocalized(Dictionary<string, object> data, string key, string path, bool required)
    {
        object raw;
        if (!data.TryGetValue(key, out raw) || raw == null)
        {
            if (required)
            {
                throw new InvalidDataException(path + "." + key + " is required");
            }

            return LocalizedText.Empty;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = raw as string;
        if (text != null)
        {
            values[WikiDataset.DefaultLanguage] = text;
        }
        else
        {
            var map = raw as Dictionary<string, object>;
            if (map == null)
            {
                throw new InvalidDataException(path + "." + key + " must be a string or an object");
            }

            foreach (var pair in map)
            {
                var localized = pair.Value as string;
                if (localized == null)
                {
                    throw new InvalidDataException(path + "." + key + "." + pair.Key + " must be a string");
                }

                if (!string.IsNullOrEmpty(localized))
                {
                    values[pair.Key] = localized;
                }
            }
        }

        if (required && values.Count == 0)
        {
            throw new InvalidDataException(path + "." + key + " must contain at least one language");
        }

        return new LocalizedText(values);
    }

    private static void ValidateReferences(WikiDataset dataset)
    {
        foreach (var entry in dataset.Entries.Values)
        {
            if (!string.IsNullOrEmpty(entry.VariantOf) && dataset.Find(entry.VariantOf) == null)
            {
                dataset.Warnings.Add(entry.Id + ".variantOf points at unknown entry '" + entry.VariantOf + "'");
            }

            if (entry.Recipe != null)
            {
                foreach (var ingredient in entry.Recipe.Inputs)
                {
                    WarnIfMissing(dataset, entry, "recipe.inputs", ingredient.Id);
                }

                foreach (var ingredient in entry.Recipe.Outputs)
                {
                    WarnIfMissing(dataset, entry, "recipe.outputs", ingredient.Id);
                }

                if (!string.IsNullOrEmpty(entry.Recipe.StationId))
                {
                    WarnIfMissing(dataset, entry, "recipe.station", entry.Recipe.StationId);
                }
            }

            if (!string.IsNullOrEmpty(entry.UnlockTechId))
            {
                WarnIfMissing(dataset, entry, "unlockTech", entry.UnlockTechId);
            }

            if (!string.IsNullOrEmpty(entry.UnlockQuestId))
            {
                WarnIfMissing(dataset, entry, "unlockQuest", entry.UnlockQuestId);
            }

            foreach (var related in entry.RelatedIds)
            {
                WarnIfMissing(dataset, entry, "related", related);
            }
        }

        foreach (var redirect in dataset.Redirects)
        {
            if (dataset.Find(redirect.To) == null)
            {
                dataset.Warnings.Add("redirect '" + redirect.From + "' points at unknown entry '" + redirect.To + "'");
            }
        }
    }

    private static void WarnIfMissing(WikiDataset dataset, WikiEntry entry, string field, string id)
    {
        if (dataset.Find(id) == null)
        {
            dataset.Warnings.Add(entry.Id + "." + field + " points at unknown entry '" + id + "'");
        }
    }

    private static void AddStrings(Dictionary<string, object> data, string key, List<string> target, string path)
    {
        var rows = ReadArray(data, key, false);
        for (var index = 0; index < rows.Count; index++)
        {
            var text = rows[index] as string;
            if (string.IsNullOrEmpty(text))
            {
                throw new InvalidDataException(path + "." + key + "[" + index + "] must be a non-empty string");
            }

            target.Add(text);
        }
    }

    private static List<object> ReadArray(Dictionary<string, object> data, string key, bool required)
    {
        object raw;
        if (!data.TryGetValue(key, out raw) || raw == null)
        {
            if (required)
            {
                throw new InvalidDataException("'" + key + "' is required");
            }

            return new List<object>();
        }

        var array = raw as List<object>;
        if (array == null)
        {
            throw new InvalidDataException("'" + key + "' must be an array");
        }

        return array;
    }

    private static string RequireString(Dictionary<string, object> data, string key, string path)
    {
        var value = ReadString(data, key, null);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidDataException(path + "." + key + " is required");
        }

        return value;
    }

    private static string ReadString(Dictionary<string, object> data, string key, string fallback)
    {
        object raw;
        if (!data.TryGetValue(key, out raw) || raw == null)
        {
            return fallback;
        }

        var text = raw as string;
        if (text == null)
        {
            throw new InvalidDataException("'" + key + "' must be a string");
        }

        return text;
    }

    private static int ReadInt(Dictionary<string, object> data, string key, int fallback)
    {
        object raw;
        if (!data.TryGetValue(key, out raw) || raw == null)
        {
            return fallback;
        }

        if (!(raw is double))
        {
            throw new InvalidDataException("'" + key + "' must be a number");
        }

        return (int)Math.Round((double)raw);
    }

    internal static string ReadAll(Stream stream)
    {
        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
        {
            return reader.ReadToEnd();
        }
    }
}
