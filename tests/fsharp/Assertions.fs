namespace common.tests

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open System.Runtime.CompilerServices
open Faqt
open Faqt.AssertionHelpers
open common

[<Extension>]
type JsonErrorAssertions =
    static member HaveMessage(t: Testable<JsonError>, expected, ?because) : AndDerived<JsonError, string> =
        use _ = t.Assert()

        let errorMessage = JsonError.getMessage t.Subject

        if errorMessage <> expected then
            t.With("Expected", expected).With("But was", errorMessage).Fail(because)

        AndDerived(t, errorMessage)

[<Extension>]
type JsonResultAssertions =
    [<Extension>]
    static member BeSuccess<'a>(t: Testable<JsonResult<'a>>, ?because) : AndDerived<JsonResult<'a>, 'a> =
        use _ = t.Assert()

        t.Subject
        |> JsonResult.map (fun a -> AndDerived(t, a))
        |> JsonResult.defaultWith (fun error ->
            t
                .With("But was", "Failure")
                .With("Error message", JsonError.getMessage error)
                .Fail(because))

    [<Extension>]
    static member BeFailure<'a>(t: Testable<JsonResult<'a>>, ?because) : AndDerived<JsonResult<'a>, JsonError> =
        use _ = t.Assert()

        t.Subject
        |> JsonResult.map (fun a -> t.With("But was", "Success").With("Value", a).Fail(because))
        |> JsonResult.defaultWith (fun error -> AndDerived(t, error))

[<Extension>]
type JsonNodeAssertions =
    [<Extension>]
    static member BeEquivalentTo
        (
            t: Testable<JsonNode>,
            expected: JsonNode,
            ?options: JsonSerializerOptions,
            ?because
        ) : AndDerived<JsonNode, JsonNode> =
        use _ = t.Assert()

        let optionsToUse =
            defaultArg options (JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

        let actualString = t.Subject.ToJsonString(optionsToUse)
        let expectedString = expected.ToJsonString(optionsToUse)

        // We compare the first 5 characters for speed, to avoid rounding issues, etc.
        // It's not exact, but should be good in most cases.
        let actualStringToCompare = actualString |> Seq.truncate 5 |> String.Concat
        let expectedStringToCompare = expectedString |> Seq.truncate 5 |> String.Concat

        if not (actualStringToCompare.Equals(expectedStringToCompare, StringComparison.OrdinalIgnoreCase)) then
            t.With("Expected", expectedString).With("But was", actualString).Fail(because)

        AndDerived(t, t.Subject)
