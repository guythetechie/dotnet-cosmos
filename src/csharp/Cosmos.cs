using LanguageExt;
using LanguageExt.Common;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Azure.Cosmos;
using OpenTelemetry.Trace;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace common;

public sealed record ETag
{
    private readonly string value;

    private ETag(string value)
    {
        this.value = value;
    }

    public static Fin<ETag> FromString(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.New("ETag cannot be null or whitespace.")
            : new ETag(value);

    public static ETag Generate() => new($"\"{Guid.NewGuid()}\"");

    public static ETag All => new("\"*\"");

    public static string ToString(ETag etag) => etag.value;

    public override string ToString() => value;
}

public sealed record ContinuationToken
{
    private readonly string value;

    private ContinuationToken(string value)
    {
        this.value = value;
    }

    public static Fin<ContinuationToken> FromString(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.New("Continuation token cannot be null or whitespace.")
            : new ContinuationToken(value);

    public static string ToString(ContinuationToken token) => token.value;
}

public abstract record CosmosError
{
    private CosmosError() { }

    public sealed record AlreadyExists : CosmosError
    {
        private AlreadyExists() { }

        public static AlreadyExists Instance { get; } = new();
    }

    public sealed record NotFound : CosmosError
    {
        private NotFound() { }

        public static NotFound Instance { get; } = new();
    }

    public sealed record ETagMismatch : CosmosError
    {
        private ETagMismatch() { }

        public static ETagMismatch Instance { get; } = new();
    }
}

public sealed record CosmosId
{
    private readonly string value;

    private CosmosId(string value)
    {
        this.value = value;
    }

    public static Fin<CosmosId> FromString(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.New("Cosmos ID cannot be null or whitespace.")
            : new CosmosId(value);

    public static CosmosId Generate() => new(Guid.CreateVersion7().ToString());

    public static string ToString(CosmosId id) => id.value;

    public override string ToString() => value;
}

public record CosmosRecord<T>
{
    public required CosmosId Id { get; init; }
    public required ETag ETag { get; init; }
    public required PartitionKey PartitionKey { get; init; }
    public required T Record { get; init; }
}

public record CosmosQueryOptions
{
    public required QueryDefinition Query { get; init; }
    public Option<ContinuationToken> ContinuationToken { get; init; } = Option<ContinuationToken>.None;
    public Option<PartitionKey> PartitionKey { get; init; } = Option<PartitionKey>.None;

    public static CosmosQueryOptions FromQueryString(string query) =>
        new()
        {
            Query = new QueryDefinition(query)
        };

    public CosmosQueryOptions SetQueryString(string query) =>
        this with { Query = new QueryDefinition(query) };

    public CosmosQueryOptions SetQueryParameter(string parameterName, object parameterValue) =>
        this with { Query = Query.WithParameter(parameterName, parameterValue) };
}

public static class Cosmos
{
    public static JsonResult<CosmosId> GetId(JsonObject json) =>
        json.GetStringProperty("id")
            .Bind(id => JsonResult.FromFin(CosmosId.FromString(id)));

    public static JsonResult<ETag> GetETag(JsonObject json) =>
        json.GetStringProperty("_etag")
            .Bind(etag => JsonResult.FromFin(ETag.FromString(etag)));

    private static JsonResult<ImmutableArray<JsonObject>> GetDocumentsFromResponseJson(JsonNode json) =>
        (JsonResult<ImmutableArray<JsonObject>>)
        (from jsonObject in json.AsJsonObject()
         from jsonArray in jsonObject.GetJsonArrayProperty("Documents")
         from jsonObjects in jsonArray.GetJsonObjects()
         select jsonObjects);

    private static async Task<JsonResult<ImmutableArray<JsonObject>>> GetDocumentsFromResponse(ResponseMessage response, CancellationToken cancellationToken)
    {
        var result = await JsonNodeModule.From(response.Content, cancellationToken: cancellationToken);
        return result.Bind(GetDocumentsFromResponseJson);
    }

    private static async Task<(IEnumerable<JsonObject> Documents, Option<ContinuationToken> Token)> GetCurrentPageResults(FeedIterator iterator, CancellationToken cancellationToken)
    {
        if (!iterator.HasMoreResults)
            return (Enumerable.Empty<JsonObject>(), Option<ContinuationToken>.None);

        using var response = await iterator.ReadNextAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var documents = await GetDocumentsFromResponse(response, cancellationToken);

        Option<ContinuationToken> continuationToken = Option<ContinuationToken>.None;
        if (!string.IsNullOrEmpty(response.ContinuationToken))
        {
            continuationToken = ContinuationToken.FromString(response.ContinuationToken)
                .Match(token => Option<ContinuationToken>.Some(token), _ => Option<ContinuationToken>.None);
        }

        return (documents.ThrowIfFail(), continuationToken);
    }

    private static async IAsyncEnumerable<JsonObject> GetFeedIteratorResults(FeedIterator iterator, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Option<ContinuationToken> continuationToken;

        do
        {
            (var documents, continuationToken) = await GetCurrentPageResults(iterator, cancellationToken);

            foreach (var document in documents)
            {
                yield return document;
            }
        }
        while (continuationToken.IsSome);
    }

    private static FeedIterator GetFeedIterator(Container container, CosmosQueryOptions options)
    {
        var continuationToken = options.ContinuationToken
                                       .Select(ContinuationToken.ToString)
                                       .ValueUnsafe();

        var queryRequestOptions = new QueryRequestOptions();
        options.PartitionKey.Iter(partitionKey => queryRequestOptions.PartitionKey = partitionKey);

        return container.GetItemQueryStreamIterator(options.Query, continuationToken, queryRequestOptions);
    }

    public static IAsyncEnumerable<JsonObject> GetQueryResults(Container container, CosmosQueryOptions query, CancellationToken cancellationToken)
    {
        var iterator = GetFeedIterator(container, query);
        return GetFeedIteratorResults(iterator, cancellationToken);
    }

    public static async Task<Either<CosmosError, JsonObject>> ReadRecord(Container container, PartitionKey partitionKey, CosmosId id, CancellationToken cancellationToken)
    {
        using var response = await container.ReadItemStreamAsync(id.ToString(), partitionKey, cancellationToken: cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return CosmosError.NotFound.Instance;
            default:
                response.EnsureSuccessStatusCode();

                var result = from jsonNode in await JsonNodeModule.From(response.Content, cancellationToken: cancellationToken)
                             from jsonObject in jsonNode.AsJsonObject()
                             select jsonObject;

                return result.ThrowIfFail();
        }
    }

    public static async Task<Either<CosmosError, Unit>> CreateRecord<T>(Container container, PartitionKey partitionKey, T record, CancellationToken cancellationToken)
    {
        using var stream = BinaryData.FromObjectAsJson(record).ToStream();
        var options = new ItemRequestOptions { IfNoneMatchEtag = "*" };
        using var response = await container.CreateItemStreamAsync(stream, partitionKey, options, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Conflict:
                return CosmosError.AlreadyExists.Instance;
            default:
                response.EnsureSuccessStatusCode();

                return Unit.Default;
        }
    }

    public static async Task<Either<CosmosError, Unit>> PatchRecord(Container container, PartitionKey partitionKey, CosmosId id, ETag eTag, IEnumerable<PatchOperation> patchOperations, CancellationToken cancellationToken)
    {
        var options = new PatchItemRequestOptions { IfMatchEtag = eTag.ToString() };

        using var response = await container.PatchItemStreamAsync(id.ToString(), partitionKey, [.. patchOperations], options, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.PreconditionFailed:
                return CosmosError.ETagMismatch.Instance;
            default:
                response.EnsureSuccessStatusCode();

                return Unit.Default;
        }
    }

    public static Task<Either<CosmosError, Unit>> SetRecordProperty(Container container, PartitionKey partitionKey, CosmosId id, ETag eTag, string propertyName, object value, CancellationToken cancellationToken) =>
        PatchRecord(container, partitionKey, id, eTag, [PatchOperation.Set($"/{propertyName}", value)], cancellationToken);

    public static Task<Either<CosmosError, Unit>> RemoveRecordProperty(Container container, PartitionKey partitionKey, CosmosId id, ETag eTag, string propertyName, CancellationToken cancellationToken) =>
        PatchRecord(container, partitionKey, id, eTag, [PatchOperation.Remove($"/{propertyName}")], cancellationToken);

    public static async Task<Either<CosmosError, Unit>> DeleteRecord(Container container, PartitionKey partitionKey, CosmosId id, ETag eTag, CancellationToken cancellationToken)
    {
        var options = new ItemRequestOptions { IfMatchEtag = eTag.ToString() };

        using var response = await container.DeleteItemStreamAsync(id.ToString(), partitionKey, options, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.PreconditionFailed:
                return CosmosError.ETagMismatch.Instance;
            default:
                response.EnsureSuccessStatusCode();

                return Unit.Default;
        }
    }

    public static TracerProviderBuilder ConfigureCosmosOpenTelemetryTracing(this TracerProviderBuilder tracing)
    {
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

        return tracing.AddSource("Azure.Cosmos.Operation");
    }
}

