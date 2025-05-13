namespace common

open System
open System.Net
open Microsoft.Azure.Cosmos
open OpenTelemetry.Trace
open FSharpPlus
open FSharp.Control

type ETag =
    private
    | ETag of string

    static member fromString value =
        match String.IsNullOrWhiteSpace value with
        | true -> Error "ETag cannot be null or whitespace."
        | false -> ETag value |> Ok

    static member generate() = $"\"{Guid.NewGuid()}\"" |> ETag

    static member All = ETag "\"*\""

    static member toString(ETag value) = value

type ContinuationToken =
    private
    | ContinuationToken of string

    static member fromString value =
        match String.IsNullOrWhiteSpace value with
        | true -> Error "Continuation token cannot be null or whitespace."
        | false -> ContinuationToken value |> Ok

    static member toString(ContinuationToken value) = value

type CosmosError =
    | AlreadyExists
    | NotFound
    | ETagMismatch

type CosmosId =
    private
    | CosmosId of string

    static member fromString value =
        match String.IsNullOrWhiteSpace value with
        | true -> Error "Cosmos ID cannot be null or whitespace."
        | false -> CosmosId value |> Ok

    static member generate() =
        Guid.CreateVersion7().ToString() |> CosmosId

    static member toString(CosmosId id) = id

type CosmosRecord<'a> =
    { Id: CosmosId
      ETag: ETag
      PartitionKey: PartitionKey
      Record: 'a }

type CosmosQueryOptions =
    { Query: QueryDefinition
      ContinuationToken: ContinuationToken option
      PartitionKey: PartitionKey option }

[<RequireQualifiedAccess>]
module CosmosQueryOptions =
    let fromQueryString query =
        { CosmosQueryOptions.Query = QueryDefinition(query)
          ContinuationToken = None
          PartitionKey = None }

    let setQueryString query options =
        { options with
            Query = QueryDefinition(query) }

    let setQueryParameter parameterName parameterValue options =
        { options with
            Query = options.Query.WithParameter(parameterName, parameterValue) }

[<RequireQualifiedAccess>]
module Cosmos =
    let getId json =
        monad {
            let! idString = JsonObject.getStringProperty "id" json

            match CosmosId.fromString idString with
            | Ok cosmosId -> return cosmosId
            | Error error -> return! JsonResult.failWithMessage error
        }

    let getETag json =
        monad {
            let! etagString = JsonObject.getStringProperty "_etag" json

            match ETag.fromString etagString with
            | Ok etag -> return etag
            | Error error -> return! JsonResult.failWithMessage error
        }

    let private getDocumentsFromResponseJson json =
        json
        |> bind JsonNode.asJsonObject
        |> bind (JsonObject.getJsonArrayProperty "Documents")
        |> bind JsonArray.asJsonObjects

    let private getDocumentsFromResponse (response: ResponseMessage) =
        async {
            let! node = JsonNode.fromStream response.Content

            return getDocumentsFromResponseJson node
        }

    let private getCurrentPageResults (iterator: FeedIterator) =
        async {
            match iterator.HasMoreResults with
            | true ->
                let! cancellationToken = Async.CancellationToken
                use! response = iterator.ReadNextAsync(cancellationToken) |> Async.AwaitTask
                let _ = response.EnsureSuccessStatusCode()

                let! documents = getDocumentsFromResponse response |> Async.map JsonResult.throwIfFail

                let continuationToken =
                    ContinuationToken.fromString response.ContinuationToken |> Result.toOption

                return (documents, continuationToken)
            | false -> return (Seq.empty, None)
        }

    let private getFeedIteratorResults (iterator: FeedIterator) =
        let generator (iterator: FeedIterator) =
            match iterator.HasMoreResults with
            | true -> getCurrentPageResults iterator |> map fst |> map (fun x -> Some(x, iterator))
            | false -> async.Return None

        iterator |> AsyncSeq.unfoldAsync generator |> AsyncSeq.concatSeq

    let private getFeedIterator (container: Container) (options: CosmosQueryOptions) =
        let queryDefinition = options.Query

        let continuationToken =
            options.ContinuationToken
            |> Option.map ContinuationToken.toString
            |> Option.toObj

        let queryRequestOptions =
            let requestOptions = QueryRequestOptions()

            options.PartitionKey
            |> Option.iter (fun partitionKey -> requestOptions.PartitionKey <- partitionKey)

            requestOptions

        container.GetItemQueryStreamIterator(queryDefinition, continuationToken, queryRequestOptions)

    let getQueryResults container query =
        getFeedIterator container query |> getFeedIteratorResults

    let readRecord (container: Container) partitionKey id =
        async {
            let! cancellationToken = Async.CancellationToken
            let id = CosmosId.toString id

            use! response =
                container.ReadItemStreamAsync(id, partitionKey, cancellationToken = cancellationToken)
                |> Async.AwaitTask

            match response.StatusCode with
            | HttpStatusCode.NotFound -> return Error CosmosError.NotFound
            | _ ->
                let _ = response.EnsureSuccessStatusCode()
                let! result = JsonObject.fromStream response.Content
                return JsonResult.throwIfFail result |> Ok
        }

    let createRecord (container: Container) partitionKey record =
        async {
            use stream = BinaryData.FromObjectAsJson(record).ToStream()

            let options =
                let options = ItemRequestOptions()
                options.IfNoneMatchEtag <- "*"
                options

            let! cancellationToken = Async.CancellationToken

            use! response =
                container.CreateItemStreamAsync(stream, partitionKey, options, cancellationToken)
                |> Async.AwaitTask

            match response.StatusCode with
            | HttpStatusCode.Conflict -> return Error CosmosError.AlreadyExists
            | _ ->
                let _ = response.EnsureSuccessStatusCode()
                return Ok()
        }

    let patchRecord (container: Container) partitionKey id eTag patchOperations =
        async {
            let! cancellationToken = Async.CancellationToken

            let id = CosmosId.toString id
            let operations = List.ofSeq patchOperations |> List.map (fun operation -> operation)

            let options =
                let options = PatchItemRequestOptions()
                options.IfMatchEtag <- ETag.toString eTag
                options

            use! response =
                container.PatchItemStreamAsync(id, partitionKey, operations, options, cancellationToken)
                |> Async.AwaitTask

            match response.StatusCode with
            | HttpStatusCode.PreconditionFailed -> return Error CosmosError.ETagMismatch
            | _ ->
                let _ = response.EnsureSuccessStatusCode()
                return Ok()
        }

    let setRecordProperty (container: Container) partitionKey id eTag propertyName value =
        patchRecord container partitionKey id eTag [ PatchOperation.Set($"/{propertyName}", value) ]

    let removeRecordProperty (container: Container) partitionKey id eTag propertyName =
        patchRecord container partitionKey id eTag [ PatchOperation.Remove($"/{propertyName}") ]

    let deleteRecord (container: Container) partitionKey id eTag =
        async {
            let! cancellationToken = Async.CancellationToken

            let id = CosmosId.toString id

            let options =
                let options = ItemRequestOptions()
                options.IfMatchEtag <- ETag.toString eTag
                options

            use! response =
                container.DeleteItemStreamAsync(id, partitionKey, options, cancellationToken)
                |> Async.AwaitTask


            match response.StatusCode with
            | HttpStatusCode.PreconditionFailed -> return Error CosmosError.ETagMismatch
            | _ ->
                let _ = response.EnsureSuccessStatusCode()
                return Ok()
        }

    let configureOpenTelemetryTracing (tracing: TracerProviderBuilder) =
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true)

        tracing.AddSource("Azure.Cosmos.Operation")
