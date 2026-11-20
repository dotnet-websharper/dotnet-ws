// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2018 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}
open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open Argu
open Fake.Core
open WebSharper.Compiler.WsFscServiceCommon

open FileCompile
open FileCompile.BuildHelpers

let extensionsToWatch =
    [
        ".fsproj"
        ".fs"
        ".fsi"
        ".fsx"
        ".html"
    ]

let foldersToExclude =
    [
        "bin"
        "obj"
        "node_modules"
        "content"
        "scripts"
        "dist"
        "out"
        "output"
    ]

/// Platforms of the per-platform boosters in WebSharper 10 and earlier packages.
type RidEnum =
    | ``win-x64`` = 1
    | ``linux-x64`` = 2
    | ``linux-musl-x64`` = 3
    | ``win-arm64`` = 4
    | ``linux-arm64`` = 5
    | ``linux-musl-arm64`` = 6
    | ``osx-x64`` = 7
    | ``osx-arm64`` = 8

type Argument =
    | [<CliPrefix(CliPrefix.None)>] Build of ParseResults<BuildArguments>
    | [<CliPrefix(CliPrefix.None)>] Start of ParseResults<StartArguments>
    | [<CliPrefix(CliPrefix.None)>] Stop of ParseResults<StopArguments>
    | [<CliPrefix(CliPrefix.None); SubCommand>] Info
    | [<CliPrefix(CliPrefix.None); SubCommand>] List
    | [<CliPrefix(CliPrefix.None)>] Stats of ParseResults<StatsArguments>
    | [<CliPrefix(CliPrefix.None)>] Compile of ParseResults<CompileArguments>
    | [<CliPrefix(CliPrefix.None)>] Watch of ParseResults<WatchArguments>
    | [<CliPrefix(CliPrefix.None)>] Compiler of ParseResults<CompilerCommand.CompilerArguments>

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Compiler _ -> "Show which WebSharper compiler the projects use, or install the matching Booster tools (WebSharper 11+)."
            | Start _ -> "Start the Booster service (wsfscservice) of the given version: the one of the WebSharper.FSharp.Booster tool if installed, otherwise the one of the WebSharper.FSharp package. If no version is given, the version of the project in the current folder, or the latest in the local NuGet cache, is used."
            | Stop _ -> "Send a stop signal to the Booster service with the given version. If no version is given all running instances are signaled. Use `--force` to kill process(es) instead of sending a stop signal."
            | List -> "List running Booster versions and their source paths."
            | Stats _ -> "Show the statistics of the boosters: how long messages, compilation stages and agent tools take (WebSharper 11+)."
            | Build _ -> "Build the WebSharper project in the current folder (or in the nearest parent folder). You can optionally specify a project file using `--project`."
            | Compile _ -> "Compile a given fs(x) file to JavaScript or TypeScript utilizing WebSharper's Booster if enabled"
            | Info -> "Prints the version of the dotnet ws tool"
            | Watch _ -> "Sets up a file watcher, that triggers recompilation via dotnet ws compile or dotnet ws build depending on the scenario"

and WatchArguments =
    // | [<AltCommandLine("-v")>] Verbosity
    | [<AltCommandLine("-p")>] Pattern of string

    interface IArgParserTemplate with
        member w.Usage =
            match w with
            | Pattern _ -> "Specifies pattern for the watch"
            // | Verbosity -> "Specifies a filtering pattern for the watch statement."

and StartArguments =
    | [<AltCommandLine("-r")>] RID of RidEnum
    | [<AltCommandLine("-v")>] Version of semver:string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | RID _ -> "WebSharper 10 and earlier only: the Runtime Identifier of the booster in the package. Defaults to the current platform."
            | Version _ -> "Specify the version of the Booster service."

and StopArguments =
    | [<AltCommandLine("-v")>] Version of semver:string
    | [<AltCommandLine("-f")>] Force
    | ``Clear-Cache``

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Version _ -> "Specify the version of the Booster service."
            | Force -> "Force the specified operation (used for the stop subcommand)."
            | ``Clear-Cache`` -> "Clear local temp folder data."

and StatsArguments =
    | [<AltCommandLine("-v")>] Version of semver:string
    | Json

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Version _ -> "Only the boosters of this version."
            | Json -> "Print the statistics as JSON."

and BuildArguments =
    | [<AltCommandLine("-p")>] Project of string
    | [<AltCommandLine("-t")>] Time

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Project _ -> "Specify the project or solution (.sln/.slnx) file to build. Defaults to a single project, or a solution, found in the current or a nearest parent folder."
            | Time -> "Print the per-stage compilation timings when the build service is used."

/// A resolved `dotnet ws build` target.
type BuildTarget =
    | SingleProject of string
    | Solution of string
    | NotFound
    | Ambiguous

let isSolutionFile (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".sln" | ".slnx" -> true
    | _ -> false

// Resolves the build target from an optional --project argument.
// With no argument, prefers a single *.fsproj in the current folder, then a
// solution (.sln/.slnx), then walks up the parent folders. The search stops at
// (and includes) the nearest folder containing a wsconfig.json.
let resolveBuildTarget (explicitTarget: string option) =
    match explicitTarget with
    | Some target ->
        if File.Exists target then
            let full = Path.GetFullPath target
            if isSolutionFile full then Solution full else SingleProject full
        else
            NotFound
    | None ->
        let rec find location =
            let fsprojs = Directory.EnumerateFiles(location, "*.fsproj") |> Seq.toList
            let solutions =
                seq {
                    yield! Directory.EnumerateFiles(location, "*.sln")
                    yield! Directory.EnumerateFiles(location, "*.slnx")
                }
                |> Seq.sort
                |> Seq.toList
            match fsprojs, solutions with
            | [ single ], _ ->
                SingleProject (Path.GetFullPath single)
            | (_ :: _ :: _), (sln :: _) ->
                Solution (Path.GetFullPath sln)
            | (_ :: _ :: _), [] ->
                Ambiguous
            | [], (sln :: _) ->
                Solution (Path.GetFullPath sln)
            | [], [] ->
                // A wsconfig.json marks a WebSharper project root: do not
                // search above it.
                if File.Exists(Path.Combine(location, "wsconfig.json")) then
                    NotFound
                else
                    let parent = Directory.GetParent location
                    // parent of root will be null
                    if isNull parent then NotFound
                    else find parent.FullName
        find "."

/// Attempts the WebSharper build-service fast path for a single project,
/// falling back to `dotnet build` when the service can't serve it. Switches the
/// current directory to the project and restores it afterwards.
let private tryBuildProjectService indent (f: Process -> unit) (verbose: bool) (projectFile: string) : int =
    let projectFile = Path.GetFullPath projectFile
    let projectDir = Path.GetDirectoryName projectFile
    let previousDir = Environment.CurrentDirectory
    let dotnetBuild () =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.Arguments <- sprintf "build \"%s\"" projectFile
        let dotnetProc = Process.Start(startInfo)
        f dotnetProc
        dotnetProc.WaitForExit()
        dotnetProc.ExitCode
    try
        Environment.CurrentDirectory <- projectDir
        // --time prints the build service's per-stage output as info; otherwise only the total, as normal text.
        let printOut (n: string) =
            if verbose then
                PrintHelpers.info indent "%s" n
            elif n.Contains "Total compilation time" then
                PrintHelpers.text indent "%s" n
        buildThroughBooster indent printOut projectFile dotnetBuild
    finally
        Environment.CurrentDirectory <- previousDir

let build indent (f: Process -> unit) (buildParams: ParseResults<BuildArguments>) =
    let projectArgument = buildParams.TryGetResult Project
    let timeRequested = buildParams.Contains Time
    try
        match resolveBuildTarget projectArgument with
        | NotFound ->
            PrintHelpers.error indent "Couldn't find a project or solution file in the current directory."
            1
        | Ambiguous ->
            PrintHelpers.error indent "Found more than one project file in the current directory. Specify one with \"--project\"."
            1
        | Solution solutionFile ->
            Environment.CurrentDirectory <- Path.GetDirectoryName solutionFile
            PrintHelpers.text indent "Building solution %s with \"dotnet build\"." (Path.GetFileName solutionFile)
            BuildInfo.captureBuild f solutionFile
        | SingleProject projectFile ->
            match BuildInfo.tryClosure projectFile with
            // Fresh manifests: fast-path every project in the closure, dependencies first.
            | Some infos ->
                BuildInfo.topoSort infos
                |> List.fold (fun code info -> max code (tryBuildProjectService indent f timeRequested info.ProjectPath)) 0
            // Cold or structurally changed: run an ordinary build (with the capture
            // target injected) to (re)generate the manifests.
            | None ->
                BuildInfo.captureBuild f projectFile
    with
    | :? UnauthorizedAccessException | :? Security.SecurityException -> 
        PrintHelpers.error indent "Couldn't search for a project file (Unauthorized)"
        1
    | :? System.ArgumentException ->
        PrintHelpers.error indent "Couldn't search for a single project file in the current directory."
        1
    | x ->
        PrintHelpers.error indent "Error while building: %s." x.Message
        1

/// dotnet ws start: the booster of the WebSharper.FSharp.Booster tool if the build would use it,
/// else the portable booster of the WebSharper.FSharp package (WebSharper 11+), else the
/// per-platform booster of a WebSharper 10 and earlier package.
let start (startParams: ParseResults<StartArguments>) =
    let nugetRoot = Boosters.defaultNuGetRoot ()
    let fsharpFolder = Path.Combine(nugetRoot, "websharper.fsharp")
    let version =
        match startParams.TryGetResult StartArguments.Version with
        | Some v -> Some v
        | None ->
            match Boosters.tryProjectPackageVersion "websharper.fsharp" Environment.CurrentDirectory with
            | Some v -> Some v
            | None ->
                try
                    Directory.EnumerateDirectories(fsharpFolder)
                    |> Seq.map (fun x -> SemVer.parse (DirectoryInfo(x).Name))
                    |> Seq.max
                    |> (fun x -> x.ToString())
                    |> Some
                with _ -> None
    match version with
    | None ->
        PrintHelpers.error 0 "Couldn't find a WebSharper.FSharp package in %s. Specify the version with --version." nugetRoot
        1
    | Some version ->
    let toolFolder =
        match Boosters.locateTool "WebSharper.FSharp.Booster" "wsfsc.dll" version Environment.CurrentDirectory None with
        | Boosters.ManifestTool (_, entry) | Boosters.GlobalTool entry -> Some (Path.GetDirectoryName entry)
        | _ -> None
    match toolFolder |> Option.orElse (Boosters.tryPortableFolder nugetRoot version) with
    | Some folder ->
        match Boosters.registered () |> List.tryFind (fun b -> Boosters.sameFolder b.Folder folder) with
        | Some b ->
            PrintHelpers.text 0 "Already running: %s" (CompilerCommand.formatBooster b)
            0
        | None ->
            try
                match Boosters.startBooster folder 10000 with
                | Some b ->
                    PrintHelpers.text 0 "Started: %s" (CompilerCommand.formatBooster b)
                    0
                | None ->
                    PrintHelpers.error 0 "The booster in %s didn't start within 10 seconds." folder
                    1
            with ex ->
                PrintHelpers.error 0 "Couldn't start the booster in %s (%s)" folder ex.Message
                1
    | None ->
        // WebSharper 10 and earlier: tools/<tfm>/<rid>/wsfscservice_start.cmd|sh
        let rid =
            match startParams.TryGetResult RID with
            | Some rid -> rid.ToString()
            | None -> Boosters.currentRid ()
        match Boosters.tryLegacyStartScript nugetRoot version rid with
        | None ->
            PrintHelpers.error 0 "Couldn't find the booster of WebSharper.FSharp %s in %s." version nugetRoot
            1
        | Some script ->
            let startInfo = ProcessStartInfo(script)
            startInfo.CreateNoWindow <- true
            startInfo.UseShellExecute <- false
            startInfo.WindowStyle <- ProcessWindowStyle.Hidden
            startInfo.Arguments <- "./websharper.log"
            try
                Process.Start(startInfo) |> ignore
                PrintHelpers.text 0 "version: %s; path: %s Started." version script
                0
            with ex ->
                PrintHelpers.error 0 "Couldn't start wsfscservice_start in %s (%s)" script ex.Message
                1

/// dotnet ws stop: an exit message (or kill with --force) to every running booster, or the ones of a version.
let stop (stopParams: ParseResults<StopArguments>) =
    let versionArg = stopParams.TryGetResult StopArguments.Version
    try
        let boosters =
            Boosters.running ()
            |> List.filter (fun b ->
                match versionArg with
                | Some v -> Boosters.matchesVersion v b || b.Version = Some v
                | None -> true)
        let results =
            boosters
            |> List.map (fun b ->
                try
                    if stopParams.Contains Force then
                        Boosters.kill b
                        PrintHelpers.text 0 "Stopped %s" (CompilerCommand.formatBooster b)
                    else
                        let exited = Boosters.sendExit 5000 b
                        PrintHelpers.text 0 "Stop signal sent to %s%s" (CompilerCommand.formatBooster b) (if exited then "" else " (still running after 5 seconds)")
                    0
                with ex ->
                    PrintHelpers.error 0 "Couldn't stop the booster with PID=%i: %s" b.Pid ex.Message
                    1)
        let returnCode =
            match versionArg, boosters with
            | Some v, [] ->
                PrintHelpers.text 0 "Could not find running service with version: %s" v
                1
            | None, [] ->
                PrintHelpers.text 0 "There are no boosters running."
                0
            | _ -> List.max results
        if returnCode = 0 && versionArg.IsNone && stopParams.Contains StopArguments.``Clear-Cache`` then
            FileCompile.clearCacheFolder ()
        returnCode
    with ex ->
        PrintHelpers.error 0 "Couldn't read processes: %s" ex.Message
        1

// Extracts out all <Compile Include../> items from the project file
let getFilesToWatch (path: string) =
    use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    use sr = new StreamReader(fs)
    let arr = ResizeArray()
    while sr.Peek() >= 0 do
        arr.Add <| sr.ReadLine()
    arr
    |> Array.ofSeq
    |> Array.choose (fun x ->
        if x.Contains "<Compile" && x.Contains "Include" then
            let pattern = "<Compile Include=\"([^\"]*)\" />"
            Regex(pattern, RegexOptions.Singleline).Match(x)
            |> fun m ->
                if m.Success && m.Groups.Count >= 2 then Some (m.Groups[1].Value.Trim().Trim([|'\"'|])) else None
        elif x.Contains "<None" && x.Contains "Include" then
            let pattern = "<None Include=\"([^\"]*)\" />"
            Regex(pattern, RegexOptions.Singleline).Match(x)
            |> fun m ->
                if m.Success && m.Groups.Count >= 2 then Some (m.Groups[1].Value.Trim().Trim([|'\"'|])) else None
        elif x.Contains "<Content" && x.Contains "Include" then
            let pattern = "<Content Include=\"([^\"]*)\" />"
            Regex(pattern, RegexOptions.Singleline).Match(x)
            |> fun m ->
                if m.Success && m.Groups.Count >= 2 then Some (m.Groups[1].Value.Trim().Trim([|'\"'|])) else None
        else
            None
    )

type WatchHandler(initialState: (string * string []) list) =
    let d =
        let d = Collections.Generic.Dictionary()
        initialState
        |> List.iter (fun (x, _) -> d.Add(x, None))
        d

    let updateDict proj proc = d.[proj] <- Some proc

    let killProject proj =
        match d.TryGetValue proj with
        | true, Some (proc: Process) ->
            PrintHelpers.text 2 "Cancelling build for %s" proj
            proc.Kill()
        | _, _ -> ()

    let processMessage (action: unit -> unit) =
        async {
            do! Async.Sleep 100 // Wait 100 ms before starting
            action()
            return ()
        }

    let mb : MailboxProcessor<unit -> unit> =
        MailboxProcessor.Start(fun m ->
            async {        
                let mutable tokensToCancel = []
                while true do
                    let tokenSource = new System.Threading.CancellationTokenSource()
                    let token = tokenSource.Token
                    let! action = m.Receive()
                    match tokensToCancel with
                    | [] -> ()
                    | xs ->
                        xs |> List.iter (fun (x: System.Threading.CancellationTokenSource) -> x.Cancel())
                        tokensToCancel <- []
                    tokensToCancel <- tokenSource :: tokensToCancel
                    Async.Start (processMessage action, token)
                return ()
            }
        )

    let mutable filesToWatch : (string * string []) list = initialState


    member this.MailBox = mb
    member this.ProjectFileWithIncludes = filesToWatch
    member this.ReloadProjectFile() =
        match filesToWatch with
        | [] -> ()
        | projects ->
            projects
            |> List.map (fun (proj, _) ->
                let fI = System.IO.FileInfo proj
                proj, getFilesToWatch proj |> Array.map (fun x -> Path.Combine(fI.Directory.FullName, x) |> Path.GetFullPath)
            )
            |> fun x -> filesToWatch <- x

    member this.Handler (_: obj) (b: FileSystemEventArgs) =
        
        let path = System.IO.Path.GetFullPath(b.FullPath)
        if foldersToExclude |> List.exists (fun x -> path.Contains x)  then
            // Skip execution here
            ()
        else
            // initiate dotnet ws build or dotnet ws compile based on what changed
            if b.FullPath.EndsWith "fsx" && filesToWatch = [] then
                mb.Post
                <| fun _ ->
                    PrintHelpers.text 0 "Compilation triggered by: %s" b.FullPath
                    let _ = FileCompile.Compile 2 ([CompileArguments.FilePath b.FullPath] |> toParseResults)
                    ()
            // otherwise run standard dotnet compilation
            else
                match filesToWatch with
                | [] ->
                    ()
                | projects ->
                    List.iter (fun (project: string, files : string []) ->
                        if b.FullPath.EndsWith "fsproj" && b.FullPath.ToLower() = project.ToLower() then
                            this.ReloadProjectFile()
                            mb.Post
                            <| fun _ ->
                                PrintHelpers.text 0 "Compilation triggered by: %s" b.FullPath
                                killProject project
                                let _ = build 2 (updateDict project) ([BuildArguments.Project b.FullPath] |> toParseResults)
                                ()
                            
                        else
                            if files |> Array.map (fun x -> x.ToLower()) |> Array.contains (b.FullPath.ToLower()) then
                                mb.Post
                                <| fun _ ->
                                    PrintHelpers.text 0 "Compilation triggered by: %s" b.FullPath
                                    killProject project
                                    let _ = build 2 (updateDict project) ([BuildArguments.Project project] |> toParseResults)
                                    ()
                            else
                                // Skip execution here
                                ()
                    ) projects

    member this.Initialize() =
        // Initialize multiple file watchers per project file
        let fw = new System.IO.FileSystemWatcher(System.Environment.CurrentDirectory)
        fw.IncludeSubdirectories <- true
        fw.EnableRaisingEvents <- true
        fw.Changed.AddHandler <| this.Handler
        fw.Created.AddHandler <| this.Handler
        fw.Deleted.AddHandler <| this.Handler
        fw.Renamed.AddHandler <| this.Handler

// Returns all fsproject files from the current folder matching the specified (optional) pattern
let checkCurrentFolderWithPattern (pattern: string option) =
    let currentDir = Environment.CurrentDirectory
    let opt = System.IO.EnumerationOptions(RecurseSubdirectories = true)
    let files =
        match pattern with
        | None ->
            System.IO.Directory.EnumerateFiles(currentDir, "*.fsproj", opt)
        | Some pattern ->
            System.IO.Directory.EnumerateFiles(currentDir, pattern, opt)
    if files |> Seq.isEmpty then
        true, []
    else
        false, files
        |> List.ofSeq
        |> List.choose (fun x ->
            if x.EndsWith "fsproj" then
                let fI = System.IO.FileInfo x
                Some (x, getFilesToWatch x |> Array.map (fun x -> Path.Combine(fI.Directory.FullName, x) |> Path.GetFullPath))
            else
                None
        )



let private usageText =
    """USAGE: dotnet ws <subcommand> [<options>]

SUBCOMMANDS:

    build [--project <PROJ>] [--time]
        Build the WebSharper project (or solution) in the current or nearest
        parent folder.
          -p, --project <PROJ>   Project or solution (.sln/.slnx) file to build.
          -t, --time             Print per-stage compilation timings.

    compile [options] <FILE>
        Compile an .fs/.fsx file to JavaScript or TypeScript using the Booster.
          -v, --version <VER>    WebSharper version to compile against.
          -o, --output <FILE>    Output file (implies BundleOnly mode).
          -p, --path <PATH>      Output location (library mode).
          -s, --standalone       Standalone compilation.
          -ts, --typescript      Emit TypeScript.
          -d, --debug            Debug compilation.
          -w, --watch            Watch the file and recompile on change.
              --force            Clear the temporary folder before compiling.

    watch [--pattern <GLOB>]
        Watch the current folder and rebuild or compile changed projects/files.
          -p, --pattern <GLOB>   Only watch files matching this glob.

    start [--version <VER>] [--rid <RID>]
        Start the Booster service (wsfscservice): the one of the
        WebSharper.FSharp.Booster tool if the build would use it, otherwise the
        one of the WebSharper.FSharp package.
          -v, --version <VER>    WebSharper version (default: the project's in the
                                 current folder, else the latest cached).
          -r, --rid <RID>        WebSharper 10 and earlier only: the platform
                                 of the booster in the package (default: current).

    stop [--version <VER>] [--force] [--clear-cache]
        Send a stop signal to running Booster service(s).
          -v, --version <VER>    Only stop services with this version.
          -f, --force            Kill the process(es) instead of signaling.
              --clear-cache      Also clear the temporary ws-compile folder.

    list
        List running Booster versions and their source paths.

    stats [--version <VER>] [--json]
        Show the statistics of the boosters (WebSharper 11+): how long each
        message, compilation stage and agent tool takes, and what agents ask.
        From running boosters, else from what stopped ones saved. They stay
        on this machine.
          -v, --version <VER>    Only the boosters of this version.
              --json             Print them as JSON.

    compiler status [--project <PROJ>]
        Show which WebSharper compiler each project builds with (the Booster
        tool or the portable compiler), and the running boosters.
        WebSharper 11+.
          -p, --project <PROJ>   Project or solution (.sln/.slnx) file.

    compiler sync [--project <PROJ>] [--local | --global] [--source <SRC>]
        Install or update the WebSharper.FSharp.Booster / WebSharper.CSharp.Booster
        tools to the WebSharper version the projects reference, and stop
        boosters the next build no longer uses. Updates a tool manifest that
        lists them; otherwise needs --local or --global. Needs .NET SDK 10+.
        WebSharper 11+.
          -p, --project <PROJ>   Project or solution (.sln/.slnx) file.
              --local            Add the tools to the nearest tool manifest, or
                                 create one in the repository root.
              --global           Install the tools globally.
          -s, --source <SRC>     An additional NuGet package source.

    info
        Print the version of the dotnet ws tool.

EXAMPLES:

    dotnet ws build
    dotnet ws build --project ./App/App.fsproj
    dotnet ws build --time
    dotnet ws compile -v 10.1.8.682 Client.fs
    dotnet ws watch --pattern '*.fs'
    dotnet ws start
    dotnet ws start -v 10.1.8.682 -r linux-x64
    dotnet ws stop --force
    dotnet ws list
    dotnet ws compiler status
    dotnet ws compiler sync --local"""

[<EntryPoint>]
let main argv =
    let v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    let helpMessage =
        sprintf "WebSharper .NET CLI version %d.%d.%d" v.Major v.Minor v.Build
    PrintHelpers.text 0 "%s" helpMessage
    let parser = ArgumentParser.Create<Argument>(programName = "dotnet ws", checkStructure = true)
    try
        let result = parser.Parse argv
        let subCommand = result.GetSubCommand()

        match subCommand with
        | Info ->
            PrintHelpers.text 0 "%d.%d.%d" v.Major v.Minor v.Build
            0
        | Watch watchParams ->
            let globPattern = watchParams.TryGetResult Pattern
            let (noFilesFoundWithPattern, projectFilesWithIncludes) = checkCurrentFolderWithPattern globPattern
            if noFilesFoundWithPattern then
                PrintHelpers.error 0 "There are no files found in the current directory with the specified pattern."
                1
            else
                match projectFilesWithIncludes with
                | [] ->
                    let watch = WatchHandler([])
                    watch.Initialize()
                    while true do
                        ()
                    0
                | projects ->
                    projects
                    |> List.iter (fun (proj, _) -> PrintHelpers.text 0 "Watching %s" proj)
                    let watch = WatchHandler(projects)
                    watch.Initialize()
                    while true do
                        ()
                    0
        | Start startParams ->
            start startParams
        | Stop stopParams ->
            stop stopParams
        | Stats statsParams ->
            StatsCommand.run (statsParams.TryGetResult StatsArguments.Version) (statsParams.Contains StatsArguments.Json)
        | List ->
            try
                CompilerCommand.printRunning (Boosters.running ())
                0
            with
            | _ ->
                PrintHelpers.error 0 "Couldn't read processes."
                1
        | Compiler compilerParams ->
            match compilerParams.GetSubCommand() with
            | CompilerCommand.Status statusParams ->
                match CompilerCommand.resolveProjects (statusParams.TryGetResult CompilerCommand.StatusArguments.Project) with
                | Result.Error msg ->
                    PrintHelpers.error 0 "%s" msg
                    1
                | Ok (projects, _) ->
                    CompilerCommand.status projects
            | CompilerCommand.Sync syncParams ->
                let mode =
                    match syncParams.Contains CompilerCommand.SyncArguments.Local, syncParams.Contains CompilerCommand.SyncArguments.Global with
                    | true, true -> None
                    | true, false -> Some CompilerCommand.LocalMode
                    | false, true -> Some CompilerCommand.GlobalMode
                    | false, false -> Some CompilerCommand.DefaultMode
                match mode with
                | None ->
                    PrintHelpers.error 0 "Use either --local or --global."
                    1
                | Some mode ->
                    match CompilerCommand.resolveProjects (syncParams.TryGetResult CompilerCommand.SyncArguments.Project) with
                    | Result.Error msg ->
                        PrintHelpers.error 0 "%s" msg
                        1
                    | Ok (projects, rootDir) ->
                        CompilerCommand.sync projects rootDir mode (syncParams.TryGetResult CompilerCommand.SyncArguments.Source)
        | Build buildParams ->
            build 0 (ignore) buildParams
        | Compile arguments -> FileCompile.CompileFile arguments
    with
    | :? Argu.ArguParseException as ex -> 
        if argv.Length = 0 || (argv |> Array.exists (fun a -> a = "--help" || a = "-h")) then
            PrintHelpers.text 0 "%s" usageText
            0
        else
            printfn "%s." ex.Message
            1
