using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WikiBook;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private const string EmbeddedDataset = "WikiBook.data.wiki-zh-Hans.json";

    internal static ManualLogSource Log { get; private set; }

    internal static WikiDataset Dataset { get; private set; }

    internal static WikiSearch Search { get; private set; }

    internal static ConfigEntry<string> OpenKey { get; private set; }

    internal static ConfigEntry<string> Language { get; private set; }

    internal static ConfigEntry<bool> SelfTest { get; private set; }

    private void Awake()
    {
        Log = Logger;

        OpenKey = Config.Bind(
            "General",
            "OpenKey",
            "F2",
            "Key that opens and closes the wiki book. Uses Unity key names (F2, B, BackQuote...).\n" +
            "打开和关闭 Wiki 手册的按键，使用 Unity 按键名（F2、B、BackQuote 等）。");

        Language = Config.Bind(
            "General",
            "Language",
            "auto",
            "\"auto\" follows the game language; otherwise a dataset language such as \"zh-Hans\" or \"en\".\n" +
            "“auto”跟随游戏语言；也可以写数据集里的语言，例如“zh-Hans”或“en”。");

        SelfTest = Config.Bind(
            "Debug",
            "SelfTest",
            false,
            "Runs one-shot search and UI checks shortly after the game starts and logs PASS/FAIL.\n" +
            "游戏启动后自动运行一次搜索与界面自检，并在日志中输出 PASS/FAIL。");

        Dataset = LoadDataset();
        Search = new WikiSearch(Dataset);

        foreach (var warning in Dataset.Warnings)
        {
            Logger.LogWarning("Dataset: " + warning);
        }

        var gui = gameObject.AddComponent<WikiBookGui>();
        gui.Initialize(Search, Dataset, ResolveLanguage, ParseOpenKey);

        try
        {
            Harmony.CreateAndPatchAll(typeof(Patches), MyPluginInfo.PLUGIN_GUID);
        }
        catch (Exception exception)
        {
            // A game update that moves MainGame.Update must not stop the book from opening.
            Logger.LogError("Harmony patches could not be applied: " + exception);
        }

        Logger.LogInfo("Wiki Book loaded: " + Dataset.Entries.Count + " entries, data " + Dataset.DataVersion + ".");
    }

    private static WikiDataset LoadDataset()
    {
        try
        {
            var assembly = typeof(Plugin).Assembly;
            using (var stream = assembly.GetManifestResourceStream(EmbeddedDataset))
            {
                if (stream != null)
                {
                    return WikiData.TryParse(WikiData.ReadAll(stream), LogWarning);
                }
            }

            var directory = Path.GetDirectoryName(assembly.Location);
            if (!string.IsNullOrEmpty(directory))
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(directory, "wiki-zh-Hans.json"),
                             Path.Combine(directory, "data", "wiki-zh-Hans.json")
                         })
                {
                    if (File.Exists(candidate))
                    {
                        Log.LogInfo("Loading dataset from " + candidate + ".");
                        return WikiData.TryParse(File.ReadAllText(candidate, Encoding.UTF8), LogWarning);
                    }
                }
            }

            LogWarning("No dataset found; the book opens with an empty index.");
        }
        catch (Exception exception)
        {
            LogWarning("Dataset could not be loaded: " + exception.Message);
        }

        return WikiDataset.Empty;
    }

    private static void LogWarning(string message)
    {
        Log.LogWarning(message);
    }

    private static string ResolveLanguage()
    {
        var configured = Language != null ? Language.Value : null;
        if (!string.IsNullOrEmpty(configured) &&
            !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        var system = Application.systemLanguage;
        return system == SystemLanguage.ChineseSimplified || system == SystemLanguage.ChineseTraditional
            ? WikiDataset.DefaultLanguage
            : "en";
    }

    private static KeyCode ParseOpenKey()
    {
        var value = OpenKey != null ? OpenKey.Value : null;
        try
        {
            return (KeyCode)Enum.Parse(typeof(KeyCode), value, true);
        }
        catch (Exception)
        {
            return KeyCode.F2;
        }
    }
}
