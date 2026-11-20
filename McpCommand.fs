/// dotnet ws mcp: runs the MCP server of the booster for AI agents (`wsfscservice --mcp`) with this process's stdio,
/// for the booster the projects in the folder build with: the WebSharper.FSharp.Booster tool when the build would
/// use it, else the portable booster of the WebSharper.FSharp package (as dotnet ws start chooses). WebSharper 11+.
module McpCommand

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open Fake.Core

let private semVer (version: string) =
    try Some (SemVer.parse version) with _ -> None

let private newestFirst (versions: string list) =
    versions |> List.distinct |> List.sortByDescending semVer

/// The obj/project.nuget.cache files of the projects in and under a folder, else of the nearest parent folder
/// that has one (as `dotnet ws build` finds projects).
let private projectCaches (folder: string) =
    let excluded = set [ "bin"; "obj"; "node_modules"; ".git"; ".vs"; ".idea"; "packages"; "paket-files" ]
    let cacheOf (projectDir: string) =
        let cache = Path.Combine(projectDir, "obj", "project.nuget.cache")
        if File.Exists cache then [ cache ] else []
    let rec down depth (dir: string) =
        [
            yield! cacheOf dir
            if depth > 0 then
                let subdirs = try Directory.GetDirectories dir with _ -> [||]
                for sub in subdirs do
                    let name = Path.GetFileName sub
                    if not (excluded.Contains (name.ToLowerInvariant())) && not (name.StartsWith ".") then
                        yield! down (depth - 1) sub
        ]
    let rec up (dir: DirectoryInfo) =
        match dir.Parent with
        | null -> []
        | parent ->
            match cacheOf parent.FullName with
            | [] -> up parent
            | found -> found
    let folder = Path.GetFullPath folder
    match down 4 folder with
    | [] -> up (DirectoryInfo folder)
    | found -> found

let private readOr (path: string) = try File.ReadAllText path with _ -> ""

/// WebSharper.FSharp versions restored by the projects in and under a folder, newest first.
let projectVersions (folder: string) =
    let regex = Regex(@"[/\\]websharper\.fsharp\.(\d[^/\\""]*?)\.nupkg\.sha512", RegexOptions.IgnoreCase)
    [ for cache in projectCaches folder do
        for m in regex.Matches(readOr cache) -> m.Groups.[1].Value ]
    |> newestFirst

/// Agent extensions of the packages the projects restored: a package's build/*.props that adds
/// $(MSBuildThisFileDirectory)<relative path>.dll to WebSharperAgentExtensions.
let projectExtensions (folder: string) =
    let packageFile = Regex(@"""([^""]+?)\.nupkg\.sha512""", RegexOptions.IgnoreCase)
    let extension = Regex(@"\$\(MSBuildThisFileDirectory\)([^;<]+?\.dll)", RegexOptions.IgnoreCase)
    [ for cache in projectCaches folder do
        for m in packageFile.Matches(readOr cache) do
            let packageDir = Path.GetDirectoryName(Regex.Unescape m.Groups.[1].Value)
            let buildDir = Path.Combine(packageDir, "build")
            let props = try Directory.GetFiles(buildDir, "*.props") with _ -> [||]
            for p in props do
                let text = readOr p
                if text.Contains "WebSharperAgentExtensions" then
                    for e in extension.Matches text do
                        let dll = Path.GetFullPath(Path.Combine(buildDir, e.Groups.[1].Value.Replace('\\', Path.DirectorySeparatorChar)))
                        if File.Exists dll then yield dll ]
    |> List.distinct

/// Whether a booster dll has the MCP server (`wsfscservice --mcp`, the WebSharper.FSharp.Service.Mcp module).
/// Older boosters would start as a booster with --mcp, holding the agent's stdio without answering.
let supportsMcp (dll: string) =
    try
        use stream = File.OpenRead dll
        use pe = new System.Reflection.PortableExecutable.PEReader(stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe)
        md.TypeDefinitions
        |> Seq.exists (fun h ->
            let t = md.GetTypeDefinition h
            md.GetString(t.Namespace) = "WebSharper.FSharp.Service" && md.GetString(t.Name) = "Mcp")
    with _ -> false

type BoosterChoice =
    {
        /// The folder of wsfscservice.dll
        Folder : string
        Version : string
        /// Why this one
        Reason : string
    }

/// The booster folder the build of a version uses for projects in a folder: the Booster tool, else the package's.
let folderFor (version: string) (projectDir: string) =
    let tool =
        match Boosters.locateTool "WebSharper.FSharp.Booster" "wsfsc.dll" version projectDir None with
        | Boosters.ManifestTool (_, entry) | Boosters.GlobalTool entry -> Some (Path.GetDirectoryName entry, "Booster tool")
        | _ -> None
    match tool with
    | Some (folder, kind) when File.Exists(Path.Combine(folder, "wsfscservice.dll")) -> Some (folder, kind)
    | _ -> Boosters.tryPortableFolder (Boosters.defaultNuGetRoot ()) version |> Option.map (fun f -> f, "portable compiler")

let private ofPath (path: string) =
    let path = Path.GetFullPath path
    if Directory.Exists path then path else Path.GetDirectoryName path

/// Chooses the booster: --booster, else --version, else the projects' version, else a running WebSharper 11
/// booster (the newest), else the newest WebSharper.FSharp in the NuGet cache.
let choose (boosterPath: string option) (version: string option) (folder: string) : Result<BoosterChoice, string> =
    let fromVersion (v: string) (reason: string) =
        match folderFor v folder with
        | Some (f, kind) -> Ok { Folder = f; Version = v; Reason = sprintf "%s, %s" reason kind }
        | None -> Error (sprintf "WebSharper.FSharp %s has no WebSharper 11 booster in %s: restore a project that references it, or install the WebSharper.FSharp.Booster tool at that version." v (Boosters.defaultNuGetRoot ()))
    match boosterPath, version with
    | Some path, _ ->
        let f = ofPath path
        if File.Exists(Path.Combine(f, "wsfscservice.dll")) then
            // the version a running booster of the folder registered, else the dll's file version
            let v =
                match Boosters.registered () |> List.tryFind (fun b -> Boosters.sameFolder b.Folder f) |> Option.bind (fun b -> b.Version) with
                | Some v -> v
                | None -> try FileVersionInfo.GetVersionInfo(Path.Combine(f, "wsfscservice.dll")).FileVersion with _ -> null
            Ok { Folder = f; Version = (match v with null | "" -> "(unknown version)" | v -> v); Reason = "given with --booster" }
        else Error (sprintf "There is no booster at %s (expected wsfscservice.dll)." f)
    | None, Some v -> fromVersion v "given with --version"
    | None, None ->
        match projectVersions folder with
        | v :: _ -> fromVersion v "used by the projects here"
        | [] ->
            let running =
                Boosters.registered ()
                |> List.sortByDescending (fun b -> b.Version |> Option.bind semVer)
            match running with
            | b :: _ -> Ok { Folder = b.Folder; Version = defaultArg b.Version "(unknown version)"; Reason = "running" }
            | [] ->
                let installed =
                    try Directory.GetDirectories(Path.Combine(Boosters.defaultNuGetRoot (), "websharper.fsharp")) |> Array.map Path.GetFileName |> List.ofArray |> newestFirst
                    with _ -> []
                match installed |> List.tryPick (fun v -> folderFor v folder |> Option.map (fun (f, kind) -> v, f, kind)) with
                | Some (v, f, kind) -> Ok { Folder = f; Version = v; Reason = "newest installed, " + kind }
                | None -> Error (sprintf "No WebSharper 11 WebSharper.FSharp package found in %s. Restore or build a WebSharper F# project first." (Boosters.defaultNuGetRoot ()))

/// `dotnet ws mcp`: everything but the MCP messages goes to stderr, stdout belongs to the protocol.
let run (boosterPath: string option) (version: string option) (project: string option) =
    let folder =
        match project with
        | Some p when File.Exists p -> Path.GetDirectoryName(Path.GetFullPath p)
        | Some p when Directory.Exists p -> p
        | Some p ->
            eprintfn "dotnet ws mcp: %s not found." p
            Environment.CurrentDirectory
        | None -> Environment.CurrentDirectory
    match choose boosterPath version folder with
    | Error msg ->
        eprintfn "dotnet ws mcp: %s" msg
        1
    | Ok choice ->
        let dll = Path.Combine(choice.Folder, "wsfscservice.dll")
        if not (supportsMcp dll) then
            eprintfn "dotnet ws mcp: the booster of WebSharper.FSharp %s (%s) can't answer agents: that needs WebSharper 11.0.0-preview.2 or newer. Update the WebSharper packages of the project, or choose a version with --version." choice.Version choice.Folder
            1
        else
        eprintfn "dotnet ws mcp: WebSharper.FSharp %s booster (%s): %s" choice.Version choice.Reason choice.Folder
        let startInfo = ProcessStartInfo(Boosters.dotnetHost ())
        startInfo.ArgumentList.Add dll
        startInfo.ArgumentList.Add "--mcp"
        for ext in projectExtensions folder do
            eprintfn "dotnet ws mcp: agent extension %s" ext
            startInfo.ArgumentList.Add "--extension"
            startInfo.ArgumentList.Add ext
        // stdin, stdout and stderr are inherited: the agent talks to the booster's MCP server directly
        startInfo.UseShellExecute <- false
        try
            use proc = Process.Start startInfo
            proc.WaitForExit()
            proc.ExitCode
        with e ->
            eprintfn "dotnet ws mcp: couldn't start %s %s --mcp: %s" (Boosters.dotnetHost ()) dll e.Message
            1
