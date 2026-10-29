module BuildInfo

// Captures the MSBuild-evaluated project graph as a side effect of a build, so
// that `dotnet ws build` can fast-path every project in a project's closure
// (the project plus its transitive ProjectReferences) without parsing project
// or solution files.
//
// A small targets file is injected into the build via
// CustomAfterMicrosoftCommonTargets. After each project's Build target it
// writes obj/wsbuild.info with the evaluated data we need. Because it runs
// post-evaluation, imports/conditions/computed items are already resolved.
//
// The manifest is invalidated by a structural fingerprint: the project file,
// its Directory.Build.*/Directory.Packages.props chain, the restore assets and
// generated NuGet props/targets. Source edits do not invalidate it.

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO

type ProjectInfo =
    {
        ProjectPath: string
        Configuration: string
        TargetFramework: string
        /// Evaluated ProjectReference targets, absolute paths.
        References: string list
        /// Structural inputs; see isFresh.
        Fingerprint: string list
    }

let manifestFileName = "wsbuild.info"

let manifestPath (projectPath: string) =
    Path.Combine(Path.GetDirectoryName(Path.GetFullPath projectPath), "obj", manifestFileName)

let private captureTargets =
    """<Project>
  <Target Name="WsCaptureBuildInfo"
          AfterTargets="Build"
          Condition="'$(WsCaptureBuildInfo)' == 'true' and '$(TargetFramework)' != ''">
    <PropertyGroup>
      <_WsRefs>@(ProjectReference->'%(FullPath)', '|')</_WsRefs>
      <_WsFingerprint>$(MSBuildProjectFullPath)|$(DirectoryBuildPropsPath)|$(DirectoryBuildTargetsPath)|$(ProjectAssetsFile)|$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).nuget.g.props|$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).nuget.g.targets</_WsFingerprint>
    </PropertyGroup>
    <ItemGroup>
      <_WsLine Include="project=$(MSBuildProjectFullPath)" />
      <_WsLine Include="configuration=$(Configuration)" />
      <_WsLine Include="tfm=$(TargetFramework)" />
      <_WsLine Include="refs=$(_WsRefs)" />
      <_WsLine Include="fingerprint=$(_WsFingerprint)" />
    </ItemGroup>
    <MakeDir Directories="$(MSBuildProjectDirectory)/obj" />
    <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/wsbuild.info"
                      Lines="@(_WsLine)"
                      Overwrite="true"
                      Encoding="UTF-8" />
  </Target>
</Project>
"""

let private captureTargetsPath =
    lazy
        (let dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dotnet-ws")
         Directory.CreateDirectory dir |> ignore
         let path = Path.Combine(dir, "ws-capture.targets")
         File.WriteAllText(path, captureTargets)
         path)

let private splitList (v: string) =
    if String.IsNullOrEmpty v then [] else v.Split('|') |> Array.filter (fun s -> s <> "") |> List.ofArray

let private tryReadManifestFile (path: string) =
    if File.Exists path then
        let lines = File.ReadAllLines path
        let value key =
            lines
            |> Array.tryPick (fun l ->
                if l.StartsWith(key + "=", StringComparison.Ordinal) then
                    Some(l.Substring(key.Length + 1))
                else
                    None)
        match value "project" with
        | Some project ->
            Some
                {
                    ProjectPath = project
                    Configuration = defaultArg (value "configuration") ""
                    TargetFramework = defaultArg (value "tfm") ""
                    References = defaultArg (value "refs") "" |> splitList
                    Fingerprint = defaultArg (value "fingerprint") "" |> splitList
                }
        | None -> None
    else
        None

let tryReadManifest (projectPath: string) =
    tryReadManifestFile (manifestPath projectPath)

// Directory.Build.* / Directory.Packages.props from the project directory up to
// the root. Not always exposed by a single MSBuild property, and a change there
// can rewire the graph, so include them explicitly.
let private ancestorBuildFiles (projectDir: string) =
    let names = [ "Directory.Build.props"; "Directory.Build.targets"; "Directory.Packages.props" ]
    let rec go (dir: DirectoryInfo) =
        seq {
            for n in names do
                let f = Path.Combine(dir.FullName, n)
                if File.Exists f then yield f
            if not (isNull dir.Parent) then yield! go dir.Parent
        }
    go (DirectoryInfo projectDir) |> Seq.toList

let private isFresh (projectPath: string) (info: ProjectInfo) =
    let path = manifestPath projectPath
    if not (File.Exists path) then
        false
    else
        let manifestTime = File.GetLastWriteTimeUtc path
        let files =
            info.Fingerprint @ ancestorBuildFiles (Path.GetDirectoryName(Path.GetFullPath projectPath))
        files
        |> List.forall (fun f -> not (File.Exists f) || File.GetLastWriteTimeUtc f <= manifestTime)

/// Reads the transitive closure of the entry project from cached manifests.
/// Returns None if any manifest is missing or stale, in which case the caller
/// should run a capture build to (re)generate them.
let tryClosure (entryProject: string) : ProjectInfo list option =
    let visited = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let acc = List<ProjectInfo>()
    let rec go (path: string) =
        let path = Path.GetFullPath path
        if not (visited.Add path) then
            true
        else
            match tryReadManifest path with
            | Some info when isFresh path info ->
                acc.Add info
                info.References |> List.forall go
            | _ -> false
    if go entryProject then Some(List.ofSeq acc) else None

/// References-first order.
let topoSort (infos: ProjectInfo list) : ProjectInfo list =
    let byPath = Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase)
    infos |> List.iter (fun i -> byPath.[Path.GetFullPath i.ProjectPath] <- i)
    let visited = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let result = List<ProjectInfo>()
    let rec visit (path: string) =
        let path = Path.GetFullPath path
        if visited.Add path then
            match byPath.TryGetValue path with
            | true, info ->
                info.References |> List.iter visit
                result.Add info
            | _ -> ()
    infos |> List.iter (fun i -> visit i.ProjectPath)
    List.ofSeq result

/// Runs a normal build of the given project/solution with the capture target
/// injected, so that manifests are written for every project that gets built.
let captureBuild (f: Process -> unit) (targetPath: string) : int =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.Arguments <-
        sprintf
            "build \"%s\" -p:CustomAfterMicrosoftCommonTargets=\"%s\" -p:WsCaptureBuildInfo=true"
            (Path.GetFullPath targetPath)
            captureTargetsPath.Value
    let proc = Process.Start startInfo
    f proc
    proc.WaitForExit()
    proc.ExitCode
