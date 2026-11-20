/// dotnet ws stats: the statistics of the boosters (WebSharper 11+): how long each message, compilation stage
/// and agent tool takes, and what the agents ask. From a running booster through its pipe, else from the file it
/// saves next to its record (websharper-boosters/stats). They stay on this machine.
module StatsCommand

open System
open System.IO
open System.Text.Json.Nodes
open FileCompile.BuildHelpers
open WebSharper.Compiler.WsFscServiceCommon

/// The statistics file of the booster of a folder, as the booster writes it.
let statsPath (folder: string) =
    Path.Combine(boosterRecordFolder (), "stats", Path.GetFileNameWithoutExtension(boosterRecordPath folder) + ".json")

/// Asks a running booster for its statistics (the "stats" message).
let request (b: Boosters.RunningBooster) : Result<JsonObject, string> =
    try
        use pipe = Boosters.clientPipe b.Folder
        Boosters.sendOneMessage pipe { args = [| "stats" |] }
        let text = Text.StringBuilder()
        let handle (message: obj) =
            async {
                let s = (message :?> Text.Json.JsonElement).GetString()
                if s.StartsWith "j: " then
                    text.Append(s.Substring 3) |> ignore
                    return None
                elif s.StartsWith "x: " then return Some (int (s.Substring 3))
                else return None
            }
        match Async.RunSynchronously(readingMessages pipe handle ignore, 10000) with
        | Some 0 ->
            match JsonNode.Parse(text.ToString()) with
            | :? JsonObject as o -> Result.Ok o
            | _ -> Result.Error "the booster's answer is not statistics"
        | _ -> Result.Error "the booster did not answer: it may be a version without statistics (they need WebSharper 11.0.0-preview.2 or newer)"
    with e -> Result.Error e.Message

let private readFile (path: string) =
    try
        match JsonNode.Parse(File.ReadAllText path) with
        | :? JsonObject as o -> Some o
        | _ -> None
    with _ -> None

/// Statistics saved by boosters that are not running now.
let saved (running: Boosters.RunningBooster list) =
    let dir = Path.Combine(boosterRecordFolder (), "stats")
    let runningPaths = running |> List.map (fun b -> Path.GetFullPath(statsPath b.Folder)) |> Set.ofList
    if not (Directory.Exists dir) then [] else
    Directory.GetFiles(dir, "*.json")
    |> Array.filter (fun p -> not (runningPaths.Contains(Path.GetFullPath p)))
    |> Array.choose (fun p -> readFile p |> Option.map (fun o -> p, o))
    |> Array.sortByDescending (fun (p, _) -> File.GetLastWriteTimeUtc p)
    |> List.ofArray

let private str (n: JsonNode) = match n with null -> "" | n -> (try n.GetValue<string>() with _ -> n.ToJsonString())
let private num (n: JsonNode) = match n with null -> 0. | n -> (try n.GetValue<float>() with _ -> 0.)

let private duration (seconds: float) =
    let t = TimeSpan.FromSeconds seconds
    if t.TotalDays >= 1. then sprintf "%id %ih" t.Days t.Hours
    elif t.TotalHours >= 1. then sprintf "%ih %im" t.Hours t.Minutes
    else sprintf "%im %is" t.Minutes t.Seconds

/// The timings whose names start with one of the prefixes, as a table: count, median, p90, p99, max, total.
let private printTimings (title: string) (timings: JsonObject) (prefixes: string list) (limit: int) =
    // durations (_ms) or other values (sizes, counts per session), as the title says
    let durations = title.Contains "(ms"
    let rows =
        timings
        |> Seq.filter (fun kv -> prefixes |> List.exists (fun p -> kv.Key.StartsWith p) && kv.Key.EndsWith "_ms" = durations)
        |> Seq.sortByDescending (fun kv -> num kv.Value.["total"])
        |> Seq.truncate limit
        |> List.ofSeq
    if not rows.IsEmpty then
        PrintHelpers.text 2 "%s" title
        let width = rows |> List.map (fun kv -> kv.Key.Length) |> List.max |> max 10
        PrintHelpers.text 4 "%-*s %8s %9s %9s %9s %9s %11s" width "" "count" "median" "p90" "p99" "max" "total"
        for kv in rows do
            let t = kv.Value
            PrintHelpers.text 4 "%-*s %8.0f %9.1f %9.1f %9.1f %9.1f %11.0f" width kv.Key (num t.["count"]) (num t.["p50"]) (num t.["p90"]) (num t.["p99"]) (num t.["max"]) (num t.["total"])
        PrintHelpers.text 0 ""

let private printCounters (title: string) (counters: JsonObject) (prefixes: string list) (limit: int) =
    let rows =
        counters
        |> Seq.filter (fun kv -> prefixes |> List.exists (fun p -> kv.Key.StartsWith p))
        |> Seq.sortByDescending (fun kv -> num kv.Value)
        |> Seq.truncate limit
        |> List.ofSeq
    if not rows.IsEmpty then
        PrintHelpers.text 2 "%s" title
        let width = rows |> List.map (fun kv -> kv.Key.Length) |> List.max
        for kv in rows do PrintHelpers.text 4 "%-*s %10.0f" width kv.Key (num kv.Value)
        PrintHelpers.text 0 ""

/// Prints the statistics of one booster. Durations are in milliseconds (names ending with _ms).
let print (header: string) (stats: JsonObject) =
    let b = stats.["booster"]
    let timings = match stats.["timings"] with :? JsonObject as o -> o | _ -> JsonObject()
    let counters = match stats.["counters"] with :? JsonObject as o -> o | _ -> JsonObject()
    PrintHelpers.text 0 "%s" header
    if not (isNull b) then
        let extensions = match b.["extensions"] with :? JsonArray as a when a.Count > 0 -> a |> Seq.map str |> String.concat ", " | _ -> "none"
        PrintHelpers.text 2 "version %s, up %s, %.0f MB (managed %.0f MB), %s/%s projects kept for agents, extensions: %s"
            (str b.["version"]) (duration (num b.["uptime_s"])) (num b.["working_set_mb"]) (num b.["managed_heap_mb"])
            (str b.["projects_kept"]) (str b.["projects_max"]) extensions
    PrintHelpers.text 0 ""
    printTimings "Messages (ms: wait in the queue, processing, total)" timings [ "msg." ] 30
    printTimings "Compilation stages (ms)" timings [ "compile.stage." ] 15
    printTimings "Agent tools in the booster (ms)" timings [ "tool." ] 30
    printTimings "Agent requests, end to end in wsfscservice --mcp (ms)" timings [ "mcp.request."; "mcp.call." ] 30
    printTimings "Extensions (ms)" timings [ "ext." ] 20
    printTimings "Agent sessions (duration in seconds, tool calls)" timings [ "mcp.session." ] 5
    printTimings "Answer sizes (characters)" timings [ "msg."; "tool." ] 30
    printTimings "Extension values" timings [ "ext." ] 20
    printCounters "Agents" counters [ "mcp.sessions"; "mcp.client."; "mcp.protocol." ] 20
    printCounters "Agent tool outcomes" counters [ "tool."; "mcp.call." ] 40
    printCounters "Pointers in answers, and whether agents followed them" counters [ "mcp.pointer." ] 20
    printCounters "Most frequent tool sequences" counters [ "mcp.sequence." ] 10
    printCounters "Compilation results and diagnostics" counters [ "msg.compile.exit."; "msg.replay."; "compile.error."; "compile.warning." ] 25
    printCounters "Extensions and statistics" counters [ "ext."; "extensions."; "agent."; "stats." ] 30

let run (version: string option) (json: bool) =
    let running =
        Boosters.running ()
        |> List.filter (fun b -> b.Kind = Boosters.Registered)
        |> List.filter (fun b -> match version with Some v -> Boosters.matchesVersion v b | None -> true)
    let fromRunning =
        running |> List.map (fun b ->
            match request b with
            | Result.Ok s -> Result.Ok (sprintf "Booster %s (running, PID %i)" (CompilerCommand.formatBooster b) b.Pid, s)
            | Result.Error e ->
                match readFile (statsPath b.Folder) with
                | Some s -> Result.Ok (sprintf "Booster %s (running, PID %i; saved statistics: %s)" (CompilerCommand.formatBooster b) b.Pid e, s)
                | None -> Result.Error (sprintf "Booster %s: no statistics, %s" (CompilerCommand.formatBooster b) e))
    let fromFiles =
        saved running
        |> List.filter (fun (_, s) -> match version with Some v -> String.Equals(str s.["booster"].["version"], v, StringComparison.OrdinalIgnoreCase) | None -> true)
        |> List.map (fun (path, s) -> Result.Ok (sprintf "Booster %s (not running, saved %s)" (str s.["booster"].["version"]) (File.GetLastWriteTime(path).ToString "yyyy-MM-dd HH:mm"), s))
    match fromRunning @ fromFiles with
    | [] ->
        PrintHelpers.text 0 "No booster statistics: no WebSharper 11 booster is running or has saved any."
        0
    | all ->
        if json then
            let a = JsonArray()
            for r in all do
                match r with
                | Result.Ok (header, s) ->
                    let o = s.DeepClone().AsObject()
                    o.["source"] <- JsonValue.Create header
                    a.Add o
                | Result.Error _ -> ()
            printfn "%s" (a.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)))
        else
            for r in all do
                match r with
                | Result.Ok (header, s) -> print header s
                | Result.Error e -> PrintHelpers.error 0 "%s" e
        if all |> List.forall Result.isError then 1 else 0
