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

    module PrintHelpers =
        let consoleColor (fc : ConsoleColor) = 
            let current = Console.ForegroundColor
            Console.ForegroundColor <- fc
            { new IDisposable with
                  member x.Dispose() = Console.ForegroundColor <- current }

        let infoColor () =
            match Console.BackgroundColor with
            | ConsoleColor.Black
            | ConsoleColor.DarkBlue
            | ConsoleColor.DarkCyan
            | ConsoleColor.DarkGray ->
                ConsoleColor.Gray
            | _ ->
                ConsoleColor.DarkGray

        
        let cprintf color str  = Printf.kprintf (fun s -> use c = consoleColor color in printf "%s" s) str
        let cprintfn color str = Printf.kprintf (fun s -> use c = consoleColor color in printfn "%s" s) str

        let getIndentString i = String.Join("", [for _ in 1..i do " "])

        let error indent str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in eprintfn "%s%s" (getIndentString indent) s) str
        let warning indent str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Yellow in printfn "%s%s" (getIndentString indent) s) str
        let info indent str = Printf.kprintf (fun s -> use c = consoleColor (infoColor ()) in printfn "%s%s" (getIndentString indent) s) str

        let log indent str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.White in printfn "%s%s" (getIndentString indent) s) str

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
        let dotnetProc = Process.Start(startInfo)
        dotnetProc.WaitForExit()
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
    
    let buildProjectFile indent (projectFile: string) (isDebug: bool) =
        let currentPath = Environment.CurrentDirectory
        try
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
                        printfn "ATRHWBTRHRYH"
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
                        printfn "VERSION: %s" version
                        match Process.GetProcessesByName("wsfscservice") |> Array.tryPick (fun x -> tryCheckVersion x version) with
                        // wsfscservice process reported back, it doesn't have the project cached
                        | Error ErrorCode.ProjectNotCached -> 
                            PrintHelpers.info indent "Build service didn't cache the result of the build. Fallback to \"dotnet build\"."
                            dotnetBuild () 
                        // the type of the project is not ws buildable (WIG or Proxy)
                        | Error ErrorCode.ProjectTypeNotPermitted -> 
                            PrintHelpers.info indent "dotnet ws build can't use caching for this project (WIG or Proxy). Fallback to \"dotnet build\"."
                            dotnetBuild()
                        // cache of the project is outdated
                        | Error ErrorCode.ProjectOutdated ->
                            PrintHelpers.info indent "Project's dependencies changed since last build. Fallback to \"dotnet build\"."
                            dotnetBuild()
                        | Some errorCode ->
                            printfn "%i" errorCode
                            errorCode
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
                    PrintHelpers.info indent "Cache file for restored nuget packages is not found. Starting an msbuild now."
                    dotnetBuild()
                | :? IOException ->
                    PrintHelpers.info indent "Couldn't read obj/project.nuget.cache. Fallback to \"dotnet build\"."
                    dotnetBuild()
                | x ->
                    PrintHelpers.info indent """Couldn't build project because: %s
    
        Fallback to "dotnet build".
        """                         x.Message
                    dotnetBuild()
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
        finally
            printfn "qw|EQWGTERAGHEs"
            Environment.CurrentDirectory <- currentPath
            

type CompileArguments =
    | [<AltCommandLine("-o")>] Output of string
    | [<AltCommandLine("-v")>] Version of string
    | [<AltCommandLine("-s")>] Standalone
    | [<MainCommand>] FilePath of string
    | [<AltCommandLine("-p")>] Path of string
    | [<AltCommandLine("-ts")>] TypeScript
    | [<AltCommandLine("-d")>] Debug
    | [<AltCommandLine("-w")>] Watch
    | Force

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
            | Force -> "Forces the temporary folder to be flushed before the compilation."
            | Watch -> "Initiates a file watcher for the given fsx file."

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

let private exitWithError indent (msg: CompileError) =
    BuildHelpers.PrintHelpers.info indent "%s" <| msg.PrettyPrint()
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
            sprintf "add %s package %s -v %s" project package v
    startInfo.Arguments <- arguments
    let dotnetProc = Process.Start(startInfo)
    dotnetProc.WaitForExit()
    dotnetProc.ExitCode

let escapeString (str: string) =
    str.Replace(@"\", @"\\")

let projectFileSetup isDebug (path: string) (dependencies: (string * string option) array) (outputPath: string) (filePath: string) (isTS: bool) (outputFilePath: string option) clearIfFolderExists =
    let extension = Path.GetExtension filePath
    let fileName =
        match outputFilePath with
        | None -> (Path.GetFileNameWithoutExtension filePath) + extension
        | Some outputFilePath -> (Path.GetFileNameWithoutExtension outputFilePath) + extension
    let fsFileName = sprintf "File%s" extension //fsFile |> Path.GetFileName
    let fsFile = Path.Combine(path, fsFileName)
    
    if System.IO.Directory.Exists path && clearIfFolderExists then
        System.IO.Directory.EnumerateFiles(path)
        |> Seq.iter (System.IO.File.Delete)
        System.IO.Directory.EnumerateDirectories(path)
        |> Seq.iter (fun d -> System.IO.Directory.Delete(d, true))

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
        BuildHelpers.PrintHelpers.info 0 "%s was cleared" path

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
    
let Compile indent (arguments: ParseResults<CompileArguments>) =
    let path = arguments.TryGetResult CompileArguments.FilePath
    match path with
    | Some path ->
        let rootedPath = Path.Combine(System.Environment.CurrentDirectory, path)
        let extension = Path.GetExtension rootedPath
        if not <| File.Exists rootedPath then exitWithError indent <| CompileError.FileNotFound rootedPath else
        let dependencies = parseDependencies rootedPath
        let websharperVersion = arguments.TryGetResult CompileArguments.Version
        let isDebug = arguments.TryGetResult CompileArguments.Debug |> Option.isSome
        let adjustedDependencies = dependencies |> Array.append [|"WebSharper.FSharp", websharperVersion; "WebSharper.MathJS", websharperVersion|] |> Array.distinctBy fst
        let isStandalone = arguments.TryGetResult CompileArguments.Standalone
        let clearIfFolderExists = arguments.TryGetResult CompileArguments.Force |> Option.isSome
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
            | None ->
                Path.GetDirectoryName(rootedPath)
                |> Path.GetFullPath
            | Some path ->
                let rootedPath = Path.Combine(System.Environment.CurrentDirectory, path)
                Path.GetDirectoryName(rootedPath)
                |> Path.GetFullPath
        let projectToBuild = projectFileSetup isDebug folderToCreateIfNeeded adjustedDependencies outputPath rootedPath isTS outputFileName clearIfFolderExists
        let r = BuildHelpers.buildProjectFile indent projectToBuild isDebug
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
                BuildHelpers.PrintHelpers.error indent "Could not find JS output"
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
                BuildHelpers.PrintHelpers.error indent "Could not find JS output"
                1
        | _ -> r
        
    | None ->
        exitWithError indent <| CompileError.MissingFilePath

type FileWatchHandler(fsx: string, args) =

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

    member this.MailBox = mb

    member this.Handler (_: obj) (b: FileSystemEventArgs) =
        // initiate dotnet ws build or dotnet ws compile based on what changed
        if b.FullPath.ToLower() = fsx.ToLower() then
            mb.Post
            <| fun _ ->
                BuildHelpers.PrintHelpers.info 0 "Compilation triggered by: %s" b.FullPath
                let _ = Compile 2 args
                ()
        else
            ()

    member this.Initialize() =
        // Initialize multiple file watchers per project file
        let fw = new System.IO.FileSystemWatcher(System.Environment.CurrentDirectory)
        fw.EnableRaisingEvents <- true
        fw.Changed.AddHandler <| this.Handler
        fw.Deleted.AddHandler <| this.Handler
        fw.Renamed.AddHandler <| this.Handler

let CompileFile (arguments: ParseResults<CompileArguments>) =
    match arguments.TryGetResult CompileArguments.Watch, arguments.TryGetResult CompileArguments.FilePath with
    | Some _, Some fp ->
        let rootedPath = Path.Combine(System.Environment.CurrentDirectory, fp) |> Path.GetFullPath
        let watcher = FileWatchHandler(rootedPath, arguments)
        watcher.Initialize()
        while true do
            ()
        0
    | _, _ ->
        Compile 0 arguments