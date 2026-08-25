using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CSharp_LiteralAndCommentDelimitersPreserveFollowingDeclarationShapes_Issue5182()
    {
        const string content = """
            internal sealed class LexicalBoundary<T>
            {
                [Marker("[(]")]
                public LexicalBoundary(string text)
                {
                    var stop = '(';
                    var message = "phantom(]";
                    // unmatched delimiters ( [ {
                    /* unmatched delimiters ) ] } */
                    var values = new[] { 1, 2, 3 };
                    var slice = values[1..^1];
                    var first = values[0];
                }

                public (
                    T? Value,
                    IReadOnlyList<string> Names) Parse<TItem>(
                        TItem item,
                        Func<TItem, string> render)
                    where TItem : notnull
                {
                    string Local(TItem current)
                    {
                        return render(current);
                    }

                    return (default, [Local(item)]);
                }

                public int Count => 1;
                public T? this[Index index] => default;

                public static LexicalBoundary<T> operator +(
                    LexicalBoundary<T> left,
                    LexicalBoundary<T> right) => left;
            }

            internal sealed class LaterType
            {
                public void LaterBlock()
                {
                }

                public int LaterExpression() => 42;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var functions = symbols.Where(symbol => symbol.Kind == "function").ToList();

        Assert.Contains(functions, symbol => symbol.Name == "LexicalBoundary" && symbol.ContainerName == "LexicalBoundary");
        Assert.Contains(functions, symbol => symbol.Name == "Parse" && symbol.ContainerName == "LexicalBoundary");
        Assert.Contains(functions, symbol => symbol.Name == "Local" && symbol.ContainerName == "Parse");
        Assert.Contains(
            symbols,
            symbol => symbol.Kind == "operator"
                && symbol.Name == "operator +"
                && symbol.ContainerName == "LexicalBoundary");
        Assert.Contains(functions, symbol => symbol.Name == "LaterBlock" && symbol.ContainerName == "LaterType");
        Assert.Contains(functions, symbol => symbol.Name == "LaterExpression" && symbol.ContainerName == "LaterType");
        Assert.Contains(symbols, symbol => symbol.Kind == "property" && symbol.Name == "Count");
        Assert.Contains(
            functions,
            symbol => symbol.Name == "Item"
                && symbol.Signature?.Contains("this[Index index]", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(functions, symbol => symbol.Name is "Count" or "Marker");
    }

    [Fact]
    public void Extract_CSharp_LongMethodDoesNotSuppressLaterExtensionsOrMethods_Issue5182()
    {
        var padding = string.Join(
            '\n',
            Enumerable.Range(0, 480).Select(index => $"        // body padding {index}"));
        var content = $$"""
            internal static class DbDebugExtensions
            {
                public static void ExecuteTrackedReader(
                    this DbCommand command)
                {
                    var end = 0;
                    var text = command.ToString();
                    while (end < text.Length && text[end] != '(')
                        end++;
            {{padding}}
                }

                public static bool TrackedRead(
                    this DbReader reader) => true;
            }

            internal sealed class QueryProfileEntry
            {
                public void AddElapsed(TimeSpan elapsed) => Total += elapsed;

                public void MarkCompletedIfSlow()
                {
                    Completed = true;
                }

                public void Use(DbCommand command, DbReader reader)
                {
                    command.ExecuteTrackedReader();
                    reader.TrackedRead();
                    AddElapsed(default);
                    MarkCompletedIfSlow();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var functions = symbols.Where(symbol => symbol.Kind == "function").ToList();

        Assert.Contains(functions, symbol => symbol.Name == "ExecuteTrackedReader" && symbol.ContainerName == "DbDebugExtensions");
        Assert.Contains(functions, symbol => symbol.Name == "TrackedRead" && symbol.ContainerName == "DbDebugExtensions");
        Assert.Contains(functions, symbol => symbol.Name == "AddElapsed" && symbol.ContainerName == "QueryProfileEntry");
        Assert.Contains(functions, symbol => symbol.Name == "MarkCompletedIfSlow" && symbol.ContainerName == "QueryProfileEntry");
        Assert.All(
            functions.Where(symbol => symbol.Name is "TrackedRead" or "AddElapsed" or "MarkCompletedIfSlow"),
            symbol => Assert.True(symbol.StartLine > 480));

        var trackedRead = Assert.Single(functions.Where(symbol => symbol.Name == "TrackedRead"));
        Assert.Equal(trackedRead.StartLine + 1, trackedRead.EndLine);

        var calls = ReferenceExtractor.Extract(1, "csharp", content, symbols)
            .Where(reference =>
                reference.ReferenceKind == "call"
                && reference.SymbolName is "ExecuteTrackedReader" or "TrackedRead" or "AddElapsed" or "MarkCompletedIfSlow")
            .ToList();

        Assert.Equal(4, calls.Count);
        Assert.All(calls, reference => Assert.Equal("Use", reference.ContainerName));
    }
}
