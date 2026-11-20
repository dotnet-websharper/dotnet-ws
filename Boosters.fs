/// Finds, starts and stops WebSharper boosters (wsfscservice), and finds the
/// WebSharper.FSharp.Booster / WebSharper.CSharp.Booster .NET tools the way the build does.
///
/// WebSharper 11+ boosters register themselves with a BoosterRecord file, so they are found whether
/// they run as the wsfscservice apphost of a Booster tool or as `dotnet wsfscservice.dll` from the
/// WebSharper.FSharp package. WebSharper 10 and earlier boosters are wsfscservice processes without
/// a record, started from the tools/<tfm>/<rid> folders of the package.
module Boosters

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open WebSharper.Compiler.WsFscServiceCommon

let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

/// The NuGet package folder: NUGET_PACKAGES, else ~/.nuget/packages.
let defaultNuGetRoot () =
    match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
    | null | "" -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
    | x -> x

// ---------------------------------------------------------------------------------------------
// Running boosters

type BoosterKind =
    /// WebSharper 11+, registered with a BoosterRecord
    | Registered
    /// WebSharper 10 and earlier: a wsfscservice process without a record
    | Legacy

type RunningBooster =
    {
        Kind: BoosterKind
        Pid: int
        /// None when the version of a legacy booster can't be read
        Version: string option
        /// The folder of wsfscservice(.dll), which also names its pipe
        Folder: string
        Process: Process
    }

let versionPattern = @"(\d+\.\d+\.\d+\.\d+[\w\d\.-]*)"

/// Best-effort WebSharper.FSharp version of a legacy wsfscservice process. On Windows it is read
/// from the embedded file version info; on Unix there is no version resource, so it is parsed from
/// the executable path instead, which has the form .../websharper.fsharp/<version>/tools/<tfm>/<rid>/wsfscservice.
let tryGetProcessVersion (proc: Process) =
    try
        let fileInfo = proc.MainModule.FileVersionInfo
        [ fileInfo.FileDescription; fileInfo.FileVersion; proc.MainModule.FileName ]
        |> List.tryPick (fun source ->
            if String.IsNullOrWhiteSpace source then
                None
            else
                let m = Regex.Match(source, versionPattern, RegexOptions.Singleline)
                if m.Success then Some m.Value else None)
    with _ -> None

let private tryGetProcess (pid: int) =
    try
        let p = Process.GetProcessById pid
        if p.HasExited then p.Dispose(); None else Some p
    with _ -> None

let private sameStart (proc: Process) (started: DateTime) =
    try abs (proc.StartTime.ToUniversalTime() - started.ToUniversalTime()).TotalSeconds < 2.0
    // the start time of another user's process may not be readable: trust the record
    with _ -> true

/// The boosters registered in the record folder. Records of processes that are gone (killed
/// boosters can't remove their record) are deleted.
let registered () =
    let dir = boosterRecordFolder ()
    if not (Directory.Exists dir) then [] else
    Directory.GetFiles(dir, "*.json")
    |> Array.choose (fun path ->
        let record =
            try JsonSerializer.Deserialize<BoosterRecord>(File.ReadAllText path) |> Option.ofObj
            with _ -> None
        match record with
        | None -> None
        | Some r ->
            match tryGetProcess r.Pid with
            | Some p when sameStart p r.Started ->
                Some { Kind = Registered; Pid = r.Pid; Version = Some r.Version; Folder = r.Folder; Process = p }
            | other ->
                other |> Option.iter (fun p -> p.Dispose())
                try File.Delete path with _ -> ()
                None)
    |> List.ofArray

/// All running boosters: the registered ones, then legacy wsfscservice processes.
let running () =
    let regs = registered ()
    let pids = regs |> List.map (fun b -> b.Pid) |> Set.ofList
    let legacy =
        Process.GetProcessesByName "wsfscservice"
        |> Array.choose (fun p ->
            if pids.Contains p.Id then
                p.Dispose()
                None
            else
                try
                    let folder = Path.GetDirectoryName p.MainModule.FileName
                    Some { Kind = Legacy; Pid = p.Id; Version = tryGetProcessVersion p; Folder = folder; Process = p }
                with _ ->
                    p.Dispose()
                    None)
        |> List.ofArray
    regs @ legacy

/// Whether a booster serves the given WebSharper version: exactly for registered boosters, by prefix
/// for legacy ones, whose version resource may lack the prerelease suffix.
let matchesVersion (version: string) (b: RunningBooster) =
    match b.Kind, b.Version with
    | Registered, Some v -> String.Equals(v, version, StringComparison.OrdinalIgnoreCase)
    | Legacy, Some v -> version.StartsWith(v, StringComparison.OrdinalIgnoreCase)
    | _, None -> false

let private normalizedFolder (folder: string) =
    Path.GetFullPath(folder).TrimEnd('/', '\\').Replace('\\', '/').ToLowerInvariant()

let sameFolder (a: string) (b: string) =
    normalizedFolder a = normalizedFolder b

/// Where a booster runs from, for listings.
let describe (b: RunningBooster) =
    match b.Kind with
    | Legacy -> "per-platform booster of the package"
    | Registered ->
        let f = normalizedFolder b.Folder
        if f.Contains "websharper.fsharp.booster" then "Booster tool"
        elif f.Contains "/websharper.fsharp/" then "portable compiler"
        else "local build"

let clientPipe (folder: string) =
    new NamedPipeClientStream(
        ".", boosterPipeName folder, PipeDirection.InOut,
        PipeOptions.WriteThrough ||| PipeOptions.Asynchronous)

let sendOneMessage (clientPipe: NamedPipeClientStream) (message: ArgsType) =
    let options = JsonSerializerOptions()
    options.Encoder <- Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    let res = JsonSerializer.Serialize(message, options)
    let bytes = Encoding.UTF8.GetBytes res
    clientPipe.Connect(2000)
    if clientPipe.IsConnected then
        clientPipe.ReadMode <- PipeTransmissionMode.Byte
        clientPipe.Write(BitConverter.GetBytes(bytes.Length), 0, 4) // prepend with message length
        clientPipe.Write(bytes, 0, bytes.Length)
        clientPipe.Flush()
        if isWindows then
            clientPipe.WaitForPipeDrain()
    else
        failwith "Pipe is not initialized yet"

/// Asks a booster to exit after its current compilations, and waits for it up to the timeout.
let sendExit (timeoutMs: int) (b: RunningBooster) =
    use pipe = clientPipe b.Folder
    sendOneMessage pipe { args = [| "exit" |] }
    b.Process.WaitForExit timeoutMs

let kill (b: RunningBooster) =
    b.Process.Kill()
    b.Process.WaitForExit 5000 |> ignore
    // a killed booster can't remove its own record
    if b.Kind = Registered then
        try File.Delete(boosterRecordPath b.Folder) with _ -> ()

// ---------------------------------------------------------------------------------------------
// Starting a booster, as core's WebSharper.FSharp/NamedPipeClient.fs does

/// Starts a process on Windows without inheriting any of this process's handles, so a booster
/// started from a redirected console (CI, MSBuild) doesn't keep its pipes open.
module private DetachedProcess =

    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type StartupInfo =
        val mutable cb: int
        val mutable lpReserved: nativeint
        val mutable lpDesktop: nativeint
        val mutable lpTitle: nativeint
        val mutable dwX: int
        val mutable dwY: int
        val mutable dwXSize: int
        val mutable dwYSize: int
        val mutable dwXCountChars: int
        val mutable dwYCountChars: int
        val mutable dwFillAttribute: int
        val mutable dwFlags: int
        val mutable wShowWindow: int16
        val mutable cbReserved2: int16
        val mutable lpReserved2: nativeint
        val mutable hStdInput: nativeint
        val mutable hStdOutput: nativeint
        val mutable hStdError: nativeint

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ProcessInformation =
        val mutable hProcess: nativeint
        val mutable hThread: nativeint
        val mutable dwProcessId: int
        val mutable dwThreadId: int

    [<DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool CreateProcess(
        string lpApplicationName, StringBuilder lpCommandLine,
        nativeint lpProcessAttributes, nativeint lpThreadAttributes,
        bool bInheritHandles, uint32 dwCreationFlags, nativeint lpEnvironment,
        string lpCurrentDirectory, StartupInfo& lpStartupInfo, ProcessInformation& lpProcessInformation)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CloseHandle(nativeint hObject)

    let CREATE_NO_WINDOW = 0x08000000u

    let start (exePath: string) (args: string list) (workingDir: string) =
        let quote (s: string) = "\"" + s + "\""
        let commandLine = StringBuilder(exePath :: args |> List.map quote |> String.concat " ")
        let mutable startupInfo = StartupInfo()
        startupInfo.cb <- Marshal.SizeOf<StartupInfo>()
        let mutable processInfo = ProcessInformation()
        // a bare name (dotnet) is looked up on PATH from the command line
        let appName = if Path.IsPathRooted exePath then exePath else null
        if not (CreateProcess(appName, commandLine, 0n, 0n, false, CREATE_NO_WINDOW, 0n, workingDir, &startupInfo, &processInfo)) then
            raise (ComponentModel.Win32Exception(Marshal.GetLastWin32Error()))
        CloseHandle processInfo.hThread |> ignore
        CloseHandle processInfo.hProcess |> ignore

/// The dotnet host: the one running this tool, then $(DOTNET_HOST_PATH), then dotnet from PATH.
let dotnetHost () =
    let isDotnet (p: string) =
        not (isNull p) && Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && File.Exists p
    let processPath = Environment.ProcessPath
    let hostPath = Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
    if isDotnet processPath then processPath
    elif isDotnet hostPath then hostPath
    else "dotnet"

/// The booster's apphost, if it can be started directly. Packages extracted on Linux and macOS
/// may not have the executable bit set, those boosters run with the dotnet host.
let tryBoosterAppHost (folder: string) =
    let path = Path.Combine(folder, if isWindows then "wsfscservice.exe" else "wsfscservice")
    if not (File.Exists path) then None
    elif isWindows then Some path
    else
        try
            if File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute) then Some path else None
        with _ -> None

/// Starts a detached process: no inherited handles on Windows, nohup with no output on Unix.
let startDetached (fileName: string) (args: string list) (workingDir: string) =
    if isWindows then
        DetachedProcess.start fileName args workingDir
    else
        let startInfo = ProcessStartInfo("/bin/sh")
        startInfo.ArgumentList.Add "-c"
        startInfo.ArgumentList.Add "nohup \"$0\" \"$@\" </dev/null >/dev/null 2>&1 &"
        startInfo.ArgumentList.Add fileName
        for a in args do startInfo.ArgumentList.Add a
        startInfo.WorkingDirectory <- workingDir
        startInfo.CreateNoWindow <- true
        startInfo.UseShellExecute <- false
        use p = Process.Start(startInfo)
        p.WaitForExit()

/// Starts the WebSharper 11+ booster of a folder (the folder of wsfscservice.dll), and waits up to
/// the timeout for it to register. Returns the registered booster.
let startBooster (folder: string) (timeoutMs: int) =
    let fileName, args =
        match tryBoosterAppHost folder with
        | Some appHost -> appHost, []
        | None -> dotnetHost (), [ Path.Combine(folder, "wsfscservice.dll") ]
    startDetached fileName args folder
    let sw = Stopwatch.StartNew()
    let rec wait () =
        match registered () |> List.tryFind (fun b -> sameFolder b.Folder folder) with
        | Some b -> Some b
        | None when sw.ElapsedMilliseconds < int64 timeoutMs ->
            Threading.Thread.Sleep 200
            wait ()
        | None -> None
    wait ()

// ---------------------------------------------------------------------------------------------
// Finding the Booster tools, as core's WebSharper.MSBuild.FSharp/BoosterLocator.cs does

type ToolLookup =
    /// A manifest lists the tool at the version, and it is restored
    | ManifestTool of manifest: string * entry: string
    /// A manifest lists another version (warning WSB9101 in the build)
    | ManifestMismatch of manifest: string * listedVersion: string
    /// A manifest lists the version, but it is not restored for this platform (warning WSB9102)
    | ManifestNotRestored of manifest: string * listedVersion: string * packageRoot: string
    /// No manifest lists it, it is installed globally at the version
    | GlobalTool of entry: string
    /// The build uses the portable compiler of the package
    | NoTool

/// The platform the Booster tool packages are chosen for.
let currentRid () = RuntimeInformation.RuntimeIdentifier

let private toolPattern (id: string) =
    Regex("\"" + Regex.Escape id + "\"\\s*:\\s*\\{[^{}]*?\"version\"\\s*:\\s*\"([^\"]*)\"",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

let private isRootPattern = Regex("\"isRoot\"\\s*:\\s*true", RegexOptions.IgnoreCase)

let private manifestCandidates (dir: string) =
    [ Path.Combine(dir, ".config", "dotnet-tools.json"); Path.Combine(dir, "dotnet-tools.json") ]

/// The first tool manifest listing the tool, from the folder up (stopping at "isRoot": true),
/// with the listed version: the manifest dotnet itself would use.
let findManifestListing (packageId: string) (startDir: string) =
    let pattern = toolPattern (packageId.ToLowerInvariant())
    let rec loop (dir: DirectoryInfo) =
        if isNull dir then None else
        let found =
            manifestCandidates dir.FullName
            |> List.tryPick (fun candidate ->
                if not (File.Exists candidate) then None else
                let text = try File.ReadAllText candidate with :? IOException -> ""
                let m = pattern.Match text
                if m.Success then Some (Choice1Of2 (candidate, m.Groups.[1].Value))
                elif isRootPattern.IsMatch text then Some (Choice2Of2 ())
                else None)
        match found with
        | Some (Choice1Of2 result) -> Some result
        | Some (Choice2Of2 ()) -> None
        | None -> loop dir.Parent
    loop (DirectoryInfo(Path.GetFullPath startDir))

/// The nearest tool manifest from the folder up, listing the tool or not.
let findNearestManifest (startDir: string) =
    let rec loop (dir: DirectoryInfo) =
        if isNull dir then None else
        match manifestCandidates dir.FullName |> List.tryFind File.Exists with
        | Some m -> Some m
        | None -> loop dir.Parent
    loop (DirectoryInfo(Path.GetFullPath startDir))

let private findEntry (toolsDir: string) (rid: string) (entryAssembly: string) =
    if not (Directory.Exists toolsDir) then None else
    Directory.GetDirectories toolsDir
    |> Array.sortWith (fun a b -> StringComparer.OrdinalIgnoreCase.Compare(b, a))
    |> Array.tryPick (fun tfmDir ->
        let entry = Path.Combine(tfmDir, rid, entryAssembly)
        if File.Exists entry then Some entry else None)

// <CLI home>/.dotnet/tools/.store/<id>/<version>/<id>.<rid>/<version>/tools/<tfm>/<rid>/<entry>
let findGlobalTool (packageId: string) (entryAssembly: string) (version: string) =
    let id = packageId.ToLowerInvariant()
    let home =
        match Environment.GetEnvironmentVariable "DOTNET_CLI_HOME" with
        | null | "" -> Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        | x -> Path.GetFullPath x
    let v = version.ToLowerInvariant()
    let rid = currentRid ()
    findEntry (Path.Combine(home, ".dotnet", "tools", ".store", id, v, id + "." + rid, v, "tools")) rid entryAssembly

/// The Booster tool the build would use for a project, see BoosterLocator.cs in core.
/// packageId: WebSharper.FSharp.Booster or WebSharper.CSharp.Booster; entryAssembly: wsfsc.dll or wscsc.dll.
let locateTool (packageId: string) (entryAssembly: string) (version: string) (projectDir: string) (nugetRoot: string option) =
    let id = packageId.ToLowerInvariant()
    match findManifestListing id projectDir with
    | Some (manifest, listed) ->
        if not (String.Equals(listed.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase)) then
            ManifestMismatch (manifest, listed)
        else
            let root =
                match Environment.GetEnvironmentVariable "NUGET_PACKAGES", nugetRoot with
                | (null | ""), Some r when r <> "" -> r
                | (null | ""), _ -> defaultNuGetRoot ()
                | env, _ -> env
                |> Path.GetFullPath
            let rid = currentRid ()
            match findEntry (Path.Combine(root, id + "." + rid, listed.ToLowerInvariant(), "tools")) rid entryAssembly with
            | Some entry -> ManifestTool (manifest, entry)
            | None -> ManifestNotRestored (manifest, listed, root)
    | None ->
        match findGlobalTool id entryAssembly version with
        | Some entry -> GlobalTool entry
        | None -> NoTool

// ---------------------------------------------------------------------------------------------
// Booster folders for dotnet ws start

/// The WebSharper.FSharp version a project restored, from obj/project.nuget.cache.
let tryProjectPackageVersion (packageId: string) (projectDir: string) =
    try
        let cache = File.ReadAllText(Path.Combine(projectDir, "obj", "project.nuget.cache"))
        let m = Regex.Match(cache, Regex.Escape(packageId.ToLowerInvariant()) + @"\.(\d[^""/\\]*?)\.nupkg\.sha512")
        if m.Success then Some m.Groups.[1].Value else None
    with _ -> None

/// The folder of the portable booster in the WebSharper.FSharp package (WebSharper 11+).
let tryPortableFolder (nugetRoot: string) (version: string) =
    let folder = Path.Combine(nugetRoot, "websharper.fsharp", version.ToLowerInvariant(), "tools", "net11.0")
    if File.Exists(Path.Combine(folder, "wsfscservice.dll")) then Some folder else None

/// Target framework folders of packages with per-platform boosters, newest first: WebSharper 10 and
/// earlier, and WebSharper 11 previews before the Booster tools.
let legacyTfms = [ "net11.0"; "net10.0"; "net9.0"; "net8.0"; "net7.0"; "net6.0"; "net5.0" ]

/// The start script of a WebSharper 10 and earlier booster: tools/<tfm>/<rid>/wsfscservice_start.cmd|sh.
let tryLegacyStartScript (nugetRoot: string) (version: string) (rid: string) =
    let script = if isWindows then "wsfscservice_start.cmd" else "wsfscservice_start.sh"
    legacyTfms
    |> List.tryPick (fun tfm ->
        let path = Path.Combine(nugetRoot, "websharper.fsharp", version.ToLowerInvariant(), "tools", tfm, rid, script)
        if File.Exists path then Some path else None)
