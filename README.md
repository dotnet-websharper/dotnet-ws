# dotnet ws 

`dotnet ws` is a .NET tool for WebSharper. You can install it with (remove the `-g` option to install locally):

```
dotnet tool install -g dotnet-ws
```

# Usage

```
USAGE: dotnet ws [--help] [<subcommand> [<options>]]

SUBCOMMANDS:

    build [--project xxx] Build the WebSharper project in the current folder (or in the nearest parent folder).
                          You can optionally specify a project file using `--project`.
    start <options>       Start the Booster service (wsfscservice) with the given RID and version. If no value
                          is given for version, the latest (as found in the local NuGet cache) will be used.
    stop <options>        Send a stop signal to the Booster service with the given version. If no version is
                          given all running instances are signaled. Use `--force` to kill process(es) instead
                          of sending a stop signal.
    list                  List running Booster versions and their source paths.

OPTIONS:

    -p, --project <PROJ>  Specify the project file to build.
    -v, --version <VER>   Specify the version of the Booster service.
    -r, --rid <RID>       Specify the Runtime Identifier (win-x64|linux-x64|linux-musl-x64) to use.
    -f, --force           Force the specified operation (used for the stop subcommand).
    --help                Display this list of options.
```
