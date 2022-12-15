﻿module ArcAPIHandler

open Giraffe
open Microsoft.AspNetCore.Http
open System
open System.IO
open System.IO.Compression
open ArcCommander
open ArcCommander.APIs
open ISADotNet

let isaJsonToARCHandler : HttpHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        // modified from: https://stackoverflow.com/questions/17232414/creating-a-zip-archive-in-memory-using-system-io-compression
        task {
            let isaJson = ctx.Request.Body
            let ms = new MemoryStream()
            let! _ = isaJson.CopyToAsync(ms)
            let isaJsonBA = ms.ToArray()

            let getByteArray (fileName : string) (data : byte []) =
                try
                    use ms = new MemoryStream()
                    (
                        use archive = new ZipArchive(ms, ZipArchiveMode.Create)
                        let entry = archive.CreateEntry(fileName)
                        use entryStream = entry.Open()
                        use bw = new BinaryWriter(entryStream)
                        bw.Write(data)
                    )
                    (ms.ToArray())
                with e -> failwithf "Cannot zip stream %s: %s" fileName e.Message
            let res = getByteArray "arc.json" isaJsonBA
            return! ctx.WriteBytesAsync res
        }

//type InitConfig = {
//    path    : string
//}

let arcInitHandler : HttpHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let! config = ctx.BindJsonAsync<{|path : string|}>()
            //let arcConfig = IniData.createDefault
            //ArcAPI.init

            return! json config next ctx
        }

let arcImportHandler : HttpHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let! isaJsonString = ctx.BindJsonAsync<string>()

            let tmpDir = Path.GetTempPath()

            //let arcFromIsaJson = 
            
            let byteArc : byte [] = [||]

            return! ctx.WriteBytesAsync byteArc
        }