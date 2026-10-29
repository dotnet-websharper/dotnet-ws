# AGENTS.md

Working notes for `dotnet-ws` — the `dotnet ws` .NET tool that drives WebSharper
compilation.

## What this is

- An F# .NET tool (`PackAsTool`, `ToolCommandName=dotnet-ws`, invoked as `dotnet ws`).
- Subcommands (parsed with Argu in `Program.fs`): `build`, `compile`, `watch`,
  `start`, `stop`, `list`, `info`.
- Purpose: fast WebSharper compilation through the long-running Booster service
  (`wsfscservice`), with `dotnet build` as the fallback; plus one-file
  compilation (`compile`) and a file watcher (`watch`).
- Targets `net8.0` with `<RollForward>LatestMajor</RollForward>` so it runs on
  newer runtimes (.NET 9/10/11) without that runtime's SDK installed.

## Build, run, pack

- Build: `dotnet build dotnet-ws.fsproj -c Release`
- Run without installing: `dotnet bin/Release/net8.0/dotnet-ws.dll <subcommand> [args]`
  (same args as `dotnet ws`, e.g. `... build --project Foo.fsproj`).
- Pack: `dotnet pack dotnet-ws.fsproj -c Release -o ./publish /p:Version=10.0.N`
  — this is exactly what `build.cmd` runs. `build.cmd` sets
  `VERSION=10.0.%VERSION_PATCH%`; the release workflow passes
  `VERSION_PATCH=${{ github.run_number }}`.
- There is **no automated test suite**. Verify changes against a real
  WebSharper project (the maintainer keeps samples under `~/ws-testing/*` and
  `~/projects/*`).
- Dogfooding gotcha: packing a version that is already in
  `~/.nuget/packages/dotnet-ws/<version>` won't be re-fetched — bump the
  version (or clear that cache folder) before installing.

## Repo map

| Path | What it is |
| --- | --- |
| `Program.fs` | Entry point; Argu CLI types; `build`/`start`/`stop`/`list`/`watch`; process + service discovery; the watch handlers |
| `FileCompile.fs` | `BuildHelpers` (build/fallback, pipe client, version discovery, `buildProjectFile` fast path); one-file `compile`; `projectFileSetup` |
| `WebSharper.Compiler/WsFscServiceCommon.fs` | Shared named-pipe protocol (`ArgsType`, `readingMessages`, `hashPath`) — must stay in sync with `core/src/compiler/WebSharper.FSharp.Service` |
| `dotnet-ws.sln`, `dotnet-ws.fsproj`, `WebSharper.Compiler/…` | Solution and projects |
| `build.cmd`, `.github/workflows/release.yml` | Packaging and release (manual `workflow_dispatch`, NuGet trusted publishing) |
| `.config/dotnet-tools.json` | Stale placeholder (`1.0.0`) — do not rely on it for the real version |

## How `dotnet ws build` works

The fast path (see `build` in `Program.fs` and `buildProjectFile` in `FileCompile.fs`):

1. Resolve the target (`resolveBuildTarget`): `--project` if given, else a single
   `*.fsproj` in the current directory, else a `.sln`/`.slnx`, else walk up the
   parents — stopping at (and including) the nearest directory that has a
   `wsconfig.json` (a WebSharper project root). A solution target is built with
   `dotnet build <sln>` (see below) and stops there; a single project takes the
   fast path. cwd is switched to the target's directory (paths are made absolute
   first).
2. Read `obj/project.nuget.cache` and regex the
   `websharper.fsharp.<version>.nupkg.sha512` entry to learn the
   WebSharper.FSharp version.
3. Find a running `wsfscservice` whose version matches, send it
   `compile:<project>` over the named pipe, and stream back `n:` / `e:` / `x:`
   messages (`stdout` / `stderr` / finish code).
4. Any failure — missing cache, no matching service, project not cached /
   outdated / type not permitted, IO/unauthorized — prints a reason and does
   `Fallback to "dotnet build"`.

## Key concepts

- **Booster / `wsfscservice`**: a long-running, per-WebSharper.FSharp-version F#
  compile service. One instance serves all compilations under a folder.
- **Pipe name**: `Path.Combine(Path.GetDirectoryName(serviceExe), "WsFscServicePipe")`
  → `hashPath` (lowercased, UTF-8, MD5 hex). On Unix it is prefixed with the
  temp dir. The tool derives it from the running process's
  `MainModule.FileName`; the service derives it from its entry-assembly
  location. Both use `WsFscServiceCommon.hashPath` — **do not change it without
  changing `core`**.
- **Message protocol**: JSON `{ args: string[] }`, 4-byte little-endian length
  prefix. Server replies are strings prefixed `n: `, `e: `, `x: <int>`.
- **Version discovery** (`BuildHelpers.tryGetProcessVersion`): on Windows the
  version comes from embedded file version info; on macOS/Linux
  `FileVersionInfo` is `null`, so it is parsed from the executable path
  (`…/websharper.fsharp/<version>/tools/<tfm>/<rid>/wsfscservice`). Guard
  against null there.
- **`dotnetBinaryFolder`** (`Program.fs`): ordered TFM list used by
  `dotnet ws start` to locate the packaged service under the WebSharper.FSharp
  package's `tools/<tfm>/<rid>/`. Add a new TFM here when WebSharper.FSharp
  starts shipping it.

## Conventions

- F# only, no `.fsi` files.
- CLI parsing via Argu (`Argument` DU with `IArgParserTemplate.Usage`). Keep
  help text consistent with the README.
- All output goes through `BuildHelpers.PrintHelpers` (`info` / `error` /
  `warning`, with indentation). Keep fallback messages uniform:
  `"<reason>. Fallback to \"dotnet build\"."`
- Shell out with `ProcessStartInfo("dotnet")`; always restore
  `Environment.CurrentDirectory` in a `finally`.
- Error codes are negative magic numbers in `BuildHelpers.ErrorCode` /
  `OutputErrorCode`; they must match what the service emits.

## Known limitations / open problems

- **Multi-project solutions** are supported only via a `dotnet build` passthrough
  (the MSBuild WebSharper targets still drive the Booster); the Booster fast path
  in `Program.fs` remains single-project. Building each solution project through
  the fast path, in dependency order, is the planned follow-up. A directory with
  multiple `.fsproj` files and no solution reports *"Found more than one project
  file…"*.
- `.config/dotnet-tools.json` pins a long-obsolete version.
- `dotnetBinaryFolder` is a hand-maintained list that must keep pace with the
  TFMs shipped by WebSharper.FSharp.
