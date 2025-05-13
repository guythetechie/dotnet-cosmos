namespace common.tests

open System
open System.Diagnostics
open Microsoft.Azure.Cosmos
open FSharp.Control
open FsCheck.FSharp
open Faqt
open common

type internal RunTests = unit -> Async<unit>

[<RequireQualifiedAccess>]
module Tests =
    let private deleteContainer (container: Container) activitySource =
        async {
            use _ =
                Activity.fromSource "delete.container" activitySource
                |> Activity.setTag "container.name" container.Id

            let! cancellationToken = Async.CancellationToken

            let! _ =
                container.DeleteContainerAsync(cancellationToken = cancellationToken)
                |> Async.AwaitTask

            return ()
        }

    let private throwIfError result =
        result |> Result.defaultWith (fun error -> failwith error)

    let private removeProperty id eTag propertyName (container: Container) activitySource =
        async {
            use _ =
                Activity.fromSource "remove.property" activitySource
                |> Activity.setTag "container.name" container.Id
                |> Activity.setTag "id" id
                |> Activity.setTag "etag" eTag
                |> Activity.setTag "property.name" propertyName

            let cosmosId = CosmosId.fromString id |> throwIfError
            let partitionKey = PartitionKey id

            return! Cosmos.removeRecordProperty container partitionKey cosmosId eTag propertyName
        }

    let private setProperty id eTag propertyName value (container: Container) activitySource =
        async {
            use _ =
                Activity.fromSource "set.property" activitySource
                |> Activity.setTag "container.name" container.Id
                |> Activity.setTag "id" id
                |> Activity.setTag "etag" eTag
                |> Activity.setTag "property.name" propertyName
                |> Activity.setTag "property.value" value

            let cosmosId = CosmosId.fromString id |> throwIfError
            let partitionKey = PartitionKey id

            return! Cosmos.setRecordProperty container partitionKey cosmosId eTag propertyName value
        }

    let private readRecord id (container: Container) activitySource =
        async {
            use _ =
                Activity.fromSource "read.record" activitySource
                |> Activity.setTag "container.name" container.Id
                |> Activity.setTag "id" id

            let cosmosId = CosmosId.fromString id |> throwIfError
            let partitionKey = PartitionKey id

            return! Cosmos.readRecord container partitionKey cosmosId
        }

    let private createRecord record (container: Container) activitySource =
        async {
            use _ =
                Activity.fromSource "create.record" activitySource
                |> Activity.setTag "container.name" container.Id
                |> Activity.setTag "record" record

            let partitionKey =
                record
                |> JsonObject.getStringProperty "id"
                |> JsonResult.throwIfFail
                |> PartitionKey

            return! Cosmos.createRecord container partitionKey record
        }

    let private createContainer name database activitySource =
        async {
            use _ =
                Activity.fromSource "create.container" activitySource
                |> Activity.setTag "container.name" name

            return! Cosmos.createContainer name "/id" database
        }

    let private getRunTests provider =
        let activitySource = ServiceProvider.getService<ActivitySource> provider
        let database = ServiceProvider.getService<Database> provider

        fun () ->
            async {
                use _ = Activity.fromSource "run.tests" activitySource

                let gen =
                    gen {
                        let! containerName =
                            Gen.guid
                            |> Gen.map (fun guid -> guid.ToString().Replace("-", "") |> Seq.take 15)
                            |> Gen.bind Gen.shuffle
                            |> Gen.map String.Concat

                        let! record =
                            gen {
                                let! jsonObject = Gen.jsonObject
                                let! id = Gen.JsonValue.guid

                                return JsonObject.setProperty "id" id jsonObject
                            }

                        let invalidETag = ETag.fromString (Guid.NewGuid().ToString()) |> throwIfError

                        let propertyName = "property"
                        let! propertyValue = Gen.jsonValue

                        return
                            {| ContainerName = containerName
                               Record = record
                               PropertyName = propertyName
                               PropertyValue = propertyValue
                               InvalidETag = invalidETag |}
                    }

                let testConfig =
                    { TestConfig.Default with
                        MaxTests = Some 1 }

                Check.fromGen gen testConfig (fun x ->
                    async {
                        // Create the container
                        let! container = createContainer x.ContainerName database activitySource

                        // Read the record from the container; should not exist
                        let id = JsonObject.getStringProperty "id" x.Record |> JsonResult.throwIfFail
                        let! result = readRecord id container activitySource
                        result.Should().BeError().WhoseValue.Should().Be(CosmosError.NotFound) |> ignore

                        // Create a new record; should be successful
                        let! result = createRecord x.Record container activitySource
                        result.Should().BeOk() |> ignore

                        // Read the record again; should be successful
                        let! result = readRecord id container activitySource
                        let cosmosRecord = result.Should().BeOk().WhoseValue

                        // Create the record again; should fail with a conflict
                        let! result = createRecord x.Record container activitySource

                        result.Should().BeError().WhoseValue.Should().Be(CosmosError.AlreadyExists)
                        |> ignore

                        // Set the property with the wrong eTag; should fail
                        let! result =
                            setProperty id x.InvalidETag x.PropertyName x.PropertyValue container activitySource

                        result.Should().BeError().WhoseValue.Should().Be(CosmosError.ETagMismatch)
                        |> ignore

                        // Set the property with the correct eTag; should be successful
                        let eTag = Cosmos.getETag cosmosRecord |> JsonResult.throwIfFail
                        let! result = setProperty id eTag x.PropertyName x.PropertyValue container activitySource
                        result.Should().BeOk() |> ignore

                        // Read the record again; should have the property set
                        let! result = readRecord id container activitySource
                        let cosmosRecord = result.Should().BeOk().WhoseValue
                        let propertyValueResult = JsonObject.getProperty x.PropertyName cosmosRecord

                        propertyValueResult
                            .Should()
                            .BeSuccess()
                            .WhoseValue.Should()
                            .BeEquivalentTo(x.PropertyValue)
                        |> ignore

                        // Remove the property with the wrong eTag; should fail
                        let! result = removeProperty id x.InvalidETag x.PropertyName container activitySource

                        result.Should().BeError().WhoseValue.Should().Be(CosmosError.ETagMismatch)
                        |> ignore

                        // Remove the property with the correct eTag; should be successful
                        let eTag = Cosmos.getETag cosmosRecord |> JsonResult.throwIfFail
                        let! result = removeProperty id eTag x.PropertyName container activitySource
                        result.Should().BeOk() |> ignore

                        // Read the record again; should not have the property
                        let! result = readRecord id container activitySource
                        let cosmosRecord = result.Should().BeOk().WhoseValue
                        let option = JsonObject.getOptionalProperty x.PropertyName cosmosRecord
                        option.Should().BeNone() |> ignore

                        // Delete the container
                        do! deleteContainer container activitySource
                    }
                    |> Async.RunSynchronously)
            }

    let configureRunTests builder =
        builder
        |> Cosmos.configureDatabase
        |> HostApplicationBuilder.tryAddSingleton<RunTests> getRunTests
