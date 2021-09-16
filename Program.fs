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

[<EntryPoint>]
let main argv =
    if argv.Length = 0 then
        printfn """
dotnet-ws is a dotnet tool for WebSharper. Available commands:
  stop       Stops all running wsfscservice instances
"""
        1
    elif argv.Length > 1 then
        (printfn """
Wrong number of parameters (%i)

dotnet-ws is a dotnet tool for WebSharper. Available commands:
  stop       Stops all running wsfscservice instances
""" argv.Length)
        1
    elif argv.[0] <> "stop" then
        printfn """
Unrecognized parameter

dotnet-ws is a dotnet tool for WebSharper. Available commands:
  stop       Stops all running wsfscservice instances
"""
        1
    else 
        let tryKill (returnCode, successfulErrorPrint) (x: Process) =
            try
                x.Kill()
                printfn "Stopped wsfscservice with PID: (%i)" x.Id
                (returnCode, successfulErrorPrint)
            with
            | _ ->
                try
                    printfn "Couldn't kill process with PID: (%i)" x.Id
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