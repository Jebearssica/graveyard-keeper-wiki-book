using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WikiBook.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "wiki-zh-Hans.json");
        var json = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

        WikiDataset dataset = null;
        var logs = new List<string>();
        if (json != null)
        {
            dataset = WikiData.TryParse(json, logs.Add);
            foreach (var log in logs)
            {
                Console.WriteLine("LOG " + log);
            }
        }

        Run("dataset parses", () =>
        {
            Assert.NotNull(dataset, "dataset is null");
            Assert.Equal(WikiData.SupportedSchemaVersion, dataset.SchemaVersion, "schema version");
            Assert.True(dataset.Entries.Count >= 1000,
                "expected the extracted dataset (1000+ entries), got " + dataset.Entries.Count);
            Assert.Equal(0, dataset.Warnings.Count, "dataset warnings: " + string.Join("; ", dataset.Warnings));
            Assert.False(string.IsNullOrEmpty(dataset.DataVersion), "dataVersion is empty");
            Console.WriteLine("     " + dataset.Entries.Count + " entries, data " + dataset.DataVersion + ", game " +
                              dataset.GameVersion);
        });

        var search = new WikiSearch(dataset);

        Run("exact Chinese name", () =>
        {
            var best = search.Best("原木", "zh-Hans");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.wood", best.Entry.Id, "resolved id");
            Assert.False(best.Redirected, "an exact primary name must not be reported as a redirect");
        });

        Run("exact English name", () =>
        {
            var best = search.Best("Iron ingot", "en");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.ingot_metal", best.Entry.Id, "resolved id");
        });

        Run("full-width and case normalization", () =>
        {
            var best = search.Best("ＩＲＯＮ　ＩＮＧＯＴ", "en");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.ingot_metal", best.Entry.Id, "resolved id");
            Assert.Equal("ironingot", WikiText.Normalize(" Iron-Ingot "), "normalized key");
        });

        Run("curated alias is visible", () =>
        {
            var best = search.Best("木头", "zh-Hans");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.wood", best.Entry.Id, "resolved id");
            Assert.True(best.Redirected, "the alias must be visible as a redirect");
            Assert.True(best.AliasNote.Contains("原木"), "the note must name the real entry: " + best.AliasNote);
        });

        Run("alias inside a sentence", () =>
        {
            var best = search.Best("我需要木头", "zh-Hans");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.wood", best.Entry.Id, "resolved id");
        });

        Run("quest wording alias", () =>
        {
            var best = search.Best("金星爱情故事", "zh-Hans");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.story", best.Entry.Id, "resolved id");
            Assert.True(best.Redirected, "quest wording must be reported as a redirect");
            Assert.True(best.AliasNote.Contains("精彩的故事"), "the note must name the real entry: " + best.AliasNote);
        });

        Run("every name and alias resolves", () =>
        {
            var failures = new List<string>();
            foreach (var entry in dataset.Entries.Values)
            {
                foreach (var pair in entry.Names.All)
                {
                    if (string.IsNullOrEmpty(pair.Value))
                    {
                        continue;
                    }

                    // Duplicate display names exist (a bucket and a barrel are both “桶”), so every entry must be
                    // present in the result list rather than necessarily ranked first.
                    var results = search.Search(pair.Value, pair.Key, 400);
                    if (!Contains(results, entry.Id))
                    {
                        failures.Add(entry.Id + " name “" + pair.Value + "” is not indexed");
                    }
                }

                foreach (var alias in entry.Aliases)
                {
                    var results = search.Search(alias, "zh-Hans", 400);
                    if (!Contains(results, entry.Id))
                    {
                        failures.Add(entry.Id + " alias “" + alias + "” is not indexed");
                    }
                }
            }

            Assert.Equal(0, failures.Count,
                failures.Count + " unresolved (first 5): " + string.Join("; ", failures.GetRange(0,
                    Math.Min(5, failures.Count))));
        });

        Run("every alias is a redirect", () =>
        {
            var failures = new List<string>();
            foreach (var entry in dataset.Entries.Values)
            {
                foreach (var alias in entry.Aliases)
                {
                    var best = search.Best(alias, "zh-Hans");
                    if (best == null || !string.Equals(best.Entry.Id, entry.Id, StringComparison.Ordinal))
                    {
                        failures.Add(entry.Id + " alias “" + alias + "” → " +
                                     (best == null ? "none" : best.Entry.Id));
                    }
                    else if (!best.Redirected)
                    {
                        failures.Add(entry.Id + " alias “" + alias + "” has no note");
                    }
                }
            }

            Assert.Equal(0, failures.Count,
                failures.Count + " bad alias(es) (first 5): " + string.Join("; ",
                    failures.GetRange(0, Math.Min(5, failures.Count))));
        });

        Run("ties prefer the concrete entry", () =>
        {
            var tiny = WikiData.Parse(
                "{ \"schemaVersion\": 1, \"entries\": [" +
                "{ \"id\": \"tech.same\", \"kind\": \"tech\", \"names\": { \"en\": \"Same\" } }, " +
                "{ \"id\": \"item.same\", \"kind\": \"item\", \"names\": { \"en\": \"Same\" } } ] }");
            var best = new WikiSearch(tiny).Best("Same", "en");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.same", best.Entry.Id, "an item should rank above a tech with the same name");
        });

        Run("graded meal has a name and recipe", () =>
        {
            // meal:burger:1..3 share the locale key "meal:burger"; those entries were missing before.
            var burger = dataset.Find("item.meal:burger");
            Assert.NotNull(burger, "burger entry missing");
            Assert.Equal("汉堡", burger.NameFor("zh-Hans"), "burger name");
            Assert.True(burger.HasRecipe, "burger has no recipe");
            Assert.Equal(3, burger.Qualities.Count, "burger grades");

            var best = search.Best("汉堡", "zh-Hans");
            Assert.NotNull(best, "burger did not resolve");
            Assert.Equal("item.meal:burger", best.Entry.Id, "resolved id");
            Assert.False(best.Redirected, "an exact name is not a redirect");
        });

        Run("material usage index", () =>
        {
            var uses = dataset.Uses("item.pumpkin_crop");
            Assert.True(uses.Count > 0, "no recipe consumes pumpkin");
            Assert.True(HasUse(uses, "item.meal:baked_pumpkin"), "baked pumpkin missing from pumpkin uses");
            Assert.True(HasUse(uses, "item.meal:bowl_pumpkin"), "pumpkin soup missing from pumpkin uses");
            Assert.True(HasUse(uses, "item.meal:vegetable_stew"), "vegetable stew missing from pumpkin uses");
            for (var index = 0; index < uses.Count; index++)
            {
                Assert.True(uses[index].Amount >= 1, "usage amount must be positive: " + uses[index].Entry.Id);
                Assert.True(uses[index].Entry.HasRecipe, "usage without recipe: " + uses[index].Entry.Id);
            }
        });

        Run("material search lists its recipes", () =>
        {
            var results = search.Search("南瓜", "zh-Hans", 120);
            Assert.True(Contains(results, "item.pumpkin_crop"), "pumpkin itself missing");
            Assert.True(Contains(results, "item.meal:baked_pumpkin"), "baked pumpkin missing");
            Assert.True(Contains(results, "item.meal:bowl_pumpkin"), "pumpkin soup missing");
            Assert.True(Contains(results, "item.meal:vegetable_stew"), "vegetable stew missing");

            var stew = Find(results, "item.meal:vegetable_stew");
            Assert.NotNull(stew, "vegetable stew missing");
            Assert.True(stew.Redirected || stew.RelatedByRecipe, "the reason for the row must be visible");
        });

        Run("material search keeps the name match first", () =>
        {
            var best = search.Best("南瓜", "zh-Hans");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.pumpkin_crop", best.Entry.Id, "the material itself must rank first");
            Assert.False(best.RelatedByRecipe, "an exact match must not be reported as a usage");
        });

        Run("fuzzy English typo", () =>
        {
            var best = search.Best("irn ingot", "en");
            Assert.NotNull(best, "no result");
            Assert.Equal("item.ingot_metal", best.Entry.Id, "resolved id");
        });

        Run("partial Chinese character", () =>
        {
            var results = search.Search("铁", "zh-Hans");
            Assert.True(Contains(results, "item.ingot_metal"), "iron ingot missing from results");
            Assert.True(results.Count > 1, "a single character should match several entries");
        });

        Run("quality grade group", () =>
        {
            var onion = dataset.Find("item.onion_crop");
            Assert.NotNull(onion, "onion crop missing");
            Assert.Equal(3, onion.Qualities.Count, "onion grade count");
            var gold = search.Best("金星洋葱", "zh-Hans");
            Assert.NotNull(gold, "quality-prefixed alias did not resolve");
            Assert.Equal("item.onion_crop", gold.Entry.Id, "gold onion id");
        });

        Run("grade names are searchable", () =>
        {
            var story = dataset.Find("item.story");
            Assert.NotNull(story, "story missing");
            Assert.Equal(3, story.Qualities.Count, "story grade count");
            var silver = search.Best("美好的故事", "zh-Hans");
            Assert.NotNull(silver, "silver story name did not resolve");
            Assert.Equal("item.story", silver.Entry.Id, "silver story id");
        });

        Run("item recipe and unlock", () =>
        {
            var ingot = dataset.Find("item.ingot_metal");
            Assert.NotNull(ingot, "iron ingot missing");
            Assert.True(ingot.HasRecipe, "iron ingot has no recipe");
            Assert.Equal(1, ingot.Recipe.Inputs.Count, "recipe input count");
            Assert.Equal("item.1h_ore_metal", ingot.Recipe.Inputs[0].Id, "recipe input");
            Assert.NotNull(dataset.Find(ingot.Recipe.StationId), "recipe station does not resolve");
            Assert.Equal("tech.Iron", ingot.UnlockTechId, "unlock tech");
        });

        Run("building recipe and unlock", () =>
        {
            var furnace = dataset.Find("building.mf_furnace_0");
            Assert.NotNull(furnace, "furnace missing");
            Assert.True(furnace.HasRecipe, "furnace has no build recipe");
            Assert.True(furnace.Recipe.Inputs.Count > 0, "furnace recipe has no inputs");
            Assert.Equal("tech.Iron", furnace.UnlockTechId, "furnace unlock tech");
        });

        Run("all references resolve", () =>
        {
            var dangling = new List<string>();
            foreach (var entry in dataset.Entries.Values)
            {
                var recipe = entry.Recipe;
                if (recipe != null)
                {
                    foreach (var row in recipe.Inputs)
                    {
                        CheckId(dataset, dangling, entry, row.Id);
                    }

                    foreach (var row in recipe.Outputs)
                    {
                        CheckId(dataset, dangling, entry, row.Id);
                    }

                    if (!string.IsNullOrEmpty(recipe.StationId))
                    {
                        CheckId(dataset, dangling, entry, recipe.StationId);
                    }
                }

                if (!string.IsNullOrEmpty(entry.UnlockTechId))
                {
                    CheckId(dataset, dangling, entry, entry.UnlockTechId);
                }

                if (!string.IsNullOrEmpty(entry.UnlockQuestId))
                {
                    CheckId(dataset, dangling, entry, entry.UnlockQuestId);
                }

                foreach (var related in entry.RelatedIds)
                {
                    CheckId(dataset, dangling, entry, related);
                }

                if (!string.IsNullOrEmpty(entry.VariantOf))
                {
                    CheckId(dataset, dangling, entry, entry.VariantOf);
                }
            }

            Assert.Equal(0, dangling.Count, "dangling ids: " + string.Join(", ", dangling));
        });

        Run("language fallback", () =>
        {
            var wood = dataset.Find("item.wood");
            Assert.NotNull(wood, "wood missing");
            Assert.Equal("原木", wood.NameFor("fr"), "unknown language should fall back to zh-Hans");
            Assert.Equal("Log", wood.NameFor("en"), "English name");
        });

        Run("empty and punctuation queries", () =>
        {
            Assert.Equal(0, search.Search("", "zh-Hans").Count, "empty query");
            Assert.Equal(0, search.Search("!!!", "zh-Hans").Count, "punctuation-only query");
        });

        Run("no false positive", () =>
        {
            Assert.Equal(0, search.Search("zzzzzzzz", "en").Count, "unexpected fuzzy match");
        });

        Run("levenshtein sanity", () =>
        {
            Assert.Equal(3, WikiText.Levenshtein("kitten", "sitting"), "distance");
            Assert.Equal(1.0, WikiText.Similarity("wood", "wood"), "identical similarity");
        });

        Run("malformed dataset degrades", () =>
        {
            var messages = new List<string>();
            var empty = WikiData.TryParse("{", messages.Add);
            Assert.NotNull(empty, "null dataset");
            Assert.Equal(0, empty.Entries.Count, "malformed dataset must be empty");
            Assert.True(messages.Count == 1, "expected one log message, got " + messages.Count);
        });

        Run("schema mismatch is rejected", () =>
        {
            var messages = new List<string>();
            var empty = WikiData.TryParse("{ \"schemaVersion\": 2, \"entries\": [] }", messages.Add);
            Assert.Equal(0, empty.Entries.Count, "future schema must not be loaded");
            Assert.True(messages.Count == 1 && messages[0].Contains("schemaVersion"),
                "expected a schemaVersion message, got: " + string.Join("; ", messages));
        });

        Run("unknown references are warnings", () =>
        {
            var malformed = WikiData.Parse(
                "{ \"schemaVersion\": 1, \"entries\": [" +
                "{ \"id\": \"a\", \"kind\": \"item\", \"names\": { \"zh-Hans\": \"A\" }, " +
                "\"recipe\": { \"inputs\": [ { \"id\": \"missing\", \"amount\": 1 } ] } } ] }");
            Assert.Equal(1, malformed.Warnings.Count, "warning count");
            Assert.True(malformed.Warnings[0].Contains("missing"), "warning should name the missing id");
        });

        Run("quality grades are validated", () =>
        {
            var parsed = WikiData.Parse(
                "{ \"schemaVersion\": 1, \"entries\": [" +
                "{ \"id\": \"a\", \"kind\": \"item\", \"names\": { \"zh-Hans\": \"A\" }, " +
                "\"qualities\": [ { \"grade\": \"gold\", \"names\": { \"en\": \"Gold A\" } } ] } ] }");
            Assert.Equal(1, parsed.Find("a").Qualities.Count, "quality count");
            Assert.Equal("gold", parsed.Find("a").Qualities[0].Grade, "grade");
        });

        Console.WriteLine();
        Console.WriteLine("passed: " + _passed + ", failed: " + _failed);
        return _failed == 0 ? 0 : 1;
    }

    private static void CheckId(WikiDataset dataset, List<string> dangling, WikiEntry entry, string id)
    {
        if (dataset.Find(id) == null)
        {
            dangling.Add(entry.Id + "→" + id);
        }
    }

    private static bool HasUse(List<RecipeUse> uses, string id)
    {
        for (var index = 0; index < uses.Count; index++)
        {
            if (string.Equals(uses[index].Entry.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static SearchResult Find(List<SearchResult> results, string id)
    {
        for (var index = 0; index < results.Count; index++)
        {
            if (string.Equals(results[index].Entry.Id, id, StringComparison.Ordinal))
            {
                return results[index];
            }
        }

        return null;
    }

    private static bool Contains(List<SearchResult> results, string id)
    {
        for (var index = 0; index < results.Count; index++)
        {
            if (string.Equals(results[index].Entry.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private static class Assert
    {
        internal static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        internal static void False(bool condition, string message)
        {
            True(!condition, message);
        }

        internal static void NotNull(object value, string message)
        {
            True(value != null, message);
        }

        internal static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + ": expected <" + expected + ">, got <" + actual + ">");
            }
        }
    }
}
