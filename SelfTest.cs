using System;
using System.Collections.Generic;
using UnityEngine;

namespace WikiBook;

/// <summary>
///     One-shot in-game verification, enabled with <c>Debug.SelfTest=true</c>. It drives the real search index and the
///     real window, then logs lines the release checklist can grep for (<c>[SELFTEST] PASS/FAIL</c>). Waiting a few
///     seconds gives the game time to finish starting; the checks themselves do not need a loaded save.
/// </summary>
internal static class SelfTest
{
    private const int FramesBeforeRun = 180;
    private const int FramesToDraw = 90;

    private static int _frames;
    private static bool _done;
    private static bool _disabled;
    private static bool _drawing;
    private static int _drawFrames;
    private static int _drawsBefore;
    private static int _passed;
    private static int _failed;

    internal static void Tick()
    {
        if (_disabled || Plugin.SelfTest == null || !Plugin.SelfTest.Value)
        {
            return;
        }

        if (_drawing)
        {
            _drawFrames++;
            if (_drawFrames < FramesToDraw)
            {
                return;
            }

            _drawing = false;
            _done = true;
            Finish();
            return;
        }

        if (_done)
        {
            return;
        }

        _frames++;
        if (_frames < FramesBeforeRun)
        {
            return;
        }

        Run();
        StartDrawCheck();
    }

    internal static void DisableAfterError(Exception exception)
    {
        if (_disabled)
        {
            return;
        }

        _disabled = true;
        if (Plugin.Log != null)
        {
            Plugin.Log.LogError("[SELFTEST] ABORT: " + exception);
        }
    }

    private static void Run()
    {
        if (Plugin.Log == null)
        {
            return;
        }

        Plugin.Log.LogInfo("[SELFTEST] running");
        var passed = 0;
        var failed = 0;

        Check("dataset", () =>
        {
            if (Plugin.Dataset == null || Plugin.Dataset.Entries.Count == 0)
            {
                return "no entries loaded";
            }

            return Plugin.Dataset.Entries.Count + " entries, schema " + Plugin.Dataset.SchemaVersion + ", data " +
                   Plugin.Dataset.DataVersion;
        }, ref passed, ref failed);

        Check("schema", () =>
        {
            if (Plugin.Dataset.SchemaVersion != WikiData.SupportedSchemaVersion)
            {
                throw new InvalidOperationException("schema " + Plugin.Dataset.SchemaVersion + " is not supported");
            }

            return "schema " + Plugin.Dataset.SchemaVersion;
        }, ref passed, ref failed);

        Check("references", () =>
        {
            if (Plugin.Dataset.Warnings.Count > 0)
            {
                throw new InvalidOperationException(Plugin.Dataset.Warnings.Count + " warning(s): " +
                                                    Plugin.Dataset.Warnings[0]);
            }

            return "all recipe, unlock and redirect ids resolve";
        }, ref passed, ref failed);

        Check("exact-search", () =>
        {
            var checkedCount = 0;
            foreach (var entry in Plugin.Dataset.Entries.Values)
            {
                var name = entry.Names.Get(WikiDataset.DefaultLanguage);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var best = Plugin.Search.Best(name, WikiDataset.DefaultLanguage);
                if (best == null || string.IsNullOrEmpty(best.Entry.Names.Get(WikiDataset.DefaultLanguage)))
                {
                    throw new InvalidOperationException("“" + name + "” did not resolve to any entry");
                }

                checkedCount++;
                if (checkedCount >= 5)
                {
                    break;
                }
            }

            if (checkedCount == 0)
            {
                throw new InvalidOperationException("the dataset has no localized names");
            }

            return checkedCount + " exact names";
        }, ref passed, ref failed);

        Check("alias-redirect", () =>
        {
            var checkedCount = 0;
            foreach (var entry in Plugin.Dataset.Entries.Values)
            {
                for (var index = 0; index < entry.Aliases.Count && checkedCount < 5; index++)
                {
                    var alias = entry.Aliases[index];
                    var best = Plugin.Search.Best(alias, WikiDataset.DefaultLanguage);
                    if (best == null || !string.Equals(best.Entry.Id, entry.Id, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("“" + alias + "” did not resolve to " + entry.Id);
                    }

                    if (!best.Redirected || string.IsNullOrEmpty(best.AliasNote))
                    {
                        throw new InvalidOperationException("“" + alias + "” has no visible redirect note");
                    }

                    checkedCount++;
                }

                if (checkedCount >= 5)
                {
                    break;
                }
            }

            return checkedCount == 0 ? "no aliases in this dataset" : checkedCount + " alias(es), visible notes shown";
        }, ref passed, ref failed);

        Check("quest-alias", () =>
        {
            // The spec's canonical case: quest text names the item differently from the item itself.
            var best = Plugin.Search.Best("金星爱情故事", WikiDataset.DefaultLanguage);
            if (best == null || !string.Equals(best.Entry.Id, "item.story", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("“金星爱情故事” did not resolve to item.story");
            }

            if (!best.Redirected || string.IsNullOrEmpty(best.AliasNote))
            {
                throw new InvalidOperationException("the quest wording has no visible redirect note");
            }

            return best.AliasNote;
        }, ref passed, ref failed);

        Check("graded-meal", () =>
        {
            // Cooking items share one locale key on the base id (meal:burger); they were missing before.
            var best = Plugin.Search.Best("汉堡", WikiDataset.DefaultLanguage);
            if (best == null || !string.Equals(best.Entry.Id, "item.meal:burger", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("“汉堡” did not resolve to item.meal:burger");
            }

            if (!best.Entry.HasRecipe)
            {
                throw new InvalidOperationException("the burger has no recipe");
            }

            return best.Entry.NameFor(WikiDataset.DefaultLanguage) + ", " + best.Entry.Qualities.Count + " grades";
        }, ref passed, ref failed);

        Check("material-uses", () =>
        {
            var uses = Plugin.Dataset.Uses("item.pumpkin_crop");
            if (uses.Count == 0)
            {
                throw new InvalidOperationException("no recipe consumes the pumpkin");
            }

            var results = Plugin.Search.Search("南瓜", WikiDataset.DefaultLanguage, 120);
            var linked = false;
            for (var index = 0; index < results.Count; index++)
            {
                if (results[index].RelatedByRecipe)
                {
                    linked = true;
                    break;
                }
            }

            if (!linked)
            {
                throw new InvalidOperationException("searching “南瓜” did not list any recipe that uses it");
            }

            return uses.Count + " recipe(s) consume the pumpkin";
        }, ref passed, ref failed);

        Check("fuzzy-search", () =>
        {
            var checkedCount = 0;
            foreach (var entry in Plugin.Dataset.Entries.Values)
            {
                var name = entry.Names.Get(WikiDataset.DefaultLanguage);
                if (string.IsNullOrEmpty(name) || name.Length < 3)
                {
                    continue;
                }

                var shortened = name.Substring(0, name.Length - 1);
                var best = Plugin.Search.Best(shortened, WikiDataset.DefaultLanguage);
                if (best == null)
                {
                    throw new InvalidOperationException("“" + shortened + "” did not resolve to any entry");
                }

                checkedCount++;
                break;
            }

            if (checkedCount == 0)
            {
                throw new InvalidOperationException("no long enough name to shorten");
            }

            return "shortened query resolved";
        }, ref passed, ref failed);

        Check("recipe-links", () =>
        {
            var checkedCount = 0;
            foreach (var entry in Plugin.Dataset.Entries.Values)
            {
                if (!entry.HasRecipe)
                {
                    continue;
                }

                var rows = new List<Ingredient>(entry.Recipe.Inputs);
                rows.AddRange(entry.Recipe.Outputs);
                for (var index = 0; index < rows.Count; index++)
                {
                    if (Plugin.Dataset.Find(rows[index].Id) == null)
                    {
                        throw new InvalidOperationException(entry.Id + " references unknown " + rows[index].Id);
                    }
                }

                if (!string.IsNullOrEmpty(entry.Recipe.StationId) &&
                    Plugin.Dataset.Find(entry.Recipe.StationId) == null)
                {
                    throw new InvalidOperationException(entry.Id + " references unknown station " +
                                                        entry.Recipe.StationId);
                }

                checkedCount++;
                if (checkedCount >= 5)
                {
                    break;
                }
            }

            return checkedCount + " recipe(s) resolve";
        }, ref passed, ref failed);

        Check("quality-grades", () =>
        {
            var checkedCount = 0;
            foreach (var entry in Plugin.Dataset.Entries.Values)
            {
                if (!entry.HasQualities)
                {
                    continue;
                }

                if (entry.Qualities.Count < 2)
                {
                    throw new InvalidOperationException(entry.Id + " lists a single quality grade");
                }

                for (var index = 0; index < entry.Qualities.Count; index++)
                {
                    if (string.IsNullOrEmpty(entry.Qualities[index].Grade) ||
                        entry.Qualities[index].Names.IsEmpty)
                    {
                        throw new InvalidOperationException(entry.Id + " has an unnamed quality grade");
                    }
                }

                checkedCount++;
                if (checkedCount >= 5)
                {
                    break;
                }
            }

            return checkedCount == 0 ? "no quality grades in this dataset" : checkedCount + " grade group(s)";
        }, ref passed, ref failed);

        Check("empty-query", () =>
        {
            var results = Plugin.Search.Search("!!!", WikiDataset.DefaultLanguage);
            if (results.Count != 0)
            {
                throw new InvalidOperationException("a punctuation-only query returned " + results.Count + " results");
            }

            return "no results, no exception";
        }, ref passed, ref failed);

        Check("kind-filter", () =>
        {
            var gui = WikiBookGui.Instance;
            if (gui == null)
            {
                throw new InvalidOperationException("the UI component was not added to the plugin");
            }

            var expected = 0;
            foreach (var entry in Plugin.Dataset.Entries.Values)
            {
                if (entry.Kind == EntryKind.Tech)
                {
                    expected++;
                }
            }

            if (expected == 0)
            {
                return "no tech entries in this dataset";
            }

            gui.Open();
            gui.SetKindFilter(EntryKind.Tech);
            var actual = gui.ResultCount;
            gui.SetKindFilter(null);
            gui.Close();

            if (actual != expected)
            {
                throw new InvalidOperationException("tech filter shows " + actual + " of " + expected + " entries");
            }

            return actual + " tech entries";
        }, ref passed, ref failed);

        Check("keyboard-selection", () =>
        {
            var gui = WikiBookGui.Instance;
            if (gui == null)
            {
                throw new InvalidOperationException("the UI component was not added to the plugin");
            }

            gui.Open();
            if (gui.ResultCount < 2)
            {
                gui.Close();
                return "dataset too small to move the selection";
            }

            var first = gui.SelectedId;
            gui.MoveSelection(1);
            var second = gui.SelectedId;
            gui.MoveSelection(-1);
            var back = gui.SelectedId;
            gui.Close();

            if (string.Equals(first, second, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("moving down did not change the selected entry");
            }

            if (!string.Equals(first, back, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("moving back up did not return to the first entry");
            }

            return "selection moved down and back up";
        }, ref passed, ref failed);

        Check("window-toggle", () =>
        {
            var gui = WikiBookGui.Instance;
            if (gui == null)
            {
                throw new InvalidOperationException("the UI component was not added to the plugin");
            }

            gui.Open();
            if (!gui.IsOpen)
            {
                throw new InvalidOperationException("Open() did not open the window");
            }

            gui.Close();
            if (gui.IsOpen)
            {
                throw new InvalidOperationException("Close() did not close the window");
            }

            return "open/close cycle completed";
        }, ref passed, ref failed);

        _passed = passed;
        _failed = failed;
    }

    /// <summary>
    ///     Opening the window is not enough: OnGUI has to draw it for a few frames so the dynamic font, the filter
    ///     tabs and the result layout really run inside the game. UI exceptions are counted by the window.
    /// </summary>
    private static void StartDrawCheck()
    {
        var gui = WikiBookGui.Instance;
        if (gui == null)
        {
            _done = true;
            Plugin.Log.LogInfo("[SELFTEST] done: " + _passed + " passed, " + _failed + " failed");
            return;
        }

        gui.Open();
        _drawsBefore = gui.DrawCount;
        _drawFrames = 0;
        _drawing = true;
    }

    private static void Finish()
    {
        var gui = WikiBookGui.Instance;
        var draws = gui != null ? gui.DrawCount - _drawsBefore : 0;
        var errors = gui != null ? gui.ErrorCount : -1;
        if (gui != null)
        {
            gui.Close();
        }

        if (draws > 0 && errors == 0)
        {
            _passed++;
            Plugin.Log.LogInfo("[SELFTEST] PASS window-draw: " + draws + " draw pass(es), no UI errors");
        }
        else
        {
            _failed++;
            Plugin.Log.LogError("[SELFTEST] FAIL window-draw: " + draws + " draw pass(es), " + errors + " UI error(s)");
        }

        Plugin.Log.LogInfo("[SELFTEST] done: " + _passed + " passed, " + _failed + " failed");
    }

    private static void Check(string name, Func<string> action, ref int passed, ref int failed)
    {
        try
        {
            var detail = action();
            passed++;
            Plugin.Log.LogInfo("[SELFTEST] PASS " + name + ": " + detail);
        }
        catch (Exception exception)
        {
            failed++;
            Plugin.Log.LogError("[SELFTEST] FAIL " + name + ": " + exception.Message);
        }
    }
}
