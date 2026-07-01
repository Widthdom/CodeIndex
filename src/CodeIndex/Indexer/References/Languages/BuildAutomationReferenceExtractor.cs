using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class BuildAutomationReferenceExtractor
{
    private static readonly Regex CMakeCommandRegex = new(
        @"^\s*(?<command>[A-Za-z_]\w*)\s*\((?<args>.*)\)\s*(?:#.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JustImportRegex = new(
        @"^\s*(?:import|mod)\s+[""'](?<name>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JustRecipeRegex = new(
        @"^(?<name>[A-Za-z_][\w.-]*)(?:\s+[^:#\r\n]+)?\s*:(?![:=])(?<deps>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MsBuildElementRegex = new(
        @"<\s*(?<element>[A-Za-z_][\w.-]*)(?<attrs>[^<>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MsBuildAttributeRegex = new(
        @"(?<name>[A-Za-z_:][\w:.-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> CMakeIgnoredDependencyTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "debug",
        "general",
        "INTERFACE",
        "LINK_PRIVATE",
        "LINK_PUBLIC",
        "optimized",
        "PRIVATE",
        "PUBLIC",
    };

    private static readonly HashSet<string> MsBuildImportItemElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "Analyzer",
        "Compile",
        "Content",
        "None",
        "PackageReference",
        "ProjectReference",
        "Reference",
    };

    public static void EmitReferences(
        string language,
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        switch (language)
        {
            case "cmake":
                EmitCMakeReferences(preparedLine, context, lineNumber, references, seen, fileId, container);
                break;
            case "justfile":
                EmitJustfileReferences(preparedLine, context, lineNumber, references, seen, fileId, container);
                break;
            case "msbuild":
                EmitMsBuildReferences(preparedLine, context, lineNumber, references, seen, fileId, container);
                break;
        }
    }

    private static void EmitCMakeReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        var match = CMakeCommandRegex.Match(line);
        if (!match.Success)
            return;

        var command = match.Groups["command"].Value;
        var argsGroup = match.Groups["args"];
        var args = TokenizeBuildArguments(argsGroup.Value, argsGroup.Index);
        if (args.Count == 0)
            return;

        if (IsCMakeImportCommand(command))
        {
            AddReference(references, seen, fileId, args[0], "import", context, lineNumber, container, "cmake");
            return;
        }

        if (string.Equals(command, "target_link_libraries", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "add_dependencies", StringComparison.OrdinalIgnoreCase))
        {
            var targetContainer = IsIgnoredCMakeDependency(args[0].Text)
                ? container
                : new SymbolRecord { Kind = "function", Name = args[0].Text };
            for (var i = 1; i < args.Count; i++)
            {
                var token = args[i];
                if (IsIgnoredCMakeDependency(token.Text))
                    continue;

                AddReference(references, seen, fileId, token, "call", context, lineNumber, targetContainer, "cmake");
            }
        }
    }

    private static void EmitJustfileReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        var importMatch = JustImportRegex.Match(line);
        if (importMatch.Success)
        {
            AddReference(references, seen, fileId, importMatch, "import", context, lineNumber, container, "justfile");
            return;
        }

        var recipeMatch = JustRecipeRegex.Match(line);
        if (!recipeMatch.Success)
            return;

        var deps = recipeMatch.Groups["deps"];
        var recipeContainer = new SymbolRecord
        {
            Kind = "function",
            Name = recipeMatch.Groups["name"].Value,
        };
        foreach (var token in TokenizeBuildArguments(StripJustComment(deps.Value), deps.Index))
        {
            if (!IsBuildIdentifier(token.Text))
                continue;

            AddReference(references, seen, fileId, token, "call", context, lineNumber, recipeContainer, "justfile");
        }
    }

    private static void EmitMsBuildReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        if (line.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
            return;

        foreach (Match elementMatch in MsBuildElementRegex.Matches(line))
        {
            if (elementMatch.Value.StartsWith("</", StringComparison.Ordinal))
                continue;

            var elementName = elementMatch.Groups["element"].Value;
            var attrs = elementMatch.Groups["attrs"];
            foreach (Match attrMatch in MsBuildAttributeRegex.Matches(attrs.Value))
            {
                var attrName = attrMatch.Groups["name"].Value;
                var valueGroup = attrMatch.Groups["double"].Success
                    ? attrMatch.Groups["double"]
                    : attrMatch.Groups["single"];
                var valueIndex = attrs.Index + valueGroup.Index;

                if (IsMsBuildTargetListAttribute(attrName))
                {
                    foreach (var token in TokenizeMsBuildList(valueGroup.Value, valueIndex))
                        AddReference(references, seen, fileId, token, "call", context, lineNumber, container, "msbuild");
                }
                else if (IsMsBuildImportAttribute(elementName, attrName))
                {
                    var trimmed = valueGroup.Value.Trim();
                    var tokenIndex = trimmed.Length == 0
                        ? -1
                        : valueIndex + valueGroup.Value.IndexOf(trimmed, StringComparison.Ordinal);
                    if (tokenIndex >= 0)
                        AddReference(references, seen, fileId, new BuildToken(trimmed, tokenIndex), "import", context, lineNumber, container, "msbuild");
                }
            }
        }
    }

    private static bool IsCMakeImportCommand(string command)
        => command.Equals("include", StringComparison.OrdinalIgnoreCase)
           || command.Equals("find_package", StringComparison.OrdinalIgnoreCase)
           || command.Equals("add_subdirectory", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredCMakeDependency(string value)
        => value.Length == 0
           || CMakeIgnoredDependencyTokens.Contains(value)
           || value[0] is '$' or '@' or '-';

    private static bool IsMsBuildTargetListAttribute(string attrName)
        => attrName.Equals("DependsOnTargets", StringComparison.OrdinalIgnoreCase)
           || attrName.Equals("BeforeTargets", StringComparison.OrdinalIgnoreCase)
           || attrName.Equals("AfterTargets", StringComparison.OrdinalIgnoreCase)
           || attrName.Equals("Targets", StringComparison.OrdinalIgnoreCase);

    private static bool IsMsBuildImportAttribute(string elementName, string attrName)
        => (elementName.Equals("Import", StringComparison.OrdinalIgnoreCase)
            && attrName.Equals("Project", StringComparison.OrdinalIgnoreCase))
           || (MsBuildImportItemElements.Contains(elementName)
               && attrName.Equals("Include", StringComparison.OrdinalIgnoreCase));

    private static List<BuildToken> TokenizeBuildArguments(string text, int absoluteStartIndex)
    {
        var tokens = new List<BuildToken>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            if (index >= text.Length)
                break;

            var quote = text[index] is '"' or '\'' ? text[index] : '\0';
            var start = quote == '\0' ? index : index + 1;
            if (quote != '\0')
                index++;

            while (index < text.Length)
            {
                if (quote == '\0' && char.IsWhiteSpace(text[index]))
                    break;
                if (quote != '\0' && text[index] == quote)
                    break;
                index++;
            }

            var tokenText = text[start..index];
            if (tokenText.Length > 0)
                tokens.Add(new BuildToken(tokenText, absoluteStartIndex + start));

            if (quote != '\0' && index < text.Length && text[index] == quote)
                index++;
        }

        return tokens;
    }

    private static IEnumerable<BuildToken> TokenizeMsBuildList(string value, int absoluteStartIndex)
    {
        var start = -1;
        for (var i = 0; i <= value.Length; i++)
        {
            var isSeparator = i == value.Length || value[i] is ';' or ',' || char.IsWhiteSpace(value[i]);
            if (!isSeparator)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                var token = value[start..i];
                if (token.Length > 0)
                    yield return new BuildToken(token, absoluteStartIndex + start);
                start = -1;
            }
        }
    }

    private static string StripJustComment(string value)
    {
        var commentIndex = value.IndexOf('#', StringComparison.Ordinal);
        return commentIndex >= 0 ? value[..commentIndex] : value;
    }

    private static bool IsBuildIdentifier(string value)
        => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_');

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

    private static void AddReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        BuildToken token,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
        => ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            token.Text,
            token.Index,
            referenceKind,
            context,
            lineNumber,
            container,
            language);

    private readonly record struct BuildToken(string Text, int Index);
}
