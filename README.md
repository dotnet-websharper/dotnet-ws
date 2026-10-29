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

# Notes

Please check the following compatibility matrix for usage with specific `WebSharper.FSharp` package versions

| dotnet-vs | WebSharper.FSharp |
|:--:|:--:|
| <= 0.1.17 | < 8.0.0.514-beta2 |
| > 0.1.17 | >= 8.0.0.514-beta2 |