using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

public partial class DbReader
{
    private static class SymbolSearchQueryNormalizer
    {
        private sealed class NormalizedSymbolSearchQueryList : List<string>
        {
            public NormalizedSymbolSearchQueryList(IEnumerable<string> queries)
                : base(queries)
            {
            }
        }

        public static IReadOnlyList<string> MarkNormalized(IEnumerable<string> queries)
            => new NormalizedSymbolSearchQueryList(queries);

        public static IReadOnlyList<string>? NormalizeQueries(
            IReadOnlyList<string>? queries,
            string? lang,
            bool exact)
        {
            if (queries == null)
                return null;
            if (queries is NormalizedSymbolSearchQueryList)
                return queries;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var normalized = new List<string>();
            foreach (var query in queries)
            {
                var value = NormalizeForSymbolSearch(query, lang, exact) ?? query ?? string.Empty;
                if (value.Length == 0 || !seen.Add(value))
                    continue;
                normalized.Add(value);
            }

            return new NormalizedSymbolSearchQueryList(normalized);
        }

        public static string? Normalize(string? query, string? lang, bool exact)
        {
            if (!string.IsNullOrWhiteSpace(lang)
                && string.Equals(lang, "rust", StringComparison.OrdinalIgnoreCase))
            {
                return RustQueryStrategy.Normalize(query, exact);
            }

            if (!string.IsNullOrWhiteSpace(lang)
                && string.Equals(lang, "javascript", StringComparison.OrdinalIgnoreCase))
            {
                return JavaScriptQueryStrategy.Normalize(query);
            }

            var terraformNormalized = TerraformQueryStrategy.Normalize(query, lang);
            return terraformNormalized ?? CSharpQueryStrategy.NormalizeVerbatim(query, lang);
        }

        public static string? NormalizeForSymbolSearch(string? query, string? lang, bool exact)
        {
            if (RustQueryStrategy.ShouldPreserveQualifiedExactQuery(query, lang, exact))
                return query?.Trim();

            if (exact
                && !string.IsNullOrWhiteSpace(query)
                && SqlNameResolver.HasQualifier(query))
            {
                if (string.Equals(NormalizeQueryLanguage(lang), "csharp", StringComparison.Ordinal))
                    return CSharpSymbolNameNormalizer.NormalizeExplicitInterfaceQueryDisplayName(query);

                // Without a language filter the query must retain its original spelling for
                // non-C# exact matching. C#-specific display and identity aliases are supplied
                // through their own SQL parameters.
                // 言語フィルターがない場合、C# 以外の完全一致を保つため query の元表記を
                // 維持する。C# 専用の表示名・identity alias は個別の SQL parameter で渡す。
                if (string.IsNullOrWhiteSpace(lang))
                {
                    var terraformNormalized = TerraformQueryStrategy.Normalize(query, lang);
                    return terraformNormalized ?? query.Trim();
                }
            }

            return Normalize(query, lang, exact) ?? query;
        }

        public static string? NormalizeCSharpVerbatim(string? query, string? lang)
            => CSharpQueryStrategy.NormalizeVerbatim(query, lang);

        public static string? ComputeSwiftBacktickAlias(string? query, string? lang)
            => SwiftQueryStrategy.ComputeBacktickAlias(query, lang);

        public static bool ShouldPreserveRustQualifiedExactQuery(string? query, string? lang, bool exact)
            => RustQueryStrategy.ShouldPreserveQualifiedExactQuery(query, lang, exact);

        public static (string? QualifiedPath, string? ContainerPath, string? LeafName)
            NormalizeRustQualifiedExactQueryParts(string query)
            => RustQueryStrategy.NormalizeQualifiedExactQueryParts(query);

        private static class TerraformQueryStrategy
        {
            private static readonly Regex VarLocalModuleQueryRegex = new(
                @"^(?:var|local|module)\.(?<name>[A-Za-z_]\w*)(?:\..*)?$",
                RegexOptions.Compiled);

            private static readonly Regex DataQueryRegex = new(
                @"^data\.[A-Za-z_]\w*\.(?<name>[A-Za-z_]\w*)(?:\..*)?$",
                RegexOptions.Compiled);

            public static string? Normalize(string? query, string? lang)
            {
                if (!string.IsNullOrWhiteSpace(lang)
                    && !string.Equals(lang, "terraform", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(query))
                    return null;

                var trimmed = query.Trim();
                if (trimmed.Length == 0)
                    return null;

                // Terraform dotted prefixes are stored as bare names in references and symbols.
                var simpleMatch = VarLocalModuleQueryRegex.Match(trimmed);
                if (simpleMatch.Success)
                    return simpleMatch.Groups["name"].Value;

                var dataMatch = DataQueryRegex.Match(trimmed);
                return dataMatch.Success ? dataMatch.Groups["name"].Value : null;
            }
        }

        private static class JavaScriptQueryStrategy
        {
            public static string? Normalize(string? query)
            {
                if (query == null)
                    return null;

                var trimmed = query.Trim();
                if (trimmed.Length == 0)
                    return null;

                var commonJsPrefixLength = GetCommonJsPrefixLength(trimmed);
                if (commonJsPrefixLength == 0)
                    return trimmed;

                trimmed = trimmed[commonJsPrefixLength..];
                if (trimmed.Length == 0)
                    return null;

                trimmed = trimmed.TrimStart();
                if (trimmed.StartsWith(".", StringComparison.Ordinal))
                    trimmed = trimmed[1..].TrimStart();

                return GetCommonJsLeaf(trimmed);
            }

            private static int GetCommonJsPrefixLength(string query)
            {
                if (query.StartsWith("module.exports", StringComparison.Ordinal))
                {
                    var nextIndex = "module.exports".Length;
                    return query.Length > nextIndex && query[nextIndex] is '.' or '[' ? nextIndex : 0;
                }

                if (query.StartsWith("exports", StringComparison.Ordinal))
                {
                    var nextIndex = "exports".Length;
                    return query.Length > nextIndex && query[nextIndex] is '.' or '[' ? nextIndex : 0;
                }

                return 0;
            }

            private static string? GetCommonJsLeaf(string query)
            {
                var bracketLeaf = NormalizeBracketLeaf(query);
                if (bracketLeaf != null)
                    return bracketLeaf;

                var leafIndex = query.LastIndexOf('.');
                var leaf = leafIndex >= 0 ? query[(leafIndex + 1)..] : query;
                return NormalizeBracketLeaf(leaf) ?? (leaf.Length == 0 ? null : leaf);
            }

            private static string? NormalizeBracketLeaf(string query)
            {
                var trimmed = query.Trim();
                if (trimmed.Length < 3 || trimmed[0] != '[' || trimmed[^1] != ']')
                    return null;

                var inner = trimmed[1..^1].Trim();
                if (inner.Length < 2)
                    return null;

                var quote = inner[0];
                if (quote is not '\'' and not '"')
                    return null;
                if (inner[^1] != quote)
                    return null;

                var leaf = inner[1..^1].Trim();
                return leaf.Length == 0 ? null : leaf;
            }
        }

        private static class RustQueryStrategy
        {
            public static string? Normalize(string? query, bool exact = false)
            {
                if (query == null)
                    return null;

                var macroQuery = query.Trim();
                if (macroQuery.Length == 0)
                    return null;

                var isMacroQuery = macroQuery.EndsWith("!", StringComparison.Ordinal);
                if (isMacroQuery)
                    macroQuery = macroQuery[..^1].TrimEnd();
                if (macroQuery.Length == 0)
                    return null;

                if (exact && isMacroQuery && macroQuery.Contains("::", StringComparison.Ordinal))
                    return NormalizeQualifiedMacroQuery(macroQuery);

                var leafIndex = macroQuery.LastIndexOf("::", StringComparison.Ordinal);
                if (leafIndex >= 0)
                    macroQuery = macroQuery[(leafIndex + 2)..].Trim();
                if (macroQuery.StartsWith("r#", StringComparison.Ordinal))
                    macroQuery = macroQuery[2..];

                return macroQuery.Length == 0 ? null : macroQuery;
            }

            public static bool ShouldPreserveQualifiedExactQuery(string? query, string? lang, bool exact)
            {
                return exact
                    && !string.IsNullOrWhiteSpace(lang)
                    && string.Equals(lang, "rust", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(query)
                    && query.Contains("::", StringComparison.Ordinal);
            }

            public static (string? QualifiedPath, string? ContainerPath, string? LeafName)
                NormalizeQualifiedExactQueryParts(string query)
            {
                var trimmed = query.Trim();
                if (trimmed.Length == 0)
                    return (null, null, null);

                if (trimmed.EndsWith("!", StringComparison.Ordinal))
                    trimmed = trimmed[..^1].TrimEnd();

                var normalized = NormalizeQualifiedMacroQuery(trimmed);
                if (string.IsNullOrWhiteSpace(normalized))
                    return (null, null, null);

                normalized = TrimQualifiedPathPrefixes(normalized.Replace("::", "."));
                var lastDot = normalized.LastIndexOf('.');
                return lastDot < 0
                    ? (normalized, string.Empty, normalized)
                    : (normalized, normalized[..lastDot], normalized[(lastDot + 1)..]);
            }

            private static string? NormalizeQualifiedMacroQuery(string query)
            {
                var segments = query
                    .Split("::", StringSplitOptions.None)
                    .Select(segment => segment.Trim())
                    .Where(segment => segment.Length > 0)
                    .Select(segment => segment.StartsWith("r#", StringComparison.Ordinal) ? segment[2..] : segment)
                    .ToList();

                return segments.Count == 0 ? null : string.Join("::", segments);
            }

            private static string TrimQualifiedPathPrefixes(string query)
            {
                while (query.StartsWith("crate.", StringComparison.Ordinal)
                    || query.StartsWith("self.", StringComparison.Ordinal)
                    || query.StartsWith("super.", StringComparison.Ordinal))
                {
                    var dotIndex = query.IndexOf('.');
                    if (dotIndex < 0 || dotIndex == query.Length - 1)
                        break;

                    query = query[(dotIndex + 1)..];
                }

                return query;
            }
        }

        private static class SwiftQueryStrategy
        {
            public static string? ComputeBacktickAlias(string? query, string? lang)
            {
                if (!string.Equals(lang, "swift", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(query))
                {
                    return null;
                }

                var trimmed = query.Trim();
                if (trimmed.Length == 0
                    || trimmed.IndexOfAny(['`', ':', '/', '<', '>', '(', ')', '[', ']', ' ']) >= 0)
                {
                    return null;
                }

                var lastDot = trimmed.LastIndexOf('.');
                if (lastDot < 0)
                    return $"`{trimmed}`";
                if (lastDot == 0 || lastDot == trimmed.Length - 1)
                    return null;

                var prefix = trimmed[..(lastDot + 1)];
                var leaf = trimmed[(lastDot + 1)..];
                return leaf.IndexOf('.') >= 0 ? null : $"{prefix}`{leaf}`";
            }
        }

        private static class CSharpQueryStrategy
        {
            // Query-side mirror of the C# declaration canonicalizer. C# source spellings such as
            // `@class` are canonicalized when no language or C# is selected. Other languages,
            // especially SQL, retain a leading `@`. Rust also retains its macro query policy.
            public static string? NormalizeVerbatim(string? query, string? lang)
            {
                if (!string.IsNullOrWhiteSpace(lang)
                    && string.Equals(lang, "rust", StringComparison.OrdinalIgnoreCase))
                {
                    var rustNormalized = RustQueryStrategy.Normalize(query);
                    return string.IsNullOrWhiteSpace(rustNormalized) ? null : rustNormalized;
                }

                if (!string.IsNullOrWhiteSpace(lang)
                    && !string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
                {
                    return query;
                }

                var normalized = query == null ? null : NormalizeDbCSharpQualifiedName(query);
                return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
            }
        }
    }
}
