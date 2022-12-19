namespace ArcCommander

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ArcCommander.ArgumentProcessing
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Cors.Infrastructure

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

    /// API function for checking the application's version.
    let versionHandler : HttpHandler =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            task {
                let ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                return! json ver next ctx
            }

    /// Endpoints
    let webApp =
        // TO DO: establish versioning for APIs: e.g. `localhost/api/v1/ping`, "v1" should be the ArcCommander's version
        choose [
            GET >=> choose [
                route "/version" >=> versionHandler
            ]
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
                        route "/import" >=> ArcAPIHandler.arcImportHandler
                    ]
                ]
            )
        ]

    let corsPolicyName = "_myAllowSpecificOrigins"

    let corsPolicyConfig =
        fun (b : CorsPolicyBuilder) ->
            b
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowAnyOrigin()
            |> ignore

    let configureApp (app : IApplicationBuilder) =
        // Add Giraffe to the ASP.NET Core pipeline
        app.UseCors(corsPolicyName) |> ignore
        app.UseGiraffe webApp

    let configureServices (services : IServiceCollection) =
        // Add Giraffe dependencies
        services.AddCors(fun options -> options.AddPolicy(corsPolicyName, corsPolicyConfig)) |> ignore
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