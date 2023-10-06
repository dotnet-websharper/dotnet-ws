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

type RidEnum =
    | ``win-x64`` = 1
    | ``linux-x64`` = 2
    | ``linux-musl-x64`` = 3

type Argument =
    | [<CliPrefix(CliPrefix.None)>] Build of ParseResults<BuildArguments>
    | [<CliPrefix(CliPrefix.None)>] Start of ParseResults<StartArguments>
    | [<CliPrefix(CliPrefix.None)>] Stop of ParseResults<StopArguments>
    | [<CliPrefix(CliPrefix.None); SubCommand>] Info
    | [<CliPrefix(CliPrefix.None); SubCommand>] List
    | [<CliPrefix(CliPrefix.None)>] Compile of ParseResults<CompileArguments>
    | [<CliPrefix(CliPrefix.None)>] Watch of ParseResults<WatchArguments>

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Start _ -> "Start the Booster service (wsfscservice) with the given RID and version. If no value is given for version, the latest (as found in the local NuGet cache) will be used."
            | Stop _ -> "Send a stop signal to the Booster service with the given version. If no version is given all running instances are signaled. Use `--force` to kill process(es) instead of sending a stop signal."
            | List -> "List running Booster versions and their source paths."
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
    | [<Mandatory; AltCommandLine("-r")>] RID of RidEnum
    | [<AltCommandLine("-v")>] Version of semver:string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | RID _ -> "Specify the Runtime Identifier (win-x64|linux-x64|linux-musl-x64) to use."
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

and BuildArguments =
    | [<AltCommandLine("-p")>] Project of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Project _ -> "Specify the project file to build."

let build indent (f: Process -> unit) (buildParams: ParseResults<BuildArguments>) =
    let projectArgument = buildParams.TryGetResult Project
    try
        let projectFile = 
            match projectArgument with
            | Some project ->
                if File.Exists project |> not then
                    // will become "Couldn't find the project file."
                    failwith "dummy"
                project |> Some
            | None -> 
                let rec findFsproj location =
                    let fsprojsInLocation =
                        Directory.EnumerateFiles(location, "*.fsproj")
                    match Seq.tryHead fsprojsInLocation with
                    | Some _ ->
                        Seq.exactlyOne fsprojsInLocation
                        |> Path.GetFullPath
                        |> Some
                    | None -> 
                        let parent = Directory.GetParent location
                        // parent of root will be null
                        if isNull parent then
                            None
                        else
                            findFsproj parent.FullName
                findFsproj "."
                        
        match projectFile with
        | Some projectFile ->
            Environment.CurrentDirectory <- Path.GetDirectoryName projectFile
            let sendCompile (proc: Process) =
                try
                    let clientPipe = clientPipeForLocation proc.MainModule.FileName
                    sendOneMessage clientPipe {args = [| sprintf "compile:%s" projectFile |]}
                    let write = async {
                        let printResponse (message: obj) = 
                            async {
                                // messages on the service have n: e: or x: prefix for stdout stderr or error code kind of output
                                match message :?> string with
                                | StdOut n ->
                                    PrintHelpers.info indent "%s" n
                                    return None
                                | StdErr e ->
                                    PrintHelpers.error indent "%s" e
                                    return None
                                | Finish i -> 
                                    return i |> Some
                                | x ->
                                    PrintHelpers.error indent "Unrecognizable message from server (%i): %s." (int OutputErrorCode.UnrecognizedMessage) x
                                    return Some (int OutputErrorCode.UnrecognizedMessage)
                            }
                        let! errorCode = readingMessages clientPipe printResponse
                        match errorCode with
                        | Some x -> return x
                        | None -> 
                            PrintHelpers.error indent "Listening for server finished abruptly (%i)" (int ErrorCode.UnexpectedFinish)
                            return (int ErrorCode.UnexpectedFinish)
                        }
                    try
                        Async.RunSynchronously(write)
                    with
                    | _ -> 
                        PrintHelpers.error indent "Listening for server finished abruptly (%i)" (int ErrorCode.UnexpectedFinish)
                        (int ErrorCode.UnexpectedFinish)
                with
                | x -> 
                    PrintHelpers.error indent "Couldn't send compile message to the build server: %s." x.Message
                    (int ErrorCode.UnexpectedFinish)
            let tryCheckVersion (proc: Process) (version: string) =
                if version.StartsWith proc.MainModule.FileVersionInfo.FileVersion then
                    sendCompile proc |> Some
                else
                    None
            let dotnetBuild() =
                let startInfo = ProcessStartInfo("dotnet")
                let arguments =
                    match projectArgument with
                    | Some project ->
                        sprintf "build %s" project
                    | None ->
                        "build"
                startInfo.Arguments <- arguments
                let dotnetProc = Process.Start(startInfo)
                f dotnetProc
                dotnetProc.WaitForExit()
                dotnetProc.ExitCode
            try
                let nugetCachePath = Path.Combine("obj", "project.nuget.cache")
                let nugetCache = File.ReadAllText(nugetCachePath)
                let regex = new Regex("websharper\.fsharp\.(.+)\.nupkg\.sha512")
                let m = regex.Match(nugetCache)
                if m.Success then
                    let version = m.Groups.[1].Value
                    match Process.GetProcessesByName("wsfscservice") |> Array.tryPick (fun x -> tryCheckVersion x version) with
                    // wsfscservice process reported back, it doesn't have the project cached
                    | Error ErrorCode.ProjectNotCached -> 
                        PrintHelpers.info indent "Build service didn't cache the result of the build. Fallback to \"dotnet build\"."
                        dotnetBuild()
                    // the type of the project is not ws buildable (WIG or Proxy)
                    | Error ErrorCode.ProjectTypeNotPermitted -> 
                        PrintHelpers.info indent "dotnet ws build can't use caching for this project (WIG or Proxy). Fallback to \"dotnet build\"."
                        dotnetBuild()
                    // cache of the project is outdated
                    | Error ErrorCode.ProjectOutdated ->
                        PrintHelpers.info indent "Project's dependencies changed since last build. Fallback to \"dotnet build\"."
                        dotnetBuild()
                    | Some errorCode -> errorCode
                    | None ->
                        PrintHelpers.info indent "No running wsfscservice found with the version %s. Fallback to \"dotnet build\"." version
                        dotnetBuild()
                else
                    PrintHelpers.info indent "No WebSharper.FSharp package found in the downloaded nuget packages. Fallback to \"dotnet build\"."
                    dotnetBuild()
            with
            | :? UnauthorizedAccessException | :? Security.SecurityException -> 
                PrintHelpers.info indent "Couldn't read obj/project.nuget.cache (Unauthorized). Fallback to \"dotnet build\"."
                dotnetBuild()
            | :? FileNotFoundException | :? DirectoryNotFoundException ->
                PrintHelpers.info indent """Cache file for downloaded nuget packages is not found. Starting an msbuild now."""
                dotnetBuild()
            | :? IOException ->
                PrintHelpers.info indent "Couldn't read obj/project.nuget.cache. Fallback to \"dotnet build\"."
                dotnetBuild()
            | x ->
                PrintHelpers.info indent """Couldn't build project because: %s

Fallback to "dotnet build".
"""                         x.Message
                dotnetBuild()
        | None ->
            PrintHelpers.error indent "Couldn't find a project file in the current directory."
            1
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

let dotnetBinaryFolder =
    [
        "net8.0"
        "net7.0"
        "net6.0"
        "net5.0"
    ]

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
            PrintHelpers.info 2 "Cancelling build for %s" proj
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
                    PrintHelpers.info 0 "Compilation triggered by: %s" b.FullPath
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
                                PrintHelpers.info 0 "Compilation triggered by: %s" b.FullPath
                                killProject project
                                let _ = build 2 (updateDict project) ([BuildArguments.Project b.FullPath] |> toParseResults)
                                ()
                            
                        else
                            if files |> Array.map (fun x -> x.ToLower()) |> Array.contains (b.FullPath.ToLower()) then
                                mb.Post
                                <| fun _ ->
                                    PrintHelpers.info 0 "Compilation triggered by: %s" b.FullPath
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



[<EntryPoint>]
let main argv =
    let v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    let helpMessage =
        sprintf "WebSharper .NET CLI version %d.%d.%d" v.Major v.Minor v.Build
    PrintHelpers.info 0 "%s" helpMessage
    let parser = ArgumentParser.Create<Argument>(programName = "dotnet ws", checkStructure = true)
    try
        let result = parser.Parse argv
        let subCommand = result.GetSubCommand()

        let pattern = @"(\d+\.\d+\.\d+\.\d+[\w\d-]*)"
        let regexOptions = RegexOptions.Singleline

        match subCommand with
        | Info ->
            PrintHelpers.info 0 "%d.%d.%d" v.Major v.Minor v.Build
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
                    |> List.iter (fun (proj, _) -> PrintHelpers.info 0 "Watching %s" proj)
                    let watch = WatchHandler(projects)
                    watch.Initialize()
                    while true do
                        ()
                    0
        | Start startParams ->
            let rid = startParams.GetResult RID
            let ridString = rid.ToString()
            try
                let nugetRoot = 
                    match Environment.GetEnvironmentVariable("NUGET_PACKAGES") |> Option.ofObj with
                    | Some x -> x
                    //%USERPROFILE%\.nuget\packages win
                    //~/.nuget/packages linux
                    | None -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
                let fsharpFolder = Path.Combine(nugetRoot, "websharper.fsharp")
                try
                    let version =
                        match startParams.TryGetResult StartArguments.Version with
                        | Some semver -> semver
                        | None ->
                            Directory.EnumerateDirectories(fsharpFolder)
                            |> Seq.map (fun x ->
                                let justFolder = DirectoryInfo(x).Name
                                SemVer.parse(justFolder))
                            |> Seq.max
                            |> (fun x -> x.ToString())
                    // start a detached wsfscservice.exe. Platform specific.
                    let cmdName = if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) then
                                      "wsfscservice_start.cmd" else "wsfscservice_start.sh"
                    let cmdFullPath =
                        dotnetBinaryFolder
                        |> List.tryPick (fun folder ->
                            Path.Combine(fsharpFolder, version, "tools", folder, ridString, cmdName)
                            |> fun path ->
                                if File.Exists path then
                                    Some path
                                else
                                    None
                        )

                    match cmdFullPath with
                    | None ->
                        failwith "dummy" // It will become "Couldn't find wsfscservice_start"
                    | Some cmdFullPath ->
                        let startInfo = ProcessStartInfo(cmdFullPath)
                        startInfo.CreateNoWindow <- true
                        startInfo.UseShellExecute <- false
                        startInfo.WindowStyle <- ProcessWindowStyle.Hidden
                        try
                            Process.Start(startInfo) |> ignore
                            PrintHelpers.info 0 "version: %s; path: %s Started." version cmdFullPath
                            0
                        with ex ->
                            PrintHelpers.error 0 "Couldn't start wsfscservice_start in %s (%s)" cmdFullPath ex.Message
                            1
                with
                | :? Argu.ArguParseException ->
                    reraise()
                | _ ->
                    PrintHelpers.error 0 "Couldn't find wsfscservice_start."
                    1
            with
            | :? Argu.ArguParseException ->
                reraise()
            | _ ->
                PrintHelpers.error 0 "Couldn't find nuget packages root folder."
                1
        | Stop stopParams ->
            let versionArg = stopParams.TryGetResult StopArguments.Version
            let tryKill (returnCode, successfulErrorPrint) (proc: Process) =
                try
                    let fd = proc.MainModule.FileVersionInfo.FileDescription
                    let killOrSend (x: Process) = 
                        if stopParams.Contains Force then
                            x.Kill()
                            PrintHelpers.info 0 "Stopped %s with PID=%i" fd x.Id
                            (0, true)
                        else
                            let clientPipe = clientPipeForLocation x.MainModule.FileName
                            sendOneMessage clientPipe {args = [|"exit"|]}
                            PrintHelpers.info 0 "Stop signal sent to %s with PID=%i" fd x.Id
                            (0, true)
                    match versionArg with
                    | Some v ->
                        let versionToMatch =
                            let regex = Regex.Match(fd, pattern, regexOptions)
                            if regex.Success then
                                regex.Value
                            else
                                proc.MainModule.FileVersionInfo.FileVersion
                        if versionToMatch = v then
                            killOrSend proc
                        else
                            if returnCode <> 0 then
                                (1, successfulErrorPrint)
                            else
                                (returnCode, successfulErrorPrint)
                    | None ->
                        killOrSend proc
                with
                | _ ->
                    try
                        PrintHelpers.error 0 "Couldn't kill process with PID=%i" proc.Id
                        (1, successfulErrorPrint)
                    with
                    | _ -> (1, false)
            try
                let (returnCode, successfulErrorPrint) = Process.GetProcessesByName("wsfscservice") |> Array.fold tryKill (1, true)
                match versionArg with
                | Some v ->
                    if returnCode <> 0 then
                        PrintHelpers.info 0 "Could not find running service with version: %s" v
                | None -> ()
                if successfulErrorPrint |> not then
                    PrintHelpers.error 0 "Couldn't read processes."
                if returnCode = 0 && stopParams.TryGetResult StopArguments.Version |> Option.isNone && stopParams.TryGetResult StopArguments.``Clear-Cache`` |> Option.isSome then
                    FileCompile.clearCacheFolder ()
                returnCode
            with
            | _ -> 1
        | List ->
            try
                let procInfos = 
                    Process.GetProcessesByName("wsfscservice")
                    |> Array.map (fun proc ->
                        let versionToQueryBy =
                            let regex = Regex.Match(proc.MainModule.FileVersionInfo.FileDescription, pattern, regexOptions)
                            if regex.Success then
                                regex.Value
                            else
                                proc.MainModule.FileVersionInfo.FileVersion
                        sprintf "version: %s; path: %s" versionToQueryBy proc.MainModule.FileName
                    )
                if procInfos.Length = 0 then
                    PrintHelpers.info 0 "There are no wsfscservices running."
                else
                    PrintHelpers.info 0 """The following wsfscservice versions are running:

  %s.""" 
                        (String.Join(sprintf "%s  " Environment.NewLine, procInfos))
                0
            with
            | _ ->
                PrintHelpers.error 0 "Couldn't read processes."
                1
        | Build buildParams ->
            build 0 (ignore) buildParams
        | Compile arguments -> FileCompile.CompileFile arguments
    with
    | :? Argu.ArguParseException as ex -> 
        printfn "%s." ex.Message
        1