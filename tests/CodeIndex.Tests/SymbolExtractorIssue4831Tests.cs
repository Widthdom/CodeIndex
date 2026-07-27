using CodeIndex.Indexer;

namespace CodeIndex.Tests;

internal static class Issue4831CSharpFixture
{
    internal const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace Issue4831Fixture;

        public delegate bool TryParser(
            string text,
            out int value);

        public sealed class Scanner
        {
            public Scanner(
                string name,
                in int seed)
            {
            }

            public IReadOnlyList<LanguageEntry> Build(
                IReadOnlyDictionary<string, long> counts,
                IEnumerable<LanguageInfo> languages,
                ref int total,
                out int matched,
                in bool include,
                params string[] filters)
            {
                var entries = languages.Select(language => new LanguageEntry(
                    language.ExactNames.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    language.Prefixes.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    language.Patterns.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    language.Symbols,
                    GetIndexedLanguageCount(counts, language.Name))).ToList();

                Use(
                    () =>
                    {
                        static int CallbackLocal(
                            int value)
                        {
                            return value;
                        }

                        return CallbackLocal(1);
                    });

                Use(
                    delegate
                    {
                        static int DelegateLocal(
                            int value)
                        {
                            return value;
                        }

                        return DelegateLocal(2);
                    });

                static long LocalCount(
                    IReadOnlyDictionary<string, long> localCounts,
                    string language)
                {
                    return localCounts.TryGetValue(language, out var count) ? count : 0;
                }

                matched = entries.Count;
                total += matched;
                return entries;
            }

            private static long GetIndexedLanguageCount(
                IReadOnlyDictionary<string, long> counts,
                string language)
            {
                return counts.TryGetValue(language, out var count) ? count : 0;
            }

            private static void Use(
                Func<int> callback)
            {
                _ = callback();
            }

            private static bool TryRecover(
                string[] lines,
                int startLine,
                int endLine,
                out (int Line, int Column) recoveredPosition)
            {
                recoveredPosition = default;
                return true;
            }
        }

        public sealed record LanguageInfo(
            string Name,
            IReadOnlyList<string> ExactNames,
            IReadOnlyList<string> Prefixes,
            IReadOnlyList<string> Patterns,
            bool Symbols);

        public sealed record LanguageEntry(
            IReadOnlyList<string> ExactNames,
            IReadOnlyList<string> Prefixes,
            IReadOnlyList<string> Patterns,
            bool Symbols,
            long Count);
        """;
}

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_RejectsInvocationAndParameterContinuationsAsFunctions_Issue4831()
    {
        var symbols = SymbolExtractor.Extract(1, "csharp", Issue4831CSharpFixture.Source);

        var indexedCount = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "function"
                && symbol.Name == "GetIndexedLanguageCount"));
        Assert.Equal(70, indexedCount.StartLine);
        Assert.Equal(75, indexedCount.EndLine);
        Assert.Equal(73, indexedCount.BodyStartLine);
        Assert.Equal(75, indexedCount.BodyEndLine);
        Assert.Equal("class", indexedCount.ContainerKind);
        Assert.Equal("Scanner", indexedCount.ContainerName);

        var constructor = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "function" && symbol.Name == "Scanner"));
        Assert.Equal((13, 17), (constructor.StartLine, constructor.EndLine));
        Assert.Equal("class", constructor.ContainerKind);
        Assert.Equal("Scanner", constructor.ContainerName);

        var build = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "function" && symbol.Name == "Build"));
        Assert.Equal((19, 68), (build.StartLine, build.EndLine));
        Assert.Equal((26, 68), (build.BodyStartLine, build.BodyEndLine));
        Assert.Equal("class", build.ContainerKind);
        Assert.Equal("Scanner", build.ContainerName);

        var callbackLocal = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "function" && symbol.Name == "CallbackLocal"));
        Assert.Equal((37, 41), (callbackLocal.StartLine, callbackLocal.EndLine));
        Assert.Equal((39, 41), (callbackLocal.BodyStartLine, callbackLocal.BodyEndLine));
        Assert.Equal("class", callbackLocal.ContainerKind);
        Assert.Equal("Scanner", callbackLocal.ContainerName);

        var delegateLocal = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "function" && symbol.Name == "DelegateLocal"));
        Assert.Equal((49, 53), (delegateLocal.StartLine, delegateLocal.EndLine));
        Assert.Equal((51, 53), (delegateLocal.BodyStartLine, delegateLocal.BodyEndLine));
        Assert.Equal("class", delegateLocal.ContainerKind);
        Assert.Equal("Scanner", delegateLocal.ContainerName);

        var localCount = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "function" && symbol.Name == "LocalCount"));
        Assert.Equal((58, 63), (localCount.StartLine, localCount.EndLine));
        Assert.Equal((61, 63), (localCount.BodyStartLine, localCount.BodyEndLine));
        Assert.Equal("class", localCount.ContainerKind);
        Assert.Equal("Scanner", localCount.ContainerName);

        var tryParser = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "delegate" && symbol.Name == "TryParser"));
        Assert.Equal((7, 7), (tryParser.StartLine, tryParser.EndLine));
        Assert.Equal("namespace", tryParser.ContainerKind);
        Assert.Equal("Issue4831Fixture", tryParser.ContainerName);

        var tryRecover = Assert.Single(symbols.Where(
            symbol => symbol.Kind == "function" && symbol.Name == "TryRecover"));
        Assert.Equal((83, 91), (tryRecover.StartLine, tryRecover.EndLine));
        Assert.Equal((88, 91), (tryRecover.BodyStartLine, tryRecover.BodyEndLine));
        Assert.Equal("class", tryRecover.ContainerKind);
        Assert.Equal("Scanner", tryRecover.ContainerName);

        Assert.DoesNotContain(
            symbols,
            symbol => symbol.Kind == "function"
                && symbol.Name is "out" or "ref" or "in" or "params");
    }
}
