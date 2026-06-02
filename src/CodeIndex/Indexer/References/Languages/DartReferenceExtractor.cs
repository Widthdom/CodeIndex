using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class DartReferenceExtractor
{
    private static readonly Regex SealedSubtypeRegex = new(
        @"^\s*(?:base\s+|final\s+|interface\s+|abstract\s+)*sealed\s+class\s+[A-Za-z_]\w*\s+extends\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex ExtensionOnRegex = new(
        @"^\s*extension(?:\s+[A-Za-z_]\w*)?\s+on\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex MixinListRegex = new(
        @"^\s*(?:base\s+|final\s+|interface\s+|abstract\s+|sealed\s+)*class\s+[A-Za-z_]\w*[^{;]*\bwith\s+(?<names>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)",
        RegexOptions.Compiled);

    private static readonly Regex NamedConstructorCallRegex = new(
        @"(?<![\w.])(?<name>[A-Z][A-Za-z_]\w*\.[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);

    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitSpecialReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "dart",
            preparedLine,
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container: null);
    }

    private static void EmitSpecialReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitSingleMatch(SealedSubtypeRegex, "sealed_subtype");
        EmitSingleMatch(ExtensionOnRegex, "extension_of");
        EmitMixinReferences();
        EmitNamedConstructorCalls();

        void EmitSingleMatch(Regex regex, string referenceKind)
        {
            var match = regex.Match(preparedLine);
            if (!match.Success)
                return;

            Add(match.Groups["name"].Value, match.Groups["name"].Index, referenceKind);
        }

        void EmitMixinReferences()
        {
            var match = MixinListRegex.Match(preparedLine);
            if (!match.Success)
                return;

            var names = match.Groups["names"];
            foreach (Match name in Regex.Matches(names.Value, @"[A-Za-z_]\w*"))
                Add(name.Value, names.Index + name.Index, "mixin_in");
        }

        void EmitNamedConstructorCalls()
        {
            foreach (Match match in NamedConstructorCallRegex.Matches(preparedLine))
            {
                var name = match.Groups["name"];
                Add(name.Value, name.Index, "named_ctor_call");
            }
        }

        void Add(string name, int nameIndex, string referenceKind)
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameIndex,
                referenceKind,
                context,
                lineNumber,
                resolveContainerForColumn(nameIndex + 1),
                "dart");
        }
    }
}
