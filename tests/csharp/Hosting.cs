using LanguageExt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace common.tests;

#pragma warning disable CA1822 // Mark members as static: Bug https://github.com/dotnet/roslyn-analyzers/issues/7646
internal static class HostingExtensions
{
    extension(IConfiguration configuration)
    {
        public Option<string> GetValue(string key)
        {
            var section = configuration.GetSection(key);

            return section.Exists()
                    ? string.IsNullOrWhiteSpace(section.Value)
                        ? Option<string>.None
                        : section.Value
                    : Option<string>.None;
        }

        public string GetRequiredValue(string key) =>
            configuration.GetValue(key)
                         .IfNone(() => throw new InvalidOperationException($"Configuration key '{key}' not found."));
    }

    extension(IConfigurationBuilder builder)
    {
        public IConfigurationBuilder AddUserSecretsWithLowestPriority(Assembly assembly)
        {
            var newSources =
                new ConfigurationBuilder()
                .AddUserSecrets(assembly, optional: true)
                .Sources
                .ToList();

            var existingSources = builder.Sources.ToList();

            builder.Sources.Clear();

            foreach (var source in newSources.Concat(existingSources))
            {
                builder.Sources.Add(source);
            }

            return builder;
        }
    }
}
#pragma warning restore CA1822 // Mark members as static