using CsCheck;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common.tests;

internal static class JsonValueGenerator
{
    public static Gen<JsonValue> Value { get; } =
        Gen.OneOf(from value in Gen.Int
                  select JsonValue.Create(value),
                  from value in Gen.String
                  select JsonValue.Create(value),
                  from value in Gen.Bool
                  select JsonValue.Create(value),
                  from value in Gen.Guid
                  select JsonValue.Create(value),
                  from value in Gen.Date
                  select JsonValue.Create(value),
                  from value in Gen.DateTime
                  select JsonValue.Create(value),
                  from value in Gen.DateTimeOffset
                  select JsonValue.Create(value),
                  from value in Gen.DateOnly
                  select JsonValue.Create(value),
                  from value in Gen.TimeOnly
                  select JsonValue.Create(value));

    public static Gen<JsonValue> String { get; } =
        from value in Gen.String
        select JsonValue.Create(value);

    private static Gen<JsonValue> WhereKind(this Gen<JsonValue> gen, Func<JsonValueKind, bool> predicate) =>
        gen.Where(value => predicate(value.GetValueKind()));

    private static Gen<JsonValue> WhereObject(this Gen<JsonValue> gen, Func<object, bool> predicate) =>
        gen.Where(value => predicate(value.GetValue<object>()));

    private static Gen<JsonValue> WhereObjectString(this Gen<JsonValue> gen, Func<string?, bool> predicate) =>
        gen.WhereObject(value => predicate(value.ToString()));

    public static Gen<JsonValue> NonString { get; } =
        Value.WhereKind(kind => kind is not JsonValueKind.String);

    public static Gen<JsonValue> Int { get; } =
        from value in Gen.Int
        select JsonValue.Create(value);

    public static Gen<JsonValue> NonInt { get; } =
        Value.WhereObject(value => value is not int and not byte);

    public static Gen<JsonValue> Bool { get; } =
        from value in Gen.OneOfConst(true, false)
        select JsonValue.Create(value);

    public static Gen<JsonValue> NonBool { get; } =
        Value.WhereKind(kind => kind is not JsonValueKind.True and not JsonValueKind.False);

    public static Gen<JsonValue> Guid { get; } =
        from value in Gen.Guid
        select JsonValue.Create(value);

    public static Gen<JsonValue> NonGuid { get; } =
        Value.WhereObject(value => value is not System.Guid)
             .WhereObjectString(value => System.Guid.TryParse(value, out var _) is false);

    public static Gen<JsonValue> AbsoluteUri { get; } =
        from value in Gen.String.AlphaNumeric
        where string.IsNullOrWhiteSpace(value) is false
        let uri = new Uri($"https://{value}.com", UriKind.Absolute)
        select JsonValue.Create(uri);

    public static Gen<JsonValue> NonAbsoluteUri { get; } =
        Value.WhereObjectString(value => Uri.TryCreate(value, UriKind.Absolute, out var _) is false);
}

internal static class JsonNodeGenerator
{
    public static Gen<JsonNode> Value { get; } = Create();

    public static Gen<JsonNode> Create() =>
        Gen.Recursive<JsonNode>((depth, nodeGen) => depth < 2
                                                    ? Gen.OneOf<JsonNode>(JsonObjectGenerator.Create(nodeGen),
                                                                          JsonArrayGenerator.Create(nodeGen),
                                                                          JsonValueGenerator.Value)
                                                    : from jsonValue in JsonValueGenerator.Value
                                                      select jsonValue as JsonNode);

    public static Gen<JsonNode> JsonObject { get; } =
        from node in JsonObjectGenerator.Create(Value)
        select node as JsonNode;

    public static Gen<JsonNode> NonJsonObject { get; } =
        Value.Where(node => node is not System.Text.Json.Nodes.JsonObject);

    public static Gen<JsonNode> JsonArray { get; } =
        from node in JsonArrayGenerator.Create(Value)
        select node as JsonNode;

    public static Gen<JsonNode> NonJsonArray { get; } =
        Value.Where(node => node is not System.Text.Json.Nodes.JsonArray);

    public static Gen<JsonNode> JsonValue { get; } =
        from node in JsonValueGenerator.Value
        select node as JsonNode;

    public static Gen<JsonNode> NonJsonValue { get; } =
        Value.Where(node => node is not System.Text.Json.Nodes.JsonValue);
}

internal static class JsonArrayGenerator
{
    public static Gen<JsonArray> Value { get; } = Create(JsonNodeGenerator.Value);

    public static Gen<JsonArray> Create(Gen<JsonNode> nodeGen) =>
        from elements in nodeGen.Null().Array
        select new JsonArray(elements);
}

internal static class JsonObjectGenerator
{
    public static Gen<JsonObject> Value { get; } = Create(JsonNodeGenerator.Value);

    public static Gen<JsonObject> Create(Gen<JsonNode> nodeGen) =>
        from elements in Gen.Select(Gen.String.AlphaNumeric, nodeGen.Null()).List[0, 10]
        let kvps = elements.Select(element => KeyValuePair.Create(element.Item1, element.Item2))
                           .Where(kvp => string.IsNullOrWhiteSpace(kvp.Key) is false)
                           .DistinctBy(kvp => kvp.Key.ToUpperInvariant())
        select new JsonObject(kvps);
}

internal static class Generator
{
    public static Gen<JsonValue> JsonValue =
        Gen.OneOf(from value in Gen.Int
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.String
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.Bool
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.Guid
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.Date
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.DateTime
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.DateTimeOffset
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.DateOnly
                  select System.Text.Json.Nodes.JsonValue.Create(value),
                  from value in Gen.TimeOnly
                  select System.Text.Json.Nodes.JsonValue.Create(value));

    public static Gen<JsonNode> JsonNode { get; } = GenerateJsonNode();

    public static Gen<JsonObject> JsonObject => GenerateJsonObject(JsonNode);

    public static Gen<JsonArray> JsonArray => GenerateJsonArray(JsonNode);

    private static Gen<JsonObject> GenerateJsonObject(Gen<JsonNode> nodeGen)
    {
        var keyGen = from key in Gen.String.AlphaNumeric
                     where string.IsNullOrWhiteSpace(key) is false
                     select key;

        return from kvps in Gen.Select(keyGen, nodeGen.Null())
                               .Select(KeyValuePair.Create)
                               .Array[0, 20]
               let deduped = kvps.DistinctBy(kvp => kvp.Key.ToUpperInvariant())
               select new JsonObject(deduped);
    }

    private static Gen<JsonNode> GenerateJsonNode() =>
        Gen.Recursive<JsonNode>((depth, nodeGen) => depth < 2
                                                    ? Gen.OneOf<JsonNode>(GenerateJsonObject(nodeGen),
                                                                          GenerateJsonArray(nodeGen),
                                                                          JsonValue)
                                                    : from jsonValue in JsonValue
                                                      select jsonValue as JsonNode);

    private static Gen<JsonArray> GenerateJsonArray(Gen<JsonNode> nodeGen) =>
        from nodes in nodeGen.Array[0, 20]
        select new JsonArray(nodes);
}