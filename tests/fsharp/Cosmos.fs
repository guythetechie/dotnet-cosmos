namespace common.tests

open Aspire.Microsoft.Azure.Cosmos
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open System
open System.Text.Json

[<RequireQualifiedAccess>]
module private Cosmos =
    let private configureCosmosSettings configuration (settings: MicrosoftAzureCosmosSettings) =
        configuration
        |> Configuration.getValue "COSMOS_CONNECTION_STRING"
        |> Option.iter (fun connectionString -> settings.ConnectionString <- connectionString)

    let private configureClientOptions (options: CosmosClientOptions) =
        options.EnableContentResponseOnWrite <- false
        options.UseSystemTextJsonSerializerWithOptions <- JsonSerializerOptions.Web

        options.CosmosClientTelemetryOptions <-
            let options = CosmosClientTelemetryOptions()
            options.DisableDistributedTracing <- false
            options

    let private configureClient (builder: IHostApplicationBuilder) =
        let connectionName =
            builder.Configuration
            |> Configuration.getValue "COSMOS_CONNECTION_NAME"
            |> Option.defaultValue String.Empty

        builder.AddAzureCosmosClient(
            connectionName,
            (configureCosmosSettings builder.Configuration),
            configureClientOptions
        )

        builder

    let private getDatabase (provider: IServiceProvider) =
        let client = provider.GetRequiredService<CosmosClient>()
        let configuration = provider.GetRequiredService<IConfiguration>()

        let databaseName =
            configuration
            |> Configuration.getValue "FSHARP_COSMOS_DATABASE_NAME"
            |> Option.defaultWith (fun () -> Configuration.getRequiredValue "COSMOS_DATABASE_NAME" configuration)

        client.GetDatabase(databaseName)

    let configureDatabase builder =
        builder |> configureClient |> HostApplicationBuilder.tryAddSingleton getDatabase

    let createContainer (containerName: string) (partitionKeyPath: string) (database: Database) =
        async {
            let! cancellationToken = Async.CancellationToken
            let containerProperties = ContainerProperties(containerName, partitionKeyPath)

            let! response =
                database.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken = cancellationToken)
                |> Async.AwaitTask

            return response.Container
        }
