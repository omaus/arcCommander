namespace ArcCommander

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ArcCommander.ArgumentProcessing
open Microsoft.AspNetCore.Http

module Server =

    /// Test-API function
    let numberHandler : HttpHandler =
        let funFunction myInt = $"Your number is {myInt}!"
        fun (next : HttpFunc) (ctx : HttpContext) ->
            task {
                let! number = ctx.BindJsonAsync<int>()
                let nextNumber = funFunction number
                // das machen wir so!
                return! json {| ``is this your number?`` = nextNumber |} next ctx
            }

    /// Endpoints
    let webApp =
        choose [
            GET >=> choose [
                route "/ping" >=> text "pong"
            ]
            POST >=> choose [
                route "/ping" >=> numberHandler
            ]
            subRoute "/arc" (
                choose [
                    POST >=> choose [
                        route "/get" >=> ArcAPIHandler.isaJsonToARCHandler
                        route "/init" >=> ArcAPIHandler.arcInitHandler
                        route "/import" >=> ArcAPIHandler.arcInitHandler
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