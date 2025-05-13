namespace common

#nowarn 3265

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FSharpPlus

type JsonError = private JsonError of Set<string>

[<RequireQualifiedAccess>]
module JsonError =
    let fromString message = Set.singleton message |> JsonError

    let fromException (exn: Exception) =
        match exn with
        | :? AggregateException as aggregateException ->
            aggregateException.Flatten().InnerExceptions
            |> Seq.map _.Message
            |> Seq.append [ aggregateException.Message ]
            |> Set.ofSeq
            |> JsonError
        | _ -> fromString exn.Message

    let getMessage (JsonError errors) = Seq.head errors

    let getMessages (JsonError errors) = errors

    let toJsonException (JsonError error) =
        match List.ofSeq error with
        | [ message ] -> new JsonException(message)
        | messages ->
            let aggregateException =
                messages
                |> Seq.map (fun message -> new JsonException(message) :> Exception)
                |> AggregateException

            new JsonException("Multiple errors, see inner exception for details..", aggregateException)

type JsonError with
    // Semigroup
    static member (+)(JsonError existingMessages, JsonError newMessages) =
        Set.union newMessages existingMessages |> JsonError

type JsonResult<'a> =
    | Success of 'a
    | Failure of JsonError

[<RequireQualifiedAccess>]
module JsonResult =
    let succeed x = Success x

    let fail e = Failure e

    let failWithMessage message = JsonError.fromString message |> fail

    let replaceErrorWith f jsonResult =
        match jsonResult with
        | Failure e -> f e |> fail
        | _ -> jsonResult

    let setErrorMessage message jsonResult =
        replaceErrorWith (fun _ -> JsonError.fromString message) jsonResult

    let map f jsonResult =
        match jsonResult with
        | Success x -> succeed (f x)
        | Failure e -> fail e

    let apply f x =
        match f, x with
        | Success f, Success x -> succeed (f x)
        | Failure e, Success _ -> fail e
        | Success _, Failure e -> fail e
        | Failure e1, Failure e2 -> fail (e1 + e2)

    let defaultWith f jsonResult =
        match jsonResult with
        | Success x -> x
        | Failure jsonError -> f jsonError

    let throwIfFail jsonResult =
        let throw error =
            JsonError.toJsonException error |> raise

        jsonResult |> defaultWith throw

type JsonResult<'a> with
    // Functor
    static member Map(x, f) =
        match x with
        | Success x -> f x |> JsonResult.succeed
        | Failure e -> JsonResult.fail e

    static member Unzip x =
        match x with
        | Success(x, y) -> JsonResult.succeed x, JsonResult.succeed y
        | Failure e -> JsonResult.fail e, JsonResult.fail e

    // Applicative
    static member Return x = JsonResult.succeed x

    static member (<*>)(f, x) = JsonResult.apply f x

    static member Lift2(f, x1, x2) =
        match x1, x2 with
        | Success x1, Success x2 -> f x1 x2 |> JsonResult.succeed
        | Failure e, Success _ -> JsonResult.fail e
        | Success _, Failure e -> JsonResult.fail e
        | Failure e1, Failure e2 -> JsonResult.fail (e1 + e2)

    static member Lift3(f, x1, x2, x3) =
        match x1, x2, x3 with
        | Success x1, Success x2, Success x3 -> f x1 x2 x3 |> JsonResult.succeed
        | Failure e1, Success _, Success _ -> JsonResult.fail e1
        | Failure e1, Success _, Failure e3 -> JsonResult.fail (e1 + e3)
        | Failure e1, Failure e2, Success _ -> JsonResult.fail (e1 + e2)
        | Failure e1, Failure e2, Failure e3 -> JsonResult.fail (e1 + e2 + e3)
        | Success _, Failure e2, Success _ -> JsonResult.fail e2
        | Success _, Failure e2, Failure e3 -> JsonResult.fail (e2 + e3)
        | Success _, Success _, Failure e3 -> JsonResult.fail e3

    // Zip applicative
    static member Pure x = JsonResult.succeed x

    static member (<.>)(f, x) = JsonResult.apply f x

    static member Zip(x1, x2) =
        match (x1, x2) with
        | Success x1, Success x2 -> JsonResult.succeed (x1, x2)
        | Failure e1, Success _ -> JsonResult.fail e1
        | Success _, Failure e2 -> JsonResult.fail e2
        | Failure e1, Failure e2 -> JsonResult.fail (e1 + e2)

    static member Map2(f, x1, x2) =
        match x1, x2 with
        | Success x1, Success x2 -> f x1 x2 |> JsonResult.succeed
        | Failure e, Success _ -> JsonResult.fail e
        | Success _, Failure e -> JsonResult.fail e
        | Failure e1, Failure e2 -> JsonResult.fail (e1 + e2)

    static member Map3(f, x1, x2, x3) =
        match x1, x2, x3 with
        | Success x1, Success x2, Success x3 -> f x1 x2 x3 |> JsonResult.succeed
        | Failure e1, Success _, Success _ -> JsonResult.fail e1
        | Failure e1, Success _, Failure e3 -> JsonResult.fail (e1 + e3)
        | Failure e1, Failure e2, Success _ -> JsonResult.fail (e1 + e2)
        | Failure e1, Failure e2, Failure e3 -> JsonResult.fail (e1 + e2 + e3)
        | Success _, Failure e2, Success _ -> JsonResult.fail e2
        | Success _, Failure e2, Failure e3 -> JsonResult.fail (e2 + e3)
        | Success _, Success _, Failure e3 -> JsonResult.fail e3

    // Monad
    static member Bind(x, f) =
        match x with
        | Success x -> f x
        | Failure e -> JsonResult.fail e

    static member (>>=)(x, f) = JsonResult<'a>.Bind(x, f)

    static member Join x =
        match x with
        | Success(Success x) -> JsonResult.succeed x
        | Success(Failure e) -> JsonResult.fail e
        | Failure e -> JsonResult.fail e

    // Foldable
    static member ToSeq x =
        match x with
        | Success x -> Seq.singleton x
        | Failure _ -> Seq.empty


[<RequireQualifiedAccess>]
module JsonNode =
    let asJsonObject (node: JsonNode | null) =
        match node with
        | :? JsonObject as jsonObject -> JsonResult.succeed jsonObject
        | Null -> JsonResult.failWithMessage "JSON node is null"
        | _ -> JsonResult.failWithMessage "JSON node is not a JSON object"

    let asJsonArray (node: JsonNode | null) =
        match node with
        | :? JsonArray as jsonArray -> JsonResult.succeed jsonArray
        | Null -> JsonResult.failWithMessage "JSON node is null"
        | _ -> JsonResult.failWithMessage "JSON node is not a JSON array"

    let asJsonValue (node: JsonNode | null) =
        match node with
        | :? JsonValue as jsonValue -> JsonResult.succeed jsonValue
        | Null -> JsonResult.failWithMessage "JSON node is null"
        | _ -> JsonResult.failWithMessage "JSON node is not a JSON value"

    let private ifNoneNullable option =
        option |> Option.map Nullable |> Option.defaultValue (Nullable())

    let private ifNoneDefault option =
        option |> Option.defaultValue Unchecked.defaultof<'a>

    let fromStreamWithOptions (stream: Stream | null) nodeOptions documentOptions =
        async {
            let! cancellationToken = Async.CancellationToken

            match stream with
            | Null -> return JsonResult.failWithMessage "Stream is null."
            | NonNull stream ->
                try
                    let nodeOptions = ifNoneNullable nodeOptions
                    let documentOptions = ifNoneDefault documentOptions

                    match!
                        JsonNode.ParseAsync(stream, nodeOptions, documentOptions, cancellationToken = cancellationToken)
                        |> Async.AwaitTask
                    with
                    | Null -> return JsonResult.failWithMessage "Deserialization returned a null result."
                    | NonNull node -> return JsonResult.succeed node
                with exn ->
                    return JsonError.fromException exn |> JsonResult.fail
        }

    let fromStream stream = fromStreamWithOptions stream None None

    let fromBinaryDataWithOptions (data: BinaryData | null) nodeOptions =
        try
            match data with
            | Null -> JsonResult.failWithMessage "Binary data is null."
            | NonNull data ->
                let nodeOptions = ifNoneNullable nodeOptions

                match JsonNode.Parse(data, nodeOptions) with
                | Null -> JsonResult.failWithMessage "Deserialization returned a null result."
                | NonNull node -> JsonResult.succeed node
        with exn ->
            JsonError.fromException exn |> JsonResult.fail

    let fromBinaryData data = fromBinaryDataWithOptions data None

    let toBinaryData (node: JsonNode) = BinaryData.FromObjectAsJson(node)

    let toStream (node: JsonNode) = toBinaryData node |> _.ToStream()

[<RequireQualifiedAccess>]
module JsonArray =
    let private toSeq (jsonArray: JsonArray) = jsonArray |> seq

    let fromSeq seq = Array.ofSeq seq |> JsonArray

    let private getResultSeq toJsonResult toErrorMessage (jsonArray: JsonArray) =
        jsonArray
        |> toSeq
        |> traversei (fun index node ->
            let replaceErrorMessage = toErrorMessage index |> JsonResult.setErrorMessage
            toJsonResult node |> replaceErrorMessage)

    let asJsonObjects jsonArray =
        getResultSeq JsonNode.asJsonObject (fun index -> $"Element at index {index} is not a JSON object.") jsonArray

    let asJsonArrays jsonArray =
        getResultSeq JsonNode.asJsonArray (fun index -> $"Element at index {index} is not a JSON array.") jsonArray

    let asJsonValues jsonArray =
        getResultSeq JsonNode.asJsonValue (fun index -> $"Element at index {index} is not a JSON value.") jsonArray

    let getJsonObjects jsonArray =
        jsonArray
        |> toSeq
        |> Seq.choose (fun node ->
            match node with
            | :? JsonObject as jsonObject -> Some jsonObject
            | _ -> None)

    let getJsonArrays jsonArray =
        jsonArray
        |> toSeq
        |> Seq.choose (fun node ->
            match node with
            | :? JsonArray as jsonArray -> Some jsonArray
            | _ -> None)

    let getJsonValues jsonArray =
        jsonArray
        |> toSeq
        |> Seq.choose (fun node ->
            match node with
            | :? JsonValue as jsonValue -> Some jsonValue
            | _ -> None)

[<RequireQualifiedAccess>]
module JsonValue =
    let private getString (jsonValue: JsonValue) = jsonValue.GetValue<obj>() |> string

    let asString (jsonValue: JsonValue) =
        match jsonValue.GetValueKind() with
        | JsonValueKind.String -> getString jsonValue |> JsonResult.succeed
        | _ -> JsonResult.failWithMessage "JSON value is not a string"

    let asInt (jsonValue: JsonValue) =
        match jsonValue.GetValueKind() with
        | JsonValueKind.Number ->
            match getString jsonValue |> Int32.TryParse with
            | true, x -> JsonResult.succeed x
            | _ -> JsonResult.failWithMessage "JSON value is not an integer"
        | _ -> JsonResult.failWithMessage "JSON value is not a number"

    let asAbsoluteUri jsonValue =
        let errorMessage = "JSON value is not an absolute URI."

        asString jsonValue
        |> bind (fun stringValue ->
            match Uri.TryCreate(stringValue, UriKind.Absolute) with
            | true, uri when
                match uri with
                | Null -> false
                | NonNull nonNullUri -> nonNullUri.HostNameType <> UriHostNameType.Unknown
                ->
                JsonResult.succeed uri
            | _ -> JsonResult.failWithMessage errorMessage)
        |> JsonResult.setErrorMessage errorMessage

    let asGuid jsonValue =
        asString jsonValue
        |> bind (fun stringValue ->
            match Guid.TryParse(stringValue) with
            | true, guid -> JsonResult.succeed guid
            | _ -> JsonResult.failWithMessage "JSON value is not a GUID.")

    let asBool (jsonValue: JsonValue) =
        match jsonValue.GetValueKind() with
        | JsonValueKind.True -> JsonResult.succeed true
        | JsonValueKind.False -> JsonResult.succeed false
        | _ -> JsonResult.failWithMessage "JSON value is not a boolean."

    let asDateTimeOffset (jsonValue: JsonValue) =
        let errorMessage = "JSON value is not a date time offset."

        match jsonValue.TryGetValue<DateTimeOffset>() with
        | true, dateTimeOffset -> JsonResult.succeed dateTimeOffset
        | _ ->
            monad {
                let! stringValue = asString jsonValue |> JsonResult.setErrorMessage errorMessage

                match DateTimeOffset.TryParse(stringValue) with
                | true, dateTimeOffset -> return dateTimeOffset
                | _ -> return! JsonResult.failWithMessage errorMessage
            }

[<RequireQualifiedAccess>]
module JsonObject =
    let getProperty propertyName (jsonObject: JsonObject | null) =
        match jsonObject with
        | null -> JsonResult.failWithMessage "JSON object is null."
        | jsonObject ->
            match jsonObject.TryGetPropertyValue(propertyName) with
            | true, property ->
                match property with
                | null -> JsonResult.failWithMessage $"Property '{propertyName}' is null."
                | property -> JsonResult.succeed property
            | _ -> JsonResult.failWithMessage $"Property '{propertyName}' is missing."

    let getOptionalProperty propertyName jsonObject =
        getProperty propertyName jsonObject
        |> map Option.Some
        |> JsonResult.defaultWith (fun _ -> Option.None)

    let private addPropertyNameToErrorMessage propertyName jsonResult =
        let replaceError jsonError =
            let originalErrorMessage = JsonError.getMessage jsonError

            let newErrorMessage =
                $"Property '{propertyName}' is invalid. {originalErrorMessage}"

            JsonError.fromString newErrorMessage

        JsonResult.replaceErrorWith replaceError jsonResult

    let private getPropertyFromResult getPropertyResult propertyName jsonObject =
        getProperty propertyName jsonObject
        |> bind getPropertyResult
        |> addPropertyNameToErrorMessage propertyName

    let getJsonObjectProperty propertyName jsonObject =
        getPropertyFromResult JsonNode.asJsonObject propertyName jsonObject

    let getJsonArrayProperty propertyName jsonObject =
        getPropertyFromResult JsonNode.asJsonArray propertyName jsonObject

    let getJsonValueProperty propertyName jsonObject =
        getPropertyFromResult JsonNode.asJsonValue propertyName jsonObject

    let getStringProperty propertyName jsonObject =
        let getPropertyResult = JsonNode.asJsonValue >> bind JsonValue.asString
        getPropertyFromResult getPropertyResult propertyName jsonObject

    let getAbsoluteUriProperty propertyName jsonObject =
        let getPropertyResult = JsonNode.asJsonValue >> bind JsonValue.asAbsoluteUri
        getPropertyFromResult getPropertyResult propertyName jsonObject

    let getGuidProperty propertyName jsonObject =
        let getPropertyResult = JsonNode.asJsonValue >> bind JsonValue.asGuid
        getPropertyFromResult getPropertyResult propertyName jsonObject

    let getBoolProperty propertyName jsonObject =
        let getPropertyResult = JsonNode.asJsonValue >> bind JsonValue.asBool
        getPropertyFromResult getPropertyResult propertyName jsonObject

    let getIntProperty propertyName jsonObject =
        let getPropertyResult = JsonNode.asJsonValue >> bind JsonValue.asInt
        getPropertyFromResult getPropertyResult propertyName jsonObject

    let getDateTimeOffsetProperty propertyName jsonObject =
        let getPropertyResult = JsonNode.asJsonValue >> bind JsonValue.asDateTimeOffset
        getPropertyFromResult getPropertyResult propertyName jsonObject

    let setProperty (propertyName: string) (propertyValue: JsonNode | null) (jsonObject: JsonObject) =
        jsonObject[propertyName] <- propertyValue
        jsonObject

    let removeProperty (propertyName: string) (jsonObject: JsonObject) =
        jsonObject.Remove(propertyName) |> ignore
        jsonObject

    let fromStreamWithOptions stream nodeOptions documentOptions =
        JsonNode.fromStreamWithOptions stream nodeOptions documentOptions
        |> map (bind JsonNode.asJsonObject)

    let fromStream stream = fromStreamWithOptions stream None None

    let fromBinaryDataWithOptions data nodeOptions =
        JsonNode.fromBinaryDataWithOptions data nodeOptions
        |> bind JsonNode.asJsonObject

    let fromBinaryData data = fromBinaryDataWithOptions data None
