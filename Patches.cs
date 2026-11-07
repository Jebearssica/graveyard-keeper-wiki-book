using System;
using HarmonyLib;

namespace WikiBook;

internal static class Patches
{
    /// <summary>
    ///     The book's Unity component has its own Update/OnGUI; this patch only drives the optional in-game self-test,
    ///     which has to wait until a save has been loaded.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.Update))]
    private static void MainGame_Update_Postfix()
    {
        try
        {
            SelfTest.Tick();
        }
        catch (Exception exception)
        {
            // An exception in MainGame.Update would repeat every frame; report it once and disable the run.
            SelfTest.DisableAfterError(exception);
        }
    }
}
