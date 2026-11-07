namespace StubGen;

/// <summary>
///     Emits declaration-only C# stubs for the game assemblies a plugin links against, so the plugin can be built on a
///     machine that does not have Graveyard Keeper installed (CI). Only the API shape is reproduced - no game code,
///     no method bodies.
///
///     Usage:
///         dotnet run --project ci/stubgen -c Release -- \
///             src/bin/Release/WikiBook.dll ci/refs "&lt;game&gt;/Graveyard Keeper_Data/Managed"
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: stubgen <plugin.dll> <outDir> <gameManagedDir>");
            return 2;
        }

        var model = new StubModel(args[2]);
        model.Collect(args[0]);
        model.Write(args[1]);

        Console.WriteLine($"types: {model.TypeCount}, members: {model.MemberCount}");
        return 0;
    }
}
