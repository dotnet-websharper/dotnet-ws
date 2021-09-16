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

type Argument =
    | [<CliPrefix(CliPrefix.None)>] Start of ParseResults<StartArguments>
    | [<CliPrefix(CliPrefix.None)>] Stop of ParseResults<StopArguments>
    | [<CliPrefix(CliPrefix.None); SubCommand>] List

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Start _ -> "Starts wsfscservice with the given version."
            | Stop _ -> "Sends a stop signal for the wsfscservice with the given version. If no version given it's all running instances. If --force given kills the process instead of stop signal."
            | List -> "Lists running wsfscservice versions."
and StartArguments =
    | Version of ver:string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Version _ -> "wsfscservice version"

and StopArguments =
    | Version of ver:string
    | Force

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Version _ -> "wsfscservice version. If empty all running instances"
            | Force -> "kills the service instead of sending stop signal"

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Argument>(programName = "dotnet-ws.exe", helpTextMessage = "dotnet-ws is a dotnet tool for WebSharper", checkStructure = true)
    try
        let result = parser.Parse argv
        let subCommand = result.GetSubCommand()
        match subCommand with
        | Start _ -> 0
        | Stop stopParams -> 
            let tryKill (returnCode, successfulErrorPrint) (proc: Process) =
                try
                    let killOrSend (x: Process) = 
                        if stopParams.Contains Force then
                            x.Kill()
                            printfn "Stopped wsfscservice with PID: (%i)" x.Id
                    let version = stopParams.TryGetResult Version
                    match version with
                    | Some v -> 
                        if proc.MainModule.FileVersionInfo.FileVersion = v then
                            killOrSend proc
                    | None ->
                        killOrSend proc
                    (returnCode, successfulErrorPrint)
                with
                | _ ->
                    try
                        printfn "Couldn't kill process with PID: (%i)" proc.Id
                        (1, successfulErrorPrint)
                    with
                    | _ -> (1, false)
            try
                let (returnCode, successfulErrorPrint) = Process.GetProcessesByName("wsfscservice") |> Array.fold tryKill (0, true)
                if successfulErrorPrint |> not then
                    printfn "Couldn't read processes"
                returnCode
            with
            | _ -> 1
        | List -> 
            try
                let procInfos = 
                    Process.GetProcessesByName("wsfscservice")
                    |> Array.map (fun proc -> sprintf "version: %s; path: %s" proc.MainModule.FileVersionInfo.FileVersion proc.MainModule.FileName)
                if procInfos.Length = 0 then
                    printfn "There are no wsfscservices running"
                else
                    printfn """The following wsfscservice versions are running:

  %s""" 
                      (String.Join(Environment.NewLine + "  ", procInfos))
                0
            with
            | _ ->
                printfn "Couldn't read processes"
                1
    with
    | :? Argu.ArguParseException as ex -> 
        printfn "%s" (parser.HelpTextMessage.Value + Environment.NewLine + Environment.NewLine + ex.Message)
        1