/// dotnet ws compiler status / sync: which WebSharper compiler the projects build with, and
/// installing the WebSharper.FSharp.Booster / WebSharper.CSharp.Booster .NET tools at the
/// version of the referenced WebSharper packages. WebSharper 11+ only: older packages ship
/// their per-platform compilers in the package.
module CompilerCommand

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Argu
open FileCompile.BuildHelpers

type StatusArguments =
    | [<AltCommandLine("-p")>] Project of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Project _ -> "Project or solution (.sln/.slnx) file. Defaults to a single project, or a solution, found in the current or a nearest parent folder."

type SyncArguments =
    | [<AltCommandLine("-p")>] Project of string
    | Local
    | Global
    | [<AltCommandLine("-s")>] Source of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Project _ -> "Project or solution (.sln/.slnx) file. Defaults to a single project, or a solution, found in the current or a nearest parent folder."
            | Local -> "Add the Booster tools to the nearest tool manifest, or create .config/dotnet-tools.json in the repository root."
            | Global -> "Install the Booster tools globally."
            | Source _ -> "An additional NuGet package source to install the tools from."

type CompilerArguments =
    | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArguments>
    | [<CliPrefix(CliPrefix.None)>] Sync of ParseResults<SyncArguments>

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Status _ -> "Show which WebSharper compiler each project builds with, and the running boosters (WebSharper 11+)."
            | Sync _ -> "Install or update the WebSharper Booster tools to the version the projects reference (WebSharper 11+)."

type Lang =
    | FSharp
    | CSharp

    member this.Name = match this with FSharp -> "F#" | CSharp -> "C#"
    member this.PackageId = match this with FSharp -> "websharper.fsharp" | CSharp -> "websharper.csharp"
    member this.BoosterId = match this with FSharp -> "WebSharper.FSharp.Booster" | CSharp -> "WebSharper.CSharp.Booster"
    member this.Entry = match this with FSharp -> "wsfsc.dll" | CSharp -> "wscsc.dll"

type WsProject =
    {
        Path: string
        Dir: string
        Lang: Lang
        /// WebSharperCompilerVersion
        Version: string
        /// WebSharperToolPath: the package's portable compiler unless set explicitly
        ToolPath: string
        ToolPathIsDefault: bool
        UseBoosterTool: bool
        NuGetPackageRoot: string option
        Standalone: bool
    }

type ProjectState =
    | Ws11 of WsProject
    | OlderWebSharper of path: string * lang: Lang * version: string
    | NotWebSharper of path: string
    | Failed of path: string * message: string

/// The compiler the WebSharper task picks, in its order.
type CompilerChoice =
    | ExplicitToolPath of string
    | OptedOut of portable: string
    | Lookup of Boosters.ToolLookup * portable: string

// ---------------------------------------------------------------------------------------------
// Projects

let private langOf (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".fsproj" -> Some FSharp
    | ".csproj" -> Some CSharp
    | _ -> None

/// Runs a process and returns its exit code and output. Without capture the output goes to the console.
let private run (workDir: string) (fileName: string) (args: string list) (capture: bool) =
    let startInfo = ProcessStartInfo(fileName)
    for a in args do startInfo.ArgumentList.Add a
    startInfo.WorkingDirectory <- workDir
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- capture
    startInfo.RedirectStandardError <- capture
    use p = Process.Start startInfo
    if capture then
        let err = p.StandardError.ReadToEndAsync()
        let out = p.StandardOutput.ReadToEnd()
        p.WaitForExit()
        p.ExitCode, out + err.Result
    else
        p.WaitForExit()
        p.ExitCode, ""

/// The projects a compiler command works on: --project, else a single .fsproj/.csproj in the current
/// folder, else a solution there, else the same in the parent folders (up to a wsconfig.json).
/// Returns the projects and the folder of the project or solution.
let resolveProjects (explicitTarget: string option) =
    let isSolution (p: string) =
        match Path.GetExtension(p).ToLowerInvariant() with ".sln" | ".slnx" -> true | _ -> false
    let solutionProjects (sln: string) =
        let code, out = run (Path.GetDirectoryName sln) "dotnet" [ "sln"; sln; "list" ] true
        if code <> 0 then
            Error (sprintf "Couldn't list the projects of %s: %s" sln (out.Trim()))
        else
            out.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun l -> l.Trim())
            |> Array.filter (fun l -> (langOf l).IsSome)
            |> Array.map (fun l -> Path.GetFullPath(Path.Combine(Path.GetDirectoryName sln, l)))
            |> List.ofArray
            |> fun ps -> Ok (ps, Path.GetDirectoryName sln)
    match explicitTarget with
    | Some target ->
        if not (File.Exists target) then Error (sprintf "%s does not exist." target)
        else
            let full = Path.GetFullPath target
            if isSolution full then solutionProjects full else Ok ([ full ], Path.GetDirectoryName full)
    | None ->
        let rec find (location: string) =
            let projects = Directory.EnumerateFiles location |> Seq.filter (fun f -> (langOf f).IsSome) |> List.ofSeq
            let solutions = Directory.EnumerateFiles location |> Seq.filter isSolution |> Seq.sort |> List.ofSeq
            match projects, solutions with
            | [ single ], _ -> Ok ([ Path.GetFullPath single ], Path.GetFullPath location)
            | _, sln :: _ -> solutionProjects (Path.GetFullPath sln)
            | _ :: _ :: _, [] -> Error "Found more than one project file in the current directory. Specify one with \"--project\"."
            | [], [] ->
                let parent = Directory.GetParent location
                if File.Exists(Path.Combine(location, "wsconfig.json")) || isNull parent then
                    Error "Couldn't find a project or solution file in the current directory."
                else find parent.FullName
        find (Path.GetFullPath ".")

let private properties =
    [
        "WebSharperCompilerVersion"
        "WebSharperToolPath"
        "_WebSharperToolPathIsDefault"
        "WebSharperUseBoosterTool"
        "NuGetPackageRoot"
        "WebSharperStandalone"
    ]

let private restore (path: string) =
    let dir = Path.GetDirectoryName path
    if File.Exists(Path.Combine(dir, "obj", "project.assets.json")) then Ok ()
    else
        PrintHelpers.info 0 "Restoring %s" path
        match run dir "dotnet" [ "restore"; path; "-v"; "q"; "-nologo" ] true with
        | 0, _ -> Ok ()
        | _, out -> Error (sprintf "dotnet restore failed:%s%s" Environment.NewLine (out.Trim()))

let private readProperties (path: string) (lang: Lang) =
    let dir = Path.GetDirectoryName path
    let args = [ "msbuild"; path; "-nologo" ] @ (properties |> List.map (sprintf "-getProperty:%s"))
    match run dir "dotnet" args true with
    | code, out when code <> 0 || out.IndexOf '{' < 0 ->
        Failed (path, sprintf "Couldn't read the project properties:%s%s" Environment.NewLine (out.Trim()))
    | _, out ->
        try
            use doc = JsonDocument.Parse(out.Substring(out.IndexOf '{', out.LastIndexOf '}' - out.IndexOf '{' + 1))
            let props = doc.RootElement.GetProperty "Properties"
            let get name =
                match props.TryGetProperty(name: string) with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString().Trim()
                | _ -> ""
            let isTrue name = String.Equals(get name, "true", StringComparison.OrdinalIgnoreCase)
            match get "WebSharperCompilerVersion" with
            | "" ->
                match Boosters.tryProjectPackageVersion lang.PackageId dir with
                | Some v -> OlderWebSharper (path, lang, v)
                | None -> NotWebSharper path
            | version ->
                Ws11 {
                    Path = path
                    Dir = dir
                    Lang = lang
                    Version = version
                    ToolPath = (match get "WebSharperToolPath" with "" -> "" | p -> Path.GetFullPath(p, dir))
                    ToolPathIsDefault = isTrue "_WebSharperToolPathIsDefault"
                    UseBoosterTool = not (String.Equals(get "WebSharperUseBoosterTool", "false", StringComparison.OrdinalIgnoreCase))
                    NuGetPackageRoot = (match get "NuGetPackageRoot" with "" -> None | r -> Some r)
                    Standalone = isTrue "WebSharperStandalone"
                }
        with ex ->
            Failed (path, sprintf "Couldn't read the project properties: %s" ex.Message)

/// Reads what the build needs to pick the compiler of a project, restoring it first if needed.
let readProject (path: string) =
    match langOf path with
    | None -> NotWebSharper path
    | Some lang ->
        match restore path with
        | Result.Error msg -> Failed (path, msg)
        | Ok () -> readProperties path lang

let choose (p: WsProject) =
    if not p.ToolPathIsDefault then ExplicitToolPath p.ToolPath
    elif not p.UseBoosterTool then OptedOut p.ToolPath
    else Lookup (Boosters.locateTool p.Lang.BoosterId p.Lang.Entry p.Version p.Dir p.NuGetPackageRoot, p.ToolPath)

let compilerPath (choice: CompilerChoice) =
    match choice with
    | ExplicitToolPath path
    | OptedOut path
    | Lookup ((Boosters.ManifestTool (_, path) | Boosters.GlobalTool path), _) -> path
    | Lookup (_, portable) -> portable

/// The booster language of a project.
let boosterLanguage (lang: Lang) =
    match lang with FSharp -> Boosters.FSharp | CSharp -> Boosters.CSharp

/// The folder of the booster a project builds with: wsfscservice next to wsfsc for F#, wscscservice next to wscsc for
/// C# (WebSharper 11.0.0-preview.2 and later; earlier C# compilers have no booster). None if it builds standalone.
let boosterFolder (p: WsProject) =
    if p.Standalone then None else
    let folder = Path.GetDirectoryName(compilerPath (choose p))
    match p.Lang with
    | FSharp -> Some folder
    | CSharp -> if File.Exists(Path.Combine(folder, Boosters.CSharp.ServiceDll)) then Some folder else None

// ---------------------------------------------------------------------------------------------
// Output

let formatBooster (b: Boosters.RunningBooster) =
    sprintf "version: %s; %s; PID: %i; path: %s" (defaultArg b.Version "unknown") (Boosters.describe b) b.Pid b.Folder

let private relativePath (path: string) =
    let rel = Path.GetRelativePath(Environment.CurrentDirectory, path)
    if rel.StartsWith ".." then path else rel

let private olderMessage = "This WebSharper version ships its per-platform compilers in the package. Booster tools and dotnet ws compiler status/sync need WebSharper 11 (11.0.0-preview.2 or later)."

let printProject (running: Boosters.RunningBooster list) (state: ProjectState) =
    match state with
    | NotWebSharper _ -> ()
    | Failed (path, msg) ->
        PrintHelpers.error 0 "%s: %s" (relativePath path) msg
    | OlderWebSharper (path, lang, version) ->
        PrintHelpers.text 0 "%s (%s, WebSharper %s)" (relativePath path) lang.Name version
        PrintHelpers.text 2 "%s" olderMessage
    | Ws11 p ->
        PrintHelpers.text 0 "%s (%s, WebSharper %s)" (relativePath p.Path) p.Lang.Name p.Version
        let id = p.Lang.BoosterId
        match choose p with
        | ExplicitToolPath path ->
            PrintHelpers.text 2 "Compiler: %s (set by WebSharperToolPath)" path
        | OptedOut path ->
            PrintHelpers.text 2 "Compiler: portable, %s (WebSharperUseBoosterTool is false)" path
        | Lookup (Boosters.ManifestTool (manifest, entry), _) ->
            PrintHelpers.text 2 "Compiler: %s tool listed in %s" id manifest
            PrintHelpers.text 12 "%s" entry
        | Lookup (Boosters.GlobalTool entry, _) ->
            PrintHelpers.text 2 "Compiler: global %s tool" id
            PrintHelpers.text 12 "%s" entry
        | Lookup (Boosters.ManifestMismatch (manifest, listed), portable) ->
            PrintHelpers.text 2 "Compiler: portable, %s" portable
            PrintHelpers.warning 2 "WSB9101: %s is listed at version %s in %s, but the referenced WebSharper is %s. Run: dotnet ws compiler sync" id listed manifest p.Version
        | Lookup (Boosters.ManifestNotRestored (manifest, listed, root), portable) ->
            PrintHelpers.text 2 "Compiler: portable, %s" portable
            PrintHelpers.warning 2 "WSB9102: %s %s is listed in %s, but it is not restored for this platform (%s) in %s. Run: dotnet ws compiler sync" id listed manifest (Boosters.currentRid ()) root
        | Lookup (Boosters.NoTool, portable) ->
            PrintHelpers.text 2 "Compiler: portable, %s" portable
            PrintHelpers.info 2 "No tool manifest lists %s and it is not installed globally at version %s. For the ReadyToRun compiler: dotnet ws compiler sync --local (or --global)" id p.Version
        match boosterFolder p with
        | None when p.Standalone -> PrintHelpers.text 2 "Booster: not used (WebSharperStandalone is true)"
        | None -> ()
        | Some folder ->
            let language = boosterLanguage p.Lang
            match running |> List.tryFind (fun b -> b.Language = language && Boosters.sameFolder b.Folder folder) with
            | Some b -> PrintHelpers.text 2 "Booster: running, PID %i" b.Pid
            | None -> PrintHelpers.text 2 "Booster: not running, the next build starts it"

let printRunning (running: Boosters.RunningBooster list) =
    match running with
    | [] -> PrintHelpers.text 0 "There are no boosters running."
    | boosters ->
        PrintHelpers.text 0 "The following boosters are running:"
        PrintHelpers.text 0 ""
        for b in boosters do
            PrintHelpers.text 2 "%s" (formatBooster b)

let private printStatus (states: ProjectState list) =
    let running = Boosters.running ()
    for s in states do printProject running s
    PrintHelpers.text 0 ""
    printRunning running

let status (projects: string list) =
    let states = projects |> List.map readProject
    printStatus states
    if states |> List.exists (function Failed _ -> true | _ -> false) then 1 else 0

// ---------------------------------------------------------------------------------------------
// Sync

type SyncMode =
    | DefaultMode
    | LocalMode
    | GlobalMode

type private SyncTarget =
    /// a manifest lists the tool, at the given version
    | ListedIn of manifest: string * listed: string
    | AddTo of manifest: string
    | CreateManifest
    | InstallGlobal
    | Undecided

/// The repository root: the nearest folder up with .git, else the given folder.
let private repoRoot (dir: string) =
    let rec loop (d: DirectoryInfo) =
        if isNull d then None
        elif Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")) then Some d.FullName
        else loop d.Parent
    loop (DirectoryInfo dir) |> Option.defaultValue dir

let private nearestNuGetConfig (dir: string) =
    let rec loop (d: DirectoryInfo) =
        if isNull d then None else
        match d.GetFiles() |> Array.tryFind (fun f -> f.Name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)) with
        | Some f -> Some f.FullName
        | None -> loop d.Parent
    loop (DirectoryInfo dir)

/// dotnet tool commands run from a folder without global.json, so a repository pinning an older
/// SDK doesn't select it: per-platform tools need SDK 10+.
let private neutralFolder () =
    let dir = Path.Combine(Path.GetTempPath(), "websharper-dotnet-tool")
    Directory.CreateDirectory dir |> ignore
    dir

let sync (projects: string list) (rootDir: string) (mode: SyncMode) (source: string option) =
    let states = projects |> List.map readProject
    let running = Boosters.running ()
    for s in states do
        match s with
        | Failed _ | OlderWebSharper _ -> printProject running s
        | _ -> ()
    let ws = states |> List.choose (function Ws11 p -> Some p | _ -> None)
    let versions = ws |> List.map (fun p -> p.Version) |> List.distinctBy (fun v -> v.ToLowerInvariant())
    if List.isEmpty ws then
        PrintHelpers.error 0 "No WebSharper 11+ project found, there is nothing to sync."
        1
    elif versions.Length > 1 then
        PrintHelpers.error 0 "The projects reference different WebSharper versions, and a tool manifest holds one version per tool:"
        for p in ws do PrintHelpers.error 2 "%s: %s" (relativePath p.Path) p.Version
        1
    else
    let version = versions.Head
    let relevant =
        ws |> List.filter (fun p ->
            match choose p with
            | ExplicitToolPath _ ->
                PrintHelpers.info 0 "%s: WebSharperToolPath is set, skipping it." (relativePath p.Path)
                false
            | OptedOut _ ->
                PrintHelpers.info 0 "%s: WebSharperUseBoosterTool is false, skipping it." (relativePath p.Path)
                false
            | Lookup _ -> true)
    let targetFor (p: WsProject) =
        match Boosters.findManifestListing p.Lang.BoosterId p.Dir with
        | Some (manifest, listed) -> ListedIn (manifest, listed)
        | None ->
            match mode with
            | LocalMode ->
                match Boosters.findNearestManifest p.Dir with
                | Some manifest -> AddTo manifest
                | None -> CreateManifest
            | GlobalMode -> InstallGlobal
            | DefaultMode -> Undecided
    let actions = relevant |> List.map (fun p -> p.Lang, targetFor p) |> List.distinct
    if actions |> List.exists (fun (_, t) -> t = Undecided) then
        printStatus states
        PrintHelpers.text 0 ""
        PrintHelpers.error 0 "No tool manifest lists the Booster tools. Run \"dotnet ws compiler sync --local\" to add them to a tool manifest, or \"dotnet ws compiler sync --global\" to install them globally."
        1
    elif List.isEmpty actions then
        PrintHelpers.text 0 "There is nothing to sync."
        0
    else
    let neutral = neutralFolder ()
    let sdkVersion =
        match run neutral "dotnet" [ "--version" ] true with
        | 0, out -> out.Trim()
        | _ -> ""
    let sdkMajor =
        match Int32.TryParse(sdkVersion.Split('.').[0]) with
        | true, m -> m
        | _ -> 0
    if sdkMajor < 10 then
        PrintHelpers.error 0 "Installing the Booster tools needs .NET SDK 10 or newer, the SDK here is \"%s\"." sdkVersion
        1
    else
    let configArgs =
        [
            match relevant |> List.tryPick (fun p -> nearestNuGetConfig p.Dir) with
            | Some config -> yield! [ "--configfile"; config ]
            | None -> ()
            match source with
            | Some s -> yield! [ "--add-source"; (if Directory.Exists s || File.Exists s then Path.GetFullPath s else s) ]
            | None -> ()
        ]
    let dotnetTool (args: string list) =
        PrintHelpers.info 0 "dotnet %s" (String.Join(" ", args))
        fst (run neutral "dotnet" args false)
    // a new manifest at the repository root, shared by every project that needs one
    let created =
        lazy (
            let root = repoRoot rootDir
            match dotnetTool [ "new"; "tool-manifest"; "-o"; root ] with
            | 0 ->
                // SDK 10 creates dotnet-tools.json in the folder, earlier SDKs .config/dotnet-tools.json
                [ Path.Combine(root, ".config", "dotnet-tools.json"); Path.Combine(root, "dotnet-tools.json") ]
                |> List.tryFind File.Exists
                |> function
                    | Some manifest -> Ok manifest
                    | None -> Error 1
            | code -> Error code)
    let beforeFolders = relevant |> List.choose boosterFolder
    // a manifest that already lists the version only needs one restore for all its tools
    let restored = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let results =
        actions
        |> List.map (fun (lang, target) ->
            let id = lang.BoosterId
            match target with
            | ListedIn (manifest, listed) when String.Equals(listed, version, StringComparison.OrdinalIgnoreCase) ->
                if restored.Add manifest then
                    dotnetTool ([ "tool"; "restore"; "--tool-manifest"; manifest ] @ configArgs)
                else 0
            | ListedIn (manifest, _) ->
                dotnetTool ([ "tool"; "update"; id; "--version"; version; "--allow-downgrade"; "--tool-manifest"; manifest ] @ configArgs)
            | AddTo manifest ->
                dotnetTool ([ "tool"; "install"; id; "--version"; version; "--tool-manifest"; manifest ] @ configArgs)
            | CreateManifest ->
                match created.Force() with
                | Ok manifest -> dotnetTool ([ "tool"; "install"; id; "--version"; version; "--tool-manifest"; manifest ] @ configArgs)
                | Result.Error code -> code
            | InstallGlobal ->
                dotnetTool ([ "tool"; "update"; "-g"; id; "--version"; version; "--allow-downgrade" ] @ configArgs)
            | Undecided -> 1)
    if results |> List.exists (fun c -> c <> 0) then
        PrintHelpers.error 0 "Installing the Booster tools failed."
        1
    else
    // the next build starts the booster of the tool: stop the boosters these projects used before,
    // other projects on the machine may still use them otherwise
    let afterFolders = relevant |> List.choose boosterFolder
    let unused = beforeFolders |> List.filter (fun f -> not (afterFolders |> List.exists (Boosters.sameFolder f)))
    for b in Boosters.registered () do
        if unused |> List.exists (Boosters.sameFolder b.Folder) then
            try
                Boosters.sendExit 5000 b |> ignore
                PrintHelpers.text 0 "Stopped the booster of the %s (PID %i), the next build starts the one of the Booster tool." (Boosters.describe b) b.Pid
            with ex ->
                PrintHelpers.warning 0 "Couldn't stop the booster with PID %i: %s" b.Pid ex.Message
    PrintHelpers.text 0 ""
    printStatus states
    let stillPortable =
        relevant |> List.filter (fun p ->
            match choose p with
            | Lookup ((Boosters.ManifestTool _ | Boosters.GlobalTool _), _) -> false
            | _ -> true)
    if List.isEmpty stillPortable then 0
    else
        PrintHelpers.error 0 "After installing, the build still doesn't find the Booster tool for: %s" (stillPortable |> List.map (fun p -> relativePath p.Path) |> String.concat ", ")
        1
