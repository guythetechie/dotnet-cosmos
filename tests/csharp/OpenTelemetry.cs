using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Diagnostics;

namespace common.tests;

internal static class ActivitySourceModule
{
    public static IHostApplicationBuilder ConfigureBuilder(IHostApplicationBuilder builder, string name)
    {
        builder.Services.TryAddSingleton(_ => new ActivitySource(name));

        return builder;
    }
}

internal static class ActivityModule
{
    public static Activity? FromSource(ActivitySource source, string name) =>
        source.StartActivity(name);
}

internal static class OpenTelemetryModule
{
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Services
               .AddOpenTelemetry()
               .SetDestination(builder.Configuration)
               .WithMetrics(ConfigureMetrics)
               .WithTracing(ConfigureTracing);

        return builder;
    }

    private static IOpenTelemetryBuilder SetDestination(this IOpenTelemetryBuilder builder, IConfiguration configuration)
    {
        configuration.GetValue("APPLICATION_INSIGHTS_CONNECTION_STRING")
                     .Iter(connectionString =>
                     {
                         switch (builder)
                         {
                             case OpenTelemetryBuilder openTelemetryBuilder:
                                 openTelemetryBuilder.UseAzureMonitor();
                                 break;
                         }
                     });

        configuration.GetValue("OTEL_EXPORTER_OTLP_ENDPOINT")
                     .Iter(endpoint => builder.UseOtlpExporter());

        return builder;
    }

    private static void ConfigureMetrics(MeterProviderBuilder builder) =>
        builder.AddHttpClientInstrumentation()
               .AddAspNetCoreInstrumentation();

    private static void ConfigureTracing(TracerProviderBuilder builder) =>
        builder.AddHttpClientInstrumentation()
               .AddAspNetCoreInstrumentation()
               .AddSource("MyCompany.MyProduct.MyLibrary")
               .ConfigureCosmosOpenTelemetryTracing();
}