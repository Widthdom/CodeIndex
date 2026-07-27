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
                    var nested = Apply(
                        static outerValue =>
                            Apply(static innerValue => outerValue + innerValue));

                    return stateful + RealStaticLocal(
                        typed(1, "x")
                        + untyped(1)
                        + asyncStatic(1).Result
                        + asyncThenStatic(1).Result
                        + nested(1));
                }

                private static Func<T, TResult> Apply<T, TResult>(Func<T, TResult> callback) => callback;
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
            "outerValue",
            "innerValue",
        };

        Assert.DoesNotContain(
            symbols,
            symbol => (symbol.Kind is "function" or "property")
                && forbiddenNames.Contains(symbol.Name));
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealStaticMember");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealStaticLocal");

        var lambda = Assert.Single(symbols.Where(symbol => symbol.Kind == "lambda" && symbol.Name == "ExistingLambda"));
        Assert.Equal("StaticLambdaSamples", lambda.ContainerName);
        Assert.Equal(8, lambda.StartLine);
        Assert.Equal(8, lambda.EndLine);
    }
}
