# dotnet ws 

`dotnet ws` is a .NET tool for WebSharper. You can install it with (remove the `-g` option to install locally):

```
dotnet tool install -g dotnet-ws
```

# Usage

```
USAGE: dotnet ws [--help] [<subcommand> [<options>]]

SUBCOMMANDS:

    build [--project xxx] Build the WebSharper project (or solution) in the current folder (or in the nearest
                          parent folder). You can optionally specify a project or solution file using `--project`.
    start <options>       Start the Booster service (wsfscservice) with the given RID and version. If no value
                          is given for version, the latest (as found in the local NuGet cache) will be used.
    stop <options>        Send a stop signal to the Booster service with the given version. If no version is
                          given all running instances are signaled. Use `--force` to kill process(es) instead
                          of sending a stop signal.
    list                  List running Booster versions and their source paths.

OPTIONS:

    -p, --project <PROJ>  Specify the project or solution (.sln/.slnx) file to build.
    -t, --time            Print the per-stage compilation timings when the build service is used.
    -v, --version <VER>   Specify the version of the Booster service.
    -r, --rid <RID>       Specify the Runtime Identifier (win-x64|linux-x64|linux-musl-x64) to use.
    -f, --force           Force the specified operation (used for the stop subcommand).
    --help                Display this list of options.
```

# Dependencies

The only external prerequisite is the **.NET SDK** (`dotnet`). It provides the CLI, MSBuild and NuGet support that `dotnet ws` invokes (`dotnet build`, `dotnet add package`, restore). The tool targets `net8.0` but rolls forward, so it runs on any newer installed runtime (.NET 9/10/11).

WebSharper packages and the Booster service (`wsfscservice`) are not prerequisites — the tool obtains them on the fly (through the packages your project references and, for `dotnet ws compile`, a generated project it sets up itself).

# Notes

Please check the following compatibility matrix for usage with specific `WebSharper.FSharp` package versions

| dotnet-vs | WebSharper.FSharp |
|:--:|:--:|
| <= 0.1.17 | < 8.0.0.514-beta2 |
| > 0.1.17 | >= 8.0.0.514-beta2 |