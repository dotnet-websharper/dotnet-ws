module WebSharper.Compiler.WsFscServiceCommon

open System.Text

type ArgsType = {args: string array}

let md5 = System.Security.Cryptography.MD5.Create()

let hashPath (fullPath: string) =
    let data =
        fullPath.ToLower()
        |> Encoding.UTF8.GetBytes
        |> md5.ComputeHash
    (System.Text.StringBuilder(), data)
    ||> Array.fold (fun sb b -> sb.Append(b.ToString("x2")))
    |> string
