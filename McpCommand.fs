/// dotnet ws mcp: runs the MCP server of the booster for AI agents with this process's stdio, for the booster the
/// projects in the folder build with: the Booster tool when the build would use it, else the portable booster of the
/// compiler package (as dotnet ws start chooses). F# projects: `wsfscservice --mcp` (WebSharper 11+); C# projects:
/// `wscscservice --mcp` (WebSharper 11.0.0-preview.2+).
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

/// WebSharper.FSharp or WebSharper.CSharp versions restored by the projects in and under a folder, newest first.
let projectVersionsOf (language: Boosters.Language) (folder: string) =
    let regex = Regex(@"[/\\]" + Regex.Escape language.PackageId + @"\.(\d[^/\\""]*?)\.nupkg\.sha512", RegexOptions.IgnoreCase)
    [ for cache in projectCaches folder do
        for m in regex.Matches(readOr cache) -> m.Groups.[1].Value ]
    |> newestFirst

/// WebSharper.FSharp versions restored by the projects in and under a folder, newest first.
let projectVersions (folder: string) = projectVersionsOf Boosters.FSharp folder

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

/// Whether a booster dll has the MCP server: the WebSharper.FSharp.Service.Mcp module of `wsfscservice --mcp`, or
/// the WebSharper.CSharp.Service.Mcp module of `wscscservice --mcp`. Older F# boosters would start as a booster with
/// --mcp, holding the agent's stdio without answering.
let supportsMcpOf (language: Boosters.Language) (dll: string) =
    let ns = match language with Boosters.FSharp -> "WebSharper.FSharp.Service" | Boosters.CSharp -> "WebSharper.CSharp.Service"
    try
        use stream = File.OpenRead dll
        use pe = new System.Reflection.PortableExecutable.PEReader(stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe)
        md.TypeDefinitions
        |> Seq.exists (fun h ->
            let t = md.GetTypeDefinition h
            md.GetString(t.Namespace) = ns && md.GetString(t.Name) = "Mcp")
    with _ -> false

let supportsMcp (dll: string) = supportsMcpOf Boosters.FSharp dll

type BoosterChoice =
    {
        Language : Boosters.Language
        /// The folder of wsfscservice.dll or wscscservice.dll
        Folder : string
        Version : string
        /// Why this one
        Reason : string
    }

/// The booster folder the build of a version uses for projects of a language in a folder: the Booster tool, else the
/// package's.
let folderForOf (language: Boosters.Language) (version: string) (projectDir: string) =
    let tool =
        match Boosters.locateTool language.BoosterToolId language.CompilerDll version projectDir None with
        | Boosters.ManifestTool (_, entry) | Boosters.GlobalTool entry -> Some (Path.GetDirectoryName entry, "Booster tool")
        | _ -> None
    match tool with
    | Some (folder, kind) when File.Exists(Path.Combine(folder, language.ServiceDll)) -> Some (folder, kind)
    | _ -> Boosters.tryPortableFolderOf language (Boosters.defaultNuGetRoot ()) version |> Option.map (fun f -> f, "portable compiler")

let folderFor (version: string) (projectDir: string) = folderForOf Boosters.FSharp version projectDir

let private ofPath (path: string) =
    let path = Path.GetFullPath path
    if Directory.Exists path then path else Path.GetDirectoryName path

/// The language of a --booster path: the dll it names, else the booster dll in its folder (--lang when both are).
let private languageOfBooster (lang: Boosters.Language option) (path: string) =
    let has (l: Boosters.Language) = File.Exists(Path.Combine(ofPath path, l.ServiceDll))
    let named (l: Boosters.Language) =
        let file = Path.GetFileName(Path.GetFullPath path)
        file.Equals(l.ServiceDll, StringComparison.OrdinalIgnoreCase)
        || file.Equals(Path.GetFileNameWithoutExtension l.ServiceDll, StringComparison.OrdinalIgnoreCase)
        || file.Equals(Path.GetFileNameWithoutExtension l.ServiceDll + ".exe", StringComparison.OrdinalIgnoreCase)
    match lang with
    | Some l -> l
    | None ->
        if named Boosters.CSharp then Boosters.CSharp
        elif named Boosters.FSharp then Boosters.FSharp
        elif has Boosters.CSharp && not (has Boosters.FSharp) then Boosters.CSharp
        else Boosters.FSharp

/// The language of the booster for the projects in a folder: --lang, else the extension of --project, else the
/// compiler package the projects restored. F# when they restored both (with a note to stderr) or neither.
let languageFor (lang: Boosters.Language option) (project: string option) (folder: string) =
    let ofExtension =
        project |> Option.bind (fun p ->
            match Path.GetExtension(p).ToLowerInvariant() with
            | ".fsproj" -> Some Boosters.FSharp
            | ".csproj" -> Some Boosters.CSharp
            | _ -> None)
    match lang |> Option.orElse ofExtension with
    | Some l -> l
    | None ->
        match projectVersionsOf Boosters.FSharp folder, projectVersionsOf Boosters.CSharp folder with
        | [], _ :: _ -> Boosters.CSharp
        | _ :: _, _ :: _ ->
            eprintfn "dotnet ws mcp: the projects here are F# and C#, using the F# booster. For the C# booster: dotnet ws mcp --lang csharp"
            Boosters.FSharp
        | _ -> Boosters.FSharp

/// The first WebSharper release whose compiler package has a booster of the language (wsfscservice in
/// tools/net11.0, or wscscservice).
let private firstBoosterVersion (language: Boosters.Language) =
    match language with
    | Boosters.FSharp -> "WebSharper 11"
    | Boosters.CSharp -> "WebSharper 11.0.0-preview.2"

/// The first WebSharper release whose booster answers agents (--mcp), both languages.
let private firstMcpVersion = "WebSharper 11.0.0-preview.2"

/// Chooses the booster of a language: --booster, else --version, else the projects' version, else a running booster
/// of the language (the newest), else the newest compiler package of the language in the NuGet cache.
let chooseOf (language: Boosters.Language) (boosterPath: string option) (version: string option) (folder: string) : Result<BoosterChoice, string> =
    let package = match language with Boosters.FSharp -> "WebSharper.FSharp" | Boosters.CSharp -> "WebSharper.CSharp"
    let fromVersion (v: string) (reason: string) =
        match folderForOf language v folder with
        | Some (f, kind) -> Ok { Language = language; Folder = f; Version = v; Reason = sprintf "%s, %s" reason kind }
        | None ->
            Error (sprintf "%s %s has no %s booster (%s) in %s: %s boosters come with %s and later releases. Restore a project that references it, install the %s tool at that version, or choose another with --version."
                    package v language.Name language.ServiceDll (Boosters.defaultNuGetRoot ()) language.Name (firstBoosterVersion language) language.BoosterToolId)
    match boosterPath, version with
    | Some path, _ ->
        let f = ofPath path
        if File.Exists(Path.Combine(f, language.ServiceDll)) then
            // the version a running booster of the folder registered, else the dll's file version
            let v =
                match Boosters.registered () |> List.tryFind (fun b -> b.Language = language && Boosters.sameFolder b.Folder f) |> Option.bind (fun b -> b.Version) with
                | Some v -> v
                | None -> try FileVersionInfo.GetVersionInfo(Path.Combine(f, language.ServiceDll)).FileVersion with _ -> null
            Ok { Language = language; Folder = f; Version = (match v with null | "" -> "(unknown version)" | v -> v); Reason = "given with --booster" }
        else Error (sprintf "There is no %s booster at %s (expected %s)." language.Name f language.ServiceDll)
    | None, Some v -> fromVersion v "given with --version"
    | None, None ->
        match projectVersionsOf language folder with
        | v :: _ -> fromVersion v "used by the projects here"
        | [] ->
            let running =
                Boosters.registered ()
                |> List.filter (fun b -> b.Language = language)
                |> List.sortByDescending (fun b -> b.Version |> Option.bind semVer)
            match running with
            | b :: _ -> Ok { Language = language; Folder = b.Folder; Version = defaultArg b.Version "(unknown version)"; Reason = "running" }
            | [] ->
                let installed =
                    try Directory.GetDirectories(Path.Combine(Boosters.defaultNuGetRoot (), language.PackageId)) |> Array.map Path.GetFileName |> List.ofArray |> newestFirst
                    with _ -> []
                match installed |> List.tryPick (fun v -> folderForOf language v folder |> Option.map (fun (f, kind) -> v, f, kind)) with
                | Some (v, f, kind) -> Ok { Language = language; Folder = f; Version = v; Reason = "newest installed, " + kind }
                | None ->
                    Error (sprintf "No %s package with a %s booster (%s or later) found in %s. Restore or build a WebSharper %s project first."
                            package language.Name (firstBoosterVersion language) (Boosters.defaultNuGetRoot ()) language.Name)

/// Chooses the F# booster, see chooseOf.
let choose (boosterPath: string option) (version: string option) (folder: string) =
    chooseOf Boosters.FSharp boosterPath version folder

/// Parses --lang: fsharp/f#/fs or csharp/c#/cs.
let parseLanguage (lang: string) =
    match lang.Trim().ToLowerInvariant() with
    | "fsharp" | "f#" | "fs" -> Ok Boosters.FSharp
    | "csharp" | "c#" | "cs" -> Ok Boosters.CSharp
    | other -> Error (sprintf "unknown language %s, expected fsharp or csharp." other)

/// `dotnet ws mcp`: everything but the MCP messages goes to stderr, stdout belongs to the protocol.
let run (boosterPath: string option) (version: string option) (project: string option) (lang: string option) =
    let folder =
        match project with
        | Some p when File.Exists p -> Path.GetDirectoryName(Path.GetFullPath p)
        | Some p when Directory.Exists p -> p
        | Some p ->
            eprintfn "dotnet ws mcp: %s not found." p
            Environment.CurrentDirectory
        | None -> Environment.CurrentDirectory
    match lang |> Option.map parseLanguage with
    | Some (Error msg) ->
        eprintfn "dotnet ws mcp: %s" msg
        1
    | parsed ->
    let lang = match parsed with Some (Ok l) -> Some l | _ -> None
    let language =
        match boosterPath with
        | Some path -> languageOfBooster lang path
        | None -> languageFor lang project folder
    match chooseOf language boosterPath version folder with
    | Error msg ->
        eprintfn "dotnet ws mcp: %s" msg
        1
    | Ok choice ->
        let dll = Path.Combine(choice.Folder, language.ServiceDll)
        let package = match language with Boosters.FSharp -> "WebSharper.FSharp" | Boosters.CSharp -> "WebSharper.CSharp"
        if not (supportsMcpOf language dll) then
            eprintfn "dotnet ws mcp: the booster of %s %s (%s) can't answer agents: that needs %s or newer. Update the WebSharper packages of the project, or choose a version with --version." package choice.Version choice.Folder firstMcpVersion
            1
        else
        eprintfn "dotnet ws mcp: %s %s booster (%s): %s" package choice.Version choice.Reason choice.Folder
        let startInfo = ProcessStartInfo(Boosters.dotnetHost ())
        startInfo.ArgumentList.Add dll
        startInfo.ArgumentList.Add "--mcp"
        // agent extensions of the projects' packages: both boosters load them (older C# boosters ignore --extension)
        for ext in projectExtensions folder do
            eprintfn "dotnet ws mcp: agent extension %s" ext
            startInfo.ArgumentList.Add "--extension"
            startInfo.ArgumentList.Add ext
        match language with
        | Boosters.FSharp -> ()
        | Boosters.CSharp ->
            // the C# booster knows only the projects built since it started
            if not (Boosters.registered () |> List.exists (fun b -> b.Language = Boosters.CSharp && Boosters.sameFolder b.Folder choice.Folder)) then
                eprintfn "dotnet ws mcp: the C# booster is not running: it starts with the next build of a WebSharper C# project (dotnet build), and answers about the projects built since."
        // stdin, stdout and stderr are inherited: the agent talks to the booster's MCP server directly
        startInfo.UseShellExecute <- false
        try
            use proc = Process.Start startInfo
            proc.WaitForExit()
            proc.ExitCode
        with e ->
            eprintfn "dotnet ws mcp: couldn't start %s %s --mcp: %s" (Boosters.dotnetHost ()) dll e.Message
            1
