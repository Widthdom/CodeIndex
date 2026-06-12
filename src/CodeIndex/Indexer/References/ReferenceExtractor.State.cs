using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal readonly record struct CSharpMultiLineTypePatternState(
        bool WaitingForHead,
        string? PendingTypeExpression,
        int PendingTypeIndex,
        int PendingTypeLineNumber,
        string? PendingContext,
        SymbolRecord? PendingContainer);

    internal sealed class CSharpWhereConstraintState
    {
        public bool Active { get; set; }
        public HashSet<string> HeaderGenericParameterNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IgnoredSegments { get; } = new(StringComparer.Ordinal);
        public bool CollectingHeaderGenericParameters { get; set; }
        public int HeaderGenericParameterDepth { get; set; }
        public string HeaderGenericParameterText { get; set; } = string.Empty;
    }

    private static readonly string[] BuiltInLanguages =
    [
        "python", "javascript", "typescript", "csharp", "go", "rust",
        "java", "kotlin", "ruby", "perl", "c", "cpp", "php", "swift",
        "dart", "scala", "elixir", "lua", "commonlisp", "racket", "vb", "fsharp", "sql", "cobol", "batch",
        "assembly",
        "r", "powershell", "shell", "haskell",
        "gradle", "terraform", "protobuf", "dockerfile", "makefile", "cmake", "justfile", "msbuild",
        "zig", "css", "graphql", "html", "markdown", "fortran", "pascal", "objc", "smalltalk"
    ];

    private static readonly IReadOnlyDictionary<string, IReferenceExtractor> Extractors =
        BuiltInLanguages.ToDictionary(
            static language => language,
            static language => (IReferenceExtractor)new BuiltInReferenceExtractor(language),
            StringComparer.Ordinal);
}
