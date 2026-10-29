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
  WebSharper project.
- Dogfooding gotcha: packing a version that is already in
  `~/.nuget/packages/dotnet-ws/<version>` won't be re-fetched — bump the
  version (or clear that cache folder) before installing.

## Releasing

Releasing is two independent steps: **publish to NuGet** (CI), then **create the
GitHub Release** (tag + notes). The NuGet run determines the version.

### 1. NuGet publish (workflow)
- Workflow: `.github/workflows/release.yml` (`Release dotnet-ws tool`), manual
  `workflow_dispatch` (runs on `windows-latest`).
- Trigger: `gh workflow run "Release dotnet-ws tool" --repo dotnet-websharper/dotnet-ws --ref main`
- It runs `build.cmd` with `VERSION_PATCH=${{ github.run_number }}`, so the
  published version is **`10.0.<github.run_number>`** — the version comes from
  the run, not from a git tag.
- Auth is NuGet **trusted publishing** (`NuGet/login@v1`, user `intellifactory`,
  OIDC) — no API key needed.
- Watch/verify: `gh run watch <id> --repo dotnet-websharper/dotnet-ws --exit-status`;
  the version is the run's `run_number`
  (`gh api repos/dotnet-websharper/dotnet-ws/actions/runs/<id> --jq .run_number`),
  also visible in the "Build and package" step log
  (`Successfully created package '…\dotnet-ws.10.0.<N>.nupkg'`).

### 2. GitHub Release
- Gather the notes: `git log --oneline <prev-tag>..main` (e.g. `10.0.22..main`).
- Conventions (matching the most recent releases):
  - title `dotnet-ws <version>`, tag `<version>` (e.g. `10.0.23`);
  - created as a **draft** (publish it from the Releases page when ready);
  - body = a short `*` bullet list, then
    `Full changelog [here](https://github.com/dotnet-websharper/dotnet-ws/compare/<prev-tag>...<new-tag>)`.
    (Older `0.1.x` releases instead used `## What's Changed` + `**Full Changelog**: <link>`.)
- **Draft releases do not create the git tag** — create/push it explicitly at
  the released commit (main's tip at publish time):
  `git tag 10.0.N <sha> && git push origin 10.0.N`.
- Create:
  `gh release create 10.0.N --repo dotnet-websharper/dotnet-ws --target <sha> --title "dotnet-ws 10.0.N" --draft --notes-file <file>`
- A draft's page URL shows an `untagged-<hash>` placeholder; it becomes
  `/releases/tag/10.0.N` once published. The release's `tag_name` is correct
  even while it's a draft.

Access needed: a GitHub token with **`repo`** + **`workflow`** scopes (to push,
tag, and `gh workflow run`). NuGet needs nothing (trusted publishing). Without
that, trigger the workflow from the Actions UI and create the release manually.

## Repo map

| Path | What it is |
| --- | --- |
| `Program.fs` | Entry point; Argu CLI types; `build`/`start`/`stop`/`list`/`watch`; process + service discovery; the watch handlers |
| `FileCompile.fs` | `BuildHelpers` (build/fallback, pipe client, version discovery, `buildProjectFile` fast path); one-file `compile`; `projectFileSetup` |
| `BuildInfo.fs` | MSBuild graph capture: injects a targets hook that writes `obj/wsbuild.info` per project, reads/invalidates those manifests, computes the closure + topological order, and runs the capture build |
| `WebSharper.Compiler/WsFscServiceCommon.fs` | Shared named-pipe protocol (`ArgsType`, `readingMessages`, `hashPath`) — must stay in sync with `core/src/compiler/WebSharper.FSharp.Service` |
| `dotnet-ws.sln`, `dotnet-ws.fsproj`, `WebSharper.Compiler/…` | Solution and projects |
| `build.cmd`, `.github/workflows/release.yml` | Packaging and release (manual `workflow_dispatch`, NuGet trusted publishing) |
| `.config/dotnet-tools.json` | Stale placeholder (`1.0.0`) — do not rely on it for the real version |

## How `dotnet ws build` works

`build` in `Program.fs` resolves a target and then:

- **Solution** (`resolveBuildTarget` → `Solution`): runs a *capture build* — an
  ordinary `dotnet build <sln>` with the capture target injected — which builds
  the solution and seeds a manifest for every project.
- **Project** (`SingleProject`): reads the transitive project closure from the
  cached `obj/wsbuild.info` manifests (`BuildInfo.tryClosure`). If every
  manifest is present and fresh, it fast-paths each project in the closure,
  references first (`BuildInfo.topoSort`). Otherwise (cold, or a structural
  change) it runs a capture build of the project, which also rebuilds and
  re-captures its dependencies.

### The capture mechanism (`BuildInfo.fs`)
The capture build injects `-p:CustomAfterMicrosoftCommonTargets=<ws-capture.targets>`
and `-p:WsCaptureBuildInfo=true`. That targets file is imported into every
project and, `AfterTargets="Build"`, writes `obj/wsbuild.info`:
`project`, `configuration`, `tfm`, `refs` (evaluated `ProjectReference` full
paths) and `fingerprint`. Because it runs post-evaluation, imports, conditions
and computed items are already resolved — no project/solution parsing.

Freshness is a structural fingerprint: the project file, its
`Directory.Build.*`/`Directory.Packages.props` chain, `project.assets.json` and
the generated NuGet props/targets. Source (`.fs`) edits do **not** invalidate a
manifest.

The per-project fast path (`tryBuildProjectService`) is the original one:
read `obj/project.nuget.cache` for the `WebSharper.FSharp` version, find a
matching running `wsfscservice`, send `compile:<project>` and stream `n:` / `e:`
/ `x:`; on any failure fall back to `dotnet build "<project>"`.

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
- All output goes through `BuildHelpers.PrintHelpers`: `text` (normal output,
  terminal default color), `info` (optional/diagnostic — e.g. `--time` stage
  timings), `error`/`warning` (red/yellow). Keep fallback messages uniform:
  `"<reason>. Fallback to \"dotnet build\"."`
- Shell out with `ProcessStartInfo("dotnet")`; always restore
  `Environment.CurrentDirectory` in a `finally`.
- Error codes are negative magic numbers in `BuildHelpers.ErrorCode` /
  `OutputErrorCode`; they must match what the service emits.

## Known limitations / open problems

- **Multi-project support is closure-based.** `dotnet ws build` inside a project
  fast-paths that project and its transitive `ProjectReference` dependencies, in
  order. Running at a solution root still does a full MSBuild build (it seeds the
  manifests but doesn't fast-path the whole solution).
- The captured graph is `ProjectReference`-based: solution-level
  `ProjectDependencies` and references produced *during* a build by tasks are not
  captured. Multi-targeted projects share one `obj/wsbuild.info` (last TFM wins).
  Projects that don't import `Microsoft.Common.targets` get no manifest and fall
  back to `dotnet build`.
- Freshness is mtime-based over a stored file list, so it can't see **set growth**
  from globs (a newly created file matched by `**/*.targets`,
  `Content Include="dir/**"`, etc. without editing a tracked file). Accepted
  limitation: explicitly-included artifacts are caught, because adding one edits
  the project file (whose mtime is tracked).
- A directory with multiple `.fsproj` files and no solution reports *"Found more
  than one project file…"*.
- `.config/dotnet-tools.json` pins a long-obsolete version.
- `dotnetBinaryFolder` is a hand-maintained list that must keep pace with the
  TFMs shipped by WebSharper.FSharp.
