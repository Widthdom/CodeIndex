using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex GraphQLInputBlockRegex = new(
        @"^\s*(?:extend\s+)?input\s+(?<name>\w+)[^{]*\{(?<body>.*?)^\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex GraphQLInputFieldRegex = new(
        @"^\s*(?<name>[_A-Za-z]\w*)\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex GraphQLUnionDeclarationRegex = new(
        @"^\s*(?:extend\s+)?union\s+(?<name>\w+)(?:\s+@\w+(?:\([^)]*\))?)*\s*=\s*(?<variants>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GraphQLUnionHeaderRegex = new(
        @"^\s*(?:extend\s+)?union\s+(?<name>\w+)(?:\s+@\w+(?:\([^)]*\))?)*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GraphQLUnionVariantRegex = new(
        @"\|?\s*(?<name>[_A-Za-z]\w*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GraphQLDeclarationStartRegex = new(
        @"^\s*(?:extend\s+)?(?:type|interface|input|enum|union|scalar|schema|query|mutation|subscription|fragment|directive)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void ExtractGraphQLMemberSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        var hasInputBlocks = LinesContain(lines, "input", StringComparison.Ordinal);
        var hasUnions = LinesContain(lines, "union", StringComparison.Ordinal);
        if (!hasInputBlocks && !hasUnions)
            return;

        if (hasInputBlocks)
        {
            var content = string.Join('\n', lines);
            var lineStarts = BuildLineStarts(content);
            foreach (Match inputMatch in GraphQLInputBlockRegex.Matches(content))
            {
                var inputName = inputMatch.Groups["name"].Value;
                var body = inputMatch.Groups["body"];
                foreach (Match fieldMatch in GraphQLInputFieldRegex.Matches(body.Value))
                {
                    var fieldGroup = fieldMatch.Groups["name"];
                    var absoluteIndex = body.Index + fieldGroup.Index;
                    var lineNumber = GetLineNumberFromOffset(lineStarts, absoluteIndex);
                    AddSymbolRecord(
                        symbols,
                        null,
                        lineNumber,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = fieldGroup.Value,
                            Line = lineNumber,
                            StartLine = lineNumber,
                            StartColumn = absoluteIndex - lineStarts[lineNumber - 1],
                            EndLine = lineNumber,
                            Signature = lines[lineNumber - 1].Trim(),
                            ContainerKind = "class",
                            ContainerName = inputName,
                        },
                        lines[lineNumber - 1]);
                }
            }
        }

        if (!hasUnions)
            return;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.IndexOf("union", StringComparison.Ordinal) < 0)
                continue;

            var match = GraphQLUnionDeclarationRegex.Match(line);
            if (match.Success)
            {
                var unionName = match.Groups["name"].Value;
                AddGraphQLUnionVariantSymbols(fileId, lines, lineIndex, match.Groups["variants"].Value, match.Groups["variants"].Index, unionName, symbols);
                for (var continuationIndex = lineIndex + 1; continuationIndex < lines.Length; continuationIndex++)
                {
                    var continuation = lines[continuationIndex];
                    if (string.IsNullOrWhiteSpace(continuation) || GraphQLDeclarationStartRegex.IsMatch(continuation))
                        break;

                    AddGraphQLUnionVariantSymbols(fileId, lines, continuationIndex, continuation, 0, unionName, symbols);
                }

                continue;
            }

            var headerMatch = GraphQLUnionHeaderRegex.Match(line);
            if (!headerMatch.Success)
                continue;

            var headerUnionName = headerMatch.Groups["name"].Value;
            for (var continuationIndex = lineIndex + 1; continuationIndex < lines.Length; continuationIndex++)
            {
                var continuation = lines[continuationIndex];
                if (string.IsNullOrWhiteSpace(continuation) || GraphQLDeclarationStartRegex.IsMatch(continuation))
                    break;

                var equalsIndex = continuation.IndexOf('=', StringComparison.Ordinal);
                if (equalsIndex >= 0)
                {
                    AddGraphQLUnionVariantSymbols(fileId, lines, continuationIndex, continuation[(equalsIndex + 1)..], equalsIndex + 1, headerUnionName, symbols);
                    for (var variantIndex = continuationIndex + 1; variantIndex < lines.Length; variantIndex++)
                    {
                        var variantContinuation = lines[variantIndex];
                        if (string.IsNullOrWhiteSpace(variantContinuation) || GraphQLDeclarationStartRegex.IsMatch(variantContinuation))
                            break;

                        AddGraphQLUnionVariantSymbols(fileId, lines, variantIndex, variantContinuation, 0, headerUnionName, symbols);
                    }

                    lineIndex = continuationIndex;
                    break;
                }
            }
        }
    }

    private static void AddGraphQLUnionVariantSymbols(
        long fileId,
        string[] lines,
        int lineIndex,
        string variantText,
        int baseColumn,
        string unionName,
        List<SymbolRecord> symbols)
    {
        variantText = StripGraphQLUnionVariantTrivia(variantText);
        if (string.IsNullOrWhiteSpace(variantText))
            return;

        foreach (Match variantMatch in GraphQLUnionVariantRegex.Matches(variantText))
        {
            var variantName = variantMatch.Groups["name"].Value;
            if (variantName == "extend" || variantName == "union")
                continue;

            AddSymbolRecord(
                symbols,
                null,
                lineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "reference",
                    Name = variantName,
                    Line = lineIndex + 1,
                    StartLine = lineIndex + 1,
                    StartColumn = baseColumn + variantMatch.Groups["name"].Index,
                    EndLine = lineIndex + 1,
                    Signature = lines[lineIndex].Trim(),
                    ContainerKind = "class",
                    ContainerName = unionName,
                },
                lines[lineIndex]);
        }
    }

    private static string StripGraphQLUnionVariantTrivia(string text)
    {
        var commentIndex = text.IndexOf('#', StringComparison.Ordinal);
        if (commentIndex >= 0)
            text = text[..commentIndex];

        var directiveIndex = text.IndexOf('@', StringComparison.Ordinal);
        if (directiveIndex >= 0)
            text = text[..directiveIndex];

        return text;
    }
}
