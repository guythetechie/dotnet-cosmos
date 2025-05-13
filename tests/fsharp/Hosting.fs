namespace common.tests

open System
open System.Diagnostics
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Hosting

[<RequireQualifiedAccess>]
module Configuration =
    let getValue key (configuration: IConfiguration) =
        let section = configuration.GetSection(key)

        if section.Exists() then
            Option.ofObj section.Value
        else
            None

    let getRequiredValue key configuration =
        configuration
        |> getValue key
        |> Option.defaultWith (fun () -> failwithf $"Configuration key '{key}' not found.")

    // Adds user secrets to the configuration builder. Added user secrets will have the lowest priority.
    let addUserSecrets assembly (builder: IConfigurationBuilder) =
        let newSources =
            ConfigurationBuilder().AddUserSecrets(assembly, optional = true).Sources
            |> List.ofSeq

        let existingSources = builder.Sources |> List.ofSeq

        builder.Sources.Clear()

        newSources @ existingSources
        |> Seq.iter (fun source -> builder.Sources.Add(source) |> ignore)

        builder

[<RequireQualifiedAccess>]
module HostApplicationBuilder =
    let tryAddSingleton<'a when 'a: not null and 'a: not struct>
        (f: IServiceProvider -> 'a)
        (builder: IHostApplicationBuilder)
        =
        builder.Services.TryAddSingleton<'a>(Func<IServiceProvider, 'a>(f))
        builder

[<RequireQualifiedAccess>]
module ServiceProvider =
    let getService<'a when 'a: not null> (provider: IServiceProvider) = provider.GetRequiredService<'a>()

[<RequireQualifiedAccess>]
module Host =
    let start (host: IHost) =
        async {
            let! cancellationToken = Async.CancellationToken
            do! host.StartAsync(cancellationToken) |> Async.AwaitTask
        }

[<RequireQualifiedAccess>]
module ActivitySource =
    let configureBuilder (name: string) builder =
        builder
        |> HostApplicationBuilder.tryAddSingleton (fun _ -> new ActivitySource(name))
