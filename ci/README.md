# CI without a game install

The plugin links against `Assembly-CSharp.dll` and the Unity modules that ship with Graveyard Keeper, so building it
normally requires the game. CI has no game, therefore `ci/refs` holds a declaration-only stand-in for the game
assembly.

The stub is generated, not hand-written, and contains **no game code** - only the type and member signatures the plugin
actually references. Every member keeps the accessibility it has in the real assembly, and nothing is compiled into the
plugin from these files.

Keeping the real accessibility is what makes the stub build a guard: a direct read of a `protected`/`private` game
member compiles against a loosened reference but throws `FieldAccessException` / `MethodAccessException` on a runtime
that enforces visibility. Because the stub (and the local build, which does not publicize the game assembly) uses the
real modifiers, such an access fails to compile instead. Non-public members go through `GameAccess.cs`.

## Layout

| Path | Purpose |
|---|---|
| `refs/Assembly-CSharp/*.Stub.cs`, `refs/Assembly-CSharp/AssemblyInfo.cs` | Generated stub sources (committed) |
| `refs/Assembly-CSharp/Assembly-CSharp.csproj` | Stub project, referenced by `WikiBook.csproj` when `UseGameRefs=false` |
| `stubgen/` | The generator (Mono.Cecil, net8.0); not part of the plugin |

The committed stub is currently a seed covering only `MainGame.Update`, the single game member the bootstrap plugin
links against. Replace it with generator output as soon as more game APIs are used.

## Regenerating

Run this from the repository root on a machine that has the game, then commit the result:

```powershell
# build the plugin against the real assemblies first - stubgen reads its IL references
dotnet build -c Release

dotnet run --project ci/stubgen -c Release -- `
    bin/Release/WikiBook.dll `
    ci/refs `
    "<game>\Graveyard Keeper_Data\Managed"
```

Regenerate whenever the plugin starts using a game API it did not use before; the stub build fails to compile
otherwise, so CI catches a stale stub immediately.

Members that are only referenced through `nameof(...)` never appear in the IL and have to be listed in
`StubModel.ExtraMembers` (currently `MainGame.Update`, used by a Harmony attribute).

Optional parameters are a caveat: the game assemblies do not store every default value in metadata, so a stub may
declare `= default` where the real default is something else. Pass such arguments explicitly instead of relying on the
default.

## Building like CI does

```powershell
dotnet build -c Release -p:UseGameRefs=false -p:DeployToGame=false -p:BepInExCore="<extracted BepInEx>\core"
```

`UseGameRefs` is detected automatically: it is `true` when `<game>\Graveyard Keeper_Data\Managed\Assembly-CSharp.dll`
exists. `DeployToGame` is `true` when `<game>\BepInEx\plugins` exists, and then the built DLL is copied into the game.

## Core tests

`tests/WikiBook.Tests` compiles the Unity-free core sources (`Json.cs`, `WikiData.cs`, `WikiEntry.cs`, `WikiSearch.cs`)
for net8.0 and runs dataset, alias, ranking and validation checks without the game. CI runs it before the plugin build.
