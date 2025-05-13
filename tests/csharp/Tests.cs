using CsCheck;
using FluentAssertions;
using LanguageExt;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common.tests;

internal delegate ValueTask RunTests(CancellationToken cancellationToken);

internal static class TestsModule
{
    public static void ConfigureRunTests(IHostApplicationBuilder builder)
    {
        CosmosModule.ConfigureDatabase(builder);

        builder.Services.TryAddSingleton(GetRunTests);
    }

    private static RunTests GetRunTests(IServiceProvider provider)
    {
        var database = provider.GetRequiredService<Database>();
        var activitySource = provider.GetRequiredService<ActivitySource>();

        return async cancellationToken =>
        {
            int i = 0;

            var gen = TestFixture.Generate();

            await gen.SampleAsync(async fixture =>
            {
                var index = Interlocked.Increment(ref i);
                using var _ = activitySource.StartActivity($"test-{index}");
                await test(fixture, cancellationToken);
            }, iter: 1);
        };

        async ValueTask test(TestFixture fixture, CancellationToken cancellationToken)
        {
            // Create a container for the test
            var container = await CreateContainer(fixture.ContainerName, database, activitySource, cancellationToken);

            // Read the record from the container; should not exist
            var id = fixture.Record
                            .GetStringProperty("id")
                            .ThrowIfFail();

            var readResult = await container.ReadRecord(id, activitySource, cancellationToken);
            readResult.Should().BeLeft().Which.Should().Be(CosmosError.NotFound.Instance);

            // Create a new record; should succeed
            var createResult = await container.CreateRecord(fixture.Record, activitySource, cancellationToken);
            createResult.Should().BeRight().Which.Should().Be(Unit.Default);

            // Read the record again; should succeed
            readResult = await container.ReadRecord(id, activitySource, cancellationToken);
            var readRecord = readResult.Should().BeRight().Subject;

            // Create the same record again; should fail with conflict
            createResult = await container.CreateRecord(fixture.Record, activitySource, cancellationToken);
            createResult.Should().BeLeft().Which.Should().Be(CosmosError.AlreadyExists.Instance);

            // Set the property with the wrong eTag; should fail
            var setResult = await container.SetProperty(id, fixture.InvalidETag, fixture.PropertyName, fixture.PropertyValue, activitySource, cancellationToken);
            setResult.Should().BeLeft().Which.Should().Be(CosmosError.ETagMismatch.Instance);

            // Set the property with the correct eTag; should succeed
            var eTag = Cosmos.GetETag(readRecord).ThrowIfFail();
            setResult = await container.SetProperty(id, eTag, fixture.PropertyName, fixture.PropertyValue, activitySource, cancellationToken);
            setResult.Should().BeRight().Which.Should().Be(Unit.Default);

            // Read the record again; should succeed with the updated property
            readResult = await container.ReadRecord(id, activitySource, cancellationToken);
            readRecord = readResult.Should().BeRight().Subject;
            readRecord.GetProperty(fixture.PropertyName).Should().BeSuccess().Which.Should().BeEquivalentTo(fixture.PropertyValue);

            // Remove the property with the wrong eTag; should fail
            var removeResult = await container.RemoveProperty(id, fixture.InvalidETag, fixture.PropertyName, activitySource, cancellationToken);
            removeResult.Should().BeLeft().Which.Should().Be(CosmosError.ETagMismatch.Instance);

            // Remove the property with the correct eTag; should succeed
            eTag = Cosmos.GetETag(readRecord).ThrowIfFail();
            removeResult = await container.RemoveProperty(id, eTag, fixture.PropertyName, activitySource, cancellationToken);
            removeResult.Should().BeRight().Which.Should().Be(Unit.Default);

            // Read the record again; should not have the property
            readResult = await container.ReadRecord(id, activitySource, cancellationToken);
            readRecord = readResult.Should().BeRight().Subject;
            readRecord.GetProperty(fixture.PropertyName).Should().BeError();

            // Delete the container
            await container.Delete(activitySource, cancellationToken);
        }
    }

    private static async ValueTask<Container> CreateContainer(string containerName, Database database, ActivitySource activitySource, CancellationToken cancellationToken)
    {
        using var activity = activitySource.StartActivity("create.container")
                                          ?.SetTag("container.name", containerName);

        return await database.CreateContainer(containerName, "/id", cancellationToken);
    }

    extension(Database database)
    {
#pragma warning disable CA1822 // Mark members as static. Suppression required due to bug https://github.com/dotnet/roslyn-analyzers/issues/7646
        public async ValueTask<Container> CreateContainer(string containerName, string partitionKeyPath, CancellationToken cancellationToken)
#pragma warning restore CA1822 // Mark members as static
        {
            var containerProperties = new ContainerProperties(containerName, partitionKeyPath);

            return await database.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken: cancellationToken);
        }
    }

#pragma warning disable CA1822 // Mark members as static. Suppression required due to bug https://github.com/dotnet/roslyn-analyzers/issues/7646
    extension(Container container)
    {
        private async ValueTask<Either<CosmosError, Unit>> CreateRecord(JsonObject record, ActivitySource activitySource, CancellationToken cancellationToken)
        {
            using var activity = activitySource.StartActivity("create.record")
                                              ?.SetTag("container.name", container.Id)
                                              ?.SetTag("record", record.ToJsonString());

            var id = record.GetStringProperty("id")
                           .ThrowIfFail();

            var partitionKey = new PartitionKey(id);

            return await Cosmos.CreateRecord(container, partitionKey, record, cancellationToken);
        }

        private async ValueTask<Either<CosmosError, JsonObject>> ReadRecord(string id, ActivitySource activitySource, CancellationToken cancellationToken)
        {
            using var activity = activitySource.StartActivity("read.record")
                                              ?.SetTag("container.name", container.Id)
                                              ?.SetTag("id", id.ToString());

            var cosmosId = CosmosId.FromString(id).ThrowIfFail();
            var partitionKey = new PartitionKey(id);

            return await Cosmos.ReadRecord(container, partitionKey, cosmosId, cancellationToken);
        }

        private async ValueTask<Either<CosmosError, Unit>> RemoveProperty(string id, ETag eTag, string propertyName, ActivitySource activitySource, CancellationToken cancellationToken)
        {
            using var activity = activitySource.StartActivity("remove.property")
                                              ?.SetTag("container.name", container.Id)
                                              ?.SetTag("id", id.ToString())
                                              ?.SetTag("eTag", eTag.ToString())
                                              ?.SetTag("property.name", propertyName);

            var cosmosId = CosmosId.FromString(id).ThrowIfFail();
            var partitionKey = new PartitionKey(id);

            return await Cosmos.RemoveRecordProperty(container, partitionKey, cosmosId, eTag, propertyName, cancellationToken);
        }

        private async ValueTask<Either<CosmosError, Unit>> SetProperty(string id, ETag eTag, string propertyName, JsonValue value, ActivitySource activitySource, CancellationToken cancellationToken)
        {
            using var activity = activitySource.StartActivity("set.property")
                                              ?.SetTag("container.name", container.Id)
                                              ?.SetTag("id", id.ToString())
                                              ?.SetTag("eTag", eTag.ToString())
                                              ?.SetTag("property.name", propertyName);

            var cosmosId = CosmosId.FromString(id).ThrowIfFail();
            var partitionKey = new PartitionKey(id);

            return await Cosmos.SetRecordProperty(container, partitionKey, cosmosId, eTag, propertyName, value, cancellationToken);
        }

        private async ValueTask Delete(ActivitySource source, CancellationToken cancellationToken)
        {
            using var activity = source.StartActivity("delete.container")
                                      ?.SetTag("container.name", container.Id);

            await container.DeleteContainerAsync(cancellationToken: cancellationToken);
        }
    }
#pragma warning restore CA1822 // Mark members as static
}

file sealed record TestFixture
{
    public required string ContainerName { get; init; }
    public required JsonObject Record { get; init; }
    public required string PropertyName { get; init; }
    public required JsonValue PropertyValue { get; init; }
    public required ETag InvalidETag { get; init; }

    public static Gen<TestFixture> Generate() =>
        from containerName in
            from guid in Gen.Guid
            let formattedString = guid.ToString().Replace("-", string.Empty).Take(15)
            from shuffledFormattedString in Gen.Shuffle(formattedString.ToArray())
            select new string(shuffledFormattedString)
        from record in
            from json in Generator.JsonObject
            from id in JsonValueGenerator.Guid
            select json.SetProperty("id", id)
        let propertyName = "property"
        from propertyValue in Generator.JsonValue
        from invalidETag in
            from guid in Gen.Guid
            select ETag.FromString(guid.ToString())
                       .ThrowIfFail()
        select new TestFixture
        {
            ContainerName = containerName,
            Record = record,
            PropertyName = propertyName,
            PropertyValue = propertyValue,
            InvalidETag = invalidETag
        };
}