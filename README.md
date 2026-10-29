# dotnet ws 

`dotnet ws` is a .NET tool for WebSharper. You can install it with (remove the `-g` option to install locally):

```
dotnet tool install -g dotnet-ws
```

# Usage

Run `dotnet ws` or `dotnet ws --help` for the full usage — subcommands (`build`, `compile`, `watch`, `start`, `stop`, `list`, `info`), their options, and examples.

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