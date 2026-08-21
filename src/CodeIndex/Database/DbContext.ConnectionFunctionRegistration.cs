using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CodeIndex.Database;

public partial class DbContext
{
    private static class ConnectionFunctionRegistrar
    {
        private static readonly ConditionalWeakTable<SqliteConnection, CSharpCallableTypeKindLookup>
            CSharpCallableTypeKindLookups = new();
        private static readonly ConditionalWeakTable<SqliteConnection, object> Registrations = new();
        private static readonly object RegistrationLock = new();
        private static readonly ConditionalWeakTable<SqliteConnection, object> PartialRegistrations = new();
        private static readonly object PartialRegistrationLock = new();

        internal static void Register(SqliteConnection connection)
        {
            lock (RegistrationLock)
            {
                if (Registrations.TryGetValue(connection, out _))
                    return;

                RegisterDependencyAndNameNormalizationFunctions(connection);
                RegisterCSharpReferenceShapeFunctions(connection);
                RegisterCSharpPartialIdentityFunctions(connection);
                RegisterCSharpFileAndBaseFunctions(connection);
                RegisterSqlResolutionFunctions(connection);
                Registrations.Add(connection, new object());
            }
        }

        private static void RegisterDependencyAndNameNormalizationFunctions(SqliteConnection connection)
        {
            connection.CreateFunction(
                "markdown_resolve_path",
                (string? sourcePath, string? targetPath) => DbReader.ResolveMarkdownDependencyPath(sourcePath, targetPath));
            connection.CreateFunction(
                "markdown_normalize_fragment",
                (string? fragment) => fragment == null ? null : MarkdownAnchorIdentity.NormalizeHeadingFragment(fragment));
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
                (string? name) => FoldSqlLeafName(name));
            connection.CreateFunction(
                "codeindex_name_fold",
                (string? name) => NameFold.Fold(name),
                isDeterministic: true);
            connection.CreateFunction(
                "sql_normalize_name",
                (string? name) => string.IsNullOrWhiteSpace(name) ? null : SqlNameResolver.NormalizeQualifiedName(name));
            connection.CreateFunction(
                "sql_normalize_name_folded",
                (string? name) => FoldSqlQualifiedName(name));
            connection.CreateFunction(
                "sql_normalize_csharp_verbatim_name",
                (string? text) => string.IsNullOrWhiteSpace(text) ? null : CSharpVerbatimNameNormalizer.Normalize(text));
        }

        private static string? FoldSqlLeafName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var leafName = SqlNameResolver.GetLeafName(name);
            return leafName.Length == 0 ? null : NameFold.Fold(leafName) ?? leafName;
        }

        private static string? FoldSqlQualifiedName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var normalizedName = SqlNameResolver.NormalizeQualifiedName(name);
            return normalizedName.Length == 0 ? null : NameFold.Fold(normalizedName) ?? normalizedName;
        }

        private static void RegisterCSharpReferenceShapeFunctions(SqliteConnection connection)
        {
            connection.CreateFunction(
                "csharp_identifier_occurrence_count",
                (string? text, string? identifier) => CountCSharpIdentifierOccurrences(text, identifier));
            connection.CreateFunction(
                "csharp_identifier_occurrence_count_in_line_range",
                (string? text, long? chunkStartLine, long? rangeStartLine, long? rangeEndLine, string? identifier) =>
                    CountCSharpIdentifierOccurrencesInLineRange(text, chunkStartLine, rangeStartLine, rangeEndLine, identifier));
            connection.CreateFunction(
                "csharp_text_in_line_range",
                (string? text, long? chunkStartLine, long? rangeStartLine, long? rangeEndLine) =>
                    GetTextInLineRange(text, chunkStartLine, rangeStartLine, rangeEndLine));
            connection.CreateFunction(
                "csharp_reference_type_arity",
                (string? context, string? identifier, long? columnNumber) =>
                    CSharpTypeReferenceArity.GetReferenceArity(context, identifier, columnNumber));
            connection.CreateFunction(
                "csharp_reference_is_member_receiver",
                (string? context, string? identifier, long? columnNumber) =>
                    CSharpTypeReferenceArity.IsMemberReceiver(context, identifier, columnNumber));
            connection.CreateFunction(
                "csharp_definition_type_arity",
                (string? signature, string? identifier, string? symbolKind) =>
                    CSharpTypeReferenceArity.GetDefinitionArity(signature, identifier, symbolKind));
            connection.CreateFunction(
                "csharp_constructor_parameter_count",
                (string? signature, string? identifier, string? symbolKind) =>
                    CSharpTypeReferenceArity.GetConstructorParameterCount(signature, identifier, symbolKind));
        }

        private static void RegisterCSharpPartialIdentityFunctions(SqliteConnection connection)
        {
            var typeKinds = CSharpCallableTypeKindLookups.GetValue(
                connection,
                static _ => new CSharpCallableTypeKindLookup());
            connection.CreateFunction(
                "csharp_partial_callable_identity",
                (string? signature, string? identifier, string? returnType) =>
                    LogicalPartialSymbolGrouper.BuildCallableIdentity(
                        signature, identifier, returnType, containerQualifiedName: null, typeKinds: null),
                isDeterministic: true);
            connection.CreateFunction(
                "csharp_partial_callable_identity",
                (string? signature, string? identifier, string? returnType, string? containerQualifiedName) =>
                    LogicalPartialSymbolGrouper.BuildCallableIdentity(
                        signature, identifier, returnType, containerQualifiedName, typeKinds));
            connection.CreateFunction(
                "csharp_partial_callable_identity",
                (string? signature, string? identifier, string? returnType, string? containerQualifiedName, long? symbolId) =>
                    LogicalPartialSymbolGrouper.BuildCallableIdentity(
                        signature, identifier, returnType, containerQualifiedName, typeKinds, symbolId));
            connection.CreateFunction(
                "csharp_partial_semantic_score",
                (string? signature, string? symbolKind) =>
                    LogicalPartialSymbolGrouper.GetSemanticScore(signature, symbolKind),
                isDeterministic: true);
            connection.CreateFunction(
                "csharp_partial_declaration_identity",
                (string? signature) => LogicalPartialSymbolGrouper.BuildCanonicalDeclarationIdentity(signature),
                isDeterministic: true);
            connection.CreateFunction(
                "codeindex_partial_family_id",
                (string? key) => key is null ? null : LogicalPartialSymbolGrouper.BuildPartialFamilyId(key),
                isDeterministic: true);
            RegisterCSharpPartialDeclaration(connection);
        }

        private static void RegisterCSharpFileAndBaseFunctions(SqliteConnection connection)
        {
            connection.CreateFunction(
                "codeindex_generated_file_name",
                (string? path) => FileIndexer.HasGeneratedCodeFileName(path ?? string.Empty),
                isDeterministic: true);
            connection.CreateFunction(
                "csharp_invocation_argument_count",
                (string? context, string? identifier, long? columnNumber) =>
                    CSharpTypeReferenceArity.GetInvocationArgumentCount(context, identifier, columnNumber));
            connection.CreateFunction(
                "csharp_definition_is_value_type",
                (string? signature, string? symbolKind) =>
                    CSharpTypeReferenceArity.IsValueTypeDeclaration(signature, symbolKind));
            connection.CreateFunction(
                "csharp_base_identifiers_json",
                (string? signature) => JsonSerializer.Serialize(
                    CSharpBaseListParser.Parse(signature, CSharpBaseListProjection.HeadIdentifier),
                    CliJsonSerializerContext.Default.ListString));
            connection.CreateFunction(
                "csharp_base_name_folded",
                (string? baseReference) =>
                {
                    var leaf = GetCSharpBaseReferenceLeaf(baseReference);
                    return leaf == null ? null : NameFold.Fold(leaf) ?? leaf;
                });
            connection.CreateFunction(
                "csharp_base_name",
                (string? baseReference) => GetCSharpBaseReferenceLeaf(baseReference));
            connection.CreateFunction(
                "csharp_base_reference_matches",
                (string? baseReference, string? candidateName, string? candidateQualifiedName, string? derivingQualifiedName) =>
                    CSharpBaseReferenceMatches(baseReference, candidateName, candidateQualifiedName, derivingQualifiedName) ? 1 : 0);
        }

        private static void RegisterSqlResolutionFunctions(SqliteConnection connection)
        {
            connection.CreateFunction(
                "sql_normalize_exact_source_name",
                (string? text, string? lang) => string.IsNullOrWhiteSpace(text) ? null : ExactSourceSearchNormalizer.Normalize(text, lang));
            connection.CreateFunction(
                "sql_segment_count",
                (string? name) => string.IsNullOrWhiteSpace(name) ? (int?)null : SqlNameResolver.GetSegmentCount(name));
            RegisterSqlContextFunctions(connection);
            RegisterSqlReferenceFunctions(connection);
        }

        private static void RegisterSqlContextFunctions(SqliteConnection connection)
        {
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
        }

        private static void RegisterSqlReferenceFunctions(SqliteConnection connection)
        {
            connection.CreateFunction(
                "sql_resolve_reference_name",
                (string? symbolName, string? context, string? containerName) => EmptyAsNull(
                    SqlNameResolver.ResolveReferenceName(symbolName, context, containerName)));
            connection.CreateFunction(
                "sql_resolve_reference_name_folded",
                (string? symbolName, string? context, string? containerName) => EmptyAsNull(
                    SqlNameResolver.ResolveReferenceNameFolded(symbolName, context, containerName)));
            connection.CreateFunction(
                "sql_resolve_reference_name_at",
                (string? symbolName, string? context, string? containerName, long? columnNumber) => EmptyAsNull(
                    SqlNameResolver.ResolveReferenceNameAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber))));
            connection.CreateFunction(
                "sql_resolve_reference_name_folded_at",
                (string? symbolName, string? context, string? containerName, long? columnNumber) => EmptyAsNull(
                    SqlNameResolver.ResolveReferenceNameFoldedAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber))));
            connection.CreateFunction(
                "sql_resolve_reference_segment_count_at",
                (string? symbolName, string? context, string? containerName, long? columnNumber) => (int?)(
                    SqlNameResolver.ResolveReferenceSegmentCountAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber)) is var count
                    && count > 0 ? count : null));
            connection.CreateFunction(
                "sql_reference_matches_target_at",
                (string? symbolName, string? context, string? containerName, long? columnNumber, string? targetName) =>
                    SqlNameResolver.ReferenceMatchesTargetAtColumn(
                        symbolName, context, containerName, ToNullableInt(columnNumber), targetName) ? 1 : 0);
            connection.CreateFunction(
                "sql_allow_leaf_fallback_at",
                (string? symbolName, string? context, string? containerName, long? columnNumber) =>
                    SqlNameResolver.AllowLeafFallbackAtColumn(
                        symbolName, context, containerName, ToNullableInt(columnNumber)) ? 1 : 0);
        }

        private static string? EmptyAsNull(string value) => value.Length == 0 ? null : value;

        private static int? ToNullableInt(long? value)
            => value is null || value < int.MinValue || value > int.MaxValue ? null : (int)value.Value;

        internal static void RegisterCSharpPartialDeclaration(SqliteConnection connection)
        {
            lock (PartialRegistrationLock)
            {
                // Raw DbReader connections need this function, while active statements make
                // re-registration unsafe; keep its guard independent from the full family.
                if (PartialRegistrations.TryGetValue(connection, out _))
                    return;

                connection.CreateFunction(
                    "csharp_is_partial_declaration",
                    (string? signature, string? kind, string? name) =>
                        LogicalPartialSymbolGrouper.ContainsPartialModifier(signature, kind, name),
                    isDeterministic: true);
                PartialRegistrations.Add(connection, new object());
            }
        }

        internal static void RefreshCSharpCallableTypeKinds(
            SqliteConnection connection,
            IReadOnlySet<string> fileColumns,
            IReadOnlySet<string> symbolColumns,
            IReadOnlyList<string>? candidateQueries,
            bool exact,
            bool useFoldedNames)
        {
            var lookup = CSharpCallableTypeKindLookups.GetValue(
                connection,
                static _ => new CSharpCallableTypeKindLookup());
            lookup.RefreshIfChanged(connection, fileColumns, symbolColumns, candidateQueries, exact, useFoldedNames);
        }

        internal static void RegisterWithRetry(
            SqliteConnection connection,
            Action<int>? sleep,
            int maxAttempts,
            CancellationToken cancellationToken,
            Action<SqliteConnection>? registerConnectionFunctions)
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
}
