using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_StaticLambdaHeadersDoNotCreateFunctionOrPropertySymbols_Issue4830()
    {
        const string content = """
            using System;
            using System.Threading.Tasks;

            public class StaticLambdaSamples
            {
                public static int RealStaticMember(int value) => value;

                public Func<int, int> ExistingLambda = value => value + 1;

                public string Format(string value)
                {
                    static int RealStaticLocal(int item) => item + 1;

                    var stateful = string.Create(
                        value.Length,
                        (Value: value, Offset: 0),
                        static (destination, state) =>
                        {
                            state.Value.AsSpan().CopyTo(destination);
                        });

                    var typed = Apply(
                        static (
                            int typedValue,
                            string typedLabel) =>
                            typedValue + typedLabel.Length);

                    var untyped = Apply(static item => item + 1);
                    var asyncStatic = Apply(static async (int asyncValue) =>
                    {
                        await Task.Yield();
                        return asyncValue;
                    });
                    var asyncThenStatic = Apply(async static (int secondAsyncValue) =>
                    {
                        await Task.Yield();
                        return secondAsyncValue;
                    });
                    var explicitReturn = Apply(
                        static int (int explicitValue) => explicitValue + 1);
                    var explicitAsyncReturn = Apply(
                        async static Task<int> (int asyncExplicitValue) =>
                        {
                            await Task.Yield();
                            return asyncExplicitValue;
                        });
                    Func<LambdaResult> explicitCustomZero =
                        static LambdaResult () => new();
                    var combiningIdentifier = Apply(
                        static café => café + 1);
                    var escapedIdentifier = Apply(
                        static \u0061 => \u0061 + 1);
                    var nested = Apply(
                        static outerValue =>
                            Apply(static innerValue => outerValue + innerValue));

                    return stateful + RealStaticLocal(
                        typed(1, "x")
                        + untyped(1)
                        + asyncStatic(1).Result
                        + asyncThenStatic(1).Result
                        + explicitReturn(1)
                        + explicitAsyncReturn(1).Result
                        + explicitCustomZero().Value
                        + combiningIdentifier(1)
                        + escapedIdentifier(1)
                        + nested(1));
                }

                static StaticLambdaSamples() => RealStaticMember(0);

                private static T RealStaticGeneric<T>(T value) => value;

                static int IStaticContract<StaticLambdaSamples>.Transform(int value) => value;

                private @static RealVerbatimTypeMember(int value) => new();

                private static @static RealStaticVerbatimTypeMember(int value) => new();

                private static int RealStaticGenericOwner(int value)
                {
                    static T RealStaticGenericLocal<T>(T item) => item;
                    return RealStaticGenericLocal(value);
                }

                private static Func<T, TResult> Apply<T, TResult>(Func<T, TResult> callback) => callback;
            }

            public class @static
            {
            }

            public sealed class LambdaResult
            {
                public int Value { get; }
            }

            public interface IStaticContract<TSelf>
                where TSelf : IStaticContract<TSelf>
            {
                static abstract int Transform(int value);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var forbiddenNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "static",
            "destination",
            "state",
            "typedValue",
            "typedLabel",
            "item",
            "asyncValue",
            "secondAsyncValue",
            "int",
            "Task",
            "LambdaResult",
            "explicitValue",
            "asyncExplicitValue",
            "café",
            "a",
            @"\u0061",
            "outerValue",
            "innerValue",
        };

        Assert.DoesNotContain(
            symbols,
            symbol => (symbol.Kind is "function" or "property")
                && forbiddenNames.Contains(symbol.Name));
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealStaticMember");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealStaticLocal");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "StaticLambdaSamples");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealStaticGeneric");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealStaticGenericLocal");
        Assert.Contains(
            symbols,
            symbol => symbol.Kind == "function"
                && symbol.Name == "Transform"
                && symbol.ContainerName == "StaticLambdaSamples");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealVerbatimTypeMember");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealStaticVerbatimTypeMember");

        var lambda = Assert.Single(symbols.Where(symbol => symbol.Kind == "lambda" && symbol.Name == "ExistingLambda"));
        Assert.Equal("StaticLambdaSamples", lambda.ContainerName);
        Assert.Equal(8, lambda.StartLine);
        Assert.Equal(8, lambda.EndLine);
    }
}
