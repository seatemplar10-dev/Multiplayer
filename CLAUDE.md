# Rimworld Multiplayer Mod

A RimWorld mod enabling cooperative multiplayer gameplay.

## Project Structure

```
Source/
├── Multiplayer.sln          # Visual Studio solution
├── Client/                  # Main client mod (net10.0) → Multiplayer.dll
├── Common/                  # Shared library (net10.0) → MultiplayerCommon.dll
├── Server/                  # Standalone dedicated server (net10.0)
├── MultiplayerLoader/       # Mod entry point/loader (net10.0)
├── Tests/                   # NUnit tests (net10.0)
└── TestsOnMono/             # Mono-only tests; out of the solution on rw-1.7
```

**How the projects relate:**
- **MultiplayerLoader** is the RimWorld mod entry point. It loads Client and Common DLLs at runtime via reflection.
- **Client** contains all gameplay patches, UI, networking, and sync logic. Depends on Common and MultiplayerLoader.
- **Common** is shared between Client and Server: networking protocol (LiteNetLib), serialization, packets.
- **Server** is a standalone .NET console app for dedicated hosting. Depends on Common.

## Build

```bash
# once, from a RimWorld 1.7 install (copies Assembly-CSharp, UnityStubs and Steamworks into gitignored Refs/RimWorld)
RIMWORLD_DIR="/path/to/RimWorld" ./scripts/fetch-rimworld-refs.sh    # or scripts\fetch-rimworld-refs.ps1
cd Source
dotnet build Multiplayer.sln -c Release
```

**Requirements:** .NET 10 SDK. SourceGen needs Roslyn 5.3; if the SDK's compiler is older, add a gitignored `Source/Client/PersonalDefinitions.targets` that references `Microsoft.Net.Compilers.Toolset` 5.3.0.

Post-build, Client automatically copies output DLLs to `Assemblies/` and `AssembliesCustom/` at the repo root (the mod folder structure RimWorld expects). On `rw-1.7` those folders are committed: rebuild and commit them with every source change, because the repo itself is what players install. Never commit anything from `Refs/`; those are the game's files.

## RimWorld 1.7 (`rw-1.7` branch)

RimWorld 1.7 runs on the Waylith engine: CoreCLR (.NET 10), Vulkan, and `UnityStubs.dll` in place of UnityEngine. Mods get Harmony 3. This branch is the 1.7 port; `master` follows rwmt for 1.6.

- **No Prepatcher.** Prepatcher cannot run on CoreCLR. Its one use, syncing ritual role assignment, is now a Harmony prefix in `MultiplayerLoader/SyncRituals.cs`.
- **Shared generic bodies.** CoreCLR compiles one body for every reference-type instantiation of a generic method, so a patch on `Foo<A>.M` also runs for `Foo<B>`, while Harmony's replacement is built for `A`. `SyncRituals` shows the pattern: a prefix that takes the generic arguments as `object`, always returns false, and runs an unpatched `DynamicMethodDefinition` copy for the instance's own instantiation.
- **Strict IL.** Mono tolerated IL that CoreCLR rejects with `InvalidProgramException`: `ret` with an empty stack for a non-void method, and branches out of a `try` block. Emitted and transpiled IL must be valid by the ECMA rules.
- **Strict patch parameters.** Harmony 3 checks `__instance` and argument types. A static target takes no `__instance`.
- **No Mono internals.** `Native` (the Mono embedding API, LMF stack walking, debug options) only runs when `Native.IsMono`. `MpUtil.MarkNoInlining` is a no-op on CoreCLR; Harmony already disables inlining of the methods it patches. `JittedMethods` (Mono profiler) is off.
- **Desync stack traces.** Fast mode reads Mono's LMF, which CoreCLR does not have, so on 1.7 Fast (the default) records no traces. Slow records managed traces (`DeferredStackTracing.ManagedTrace`, one `System.Diagnostics.StackTrace` per traced call: slower ticks). The mode is not saved in the settings file; `-mptracemode=Slow` sets it for one run. The trace hash is the random state only, because tiered compilation can inline a frame away in one process and not another; `-mptraceframehash` hashes the frames too. `-mptracemode=Lean` (FIXES row 267) records the same entries and hashes with no stack (~15 ns instead of ~5 µs per traced call), so a Lean PC and a Slow PC compare and the desync index is found; only the Lean side's traces have no frames. With `-mptraceframehash`, Lean traces like Slow.
- **Arbiter.** `ArbiterLauncher` starts it with `-headless -NoFileLog -arbiter -connect=... -savedatafolder=<host save data>\MpArbiter -mptracemode=<host's mode>`; the Unity switches and `-logfile` do nothing on 1.7. Its stdout and stderr go to `<host save data>\arbiter_log.txt`. It gets its own save data folder because the game rewrites config files on its own; before each start every top-level folder of the host's save data is mirrored into it (mods keep settings and data outside Config), except saves, Multiplayer's output and caches (MpReplays, MpDesyncs, MpLogs, MpCache), the game's output (Screenshots, DevOutput, External), the engine caches, RimSaves' save previews, and subfolders named Cache, Logs or Reports. A headless arbiter cannot click "Connect anyway", so it joins on a mod or config mismatch as long as the defs match, and logs the difference. `ArbiterHeadlessPatches` lifts the 10,000-message log limit for the arbiter (its log would stop while it loads) and skips the colonist bar layout under `-headless` (it never ends at screen width 0). Headless Waylith never runs OnGUI, so a synchronous long event with a text (the join point's "MpCreatingJoinPoint") would wait forever to be displayed; `HeadlessLongEventDisplayed` marks the current event displayed before each `LongEventsUpdate`.
- **Waylith's file log.** `<persistent data>\Logs\Player.log` is written outside `-savedatafolder` and shared by every instance on the machine; the desync report uses the in-memory log instead.
- **Early static constructors.** Don't call `Verse.Widgets` helpers (or anything that touches textures and shaders) from patch setup; it runs the class constructor before the graphics device is ready.

Check a change against the real game DLLs before shipping it. `rimworld-mods/tools/RwCompat` lists members a DLL references that 1.7 no longer has, and `rimworld-mods/tools/RwPatchHarness --patch --mp-init` applies every Harmony patch and runs Multiplayer's sync setup under .NET 10 without starting the game.

### Installing this build

1. Unsubscribe the Workshop Multiplayer mod (2606448745); this build uses the same package id, `rwmt.Multiplayer`.
2. `git clone --recurse-submodules -b waylith-1.7-port https://github.com/seatemplar10-dev/Multiplayer.git`, then junction or copy the clone into RimWorld's `Mods` folder. (StephenSHorton `rw-1.7` is protocol 56 and cannot join this protocol 57 build.)
3. Every player needs RimWorld 1.7 and the same commit of this branch. Multiplayer compares mod files when a player joins.
4. Pull with RimWorld closed.

## Key Architecture

### Harmony Patching
Extensive use of Harmony to patch RimWorld. Patches live in `Client/Patches/` organized by concern (Determinism, Alerts, Letters, etc.). Convention:
```csharp
[HarmonyPatch(typeof(ClassName), nameof(ClassName.MethodName))]
static class ClassName_MethodName_Patch
{
    static void Prefix() { }
    static void Postfix() { }
}
```

### Synchronization System
- `SyncHandler` base class for syncing fields, methods, and actions
- `SyncSerialization` / `SyncWorkerDictionary` for serializing game state
- `ReadingSyncWorker` / `WritingSyncWorker` for binary protocol

### Networking (LiteNetLib)
- `ConnectionBase` → `LiteNetConnection` abstraction
- State machine: Disconnected → Connecting → Loading → Playing
- Type-safe `IPacket` implementations in `Common/Networking/`

### Time & Desync
- `AsyncTimeComp` / `AsyncWorldTimeComp` handle time synchronization
- `SyncCoordinator` validates world state checksums across clients
- `DeferredStackTracing` for desync debugging

### Multi-faction
- `FactionContext` tracks current faction context for determinism

## Code Style

- **Indent:** 4 spaces for C#, CRLF line endings
- **Naming:** PascalCase for public members/classes/constants, camelCase for private fields
- **Language:** C# 12 features (pattern matching, using declarations, etc.)
- **Nullable:** Enabled in Common and Server projects

## Testing

```bash
cd Source
dotnet test
```

Uses NUnit 4, FluentAssertions, and Verify (snapshot testing). Tests cover serialization, packets, and server logic.

## Version

Version is defined in `Source/Common/Version.cs` (`SimpleVersion` constant). Also update `About/About.xml` for RimWorld mod metadata.

## Git Workflow

- **Branch target:** PRs go to `dev` branch
- **Branch naming:** `issue-{number}-description` (e.g. `issue-9-feature`)
- **Commit prefix:** `#{issue_number}: description`
- **Merge policy:** Squash merge, 2+ reviewer approval
- **CI:** GitHub Actions runs restore → build (Debug + Release) → test on `ubuntu-latest` with .NET 9 (upstream; `rw-1.7` needs the game refs and is not built in CI)
