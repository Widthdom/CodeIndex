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
        public IReadOnlyDictionary<string, string>? MarkdownReferenceTargets { get; set; }
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
        @"!?\[(?<text>[^\]\r\n]+)\]\[(?<label>[^\]\r\n]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static MarkupState CreateState(string language, string[] lines) =>
        new()
        {
            MarkdownReferenceTargets = language == "markdown"
                ? BuildMarkdownReferenceTargets(lines)
                : null,
        };

    public static void EmitReferences(
        string language,
        string originalLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(GraphQLFragmentSpreadRegex, scanLine, references))
        {
            AddReference(references, seen, fileId, match, "call", context, lineNumber, lineContainer, "graphql");
        }

        if (!isDirectiveDeclaration)
        {
            foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(GraphQLTypeConditionRegex, scanLine, references))
            {
                AddGraphQLTypeReference(references, seen, fileId, match.Groups["name"], context, lineNumber, lineContainer);
            }
        }

        var implementsMatch = GraphQLImplementsRegex.Match(scanLine);
        if (implementsMatch.Success)
            EmitGraphQLTypeTokens(implementsMatch.Groups["tail"], context, lineNumber, references, seen, fileId, lineContainer);

        var unionMatch = GraphQLUnionRegex.Match(scanLine);
        if (unionMatch.Success)
            EmitGraphQLTypeTokens(unionMatch.Groups["tail"], context, lineNumber, references, seen, fileId, lineContainer);

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(GraphQLFieldTypeRegex, scanLine, references))
        {
            AddGraphQLTypeReference(references, seen, fileId, match.Groups["name"], context, lineNumber, lineContainer);
        }

        if (!isDirectiveDeclaration)
        {
            foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(GraphQLDirectiveUseRegex, scanLine, references))
            {
                AddReference(references, seen, fileId, match, "call", context, lineNumber, lineContainer, "graphql");
            }
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
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container)
    {
        foreach (Match token in ReferenceExtractor.EnumerateReferenceMatches(GraphQLNameTokenRegex, group.Value, references))
        {
            AddGraphQLTypeReference(references, seen, fileId, token.Groups[0], context, lineNumber, container, group.Index);
        }
    }

    private static void AddGraphQLTypeReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container,
        MarkupState? state)
    {
        state ??= new MarkupState();
        if (!TryPrepareHtmlLineForReferenceScan(line, state, out var scanLine))
            return;

        foreach (Match tagMatch in ReferenceExtractor.EnumerateReferenceMatches(HtmlTagRegex, scanLine, references))
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
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container)
    {
        foreach (Match attrMatch in ReferenceExtractor.EnumerateReferenceMatches(HtmlAttributeRegex, attrsGroup.Value, references))
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
        ReferenceDedupeSet seen,
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
            return;

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(MarkdownInlineLinkRegex, scanLine, references))
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

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(MarkdownReferenceLinkRegex, scanLine, references))
        {
            var label = match.Groups["label"].Value.Trim();
            if (label.Length == 0)
                label = match.Groups["text"].Value.Trim();

            if (label.Length > 0
                && state.MarkdownReferenceTargets?.TryGetValue(label, out var target) == true)
            {
                AddMarkdownTargetReference(
                    target,
                    match.Groups["label"].Success
                        ? match.Groups["label"].Index
                        : match.Groups["text"].Index,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container);
            }
        }
    }

    private static void AddMarkdownTargetReference(
        string rawTarget,
        int targetIndex,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container)
    {
        var target = NormalizeMarkdownLinkTarget(rawTarget);
        if (target.Length == 0)
            return;

        var fragmentIndex = target.IndexOf('#');
        if (fragmentIndex == 0)
        {
            AddMarkdownAnchorReference(
                target[1..],
                targetIndex,
                targetQualifier: null,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                container);
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

        if (fragmentIndex > 0
            && !target.Contains("://", StringComparison.Ordinal)
            && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            AddMarkdownAnchorReference(
                target[(fragmentIndex + 1)..],
                targetIndex + fragmentIndex + 1,
                target[..fragmentIndex],
                context,
                lineNumber,
                references,
                seen,
                fileId,
                container);
        }
    }

    private static void AddMarkdownAnchorReference(
        string anchor,
        int targetIndex,
        string? targetQualifier,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container)
    {
        var explicitAnchorIdentity = MarkdownAnchorIdentity.DecodeExplicitAnchorFragment(anchor);
        var headingIdentity = MarkdownAnchorIdentity.NormalizeHeadingFragment(anchor);
        if (explicitAnchorIdentity.Length == 0)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            explicitAnchorIdentity,
            targetIndex,
            "reference",
            context,
            lineNumber,
            container,
            "markdown",
            targetQualifier,
            sourceLength: anchor.Length,
            identitySymbolNameFolded: headingIdentity);
    }

    private static IReadOnlyDictionary<string, string> BuildMarkdownReferenceTargets(string[] lines)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scanState = new MarkupState();
        foreach (var line in lines)
        {
            if (TryToggleMarkdownFence(line, scanState) || scanState.InMarkdownFence)
                continue;

            var match = MarkdownReferenceDefinitionRegex.Match(StripMarkdownInlineCode(line));
            if (!match.Success)
                continue;

            var label = match.Groups["label"].Value.Trim();
            var target = NormalizeMarkdownLinkTarget(match.Groups["target"].Value);
            if (label.Length > 0 && target.Length > 0)
                targets[label] = target;
        }

        return targets;
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
        foreach (Match tagMatch in BoundedRegex.EnumerateMatches(HtmlTagRegex, line))
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
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container)
    {
        var target = rawTarget.Trim();
        if (target.Length == 0)
            return;

        if (referenceKind == "reference")
            target = MarkdownAnchorIdentity.NormalizeHeadingFragment(target);

        AddHtmlReference(references, seen, fileId, target, targetIndex, referenceKind, context, lineNumber, container);
    }

    private static void AddHtmlReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
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
        if (line.IndexOf('`') < 0)
            return line;

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

    private static void AddReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
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
