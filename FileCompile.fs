module FileCompile

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.Serialization.Formatters.Binary
open System.Text
open System.Text.RegularExpressions
open System.Threading
open Argu
open WebSharper.Compiler.WsFscServiceCommon

module BuildHelpers =

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

    let dotnetBuild project (isDebug: bool) =
        let startInfo = ProcessStartInfo("dotnet")
        let arguments = sprintf "build %s -c %s" project (if isDebug then "Debug" else "Release")
        startInfo.Arguments <- arguments
        startInfo.RedirectStandardOutput <- not isDebug
        startInfo.RedirectStandardInput <- not isDebug
        let dotnetProc = Process.Start(startInfo)
        dotnetProc.WaitForExit()
        if not isDebug then
            dotnetProc.OutputDataReceived.Add(ignore)
        dotnetProc.ExitCode

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
    
    let buildProjectFile (projectFile: string) (isDebug: bool) =
        try                
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
            let dotnetBuild () =
                dotnetBuild projectFile isDebug
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
                        dotnetBuild () 
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

type CompileArguments =
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-v")>] Version of string
    | [<AltCommandLine("-s")>] Standalone
    | [<MainCommand>] FilePath of string
    | [<AltCommandLine("-p")>] Path of string
    | [<AltCommandLine("-ts")>] TypeScript
    | [<AltCommandLine("-d")>] Debug

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Output _ -> "Specify the name of the output file. Also forces the compilation to use BundleOnly mode."
            | Version _ -> "Specify the version of WebSharper to use."
            | Standalone -> "Standalone mode."
            | Path _ -> "Specify the output location for library mode."
            | Debug -> "Use debug mode for compilation."
            | TypeScript -> "Enable TypeScript output."
            | FilePath _ -> "Specify the path to the file to compile."

type CompileError =
    | FileNotFound of string
    | MissingFilePath

    member this.ToErrorCode =
        match this with
        | FileNotFound _ -> 1
        | MissingFilePath -> 2

    member this.PrettyPrint() =
        match this with
        | FileNotFound path -> sprintf "%s does not exist" path
        | MissingFilePath -> "You must specify a valid path"

module Files =
    let projectFile = """<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>netstandard2.0</TargetFramework>
    </PropertyGroup>
    <ItemGroup>
        <Compile Include="File%EXT" />
        <None Include="wsconfig.json" />
    </ItemGroup>
</Project>
"""

    let configFile = """{
    "$schema": "https://websharper.com/wsconfig.schema.json",
    "project": "library",
    "jsOutput": "%OUTPUT",
    "javascript": true,
    "javaScriptExport": true,
    "ts": %TS
}
"""

let private exitWithError (msg: CompileError) =
    printfn "%s" <| msg.PrettyPrint()
    msg.ToErrorCode

let parseDependencies path =
    let lines = File.ReadAllLines path
    lines
    |> Array.filter (fun line ->
        line.StartsWith("#r")
    )
    |> Array.map (fun line ->
        let trimmed = line.Replace("#r", "").Trim().Trim('\"').Replace("nuget:", "")
        let split = trimmed.Split(",")
        if split.Length > 1 then
            split.[0], Some split.[1]
        else
            split.[0], None // TODO: This should be going to nuget
    )

let salt = "ws-compile"

let getEncodedFolderName (dependencies: (string * string option) array) extension =
    dependencies
    |> Array.map (fun (package, version) ->
        match version with
        | None -> package
        | Some v -> sprintf "%s:%s" package v
    )
    |> String.concat "|"
    |> fun s -> s + "|" + extension
    |> fun deps ->
        let sha256 = System.Security.Cryptography.SHA256.Create()
        sha256.ComputeHash(Encoding.Unicode.GetBytes(deps + salt))
        |> BitConverter.ToString
        |> fun x -> x.Replace("-", "")

let dotnetAddPackage isDebug package project version =
    let startInfo = ProcessStartInfo("dotnet")
    let arguments =
        match version with
        | None ->
            sprintf "add %s package %s --prerelease" project package 
        | Some v ->
            sprintf "add %s package %s -v %s --prerelease" project package v
    startInfo.Arguments <- arguments
    startInfo.RedirectStandardOutput <- not isDebug
    startInfo.RedirectStandardInput <- not isDebug
    let dotnetProc = Process.Start(startInfo)
    dotnetProc.WaitForExit()
    if isDebug then
        dotnetProc.OutputDataReceived.Add(ignore)
    dotnetProc.ExitCode

let escapeString (str: string) =
    str.Replace(@"\", @"\\")

let projectFileSetup isDebug (path: string) (dependencies: (string * string option) array) (outputPath: string) (filePath: string) (isTS: bool) (outputFilePath: string option) =
    let extension = Path.GetExtension filePath
    let fileName =
        match outputFilePath with
        | None -> (Path.GetFileNameWithoutExtension filePath) + extension
        | Some outputFilePath -> (Path.GetFileNameWithoutExtension outputFilePath) + extension
    let fsFileName = sprintf "File%s" extension //fsFile |> Path.GetFileName
    let fsFile = Path.Combine(path, fsFileName)
    
    if not <| System.IO.Directory.Exists path then
        System.IO.Directory.CreateDirectory(path) |> ignore

    let projectFile = Path.Combine(path, "MyProject.fsproj")
    if not <| System.IO.File.Exists projectFile then
        System.IO.File.WriteAllText(projectFile, Files.projectFile.Replace("%EXT", extension))
        dependencies
        |> Array.iter (fun (package, version) ->
            dotnetAddPackage isDebug package projectFile version |> ignore
        )
    else
        let projectLines = System.IO.File.ReadAllLines projectFile
        if projectLines |> Array.exists (fun line -> line.Contains(sprintf "<Compile Include=\"%s\"" fsFileName)) then
            ()
        else
            projectLines
            |> Array.map (fun x -> if x.ToLower().Contains("compile") then sprintf "        <Compile Include=\"%s\" />" fsFileName else x)
            |> fun content -> System.IO.File.WriteAllLines(projectFile, content)

    let configFile = Path.Combine(path, "wsconfig.json")
    System.IO.File.WriteAllText(configFile, Files.configFile.Replace("%OUTPUT", escapeString outputPath).Replace("%TS", if isTS then "true" else "false"))

    let content =
        System.IO.File.ReadAllLines(filePath)
        |> Array.map (fun (line: string) ->
            if line.Trim().StartsWith("#r") then
                "//" + line
            else if line.Trim().StartsWith("#i") then
                "//" + line
            else if line.Trim().StartsWith("#load") then
                "//" + line
            else
                line
        )
        |> String.concat System.Environment.NewLine
    System.IO.File.WriteAllText(fsFile, content)
    projectFile
    
let clearCacheFolder () =
    let path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ws-compile")
    let directoryInfo = DirectoryInfo path
    if directoryInfo.Exists then
        directoryInfo.Delete(true)
        printfn "%s was cleared" path

let copyDirectoryContents (source: string) (dest: string) =
    let sourceDir = source |> DirectoryInfo
    if dest |> System.IO.Directory.Exists then
        let rec copyRecursive source dest =
            let sD = source |> DirectoryInfo

            if dest |> Directory.Exists |> not then
                Directory.CreateDirectory dest |> ignore

            sD.GetFiles()
            |> Array.iter (fun file -> file.CopyTo(Path.Combine(dest, file.Name), true) |> ignore)
            
            sD.GetDirectories()
            |> Array.iter (fun directory ->
                let newDir = Path.Combine(dest, directory.Name)
                copyRecursive directory.FullName newDir
            )
        copyRecursive source dest
        sourceDir.Delete(true)
    else
        sourceDir.MoveTo <| dest
    
let Compile (arguments: ParseResults<CompileArguments>) =
    let path = arguments.TryGetResult CompileArguments.FilePath
    match path with
    | Some path ->
        let rootedPath = Path.Combine(System.Environment.CurrentDirectory, path)
        let extension = Path.GetExtension rootedPath
        if not <| File.Exists rootedPath then exitWithError <| CompileError.FileNotFound rootedPath else
        let dependencies = parseDependencies rootedPath
        let websharperVersion = arguments.TryGetResult CompileArguments.Version
        let isDebug = arguments.TryGetResult CompileArguments.Debug |> Option.isSome
        let adjustedDependencies = dependencies |> Array.append [|"WebSharper.FSharp", websharperVersion; "WebSharper.MathJS", websharperVersion|] |> Array.distinctBy fst
        let isStandalone = arguments.TryGetResult CompileArguments.Standalone
        let folderToCreateIfNeeded =
            match isStandalone with
            | Some _ -> Path.GetDirectoryName(rootedPath)
            | None _ ->
                let encodedFolderName = getEncodedFolderName adjustedDependencies extension
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ws-compile", encodedFolderName)
        let isTS = arguments.TryGetResult CompileArguments.TypeScript |> Option.isSome
        let outputFileName = arguments.TryGetResult CompileArguments.Output
        let outputPath =
            match arguments.TryGetResult CompileArguments.Path with
            | None -> Path.GetDirectoryName(rootedPath)
            | Some path ->
                let rootedPath = Path.Combine(System.Environment.CurrentDirectory, path)
                Path.GetDirectoryName(rootedPath)
                |> Path.GetFullPath
        let projectToBuild = projectFileSetup isDebug folderToCreateIfNeeded adjustedDependencies outputPath rootedPath isTS outputFileName
        let r = BuildHelpers.buildProjectFile projectToBuild isDebug
        match arguments.TryGetResult CompileArguments.Path with
        | None when r = 0 ->
            let rootedPath = Path.Combine(System.Environment.CurrentDirectory, path)
            let outputFolder = Path.GetDirectoryName rootedPath
            try
                let source = Path.Combine(outputPath, "MyProject")
                let dest = Path.Combine(outputPath, outputFolder)
                copyDirectoryContents source dest
                0
            with ex ->
                eprintfn "Could not find JS output"
                1
        | Some path when r = 0 ->
            let rootedPath = Path.Combine(System.Environment.CurrentDirectory, path)
            let outputFolder = Path.GetFileName rootedPath
            try
                let source = Path.Combine(outputPath, "MyProject")
                let dest = Path.Combine(outputPath, outputFolder)
                copyDirectoryContents source dest
                0
            with ex ->
                eprintfn "Could not find JS output"
                1
        | _ -> r
        
    | None ->
        exitWithError <| CompileError.MissingFilePath