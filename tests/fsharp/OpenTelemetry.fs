namespace common.tests

open System.Diagnostics
open Azure.Monitor.OpenTelemetry.AspNetCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open OpenTelemetry
open OpenTelemetry.Metrics
open OpenTelemetry.Trace
open FSharpPlus
open common

[<RequireQualifiedAccess>]
module Activity =
    let fromSource (name: string) (activitySource: ActivitySource) = activitySource.StartActivity(name)

    let setTag key value (activity: Activity | null) =
        Option.ofObj activity
        |> Option.map _.SetTag(key, value)
        |> Option.defaultValue Unchecked.defaultof<Activity>

[<RequireQualifiedAccess>]
module OpenTelemetry =
    let private setDestination configuration (builder: IOpenTelemetryBuilder) =
        Configuration.getValue "APPLICATION_INSIGHTS_CONNECTION_STRING" configuration
        |> iter (fun _ ->
            match builder with
            | :? OpenTelemetryBuilder as builder -> builder.UseAzureMonitor() |> ignore
            | _ -> ())

        Configuration.getValue "OTEL_EXPORTER_OTLP_ENDPOINT" configuration
        |> iter (fun _ -> builder.UseOtlpExporter() |> ignore)

        builder

    let private configureMetrics (builder: MeterProviderBuilder) =
        builder.AddHttpClientInstrumentation()
        |> _.AddAspNetCoreInstrumentation()
        |> ignore

    let private configureTracing (builder: TracerProviderBuilder) =
        builder.SetSampler(AlwaysOnSampler())
        |> _.AddHttpClientInstrumentation()
        |> _.AddAspNetCoreInstrumentation()
        |> Cosmos.configureOpenTelemetryTracing
        |> ignore

    let configureBuilder (builder: IHostApplicationBuilder) =
        builder.Services.AddOpenTelemetry()
        |> setDestination builder.Configuration
        |> _.WithMetrics(configureMetrics)
        |> _.WithTracing(configureTracing)
        |> ignore

        builder
