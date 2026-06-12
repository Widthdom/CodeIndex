using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class MarkupSchemaReferenceExtractor
{
    internal sealed class MarkupState
    {
        public bool InMarkdownFence { get; set; }
        public char MarkdownFenceChar { get; set; }
        public int MarkdownFenceLength { get; set; }
        public bool InHtmlComment { get; set; }
        public string? HtmlRawTextTag { get; set; }
        public int GraphQLBraceDepth { get; set; }
        public List<GraphQLFrame> GraphQLFrames { get; } = [];
    }

    internal readonly record struct GraphQLFrame(string Kind, string Name, int Depth);

    private static readonly Regex GraphQLFragmentSpreadRegex = new(
        @"\.\.\.\s*(?!on\b)(?<name>[_A-Za-z]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphQLTypeConditionRegex = new(
        @"\bon\s+(?<name>[_A-Za-z]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphQLImplementsRegex = new(
        @"\bimplements\s+(?<tail>[^#{]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphQLUnionRegex = new(
        @"^\s*(?:extend\s+)?union\s+[_A-Za-z]\w*(?:\s+@\w+(?:\([^)]*\))?)*\s*=\s*(?<tail>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphQLFieldTypeRegex = new(
        @":\s*(?:\[\s*)*(?<name>[_A-Za-z]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphQLDirectiveUseRegex = new(
        @"@(?<name>[_A-Za-z]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphQLDeclarationRegex = new(
        @"^\s*(?:extend\s+)?(?<kind>type|interface|input|enum|union|scalar|query|mutation|subscription|fragment)\s+(?<name>[_A-Za-z]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GraphQLNameTokenRegex = new(
        @"[_A-Za-z]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> GraphQLBuiltInTypes = new(StringComparer.Ordinal)
    {
        "Boolean",
        "Float",
        "ID",
        "Int",
        "String",
    };

    private static readonly Regex HtmlTagRegex = new(
        @"<\s*(?<closing>/)?(?<name>[A-Za-z][\w:.-]*)(?<attrs>[^<>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlAttributeRegex = new(
        @"(?<name>[A-Za-z_:][\w:.-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s""'=<>`]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> HtmlReservedHyphenatedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "annotation-xml",
        "color-profile",
        "font-face",
        "font-face-format",
        "font-face-name",
        "font-face-src",
        "font-face-uri",
        "missing-glyph",
    };

    private static readonly HashSet<string> HtmlSrcResourceTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio",
        "embed",
        "iframe",
        "img",
        "input",
        "script",
        "source",
        "track",
        "video",
    };

    private static readonly HashSet<string> HtmlHrefResourceTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "area",
        "base",
        "link",
    };

    private static readonly Regex MarkdownReferenceDefinitionRegex = new(
        @"^\s{0,3}\[(?<label>[^\]\r\n]+)\]:\s*(?<target><[^>\r\n]+>|[^\s\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarkdownInlineLinkRegex = new(
        @"!?\[[^\]\r\n]*\]\(\s*(?<target><[^>\r\n]+>|[^)\s\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarkdownReferenceLinkRegex = new(
        @"!?\[[^\]\r\n]+\]\[(?<label>[^\]\r\n]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void EmitReferences(
        string language,
        string originalLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        MarkupState? state)
    {
        switch (language)
        {
            case "graphql":
                EmitGraphQLReferences(originalLine, context, lineNumber, references, seen, fileId, container, state);
                break;
            case "html":
                EmitHtmlReferences(originalLine, context, lineNumber, references, seen, fileId, container, state);
                break;
            case "markdown":
                EmitMarkdownReferences(originalLine, context, lineNumber, references, seen, fileId, container, state);
                break;
        }
    }

    private static void EmitGraphQLReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        MarkupState? state = null)
    {
        var scanLine = StripGraphQLComment(line);
        if (string.IsNullOrWhiteSpace(scanLine))
            return;

        var declarationMatch = GraphQLDeclarationRegex.Match(scanLine);
        var lineContainer = TryGetGraphQLLineContainer(declarationMatch) ?? TryGetGraphQLStateContainer(state) ?? container;
        var isDirectiveDeclaration = scanLine.TrimStart().StartsWith("directive ", StringComparison.Ordinal);
        foreach (Match match in GraphQLFragmentSpreadRegex.Matches(scanLine))
            AddReference(references, seen, fileId, match, "call", context, lineNumber, lineContainer, "graphql");

        if (!isDirectiveDeclaration)
        {
            foreach (Match match in GraphQLTypeConditionRegex.Matches(scanLine))
                AddGraphQLTypeReference(references, seen, fileId, match.Groups["name"], context, lineNumber, lineContainer);
        }

        var implementsMatch = GraphQLImplementsRegex.Match(scanLine);
        if (implementsMatch.Success)
            EmitGraphQLTypeTokens(implementsMatch.Groups["tail"], context, lineNumber, references, seen, fileId, lineContainer);

        var unionMatch = GraphQLUnionRegex.Match(scanLine);
        if (unionMatch.Success)
            EmitGraphQLTypeTokens(unionMatch.Groups["tail"], context, lineNumber, references, seen, fileId, lineContainer);

        foreach (Match match in GraphQLFieldTypeRegex.Matches(scanLine))
            AddGraphQLTypeReference(references, seen, fileId, match.Groups["name"], context, lineNumber, lineContainer);

        if (!isDirectiveDeclaration)
        {
            foreach (Match match in GraphQLDirectiveUseRegex.Matches(scanLine))
                AddReference(references, seen, fileId, match, "call", context, lineNumber, lineContainer, "graphql");
        }

        UpdateGraphQLState(scanLine, declarationMatch, state);
    }

    private static SymbolRecord? TryGetGraphQLLineContainer(Match match)
    {
        if (!match.Success)
            return null;

        var kind = match.Groups["kind"].Value switch
        {
            "interface" => "interface",
            "enum" => "enum",
            "query" or "mutation" or "subscription" or "fragment" => "function",
            _ => "class",
        };

        return new SymbolRecord
        {
            Kind = kind,
            Name = match.Groups["name"].Value,
        };
    }

    private static SymbolRecord? TryGetGraphQLStateContainer(MarkupState? state)
    {
        if (state == null || state.GraphQLFrames.Count == 0)
            return null;

        var frame = state.GraphQLFrames[^1];
        return new SymbolRecord { Kind = frame.Kind, Name = frame.Name };
    }

    private static void UpdateGraphQLState(string line, Match declarationMatch, MarkupState? state)
    {
        if (state == null)
            return;

        if (declarationMatch.Success && line.IndexOf('{') >= 0)
        {
            var container = TryGetGraphQLLineContainer(declarationMatch);
            if (container != null)
                state.GraphQLFrames.Add(new GraphQLFrame(container.Kind, container.Name, state.GraphQLBraceDepth + 1));
        }

        foreach (var ch in line)
        {
            if (ch == '{')
                state.GraphQLBraceDepth++;
            else if (ch == '}')
                state.GraphQLBraceDepth = Math.Max(0, state.GraphQLBraceDepth - 1);
        }

        while (state.GraphQLFrames.Count > 0 && state.GraphQLFrames[^1].Depth > state.GraphQLBraceDepth)
            state.GraphQLFrames.RemoveAt(state.GraphQLFrames.Count - 1);
    }

    private static void EmitGraphQLTypeTokens(
        Group group,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        foreach (Match token in GraphQLNameTokenRegex.Matches(group.Value))
            AddGraphQLTypeReference(references, seen, fileId, token.Groups[0], context, lineNumber, container, group.Index);
    }

    private static void AddGraphQLTypeReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Group nameGroup,
        string context,
        int lineNumber,
        SymbolRecord? container,
        int baseIndex = 0)
    {
        var name = nameGroup.Value;
        if (GraphQLBuiltInTypes.Contains(name))
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name,
            baseIndex + nameGroup.Index,
            "type_reference",
            context,
            lineNumber,
            container,
            "graphql");
    }

    private static void EmitHtmlReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        MarkupState? state)
    {
        state ??= new MarkupState();
        if (!TryPrepareHtmlLineForReferenceScan(line, state, out var scanLine))
            return;

        foreach (Match tagMatch in HtmlTagRegex.Matches(scanLine))
        {
            if (tagMatch.Groups["closing"].Success)
                continue;

            var tagName = tagMatch.Groups["name"].Value;
            if (tagName.Contains('-') && !HtmlReservedHyphenatedTags.Contains(tagName))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    tagName,
                    tagMatch.Groups["name"].Index,
                    "call",
                    context,
                    lineNumber,
                    container,
                    "html");
            }

            EmitHtmlAttributeReferences(tagName, tagMatch.Groups["attrs"], context, lineNumber, references, seen, fileId, container);
        }

        UpdateHtmlRawTextState(scanLine, state);
    }

    private static void EmitHtmlAttributeReferences(
        string tagName,
        Group attrsGroup,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        foreach (Match attrMatch in HtmlAttributeRegex.Matches(attrsGroup.Value))
        {
            var attrName = attrMatch.Groups["name"].Value;
            var valueGroup = GetHtmlAttributeValueGroup(attrMatch);
            if (!valueGroup.Success || valueGroup.Value.Length == 0)
                continue;

            var attrValueIndex = attrsGroup.Index + valueGroup.Index;
            var attrNameLower = attrName.ToLowerInvariant();
            var tagNameLower = tagName.ToLowerInvariant();
            if (attrNameLower == "src" && HtmlSrcResourceTags.Contains(tagNameLower))
            {
                AddHtmlTargetReferences(valueGroup.Value, attrValueIndex, "import", context, lineNumber, references, seen, fileId, container);
            }
            else if (attrNameLower == "srcset" && HtmlSrcResourceTags.Contains(tagNameLower))
            {
                foreach (var (url, index) in EnumerateHtmlSrcsetUrls(valueGroup.Value, attrValueIndex))
                    AddHtmlTargetReferences(url, index, "import", context, lineNumber, references, seen, fileId, container);
            }
            else if ((attrNameLower == "href" || attrNameLower == "xlink:href") && HtmlHrefResourceTags.Contains(tagNameLower))
            {
                var kind = valueGroup.Value.TrimStart().StartsWith("#", StringComparison.Ordinal) ? "reference" : "import";
                AddHtmlTargetReferences(valueGroup.Value, attrValueIndex, kind, context, lineNumber, references, seen, fileId, container);
            }
            else if (attrNameLower == "data" && tagNameLower == "object")
            {
                AddHtmlTargetReferences(valueGroup.Value, attrValueIndex, "import", context, lineNumber, references, seen, fileId, container);
            }
            else if (attrNameLower == "poster" && tagNameLower == "video")
            {
                AddHtmlTargetReferences(valueGroup.Value, attrValueIndex, "import", context, lineNumber, references, seen, fileId, container);
            }
            else if (attrNameLower is "class" or "classname")
            {
                foreach (var (className, index) in EnumerateWhitespaceTokens(valueGroup.Value, attrValueIndex))
                    AddHtmlReference(references, seen, fileId, className, index, "reference", context, lineNumber, container);
            }
        }
    }

    private static void EmitMarkdownReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        MarkupState? state)
    {
        state ??= new MarkupState();
        if (TryToggleMarkdownFence(line, state))
            return;

        if (state.InMarkdownFence)
            return;

        var scanLine = StripMarkdownInlineCode(line);
        var definitionMatch = MarkdownReferenceDefinitionRegex.Match(scanLine);
        if (definitionMatch.Success)
        {
            AddMarkdownTargetReference(
                definitionMatch.Groups["target"].Value,
                definitionMatch.Groups["target"].Index,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                container);
            return;
        }

        foreach (Match match in MarkdownInlineLinkRegex.Matches(scanLine))
        {
            AddMarkdownTargetReference(
                match.Groups["target"].Value,
                match.Groups["target"].Index,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                container);
        }

        foreach (Match match in MarkdownReferenceLinkRegex.Matches(scanLine))
        {
            var label = match.Groups["label"].Value.Trim();
            if (label.Length == 0)
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                NormalizeMarkdownAnchor(label),
                match.Groups["label"].Index,
                "reference",
                context,
                lineNumber,
                container,
                "markdown");
        }
    }

    private static void AddMarkdownTargetReference(
        string rawTarget,
        int targetIndex,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        var target = NormalizeMarkdownLinkTarget(rawTarget);
        if (target.Length == 0)
            return;

        if (target.StartsWith("#", StringComparison.Ordinal))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                NormalizeMarkdownAnchor(target),
                targetIndex,
                "reference",
                context,
                lineNumber,
                container,
                "markdown");
            return;
        }

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            target,
            targetIndex,
            "import",
            context,
            lineNumber,
            container,
            "markdown");
    }

    private static string StripGraphQLComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            else if (ch == '#' && !inString)
                return line[..i];
        }

        return line;
    }

    private static bool TryPrepareHtmlLineForReferenceScan(string line, MarkupState state, out string scanLine)
    {
        scanLine = string.Empty;
        if (state.HtmlRawTextTag != null)
        {
            if (line.IndexOf("</" + state.HtmlRawTextTag, StringComparison.OrdinalIgnoreCase) >= 0)
                state.HtmlRawTextTag = null;
            return false;
        }

        if (state.InHtmlComment)
        {
            var commentEnd = line.IndexOf("-->", StringComparison.Ordinal);
            if (commentEnd < 0)
                return false;

            state.InHtmlComment = false;
            line = new string(' ', commentEnd + 3) + line[(commentEnd + 3)..];
        }

        var chars = line.ToCharArray();
        var searchStart = 0;
        while (searchStart < chars.Length)
        {
            var commentStart = line.IndexOf("<!--", searchStart, StringComparison.Ordinal);
            if (commentStart < 0)
                break;

            var commentEnd = line.IndexOf("-->", commentStart + 4, StringComparison.Ordinal);
            var endExclusive = commentEnd < 0 ? chars.Length : commentEnd + 3;
            for (var i = commentStart; i < endExclusive; i++)
                chars[i] = ' ';

            if (commentEnd < 0)
            {
                state.InHtmlComment = true;
                break;
            }

            searchStart = endExclusive;
        }

        scanLine = new string(chars);
        return !string.IsNullOrWhiteSpace(scanLine);
    }

    private static void UpdateHtmlRawTextState(string line, MarkupState state)
    {
        foreach (Match tagMatch in HtmlTagRegex.Matches(line))
        {
            if (tagMatch.Groups["closing"].Success)
                continue;

            var tagName = tagMatch.Groups["name"].Value;
            if (!tagName.Equals("script", StringComparison.OrdinalIgnoreCase)
                && !tagName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.IndexOf("</" + tagName, tagMatch.Index + tagMatch.Length, StringComparison.OrdinalIgnoreCase) < 0)
                state.HtmlRawTextTag = tagName;
        }
    }

    private static Group GetHtmlAttributeValueGroup(Match attrMatch)
    {
        if (attrMatch.Groups["double"].Success)
            return attrMatch.Groups["double"];
        if (attrMatch.Groups["single"].Success)
            return attrMatch.Groups["single"];
        return attrMatch.Groups["bare"];
    }

    private static void AddHtmlTargetReferences(
        string rawTarget,
        int targetIndex,
        string referenceKind,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        var target = rawTarget.Trim();
        if (target.Length == 0)
            return;

        if (referenceKind == "reference")
            target = NormalizeMarkdownAnchor(target);

        AddHtmlReference(references, seen, fileId, target, targetIndex, referenceKind, context, lineNumber, container);
    }

    private static void AddHtmlReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string name,
        int nameIndex,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name,
            nameIndex,
            referenceKind,
            context,
            lineNumber,
            container,
            "html");

    private static IEnumerable<(string Url, int Index)> EnumerateHtmlSrcsetUrls(string value, int absoluteStartIndex)
    {
        var start = 0;
        while (start < value.Length)
        {
            while (start < value.Length && (char.IsWhiteSpace(value[start]) || value[start] == ','))
                start++;

            if (start >= value.Length)
                yield break;

            var end = start;
            while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] != ',')
                end++;

            if (end > start)
                yield return (value[start..end], absoluteStartIndex + start);

            var comma = value.IndexOf(',', end);
            if (comma < 0)
                yield break;
            start = comma + 1;
        }
    }

    private static IEnumerable<(string Token, int Index)> EnumerateWhitespaceTokens(string value, int absoluteStartIndex)
    {
        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;

            var start = index;
            while (index < value.Length && !char.IsWhiteSpace(value[index]))
                index++;

            if (index > start)
                yield return (value[start..index], absoluteStartIndex + start);
        }
    }

    private static bool TryToggleMarkdownFence(string line, MarkupState state)
    {
        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
            index++;

        if (index > 3 || index >= line.Length)
            return false;

        var marker = line[index];
        if (marker is not ('`' or '~'))
            return false;

        var length = index;
        while (length < line.Length && line[length] == marker)
            length++;

        var markerLength = length - index;
        if (markerLength < 3)
            return false;

        if (state.InMarkdownFence)
        {
            if (marker != state.MarkdownFenceChar || markerLength < state.MarkdownFenceLength)
                return false;

            state.InMarkdownFence = false;
            state.MarkdownFenceChar = '\0';
            state.MarkdownFenceLength = 0;
            return true;
        }

        state.InMarkdownFence = true;
        state.MarkdownFenceChar = marker;
        state.MarkdownFenceLength = markerLength;
        return true;
    }

    private static string StripMarkdownInlineCode(string line)
    {
        var chars = line.ToCharArray();
        var inCode = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '`')
                continue;

            inCode = !inCode;
            chars[i] = ' ';
            i++;
            while (inCode && i < chars.Length)
            {
                if (chars[i] == '`')
                {
                    inCode = false;
                    chars[i] = ' ';
                    break;
                }

                chars[i] = ' ';
                i++;
            }
        }

        return new string(chars);
    }

    private static string NormalizeMarkdownLinkTarget(string rawTarget)
    {
        var target = rawTarget.Trim();
        if (target.Length >= 2 && target[0] == '<' && target[^1] == '>')
            target = target[1..^1].Trim();
        return target;
    }

    private static string NormalizeMarkdownAnchor(string value)
    {
        var anchor = value.Trim().TrimStart('#').Trim();
        if (anchor.Length == 0)
            return string.Empty;

        var chars = new List<char>(anchor.Length);
        var previousDash = false;
        foreach (var ch in anchor.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                chars.Add(ch);
                previousDash = ch == '-';
            }
            else if (char.IsWhiteSpace(ch) && !previousDash)
            {
                chars.Add('-');
                previousDash = true;
            }
        }

        return new string(chars.ToArray()).Trim('-');
    }

    private static void AddReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Match match,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
        => ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            match,
            referenceKind,
            context,
            lineNumber,
            container,
            language);
}
