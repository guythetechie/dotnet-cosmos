module common.tests.Program

open System
open System.Diagnostics
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open common
open System.Reflection

let private applicationName = "common.tests"

let private runHost (host: IHost) =
    let services = host.Services

    let applicationLifetime =
        ServiceProvider.getService<IHostApplicationLifetime> services

    let cancellationToken = applicationLifetime.ApplicationStopping
    let activitySource = ServiceProvider.getService<ActivitySource> services
    let logger = ServiceProvider.getService<ILogger> services
    let runTests = ServiceProvider.getService<RunTests> services

    try
        try
            let computation =
                async {
                    use _ = Activity.fromSource applicationName activitySource
                    do! Host.start host
                    do! runTests ()
                }

            Async.RunSynchronously(computation, cancellationToken = cancellationToken)
            0
        with exn ->
            logger.LogCritical(exn, "Tests failed.")
            Environment.ExitCode <- -1
            reraise ()
            -1
    finally
        applicationLifetime.StopApplication()

let private getLogger provider =
    ServiceProvider.getService<ILoggerFactory> provider
    |> _.CreateLogger("common.tests")

let private configureLogging (builder: IHostApplicationBuilder) =
    builder |> HostApplicationBuilder.tryAddSingleton<ILogger> getLogger

let private configureConfiguration (builder: IHostApplicationBuilder) =
    Configuration.addUserSecrets (Assembly.GetExecutingAssembly()) builder.Configuration
    |> ignore

    builder

let private configureBuilder builder =
    builder
    |> configureConfiguration
    |> configureLogging
    |> ActivitySource.configureBuilder applicationName
    |> OpenTelemetry.configureBuilder
    |> Tests.configureRunTests

let private getHost (args: string[]) =
    let builder = Host.CreateApplicationBuilder(args)
    configureBuilder builder |> ignore
    builder.Build()

[<EntryPoint>]
let main args =
    use host = getHost args
    runHost host
