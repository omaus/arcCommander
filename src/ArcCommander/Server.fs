namespace ArcCommander

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ArcCommander.ArgumentProcessing
open Microsoft.AspNetCore.Http
open System.IO
open System.IO.Compression

module Server =

    let funFunction myInt = $"Your number is {myInt}!"

    let numberHandler : HttpHandler =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            task {
                let! number = ctx.BindJsonAsync<int>()
                //use __ = somethingToBeDisposedAtTheEndOfTheRequest
                let nextNumber = funFunction number
                //return! Successful.OK number next ctx
                //return! Successful.OK nextNumber next ctx
                return! json {| asdjkhjkasdh = nextNumber |} next ctx
            }

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
                    with e ->
                        failwithf "Cannot zip stream %s: %s" fileName e.Message
                let res = getByteArray "arc.json" isaJsonBA
                return! ctx.WriteBytesAsync res
            }


    let webApp =
        choose [
            route "/ping"   >=> text "pong"
            GET >=> choose [
                route "/number" >=> text (string 42)
            ]
            POST >=> choose [
                route "/number" >=> numberHandler
            ]
            //route "/"       >=> htmlFile "/APIDocs/IArcAPI.html"
            subRoute "/arc" (
                choose [
                    route "/foo" >=> text "barzzzzzzz"
                    POST >=> choose [
                        route "/get" >=> isaJsonToARCHandler
                    ]
                ]
            )
        ]

    let configureApp (app : IApplicationBuilder) =
        // Add Giraffe to the ASP.NET Core pipeline
        app.UseGiraffe webApp

    let configureServices (services : IServiceCollection) =
        // Add Giraffe dependencies
        services.AddGiraffe() |> ignore

    let start arcConfiguration (arcServerArgs : Map<string,Argument>) =

        let port = 
            tryGetFieldValueByName "Port" arcServerArgs
            |> Option.defaultValue "5000"

        Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(
                fun webHostBuilder ->
                    webHostBuilder
                        .UseUrls([|$"http://*:{port}"|])
                        .Configure(configureApp)
                        .ConfigureServices(configureServices)
                        |> ignore)
            .Build()
            .Run()