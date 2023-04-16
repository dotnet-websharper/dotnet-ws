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
open System.Diagnostics
open Argu
open Fake.Core
open System.IO.Pipes
open System.IO
open System.Runtime.Serialization.Formatters.Binary
open System.Text.RegularExpressions
open WebSharper.Compiler.WsFscServiceCommon
open System.Threading

type ErrorCode =
    | ProjectNotCached          = -33212
    | ProjectOutdated           = -11234
    | UnexpectedFinish          = -12211
    | ProjectTypeNotPermitted   = -21233

type OutputErrorCode =
    | UnrecognizedMessage       = -13311

let (|Error|_|) code =
    match code with
    | Some i when i = int ErrorCode.ProjectNotCached ->
        Some ErrorCode.ProjectNotCached
    | Some i when i = int ErrorCode.ProjectOutdated ->
        Some ErrorCode.ProjectOutdated
    | Some i when i = int ErrorCode.ProjectTypeNotPermitted ->
        Some ErrorCode.ProjectTypeNotPermitted
    | _ ->
        None

let (|StdOut|_|) (str: string) =
    if str.StartsWith("n: ") then
        str.Substring(3) |> Some
    else
        None

let (|StdErr|_|) (str: string) =
    if str.StartsWith("e: ") then
        str.Substring(3) |> Some
    else
        None

let (|Finish|_|) (str: string) =
    if str.StartsWith("x: ") then
        let trimmed = 
            str.Substring(3)
        trimmed
        |> System.Int32.Parse
        |> Some
    else
        None

type RidEnum =
    | ``win-x64`` = 1
    | ``linux-x64`` = 2
    | ``linux-musl-x64`` = 3

type Argument =
    | [<CliPrefix(CliPrefix.None)>] Build of ParseResults<BuildArguments>
    | [<CliPrefix(CliPrefix.None)>] Start of ParseResults<StartArguments>
    | [<CliPrefix(CliPrefix.None)>] Stop of ParseResults<StopArguments>
    | [<CliPrefix(CliPrefix.None); SubCommand>] List

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Start _ -> "Start the Booster service (wsfscservice) with the given RID and version. If no value is given for version, the latest (as found in the local NuGet cache) will be used."
            | Stop _ -> "Send a stop signal to the Booster service with the given version. If no version is given all running instances are signaled. Use `--force` to kill process(es) instead of sending a stop signal."
            | List -> "List running Booster versions and their source paths."
            | Build _ -> "Build the WebSharper project in the current folder (or in the nearest parent folder). You can optionally specify a project file using `--project`."
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

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Version _ -> "Specify the version of the Booster service."
            | Force -> "Force the specified operation (used for the stop subcommand)."

and BuildArguments =
    | [<AltCommandLine("-p")>] Project of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Project _ -> "Specify the project file to build."

let dotnetBinaryFolder =
    [
        "net7.0"
        "net6.0"
        "net5.0"
    ]

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Argument>(programName = "dotnet-ws.exe", helpTextMessage = "dotnet-ws is a dotnet tool for WebSharper.", checkStructure = true)
    try
        let result = parser.Parse argv
        let subCommand = result.GetSubCommand()

        let clientPipeForLocation (location:string) =
            let location = IO.Path.GetDirectoryName(location)
            let pipeName = (location, "WsFscServicePipe") |> Path.Combine |> hashPath
            let serverName = "." // local machine server name
            new NamedPipeClientStream( serverName, //server name, local machine is .
                                 pipeName, // name of the pipe,
                                 PipeDirection.InOut, // direction of the pipe 
                                 PipeOptions.WriteThrough // the operation will not return the control until the write is completed
                                 ||| PipeOptions.Asynchronous)

        let sendOneMessage (clientPipe: NamedPipeClientStream) (message: ArgsType) =
            let bf = BinaryFormatter();
            use ms = new MemoryStream()
            // args going binary serialized to the service.
            bf.Serialize(ms, message);
            ms.Flush();
            ms.Position <- 0L
            clientPipe.Connect()
            let tryCompileTimeOutSeconds = 5.0
            use cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(tryCompileTimeOutSeconds))
            ms.CopyToAsync(clientPipe, cancellationTokenSource.Token) |> Async.AwaitTask |> Async.RunSynchronously
            clientPipe.Flush()

        match subCommand with
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
                            printfn "version: %s; path: %s Started." version cmdFullPath
                            0
                        with ex ->
                            eprintfn "Couldn't start wsfscservice_start in %s (%s)" cmdFullPath ex.Message
                            1
                with
                | :? Argu.ArguParseException ->
                    reraise()
                | _ ->
                    eprintfn "Couldn't find wsfscservice_start."
                    1
            with
            | :? Argu.ArguParseException ->
                reraise()
            | _ ->
                eprintfn "Couldn't find nuget packages root folder."
                1
        | Stop stopParams -> 
            let tryKill (returnCode, successfulErrorPrint) (proc: Process) =
                try
                    let killOrSend (x: Process) = 
                        if stopParams.Contains Force then
                            x.Kill()
                            printfn "Stopped wsfscservice with PID: (%i)" x.Id
                        else
                            let clientPipe = clientPipeForLocation x.MainModule.FileName
                            sendOneMessage clientPipe {args = [|"exit"|]}
                            printfn "Stop signal sent to wsfscservice with PID: (%i)" x.Id
                    match stopParams.TryGetResult Version with
                    | Some v -> 
                        if proc.MainModule.FileVersionInfo.FileVersion = v then
                            killOrSend proc
                        else
                            printfn "Could not find running service with version: %s" v
                    | None ->
                        killOrSend proc
                    (returnCode, successfulErrorPrint)
                with
                | _ ->
                    try
                        eprintfn "Couldn't kill process with PID: (%i)" proc.Id
                        (1, successfulErrorPrint)
                    with
                    | _ -> (1, false)
            try
                let (returnCode, successfulErrorPrint) = Process.GetProcessesByName("wsfscservice") |> Array.fold tryKill (0, true)
                if successfulErrorPrint |> not then
                    eprintfn "Couldn't read processes."
                returnCode
            with
            | _ -> 1
        | List -> 
            try
                let procInfos = 
                    Process.GetProcessesByName("wsfscservice")
                    |> Array.map (fun proc -> sprintf "version: %s; path: %s" proc.MainModule.FileVersionInfo.FileVersion proc.MainModule.FileName)
                if procInfos.Length = 0 then
                    printfn "There are no wsfscservices running."
                else
                    printfn """The following wsfscservice versions are running:

  %s.""" 
                        (String.Join(sprintf "%s  " Environment.NewLine, procInfos))
                0
            with
            | _ ->
                eprintfn "Couldn't read processes."
                1
        | Build buildParams ->
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
                                            printfn "%s" n
                                            return None
                                        | StdErr e ->
                                            eprintfn "%s" e
                                            return None
                                        | Finish i -> 
                                            return i |> Some
                                        | x ->
                                            eprintfn "Unrecognizable message from server (%i): %s." (int OutputErrorCode.UnrecognizedMessage) x
                                            return Some (int OutputErrorCode.UnrecognizedMessage)
                                    }
                                let! errorCode = readingMessages clientPipe printResponse
                                match errorCode with
                                | Some x -> return x
                                | None -> 
                                    eprintfn "Listening for server finished abruptly (%i)" (int ErrorCode.UnexpectedFinish)
                                    return (int ErrorCode.UnexpectedFinish)
                                }
                            try
                                Async.RunSynchronously(write)
                            with
                            | _ -> 
                                eprintfn "Listening for server finished abruptly (%i)" (int ErrorCode.UnexpectedFinish)
                                (int ErrorCode.UnexpectedFinish)
                        with
                        | x -> 
                            eprintfn "Couldn't send compile message to the build server: %s." x.Message
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
                                eprintfn "Build service didn't cache the result of the build. Fallback to \"dotnet build\"."
                                dotnetBuild()
                            // the type of the project is not ws buildable (WIG or Proxy)
                            | Error ErrorCode.ProjectTypeNotPermitted -> 
                                eprintfn "dotnet ws build can't use caching for this project (WIG or Proxy). Fallback to \"dotnet build\"."
                                dotnetBuild()
                            // cache of the project is outdated
                            | Error ErrorCode.ProjectOutdated ->
                                eprintfn "Project's dependencies changed since last build. Fallback to \"dotnet build\"."
                                dotnetBuild()
                            | Some errorCode -> errorCode
                            | None ->
                                eprintfn "No running wsfscservice found with the version %s. Fallback to \"dotnet build\"." version
                                dotnetBuild()
                        else
                            eprintfn "No WebSharper.FSharp package found in the downloaded nuget packages. Fallback to \"dotnet build\"."
                            dotnetBuild()
                    with
                    | :? UnauthorizedAccessException | :? Security.SecurityException -> 
                        eprintfn "Couldn't read obj/project.nuget.cache (Unauthorized). Fallback to \"dotnet build\"."
                        dotnetBuild()
                    | :? FileNotFoundException | :? DirectoryNotFoundException ->
                        eprintfn """Cache file for downloaded nuget packages is not found. Try the following:
                        
- Add WebSharper.FSharp nuget to the current project (for example "> dotnet add package WebSharper.FSharp")
- Build prior running "dotnet ws build". Build will run in a moment.
"""
                        dotnetBuild()
                    | :? IOException ->
                        eprintfn "Couldn't read obj/project.nuget.cache. Fallback to \"dotnet build\"."
                        dotnetBuild()
                    | x ->
                        eprintfn """Couldn't build project because: %s

Fallback to "dotnet build".
"""                         x.Message
                        dotnetBuild()
                | None ->
                    eprintfn "Couldn't find a project file in the current directory."
                    1
            with
            | :? UnauthorizedAccessException | :? Security.SecurityException -> 
                eprintfn "Couldn't search for a project file (Unauthorized)"
                1
            | :? System.ArgumentException ->
                eprintfn "Couldn't search for a single project file in the current directory."
                1
            | x ->
                eprintfn "Error while building: %s." x.Message
                1
    with
    | :? Argu.ArguParseException as ex -> 
        eprintfn """%s

%s."""       parser.HelpTextMessage.Value ex.Message
        1