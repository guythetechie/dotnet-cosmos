[<RequireQualifiedAccess>]
module common.tests.Gen

open System
open System.Text.Json.Nodes
open System.Collections.Generic
open FsCheck
open FsCheck.FSharp

let private default'<'a> () = ArbMap.defaults |> ArbMap.generate<'a>

let guid = default'<Guid>()

let jsonValue =
    Gen.oneof
        [ default'<int> () |> Gen.map JsonValue.Create
          default'<string> () |> Gen.map JsonValue.Create
          default'<bool> () |> Gen.map JsonValue.Create
          default'<double> ()
          |> Gen.filter (Double.IsInfinity >> not)
          |> Gen.filter (Double.IsNaN >> not)
          |> Gen.map JsonValue.Create
          default'<byte> () |> Gen.map JsonValue.Create
          default'<Guid> () |> Gen.map JsonValue.Create ]

let private toJsonNode gen =
    gen |> Gen.map (fun value -> value :> JsonNode)

let private jsonValueAsNode = toJsonNode jsonValue

let private generateJsonArray (nodeGen: Gen<JsonNode | null>) =
    Gen.arrayOf nodeGen |> Gen.map JsonArray

let generateJsonObject (nodeGen: Gen<JsonNode | null>) =
    let propertyGen = default'<string> () |> Gen.filter (String.IsNullOrWhiteSpace >> not)

    Gen.zip propertyGen nodeGen
    |> Gen.listOf
    |> Gen.map (Seq.distinctBy (fun (first, second) -> first.ToUpperInvariant()))
    |> Gen.map (Seq.map KeyValuePair.Create)
    |> Gen.map JsonObject

let jsonNode =
    let rec generateJsonNode size =
        if size < 1 then
            jsonValueAsNode
        else
            let reducedSizeGen = generateJsonNode (size / 2)

            Gen.oneof
                [ jsonValueAsNode
                  generateJsonArray reducedSizeGen |> toJsonNode
                  generateJsonObject reducedSizeGen |> toJsonNode ]

    Gen.sized generateJsonNode

let jsonObject = generateJsonObject jsonNode

let jsonArray = generateJsonArray jsonNode

[<RequireQualifiedAccess>]
module JsonValue =
    let guid =
        guid
        |> Gen.map (fun value -> value.ToString())
        |> Gen.map JsonValue.Create