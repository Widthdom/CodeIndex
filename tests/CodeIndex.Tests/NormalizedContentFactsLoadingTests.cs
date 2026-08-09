using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class FileIndexerTests
{
    [Fact]
    public void FileContentLoader_Load_HighInvalidUtf8RatioDropsLineDetailsAndPreservesIssues()
    {
        const int lineCount = 20_000;
        var rawBytes = new byte[(lineCount * 2) - 1];
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            rawBytes[lineIndex * 2] = 0xFF;
            if (lineIndex + 1 < lineCount)
                rawBytes[(lineIndex * 2) + 1] = (byte)'\n';
        }

        var loaded = LoadFileContentForTest(rawBytes);

        Assert.Contains("invalid UTF-8", loaded.Warning, StringComparison.Ordinal);
        Assert.Equal(lineCount, loaded.Facts.ReplacementCharacterCount);
        Assert.Null(loaded.Facts.ReplacementCharacterLines);

        var factsIssues = FileIndexer.ValidateContent(
            "invalid.txt",
            rawBytes,
            loaded.Content,
            "text",
            loaded.Inspection,
            loaded.Facts);
        var fallbackIssues = FileIndexer.ValidateContent(
            "invalid.txt",
            rawBytes,
            loaded.Content,
            "text");

        Assert.Equal(
            fallbackIssues.Select(IssueIdentity.FromIssue),
            factsIssues.Select(IssueIdentity.FromIssue));
        var aggregate = Assert.Single(factsIssues, issue => issue.Kind == "non_utf8_likely");
        Assert.Equal(FileIssue.OriginDecodeReplacement, aggregate.Origin);
        Assert.DoesNotContain(factsIssues, issue => issue.Kind == "replacement_char");
    }

    private readonly record struct IssueIdentity(
        string Kind,
        int Line,
        string Message,
        string? Origin,
        string? Severity)
    {
        internal static IssueIdentity FromIssue(FileIssue issue)
            => new(issue.Kind, issue.Line, issue.Message, issue.Origin, issue.Severity);
    }
}
