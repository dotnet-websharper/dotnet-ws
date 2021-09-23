# dotnet-ws 
 
```
dotnet-ws is a dotnet tool for WebSharper

USAGE: dotnet-ws.exe [--help] [<subcommand> [<options>]]

SUBCOMMANDS:

    start <options>       Starts wsfscservice with the given RID (Runtime Identifier) (win-x64 or linux-x64 or
                          linux-musl-x64). And version. If no value given for version, the latest will be used.
    stop <options>        Sends a stop signal for the wsfscservice with the given version. If no version given it's
                          all running instances. If --force given kills the process instead of stop signal.
    list                  Lists running wsfscservice versions.

    Use 'dotnet-ws.exe <subcommand> --help' for additional information.

OPTIONS:

    --help                display this list of options.
```
