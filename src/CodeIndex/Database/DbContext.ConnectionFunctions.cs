using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeIndex.Database;

public partial class DbContext : IDisposable
{
    private static SqliteConnection OpenArtifactPreservingQueryOnly(string dbPath)
    {
        var connection = CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling: false,
            out _,
            out _,
            out _);
        connection.Open();
        return connection;
    }

    internal static void RegisterConnectionFunctions(SqliteConnection connection)
    {
        static int? ToNullableInt(long? value)
            => value is null || value < int.MinValue || value > int.MaxValue ? null : (int)value.Value;

        connection.CreateFunction(
            "markdown_resolve_path",
            (string? sourcePath, string? targetPath) => DbReader.ResolveMarkdownDependencyPath(sourcePath, targetPath));
        connection.CreateFunction(
            "python_import_resolves",
            (string? sourcePath, string? targetPath, string? referenceName, string? referenceKind, string? context, long? columnNumber, string? signature) =>
                PythonImportBindingResolver.ResolvesDependency(sourcePath, targetPath, referenceName, referenceKind, context, columnNumber, signature));
        connection.CreateFunction(
            "python_import_target_name",
            (string? sourcePath, string? referenceName, string? context, long? columnNumber, string? signature) =>
                PythonImportBindingResolver.ResolveTargetName(sourcePath, referenceName, context, columnNumber, signature));
        connection.CreateFunction(
            "sql_leaf_name",
            (string? name) => string.IsNullOrWhiteSpace(name) ? null : SqlNameResolver.GetLeafName(name));
        connection.CreateFunction(
            "sql_leaf_name_folded",
            (string? name) =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                var leafName = SqlNameResolver.GetLeafName(name);
                return leafName.Length == 0 ? null : NameFold.Fold(leafName) ?? leafName;
            });
        connection.CreateFunction(
            "sql_normalize_name",
            (string? name) => string.IsNullOrWhiteSpace(name) ? null : SqlNameResolver.NormalizeQualifiedName(name));
        connection.CreateFunction(
            "sql_normalize_name_folded",
            (string? name) =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                var normalizedName = SqlNameResolver.NormalizeQualifiedName(name);
                return normalizedName.Length == 0 ? null : NameFold.Fold(normalizedName) ?? normalizedName;
            });
        connection.CreateFunction(
            "sql_normalize_csharp_verbatim_name",
            (string? text) => string.IsNullOrWhiteSpace(text) ? null : CSharpVerbatimNameNormalizer.Normalize(text));
        connection.CreateFunction(
            "csharp_identifier_occurrence_count",
            (string? text, string? identifier) => CountCSharpIdentifierOccurrences(text, identifier));
        connection.CreateFunction(
            "sql_normalize_exact_source_name",
            (string? text, string? lang) => string.IsNullOrWhiteSpace(text) ? null : ExactSourceSearchNormalizer.Normalize(text, lang));
        connection.CreateFunction(
            "sql_segment_count",
            (string? name) => string.IsNullOrWhiteSpace(name) ? (int?)null : SqlNameResolver.GetSegmentCount(name));
        connection.CreateFunction(
            "sql_context_has_name",
            (string? context, string? query) => SqlNameResolver.ContextContainsQualifiedName(context, query) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_has_name_folded",
            (string? context, string? query) => SqlNameResolver.ContextContainsQualifiedNameFolded(context, query) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_has_name_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_has_name_folded_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameFoldedAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_like_name_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameLikeAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_like_name_folded_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameLikeFoldedAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_resolve_reference_name",
            (string? symbolName, string? context, string? containerName) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceName(symbolName, context, containerName);
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_name_folded",
            (string? symbolName, string? context, string? containerName) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceNameFolded(symbolName, context, containerName);
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_name_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceNameAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber));
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_name_folded_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceNameFoldedAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber));
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_segment_count_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) => (int?)(
                SqlNameResolver.ResolveReferenceSegmentCountAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber)) is var segmentCount
                && segmentCount > 0
                    ? segmentCount
                    : null));
        connection.CreateFunction(
            "sql_reference_matches_target_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber, string? targetName) =>
                SqlNameResolver.ReferenceMatchesTargetAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber), targetName) ? 1 : 0);
        connection.CreateFunction(
            "sql_allow_leaf_fallback_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) =>
                SqlNameResolver.AllowLeafFallbackAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber)) ? 1 : 0);
    }

    internal static int CountCSharpIdentifierOccurrences(string? text, string? identifier)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(identifier))
            return 0;

        text = MaskCSharpCommentsAndStrings(text);
        var count = 0;
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var index = text.IndexOf(identifier, searchIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var beforeIndex = index - 1;
            var afterIndex = index + identifier.Length;
            var hasIdentifierBefore = beforeIndex >= 0 && IsCSharpIdentifierPart(text[beforeIndex]);
            var hasIdentifierAfter = afterIndex < text.Length && IsCSharpIdentifierPart(text[afterIndex]);
            if (!hasIdentifierBefore && !hasIdentifierAfter)
                count++;

            searchIndex = index + identifier.Length;
        }

        return count;
    }

    internal static bool HasCSharpIdentifierOccurrenceOutsideLineRange(string? text, string? identifier, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(identifier))
            return false;

        var normalizedStartLine = Math.Max(1, startLine);
        var normalizedEndLine = Math.Max(normalizedStartLine, endLine);
        text = MaskCSharpCommentsAndStrings(text);

        var inRangeOccurrences = 0;
        var lineNumber = 1;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = text.Length;

            var lineOccurrences = CountCSharpIdentifierOccurrencesInRange(text, identifier, lineStart, lineEnd);
            if (lineOccurrences > 0)
            {
                if (lineNumber < normalizedStartLine || lineNumber > normalizedEndLine)
                    return true;

                inRangeOccurrences += lineOccurrences;
                if (inRangeOccurrences > 1)
                    return true;
            }

            if (lineEnd == text.Length)
                break;

            lineStart = lineEnd + 1;
            lineNumber++;
        }

        return false;
    }

    private static int CountCSharpIdentifierOccurrencesInRange(string text, string identifier, int start, int end)
    {
        var count = 0;
        var searchIndex = start;
        while (searchIndex < end)
        {
            var index = text.IndexOf(identifier, searchIndex, end - searchIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var beforeIndex = index - 1;
            var afterIndex = index + identifier.Length;
            var hasIdentifierBefore = beforeIndex >= start && IsCSharpIdentifierPart(text[beforeIndex]);
            var hasIdentifierAfter = afterIndex < end && IsCSharpIdentifierPart(text[afterIndex]);
            if (!hasIdentifierBefore && !hasIdentifierAfter)
                count++;

            searchIndex = index + identifier.Length;
        }

        return count;
    }

    private static bool IsCSharpIdentifierPart(char ch)
    {
        return ch == '_' || char.IsLetterOrDigit(ch);
    }

    private static string MaskCSharpCommentsAndStrings(string text)
    {
        var chars = text.ToCharArray();
        var inBlockComment = false;
        var inLineComment = false;
        var inString = false;
        var inChar = false;
        var inVerbatimString = false;

        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch is '\r' or '\n')
                    inLineComment = false;
                else
                    chars[i] = ' ';
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    inBlockComment = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[i] = ' ';
                }
                continue;
            }

            if (inString)
            {
                if (ch == '\\' && !inVerbatimString && next != '\0')
                {
                    chars[i] = ' ';
                    if (next is not ('\r' or '\n'))
                        chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (inVerbatimString && ch == '"' && next == '"')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (ch == '"')
                    inString = false;

                chars[i] = ch is '\r' or '\n' ? ch : ' ';
                continue;
            }

            if (inChar)
            {
                if (ch == '\\' && next != '\0')
                {
                    chars[i] = ' ';
                    if (next is not ('\r' or '\n'))
                        chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (ch == '\'')
                    inChar = false;

                chars[i] = ch is '\r' or '\n' ? ch : ' ';
                continue;
            }

            if (ch == '/' && next == '/')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                inLineComment = true;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                inBlockComment = true;
                continue;
            }

            if (TryMaskCSharpRawString(chars, ref i))
                continue;

            if (TryMaskCSharpInterpolatedString(chars, ref i))
                continue;

            if (ch == '@' && next == '"')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                inString = true;
                inVerbatimString = true;
                continue;
            }

            if (ch == '"')
            {
                chars[i] = ' ';
                inString = true;
                inVerbatimString = false;
                continue;
            }

            if (ch == '\'')
            {
                chars[i] = ' ';
                inChar = true;
            }
        }

        return new string(chars);
    }

    private static bool TryMaskCSharpRawString(char[] chars, ref int index)
    {
        var start = index;
        var cursor = start;
        while (cursor < chars.Length && chars[cursor] == '$')
            cursor++;

        if (cursor + 2 >= chars.Length
            || chars[cursor] != '"'
            || chars[cursor + 1] != '"'
            || chars[cursor + 2] != '"')
        {
            return false;
        }

        var quoteCount = 0;
        while (cursor + quoteCount < chars.Length && chars[cursor + quoteCount] == '"')
            quoteCount++;
        if (quoteCount < 3)
            return false;

        var interpolationDollarCount = cursor - start;
        MaskRangePreservingNewLines(chars, start, cursor + quoteCount);
        var search = cursor + quoteCount;
        var interpolationBraceDepth = 0;
        while (search < chars.Length)
        {
            if (interpolationBraceDepth == 0 && HasQuoteRun(chars, search, quoteCount))
            {
                MaskRangePreservingNewLines(chars, search, search + quoteCount);
                index = search + quoteCount - 1;
                return true;
            }

            if (interpolationDollarCount > 0 && chars[search] == '{')
            {
                interpolationBraceDepth++;
            }
            else if (interpolationBraceDepth > 0 && chars[search] == '}')
            {
                interpolationBraceDepth--;
            }
            else if (interpolationBraceDepth == 0 && chars[search] is not ('\r' or '\n'))
            {
                chars[search] = ' ';
            }
            search++;
        }

        index = chars.Length - 1;
        return true;
    }

    private static bool TryMaskCSharpInterpolatedString(char[] chars, ref int index)
    {
        var start = index;
        if (chars[start] != '$')
            return false;

        var cursor = start + 1;
        var verbatim = false;
        if (cursor < chars.Length && chars[cursor] == '@')
        {
            verbatim = true;
            cursor++;
        }

        if (cursor >= chars.Length || chars[cursor] != '"')
            return false;

        MaskRangePreservingNewLines(chars, start, cursor + 1);
        var braceDepth = 0;
        for (var i = cursor + 1; i < chars.Length; i++)
        {
            var ch = chars[i];
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';

            if (braceDepth == 0 && ch == '"' && !(verbatim && next == '"'))
            {
                chars[i] = ' ';
                index = i;
                return true;
            }

            if (verbatim && braceDepth == 0 && ch == '"' && next == '"')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                continue;
            }

            if (!verbatim && braceDepth == 0 && ch == '\\' && next != '\0')
            {
                chars[i] = ' ';
                if (next is not ('\r' or '\n'))
                    chars[i + 1] = ' ';
                i++;
                continue;
            }

            if (ch == '{')
            {
                braceDepth++;
                continue;
            }

            if (braceDepth > 0 && ch == '}')
            {
                braceDepth--;
                continue;
            }

            if (braceDepth == 0 && ch is not ('\r' or '\n'))
                chars[i] = ' ';
        }

        index = chars.Length - 1;
        return true;
    }

    private static bool HasQuoteRun(char[] chars, int start, int quoteCount)
    {
        if (start + quoteCount > chars.Length)
            return false;
        for (var i = 0; i < quoteCount; i++)
        {
            if (chars[start + i] != '"')
                return false;
        }
        return true;
    }

    private static void MaskRangePreservingNewLines(char[] chars, int start, int end)
    {
        for (var i = start; i < end && i < chars.Length; i++)
        {
            if (chars[i] is not ('\r' or '\n'))
                chars[i] = ' ';
        }
    }

    internal static void RegisterConnectionFunctionsWithRetry(
        SqliteConnection connection,
        Action<int>? sleep = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default,
        Action<SqliteConnection>? registerConnectionFunctions = null)
    {
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be at least 1.");

        cancellationToken.ThrowIfCancellationRequested();
        registerConnectionFunctions ??= RegisterConnectionFunctions;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                registerConnectionFunctions(connection);
                return;
            }
            catch (SqliteException ex) when (DbConnectionFactory.IsTransientBusyError(ex) && attempt < maxAttempts)
            {
                DbConnectionFactory.SleepBeforeRetry(50 * attempt, sleep, cancellationToken);
            }
        }
    }

}
