# dotnet ws 

`dotnet ws` is a .NET tool for WebSharper. You can install it with (remove the `-g` option to install locally):

```
dotnet tool install -g dotnet-ws
```

# Usage

Run `dotnet ws` or `dotnet ws --help` for the full usage — subcommands (`build`, `compile`, `watch`, `start`, `stop`, `list`, `stats`, `mcp`, `compiler status`, `compiler sync`, `info`), their options, and examples.

`dotnet ws stats` shows the statistics the WebSharper 11 boosters keep on this machine: how long messages, compilation stages and agent tools take, and what agents ask. `--json` prints them as JSON.

## WebSharper 11 compilers and boosters

From WebSharper 11, the `WebSharper.FSharp` and `WebSharper.CSharp` packages contain the portable compiler, and the faster ReadyToRun compilers come as the optional `WebSharper.FSharp.Booster` and `WebSharper.CSharp.Booster` .NET tools. The build uses a tool when a tool manifest visible from the project (or a global install) has it at exactly the referenced WebSharper version.

- `dotnet ws compiler status` shows which compiler each project builds with and why, and the running boosters.
- `dotnet ws compiler sync --local` adds the Booster tools at the projects' WebSharper version to the nearest tool manifest (or creates one in the repository root), `--global` installs them globally, and with neither it updates a manifest that already lists them. After a WebSharper update, run `dotnet ws compiler sync` again. Installing the tools needs .NET SDK 10 or newer.
- `dotnet ws start`, `stop`, `list` and the `build` fast path find WebSharper 11 boosters through the records they write to `websharper-boosters` in the temp folder, whether they run from a Booster tool or from the package (as `dotnet wsfscservice.dll`). Boosters of WebSharper 10 and earlier still work as before; for them, `start` takes `--rid`. The C# Booster (`wscscservice`, WebSharper 11.0.0-preview.2+) registers the same way under its own record name; `stop`, `list` and `stats` include it, while `start` and the `build` fast path use F# Boosters only, since the C# Booster doesn't compile.

## Using with AI agents

`dotnet ws mcp` runs the WebSharper Booster's [MCP](https://modelcontextprotocol.io) server over stdio, so AI agents can ask about the projects the Booster compiled: diagnostics, F# signatures, symbol search and usages, how WebSharper translates code to JavaScript, RPC methods, and fast rebuilds. Configure it as a stdio MCP server started in your project's folder, for example in `.mcp.json`:

```json
{
  "mcpServers": {
    "websharper": { "command": "dotnet", "args": [ "ws", "mcp" ] }
  }
}
```

It uses the Booster your projects build with: the `WebSharper.FSharp.Booster` tool if the build uses it, else the one of the WebSharper.FSharp package (`--project`, `--version` or `--booster` choose another). That needs WebSharper 11.0.0-preview.2 or newer. Build the project once to start the Booster and give it something to answer about.

Packages that add agent tools (they set `WebSharperAgentExtensions` in their `.props`) are found in the restored packages of the projects and passed to the server with `--extension`.

For C# projects it runs the C# Booster's server instead (`wscscservice --mcp`, WebSharper 11.0.0-preview.2 or newer), from the `WebSharper.CSharp.Booster` tool or the WebSharper.CSharp package. The C# Booster doesn't compile: `wscsc` reports each build to it, so it answers about the C# projects built since it started. It answers WebSharper diagnostics, how WebSharper translates code to JavaScript, symbol search and RPC methods, but has no C# signatures or usages, and it loads no agent extensions. `dotnet ws mcp` picks the language of `--project`, or of the projects in the folder; in a folder with both, it uses the F# Booster unless you pass `--lang csharp`:

```json
{
  "mcpServers": {
    "websharper-csharp": { "command": "dotnet", "args": [ "ws", "mcp", "--lang", "csharp" ] }
  }
}
```

`dotnet ws list`, `stop`, `stats` and `compiler status` show C# Boosters too, labelled `C#`.

# Dependencies

The only external prerequisite is the **.NET SDK** (`dotnet`). It provides the CLI, MSBuild and NuGet support that `dotnet ws` invokes (`dotnet build`, `dotnet add package`, restore). The tool targets `net8.0` but rolls forward, so it runs on any newer installed runtime (.NET 9/10/11).

WebSharper packages and the Booster service (`wsfscservice`) are not prerequisites — the tool obtains them on the fly (through the packages your project references and, for `dotnet ws compile`, a generated project it sets up itself).

# Contributing

Build `dotnet ws` from source and install it to test against a WebSharper project.

## Build

```
dotnet build dotnet-ws.fsproj -c Release
```

You can run the built tool directly, without installing it:

```
dotnet bin/Release/net8.0/dotnet-ws.dll build
```

## Package a development build

Pack to a local folder. NuGet caches tools by version, so use a fresh version each
iteration (or clear `~/.nuget/packages/dotnet-ws/<version>`):

```
dotnet pack dotnet-ws.fsproj -c Release -o ./artifacts /p:Version=10.0.99
```

## Install and test from a WebSharper project

`<artifacts>` below is the pack output folder from the previous step.

Locally — per-project, via a tool manifest (recommended):

```
cd /path/to/your-ws-project
dotnet new tool-manifest        # only if the project has no .config/dotnet-tools.json
dotnet tool install dotnet-ws --version 10.0.99 --add-source <artifacts>
dotnet ws build
```

Globally — for all projects:

```
dotnet tool install -g dotnet-ws --version 10.0.99 --add-source <artifacts>
# to update an existing install:
dotnet tool update -g dotnet-ws --version 10.0.99 --add-source <artifacts>
```

Or into an isolated tool path (no manifest, no PATH changes):

```
dotnet tool install --tool-path /tmp/ws-tools dotnet-ws --version 10.0.99 --add-source <artifacts>
/tmp/ws-tools/dotnet-ws info
```

## Tips

- `dotnet ws` is the command name of the `dotnet-ws` tool (`ToolCommandName`).
- There is no automated test suite — verify changes against a real WebSharper project.
- For the release process (NuGet publish + GitHub release), see `AGENTS.md`.

# Notes

Please check the following compatibility matrix for usage with specific `WebSharper.FSharp` package versions

| dotnet-vs | WebSharper.FSharp |
|:--:|:--:|
| <= 0.1.17 | < 8.0.0.514-beta2 |
| > 0.1.17 | >= 8.0.0.514-beta2 |