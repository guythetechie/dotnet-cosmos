using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Primitives;
using LanguageExt;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace common.tests;

internal sealed class EitherAssertions<TLeft, TRight>(Either<TLeft, TRight> subject, AssertionChain assertionChain) : ReferenceTypeAssertions<Either<TLeft, TRight>, EitherAssertions<TLeft, TRight>>(subject, assertionChain)
{
    private readonly AssertionChain assertionChain = assertionChain;

    protected override string Identifier { get; } = "either";

    public AndWhichConstraint<EitherAssertions<TLeft, TRight>, TLeft> BeLeft([StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        assertionChain.BecauseOf(because, becauseArgs);
        return Subject.Match(left => new AndWhichConstraint<EitherAssertions<TLeft, TRight>, TLeft>(this, left),
                            right =>
                            {
                                assertionChain.FailWith("Expected {context:either} to be left, but it is right {0}", right);
                                return new AndWhichConstraint<EitherAssertions<TLeft, TRight>, TLeft>(this, []);
                            });
    }

    public AndWhichConstraint<EitherAssertions<TLeft, TRight>, TRight> BeRight([StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        assertionChain.BecauseOf(because, becauseArgs);

        return Subject.Match(left =>
                            {
                                assertionChain.FailWith("Expected {context:either} to be right, but it is left {0}", left);
                                return new AndWhichConstraint<EitherAssertions<TLeft, TRight>, TRight>(this, []);
                            },
                            right => new AndWhichConstraint<EitherAssertions<TLeft, TRight>, TRight>(this, right));
    }
}

internal sealed class JsonNodeAssertions(JsonNode subject, AssertionChain assertionChain) : ReferenceTypeAssertions<JsonNode, JsonNodeAssertions>(subject, assertionChain)
{
    private readonly AssertionChain assertionChain = assertionChain;

    protected override string Identifier { get; } = "json node";

    public AndConstraint<JsonNode> BeEquivalentTo(JsonNode expected, [StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        // We compare the first 5 characters for speed, to avoid rounding issues, etc.
        // It's not exact, but should be good in most cases.
        var actualComparisonString = Subject.ToJsonString(options);
        var expectedComparisonString = expected.ToJsonString(options);

        assertionChain.BecauseOf(because, becauseArgs)
                      .ForCondition(actualComparisonString.Equals(expectedComparisonString, StringComparison.OrdinalIgnoreCase))
                      .FailWith("Expected {context:json node} to be equivalent to {0}, but it is {1}.", expected.ToJsonString(options), Subject.ToJsonString(options));

        return new(Subject);
    }
}

internal sealed class JsonResultAssertions<T>(JsonResult<T> subject, AssertionChain assertionChain) : ReferenceTypeAssertions<JsonResult<T>, JsonResultAssertions<T>>(subject, assertionChain)
{
    private readonly AssertionChain assertionChain = assertionChain;

    protected override string Identifier { get; } = "JSON result";

    public AndWhichConstraint<JsonResultAssertions<T>, T> BeSuccess([StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        assertionChain.BecauseOf(because, becauseArgs);

        return Subject.Match(success => new AndWhichConstraint<JsonResultAssertions<T>, T>(this, success),
                             error =>
                             {
                                 assertionChain.FailWith("Expected {context:JSON result} to be a success, but it failed with error {0}.", error);
                                 return new AndWhichConstraint<JsonResultAssertions<T>, T>(this, []);
                             });
    }

    public AndWhichConstraint<JsonResultAssertions<T>, JsonError> BeError([StringSyntax("CompositeFormat")] string because = "", params object[] becauseArgs)
    {
        assertionChain.BecauseOf(because, becauseArgs);

        return Subject.Match(success =>
                             {
                                 assertionChain.FailWith("Expected {context:JSON result} to be an error, but it succeeded with value {0}.", success);
                                 return new AndWhichConstraint<JsonResultAssertions<T>, JsonError>(this, []);
                             },
                             error => new AndWhichConstraint<JsonResultAssertions<T>, JsonError>(this, error));
    }
}

internal static class AssertionExtensions
{
    public static EitherAssertions<TLeft, TRight> Should<TLeft, TRight>(this Either<TLeft, TRight> subject) =>
        new(subject, AssertionChain.GetOrCreate());

    public static JsonNodeAssertions Should(this JsonNode subject) =>
        new(subject, AssertionChain.GetOrCreate());

    public static JsonResultAssertions<T> Should<T>(this JsonResult<T> subject) =>
        new(subject, AssertionChain.GetOrCreate());   //    new(subject, AssertionChain.GetOrCreate());
}