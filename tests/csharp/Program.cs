namespace common.tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

internal static class Program
{
    private const string applicationName = "common.tests";

    public static async Task Main(string[] args)
    {
        using var host = GetHost(args);
        await RunHost(host);
    }

    private static IHost GetHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureBuilder(builder);
        return builder.Build();
    }

    private static void ConfigureBuilder(IHostApplicationBuilder builder)
    {
        ConfigureConfiguration(builder);
        ConfigureLogger(builder);
        ActivitySourceModule.ConfigureBuilder(builder, applicationName);
        OpenTelemetryModule.ConfigureOpenTelemetry(builder);
        TestsModule.ConfigureRunTests(builder);
    }

    private static void ConfigureConfiguration(IHostApplicationBuilder builder) =>
        builder.Configuration.AddUserSecretsWithLowestPriority(typeof(Program).Assembly);

    private static void ConfigureLogger(IHostApplicationBuilder builder) =>
        builder.Services.TryAddSingleton(GetLogger);

    private static ILogger GetLogger(IServiceProvider provider) =>
        provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(applicationName);

    private static async ValueTask RunHost(IHost host)
    {
        var services = host.Services;
        var applicationLifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var cancellationToken = applicationLifetime.ApplicationStopping;
        var logger = services.GetRequiredService<ILogger>();

        try
        {
            await host.StartAsync(cancellationToken);
            var runTests = services.GetRequiredService<RunTests>();
            await runTests(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Tests failed.");
            Environment.ExitCode = -1;
            throw;
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}