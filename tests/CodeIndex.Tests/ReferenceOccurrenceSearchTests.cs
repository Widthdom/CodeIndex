using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class ReferenceOccurrenceSearchTests
{
    [Fact]
    public void ClosestOccurrence_PreservesNonOverlappingMatchesAndEarlierTies()
    {
        // Regex matches provide an independent non-overlapping oracle, including
        // self-overlapping names where an exact-column shortcut would be unsafe.
        foreach (var text in EnumerateShortTexts())
        foreach (var name in new[] { "a", "aa", "aba", "ab", "ba", "a.a", "." })
        {
            var matches = Regex.Matches(text, Regex.Escape(name))
                .Select(match => match.Index).ToArray();
            for (var column = -1; column <= text.Length + 2; column++)
            {
                var expected = matches.OrderBy(index => Math.Abs(index + 1 - column))
                    .Select(index => (int?)index).FirstOrDefault() ?? -1;
                Assert.Equal(expected, ReferenceOccurrenceSearch.FindClosest(text, name, column, out _));
            }
        }
    }

    [Fact]
    public void ClosestOccurrence_RecordedPositionsAvoidDenseLineRescans()
    {
        const string part = "Receiver.Widget(); ";
        var context = string.Concat(Enumerable.Repeat(part, 2048));
        var totalExamined = 0;
        for (var index = 0; index < 2048; index++)
        {
            var occurrence = index * part.Length + "Receiver.".Length;
            Assert.Equal(occurrence, ReferenceOccurrenceSearch.FindClosest(
                context, "Widget", occurrence + 1, out var examined));
            totalExamined += examined;
        }
        Assert.Equal(2048, totalExamined);
        Assert.Equal("Receiver.".Length, ReferenceOccurrenceSearch.FindClosest(
            context, "Widget", 12, out var nearbyExamined));
        Assert.Equal(2, nearbyExamined);

        Assert.Equal(0, ReferenceOccurrenceSearch.FindClosest("ababa", "aba", 3, out _));
        Assert.Equal(0, ReferenceOccurrenceSearch.FindClosest("a---a", "a", 3, out _));
    }

    [Theory]
    [InlineData("Widget")]
    [InlineData("café")]
    [InlineData("@event")]
    public void CSharpArityLookup_PreservesTrimmedColumnsBoundariesAndFallbacks(string spelling)
    {
        var name = spelling.TrimStart('@');
        var context = $"{name}Suffix<int> first; {spelling}<int> one; {spelling}<int, string> two; {spelling}.Member;";
        var one = context.IndexOf(spelling + "<int> one", StringComparison.Ordinal);
        var two = context.IndexOf(spelling + "<int, string>", StringComparison.Ordinal);
        var member = context.IndexOf(spelling + ".Member", StringComparison.Ordinal);
        var escapeOffset = spelling.StartsWith('@') ? 1 : 0;
        Assert.Equal(1, CSharpTypeReferenceArity.GetReferenceArity(context, name, one + escapeOffset + 1));
        Assert.Equal(1, CSharpTypeReferenceArity.GetReferenceArity(context, name, one + escapeOffset + 5));
        Assert.Equal(2, CSharpTypeReferenceArity.GetReferenceArity(context, name, two + escapeOffset + 1));
        Assert.True(CSharpTypeReferenceArity.IsMemberReceiver(context, name, member + escapeOffset + 1));
        foreach (var column in new long?[] { null, 0, -1, long.MaxValue, 1 })
            Assert.Equal(1, CSharpTypeReferenceArity.GetReferenceArity(context, name, column));
        Assert.True(CSharpTypeReferenceArity.IsMemberReceiver(context, name, int.MaxValue));

        var calls = $"{spelling}(9); new {spelling}(1); new {spelling}(1, 2);";
        var firstNew = calls.IndexOf("new " + spelling, StringComparison.Ordinal) + 4 + escapeOffset;
        var secondNew = calls.LastIndexOf("new " + spelling, StringComparison.Ordinal) + 4 + escapeOffset;
        Assert.Equal(1, CSharpTypeReferenceArity.GetInvocationArgumentCount(calls, name, firstNew + 1));
        Assert.Equal(2, CSharpTypeReferenceArity.GetInvocationArgumentCount(calls, name, secondNew + 1));
        foreach (var column in new long?[] { null, 0, -1, long.MaxValue, 1 })
            Assert.Equal(1, CSharpTypeReferenceArity.GetInvocationArgumentCount(calls, name, column));
        Assert.Equal(2, CSharpTypeReferenceArity.GetInvocationArgumentCount(calls, name, int.MaxValue));
        Assert.Null(CSharpTypeReferenceArity.GetInvocationArgumentCount($"{spelling}(9)", name, 1));
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("razor")]
    [InlineData("blazor")]
    [InlineData("cshtml")]
    [InlineData("java")]
    [InlineData("python")]
    [InlineData("javascript")]
    [InlineData("typescript")]
    [InlineData("cpp")]
    [InlineData("go")]
    [InlineData("rust")]
    [InlineData("kotlin")]
    public void WriterQualifierLookup_PreservesDenseCoordinatesAcrossRawAndProviderPaths(string language)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_reference_occurrences");
        using var db = new DbContext(DbOpenIntent.WriteIndex, Path.Combine(project.Root, "index.db"));
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        using var transaction = writer.BeginTransaction();
        using var graph = writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: true, useFreshReferenceResolutionDefaults: true);
        using var raw = writer.BeginAuthoritativeFreshBulkInsertScope(true, default);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "source.fixture", Lang = language, Size = 100, Lines = 1,
            Checksum = "occurrences", Modified = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
        });
        var context = new StringBuilder();
        var references = new List<ReferenceRecord>();
        for (var index = 0; index < 64; index++)
        {
            context.Append($"Receiver{index}.");
            references.Add(new ReferenceRecord
            {
                FileId = fileId, SymbolName = "Run", ReferenceKind = "call", Line = 1,
                Column = context.Length + 1,
            });
            context.Append("Run(); ");
        }
        var sourceLine = context.ToString();
        foreach (var reference in references)
            reference.Context = sourceLine;
        writer.InsertReferences(references, refreshMutualRecursionFlags: false);
        raw!.Complete();
        raw.Dispose();
        writer.InsertReferences(references, refreshMutualRecursionFlags: false);

        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT target_qualifier FROM symbol_references ORDER BY id";
        using var rows = command.ExecuteReader();
        for (var index = 0; index < 128; index++)
        {
            Assert.True(rows.Read());
            Assert.Equal($"Receiver{index % 64}", rows.GetString(0));
        }
        Assert.False(rows.Read());
    }

    private static IEnumerable<string> EnumerateShortTexts()
    {
        yield return string.Empty;
        var generation = new List<string> { string.Empty };
        for (var length = 1; length <= 5; length++)
        {
            generation = generation.SelectMany(prefix => "ab.".Select(next => prefix + next)).ToList();
            foreach (var text in generation)
                yield return text;
        }
    }
}
