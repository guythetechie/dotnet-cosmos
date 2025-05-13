using Aspire.Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace common.tests;

internal static class CosmosModule
{
    public static void ConfigureDatabase(IHostApplicationBuilder builder)
    {
        ConfigureCosmosClient(builder);
        builder.Services.TryAddSingleton(GetDatabase);
    }

    private static Database GetDatabase(IServiceProvider provider)
    {
        var client = provider.GetRequiredService<CosmosClient>();
        var configuration = provider.GetRequiredService<IConfiguration>();

        var databaseName = configuration.GetValue("CSHARP_COSMOS_DATABASE_NAME")
                                        .IfNone(() => configuration.GetRequiredValue("COSMOS_DATABASE_NAME"));

        return client.GetDatabase(databaseName);
    }

    private static void ConfigureCosmosClient(IHostApplicationBuilder builder)
    {
        var connectionName = builder.Configuration
                                    .GetValue("COSMOS_CONNECTION_NAME")
                                    .IfNone(string.Empty);

        builder.AddAzureCosmosClient(connectionName, configureSettings, configureClientOptions);

        void configureSettings(MicrosoftAzureCosmosSettings settings) =>
            builder.Configuration
                   .GetValue("COSMOS_CONNECTION_STRING")
                   .Iter(connectionString => settings.ConnectionString = connectionString);

        void configureClientOptions(CosmosClientOptions options)
        {
            options.EnableContentResponseOnWrite = false;
            options.UseSystemTextJsonSerializerWithOptions = JsonSerializerOptions.Web;
            options.CosmosClientTelemetryOptions = new CosmosClientTelemetryOptions { DisableDistributedTracing = false };
        }
    }
}
