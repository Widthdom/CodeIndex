using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

public partial class DbReader
{
    private const string UnusedBucketLikelyPrivate = "likely_unused_private";
    private const string UnusedBucketMaybeNonPublic = "maybe_unused_nonpublic";
    private const string UnusedBucketPublicOrExported = "public_or_exported_no_refs";
    private const string UnusedBucketReflectionOrConfig = "reflection_or_config_suspect";
    private const string UnusedContractDomainPrivate = "private_or_file_local";
    private const string UnusedContractDomainNonPublic = "nonpublic_internal";
    private const string UnusedContractDomainPublicApi = "public_api_surface";
    private const string UnusedContractDomainCli = "cli_contract";
    private const string UnusedContractDomainJson = "json_contract";
    private const string UnusedContractDomainMcp = "mcp_contract";
    private const string UnusedContractDomainLsp = "lsp_contract";
    private const string UnusedContractDomainConfig = "configuration_contract";
    private const string UnusedContractDomainSerialization = "serialization_or_reflection_contract";
    private const string UnusedContractDomainGenerated = "generated_code";
    private const string UnusedContractDomainDocumentation = "documentation_surface";
    private const string UnusedContractDomainTest = "test_contract";
    private const string UnusedContractDomainFrameworkOverride = "framework_override";
    private const string UnusedContractDomainExceptionDiagnostic = "exception_diagnostic";
    internal static readonly string[] OrderedUnusedContractDomains =
    [
        UnusedContractDomainPrivate,
        UnusedContractDomainNonPublic,
        UnusedContractDomainPublicApi,
        UnusedContractDomainCli,
        UnusedContractDomainJson,
        UnusedContractDomainMcp,
        UnusedContractDomainLsp,
        UnusedContractDomainConfig,
        UnusedContractDomainSerialization,
        UnusedContractDomainGenerated,
        UnusedContractDomainDocumentation,
        UnusedContractDomainTest,
        UnusedContractDomainFrameworkOverride,
        UnusedContractDomainExceptionDiagnostic,
    ];
    private static readonly HashSet<string> ReflectionPropertyAttributeNames = new(StringComparer.Ordinal)
    {
        "jsonpropertyname",
        "jsonproperty",
        "jsoninclude",
        "jsonextensiondata",
        "jsonconverter",
        "jsonrequired",
        "jsonpropertyorder",
        "jsonnumberhandling",
        "jsonobjectcreationhandling",
        "datamember",
        "bsonelement",
        "bsonid",
        "xmlelement",
        "xmlattribute",
        "yamlmember",
        "column",
        "key",
        "required",
        "bindproperty",
        "parameter",
        "inject",
        "bindnever",
        "dynamicallyaccessedmembers",
        "dynamicdependency",
        "preserve",
        "usedimplicitly",
        "publicapi",
    };
    private static readonly HashSet<string> ReflectionTypeAttributeNames = new(StringComparer.Ordinal)
    {
        "serializable",
        "jsonserializable",
        "jsonsourcegenerationoptions",
        "jsonconverter",
        "jsonderivedtype",
        "jsonpolymorphic",
        "datacontract",
        "xmlroot",
        "xmltype",
        "xmlinclude",
        "knowntype",
        "protocontract",
        "messagepackobject",
        "table",
        "complextype",
        "owned",
        "keyless",
        "attributeusage",
        "dynamicallyaccessedmembers",
        "dynamicdependency",
        "preserve",
        "usedimplicitly",
        "publicapi",
    };
    private static readonly HashSet<string> ReflectionFunctionAttributeNames = new(StringComparer.Ordinal)
    {
        "jsonconstructor",
        "onserializing",
        "onserialized",
        "ondeserializing",
        "ondeserialized",
        "dynamicdependency",
        "dynamicallyaccessedmembers",
        "preserve",
        "usedimplicitly",
        "publicapi",
    };
    private static readonly HashSet<string> ReflectionIgnoreAttributeNames = new(StringComparer.Ordinal)
    {
        "jsonignore",
        "ignoredatamember",
    };
    private static readonly HashSet<string> AttributeTargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "assembly",
        "module",
        "field",
        "event",
        "method",
        "param",
        "property",
        "return",
        "type",
    };
    private static readonly string[] UnusedContractPathSegments =
    [
        "/contracts/",
        "/contract/",
        "/dtos/",
        "/dto/",
        "/models/",
        "/model/",
        "/schemas/",
        "/schema/",
    ];
    private static readonly string[] UnusedRecordContractSuffixes =
    [
        "Dto",
        "DTO",
        "Request",
        "Response",
        "Result",
        "Results",
        "Model",
        "Payload",
        "Envelope",
    ];
    private static readonly string[] UnusedGeneratedPathMarkers =
    [
        ".g.cs",
        ".generated.",
        "/generated/",
        "/obj/",
        "/bin/",
    ];
    private static readonly string[] UnusedCliPathMarkers = ["/cli/", "/commands/", "/commandline/"];
    private static readonly string[] UnusedMcpPathMarkers = ["/mcp/"];
    private static readonly string[] UnusedLspPathMarkers = ["/lsp/", "/languageserver/"];
    private static readonly string[] UnusedJsonContractTerms =
    [
        "Json",
        "Dto",
        "DTO",
        "Request",
        "Response",
        "Result",
        "Results",
        "Payload",
        "Envelope",
        "Contract",
        "Schema",
    ];
    private static readonly HashSet<string> UnusedFrameworkOverrideMemberNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CanRead",
        "CanSeek",
        "CanWrite",
        "Length",
        "Position",
        "Flush",
        "FlushAsync",
        "Read",
        "ReadAsync",
        "ReadByte",
        "Seek",
        "SetLength",
        "Write",
        "WriteAsync",
        "WriteByte",
        "Dispose",
        "DisposeAsync",
    };
    private static readonly HashSet<string> UnusedExceptionMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CharactersRead",
        "Utf8BytesRead",
        "SizeLimit",
        "Limit",
        "ByteCount",
        "BytesRead",
        "Offset",
        "Position",
        "Path",
        "FileName",
        "LineNumber",
        "ColumnNumber",
        "ActualSize",
        "MaxSize",
        "Length",
    };
    private static readonly string[] UnusedConfigMemberTerms =
    [
        "Configuration",
        "Config",
        "Options",
        "Settings",
        "Manifest",
        "Schema",
        "Metadata",
        "Limit",
        "Max",
        "Min",
        "Size",
        "Bytes",
        "Count",
        "Capacity",
        "Timeout",
        "Version",
        "Kind",
        "Category",
        "Severity",
        "Source",
        "Target",
        "Path",
        "Name",
        "Id",
        "Key",
    ];
    private const int UnusedAttributeContextWindow = 16;
    private const int UnusedPublicOverfetchMultiplier = 16;
    private const int UnusedPublicOverfetchMinimum = 64;
    private const int UnusedPublicOverfetchMaximum = 1024;
    private const int UnusedPublicCandidateBudget = 2048;

    private static readonly Regex CSharpConstFieldSignatureRegex = new(
        @"^(?:(?:public|private|protected\s+internal|private\s+protected|protected|internal|static|new|unsafe|readonly|volatile|required)\s+)*const\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpFieldLikeSignatureRegex = new(
        @"^(?:(?:public|private|protected\s+internal|private\s+protected|protected|internal|static|readonly|volatile|new|unsafe|required)\s+)*(?!event\b|delegate\b|const\b).+\s+@?[\p{L}_][\p{L}\p{Nd}_]*\s*(?:=(?![=>])|;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpPropertyAccessorSignatureRegex = new(
        @"\{\s*(?:get|set|init)\b|=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class UnusedCandidateSymbol
    {
        public long FileId { get; init; }
        public string Path { get; init; } = string.Empty;
        public string? Lang { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Line { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public string? Signature { get; init; }
        public string? Visibility { get; init; }
        public string? ReturnType { get; init; }
        public string? ContainerKind { get; init; }
        public string? ContainerName { get; init; }
        public string? ContainerQualifiedName { get; init; }
        public bool IsPublicOrExported { get; init; }
        public bool IsReflectionOrConfigSuspect { get; init; }
        public int ProvisionalBucketOrder { get; init; }
    }

    private readonly record struct UnusedCandidateChunk(int StartLine, int EndLine, string Content);
    private readonly record struct UnusedContractDomainClassification(string Domain, List<string> Tags);


    private string BuildAmbiguousCSharpEnumMemberExclusionSql(
        string symbolAlias,
        string fileAlias,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        var symbolContainerKindSql = GetSymbolColumnSql("container_kind", "''", symbolAlias);
        var symbolContainerNameSql = GetSymbolColumnSql("container_name", "''", symbolAlias);
        var symbolContainerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", symbolContainerNameSql, symbolAlias);
        var peerContainerKindSql = GetSymbolColumnSql("container_kind", "''", "s_peer");
        var peerContainerNameSql = GetSymbolColumnSql("container_name", "''", "s_peer");
        var peerContainerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", peerContainerNameSql, "s_peer");
        var peerPathFiltersSql = BuildPathFiltersSql("f_peer", pathPatterns, excludePathPatterns, excludeTests);

        return $@"
                NOT (
                    {fileAlias}.lang = 'csharp'
                    AND {symbolAlias}.kind = 'enum'
                    AND {symbolContainerKindSql} = 'enum'
                    AND EXISTS (
                        SELECT 1
                        FROM symbols s_peer
                        JOIN files f_peer ON f_peer.id = s_peer.file_id
                        WHERE f_peer.lang = 'csharp'
                          {peerPathFiltersSql}
                          AND s_peer.kind = 'enum'
                          AND {peerContainerKindSql} = 'enum'
                          AND s_peer.name = {symbolAlias}.name
                          AND {peerContainerQualifiedNameSql} <> {symbolContainerQualifiedNameSql}
                    )
                )";
    }

    /// <summary>
    /// Find symbols that have no matching references in the reference table (potential dead code).
    /// Only meaningful for graph-supported languages — unsupported languages are excluded by default.
    /// 参照テーブルに一致する参照がないシンボルを検索する（潜在的なデッドコード）。
    /// グラフ対応言語でのみ意味がある — 未対応言語はデフォルトで除外。
    /// </summary>
    public List<UnusedSymbolResult> GetUnusedSymbols(
        int limit,
        string? kind,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        IReadOnlyList<string>? visibilityFilters = null,
        IReadOnlyList<string>? excludeVisibilityFilters = null,
        string? bucketFilter = null,
        string? minConfidence = null)
    {
        // Without symbol_references (legacy read-only DB), every symbol would appear unused,
        // which is a meaningless signal. Return empty rather than drowning the caller in noise.
        // symbol_references が無いレガシー read-only DB では全シンボルが未使用扱いになってしまうため、
        // ノイズを返すより空を返す。
        if (!_hasReferencesTable) return new List<UnusedSymbolResult>();
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            return [];
        // Restrict to graph-supported languages to avoid false positives
        // (unsupported languages have no references indexed, so all symbols appear unused)
        // グラフ対応言語に制限して偽陽性を防ぐ
        // （未対応言語は参照がインデックスされないため全シンボルが未使用に見える）
        if (HasEffectiveUnusedFilter(bucketFilter, minConfidence))
            return GetFilteredUnusedSymbols(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return GetUnusedSymbolsWithoutSqlResolver(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);

        var targetCount = Math.Max(limit, 1);
        var privateLike = FetchUnusedCandidates(targetCount, 0, 0, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var maybeNonPublic = FetchUnusedCandidates(targetCount, 1, 0, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var reflectionOrConfig = FetchUnusedCandidates(targetCount, 3, 0, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);

        var publicOrExported = new List<UnusedSymbolResult>();
        var publicBucketOffset = 0;
        var publicBatchSize = Math.Min(
            Math.Max(targetCount * UnusedPublicOverfetchMultiplier, UnusedPublicOverfetchMinimum),
            UnusedPublicOverfetchMaximum);
        var publicFetchBudget = Math.Max(targetCount, Math.Max(publicBatchSize, UnusedPublicCandidateBudget));
        var publicCandidatesFetched = 0;
        while ((publicOrExported.Count < targetCount || reflectionOrConfig.Count < targetCount)
            && publicCandidatesFetched < publicFetchBudget)
        {
            var batch = FetchUnusedCandidates(publicBatchSize, 2, publicBucketOffset, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
            if (batch.Count == 0)
                break;

            foreach (var candidate in batch)
            {
                if (candidate.UnusedBucket == UnusedBucketReflectionOrConfig)
                    reflectionOrConfig.Add(candidate);
                else
                    publicOrExported.Add(candidate);
            }

            publicBucketOffset += batch.Count;
            publicCandidatesFetched += batch.Count;
            if (batch.Count < publicBatchSize)
                break;
        }

        var merged = new List<UnusedSymbolResult>(privateLike.Count + maybeNonPublic.Count + publicOrExported.Count + reflectionOrConfig.Count);
        merged.AddRange(privateLike);
        merged.AddRange(maybeNonPublic);
        merged.AddRange(publicOrExported);
        merged.AddRange(reflectionOrConfig);
        return DiversifyUnusedResults(FilterUnusedResults(merged, bucketFilter, minConfidence), limit);
    }

    private List<UnusedSymbolResult> GetFilteredUnusedSymbols(int limit, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return GetFilteredUnusedSymbolsWithoutSqlResolver(limit, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);

        var targetCount = Math.Max(limit, 1);
        var targetBuckets = GetTargetUnusedBuckets(bucketFilter, minConfidence);
        if (targetBuckets.Count == 0)
            return [];

        var resultsByBucket = CreateUnusedBucketResultLists();
        const int batchSize = UnusedPublicOverfetchMaximum;
        foreach (var provisionalBucket in GetRelevantUnusedProvisionalBuckets(targetBuckets))
        {
            var offset = 0;
            while (!AllTargetUnusedBucketsFilled(resultsByBucket, targetBuckets, targetCount))
            {
                var batch = FetchUnusedCandidates(batchSize, provisionalBucket, offset, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var result in batch)
                    AddFilteredUnusedResult(resultsByBucket, targetBuckets, targetCount, result, bucketFilter, minConfidence);

                if (batch.Count < batchSize)
                    break;
            }
        }

        var merged = OrderedUnusedBuckets
            .SelectMany(bucket => resultsByBucket[bucket])
            .ToList();
        return DiversifyUnusedResults(merged, limit);
    }

    private List<UnusedSymbolResult> GetUnusedSymbolsWithoutSqlResolver(int limit, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null,
        string? bucketFilter = null, string? minConfidence = null)
    {
        var targetCount = Math.Max(limit, 1);
        var publicFetchBudget = Math.Max(
            targetCount,
            Math.Max(
                Math.Min(
                    Math.Max(targetCount * UnusedPublicOverfetchMultiplier, UnusedPublicOverfetchMinimum),
                    UnusedPublicOverfetchMaximum),
                UnusedPublicCandidateBudget));
        var batchSize = Math.Min(
            Math.Max(targetCount * UnusedPublicOverfetchMultiplier, UnusedPublicOverfetchMinimum),
            UnusedPublicOverfetchMaximum);
        var publicOrExported = new List<UnusedSymbolResult>(targetCount);
        var fileContentByFileId = new Dictionary<long, string>();
        var privateLike = CollectUnusedCandidateBucket(targetCount, batchSize, 0, fileContentByFileId,
            kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var maybeNonPublic = CollectUnusedCandidateBucket(targetCount, batchSize, 1, fileContentByFileId,
            kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        var reflectionOrConfig = CollectUnusedCandidateBucket(targetCount, batchSize, 3, fileContentByFileId,
            kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
        CollectPublicUnusedCandidateBucket(targetCount, batchSize, publicFetchBudget, fileContentByFileId,
            publicOrExported, reflectionOrConfig, kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);

        var merged = new List<UnusedSymbolResult>(privateLike.Count + maybeNonPublic.Count + publicOrExported.Count + reflectionOrConfig.Count);
        merged.AddRange(privateLike);
        merged.AddRange(maybeNonPublic);
        merged.AddRange(publicOrExported);
        merged.AddRange(reflectionOrConfig);
        return DiversifyUnusedResults(FilterUnusedResults(merged, bucketFilter, minConfidence), limit);
    }

    private List<UnusedSymbolResult> GetFilteredUnusedSymbolsWithoutSqlResolver(int limit, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        var targetCount = Math.Max(limit, 1);
        var targetBuckets = GetTargetUnusedBuckets(bucketFilter, minConfidence);
        if (targetBuckets.Count == 0)
            return [];

        var fileContentByFileId = new Dictionary<long, string>();
        var resultsByBucket = CreateUnusedBucketResultLists();
        const int batchSize = UnusedPublicOverfetchMaximum;
        foreach (var provisionalBucket in GetRelevantUnusedProvisionalBuckets(targetBuckets))
        {
            var offset = 0;
            while (!AllTargetUnusedBucketsFilled(resultsByBucket, targetBuckets, targetCount))
            {
                var batch = FetchUnusedCandidateSymbols(batchSize, offset, provisionalBucket, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var candidate in batch)
                {
                    if (HasPrivateCSharpUse(candidate, fileContentByFileId))
                        continue;

                    var result = CreateUnusedSymbolResult(candidate);
                    AddFilteredUnusedResult(resultsByBucket, targetBuckets, targetCount, result, bucketFilter, minConfidence);
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        var merged = OrderedUnusedBuckets
            .SelectMany(bucket => resultsByBucket[bucket])
            .ToList();
        return DiversifyUnusedResults(merged, limit);
    }

    private List<UnusedSymbolResult> CollectUnusedCandidateBucket(int targetCount, int batchSize, int provisionalBucketOrder,
        Dictionary<long, string> fileContentByFileId, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var results = new List<UnusedSymbolResult>(targetCount);
        var offset = 0;
        while (results.Count < targetCount)
        {
            var batch = FetchUnusedCandidateSymbols(batchSize, offset, provisionalBucketOrder, kind, lang,
                pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
            if (batch.Count == 0)
                break;

            offset += batch.Count;
            foreach (var candidate in batch)
            {
                if (HasPrivateCSharpUse(candidate, fileContentByFileId))
                    continue;

                results.Add(CreateUnusedSymbolResult(candidate));
                if (results.Count >= targetCount)
                    break;
            }

            if (batch.Count < batchSize)
                break;
        }

        return results;
    }

    private void CollectPublicUnusedCandidateBucket(int targetCount, int batchSize, int candidateBudget,
        Dictionary<long, string> fileContentByFileId,
        List<UnusedSymbolResult> publicOrExported, List<UnusedSymbolResult> reflectionOrConfig, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var offset = 0;
        var candidatesFetched = 0;
        while ((publicOrExported.Count < targetCount || reflectionOrConfig.Count < targetCount)
            && candidatesFetched < candidateBudget)
        {
            var batch = FetchUnusedCandidateSymbols(batchSize, offset, 2, kind, lang,
                pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
            if (batch.Count == 0)
                break;

            offset += batch.Count;
            foreach (var candidate in batch)
            {
                if (HasPrivateCSharpUse(candidate, fileContentByFileId))
                    continue;

                candidatesFetched++;
                var result = CreateUnusedSymbolResult(candidate);
                if (result.UnusedBucket == UnusedBucketReflectionOrConfig)
                {
                    if (reflectionOrConfig.Count < targetCount)
                        reflectionOrConfig.Add(result);
                }
                else if (publicOrExported.Count < targetCount)
                {
                    publicOrExported.Add(result);
                }

                if ((publicOrExported.Count >= targetCount && reflectionOrConfig.Count >= targetCount)
                    || candidatesFetched >= candidateBudget)
                    break;
            }

            if (batch.Count < batchSize)
                break;
        }
    }

    private bool HasPrivateCSharpUse(UnusedCandidateSymbol candidate, Dictionary<long, string> fileContentByFileId)
        => HasSameFilePrivateUse(candidate, fileContentByFileId)
           || HasCSharpPartialContainingTypeUse(candidate);

    private bool HasSameFilePrivateUse(UnusedCandidateSymbol candidate, Dictionary<long, string> fileContentByFileId)
    {
        if (!string.Equals(candidate.Lang, "csharp", StringComparison.Ordinal)
            || !IsPrivateLikeVisibility(candidate.Visibility)
            || candidate.Name.Length == 0
            || !_hasChunksTable
            || !HasTable("chunks"))
            return false;

        var fileContent = GetUnusedCandidateFileContent(candidate.FileId, fileContentByFileId);
        return DbContext.HasCSharpIdentifierOccurrenceOutsideLineRange(
            fileContent,
            candidate.Name,
            candidate.StartLine,
            candidate.EndLine);
    }

    private bool HasCSharpPartialContainingTypeUse(UnusedCandidateSymbol candidate)
    {
        if (!string.Equals(candidate.Lang, "csharp", StringComparison.Ordinal)
            || !IsPrivateLikeVisibility(candidate.Visibility)
            || candidate.Name.Length == 0
            || !IsCSharpPartialContainerKind(candidate.ContainerKind)
            || string.IsNullOrWhiteSpace(candidate.ContainerName)
            || !_hasChunksTable
            || !HasTable("chunks"))
        {
            return false;
        }

        var ownContainerNameSql = GetSymbolColumnSql("container_name", "''", "own_type");
        var ownSignatureSql = GetSymbolColumnSql("signature", "''", "own_type");
        var peerContainerNameSql = GetSymbolColumnSql("container_name", "''", "peer_type");
        var peerSignatureSql = GetSymbolColumnSql("signature", "''", "peer_type");
        var ownQualifiedNameSql = $@"CASE
                WHEN {ownContainerNameSql} <> '' THEN {ownContainerNameSql} || '.' || own_type.name
                ELSE own_type.name
            END";
        var peerQualifiedNameSql = $@"CASE
                WHEN {peerContainerNameSql} <> '' THEN {peerContainerNameSql} || '.' || peer_type.name
                ELSE peer_type.name
            END";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT 1
            FROM symbols own_type
            JOIN symbols peer_type
              ON peer_type.file_id <> own_type.file_id
             AND peer_type.kind = own_type.kind
             AND peer_type.name = own_type.name
            JOIN files peer_file ON peer_file.id = peer_type.file_id
            JOIN chunks peer_chunk ON peer_chunk.file_id = peer_type.file_id
            WHERE own_type.file_id = @fileId
              AND own_type.kind = @containerKind
              AND own_type.name = @containerName
              AND lower({ownSignatureSql}) LIKE '%partial%'
              AND lower({peerSignatureSql}) LIKE '%partial%'
              AND peer_file.lang = 'csharp'
              AND (
                  @containerQualifiedName = ''
                  OR @containerQualifiedName = own_type.name
                  OR @containerQualifiedName = {ownQualifiedNameSql}
              )
              AND (
                  @containerQualifiedName = ''
                  OR @containerQualifiedName = peer_type.name
                  OR @containerQualifiedName = {peerQualifiedNameSql}
              )
              AND csharp_identifier_occurrence_count(peer_chunk.content, @symbolName) > 0
            LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@fileId", candidate.FileId);
        SqliteCommandPolicy.Add(cmd, "@containerKind", candidate.ContainerKind);
        SqliteCommandPolicy.Add(cmd, "@containerName", candidate.ContainerName);
        SqliteCommandPolicy.Add(cmd, "@containerQualifiedName", candidate.ContainerQualifiedName ?? string.Empty);
        SqliteCommandPolicy.Add(cmd, "@symbolName", candidate.Name);

        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead();
    }

    private static bool IsCSharpPartialContainerKind(string? kind)
        => kind is "class" or "struct" or "interface";

    private string GetUnusedCandidateFileContent(long fileId, Dictionary<long, string> fileContentByFileId)
    {
        if (fileContentByFileId.TryGetValue(fileId, out var cached))
            return cached;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT start_line, end_line, content
            FROM chunks
            WHERE file_id = @fileId
            ORDER BY start_line, chunk_index
            """;
        SqliteCommandPolicy.Add(cmd, "@fileId", fileId);

        var chunks = new List<UnusedCandidateChunk>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            chunks.Add(new UnusedCandidateChunk(
                GetInt32OrFallback(reader, 0, 1),
                GetInt32OrFallback(reader, 1, 0),
                GetNullableString(reader, 2) ?? string.Empty));
        }

        var linesByNumber = new SortedDictionary<int, string>();
        foreach (var chunk in chunks)
            AddUnusedCandidateChunkLines(linesByNumber, chunk);

        var builder = new StringBuilder();
        var nextLine = 1;
        foreach (var line in linesByNumber)
        {
            while (nextLine < line.Key)
            {
                builder.Append('\n');
                nextLine++;
            }

            builder.Append(line.Value);
            builder.Append('\n');
            nextLine = line.Key + 1;
        }

        var content = builder.ToString();
        fileContentByFileId[fileId] = content;
        return content;
    }

    private static void AddUnusedCandidateChunkLines(SortedDictionary<int, string> linesByNumber, UnusedCandidateChunk chunk)
    {
        if (chunk.StartLine <= 0 || chunk.Content.Length == 0)
            return;

        var normalized = chunk.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var lineCount = normalized.Length > 0 && normalized[^1] == '\n' ? lines.Length - 1 : lines.Length;
        if (chunk.EndLine >= chunk.StartLine)
            lineCount = Math.Min(lineCount, chunk.EndLine - chunk.StartLine + 1);

        for (var i = 0; i < lineCount; i++)
        {
            var lineNumber = chunk.StartLine + i;
            if (!linesByNumber.ContainsKey(lineNumber))
                linesByNumber.Add(lineNumber, lines[i]);
        }
    }

    private IEnumerable<UnusedCandidateSymbol> FetchUnusedCandidateSymbols(int fetchLimit, int offset, int provisionalBucketOrder, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var graphLangs = ReferenceExtractor.GetSupportedLanguages()
            .Where(value => !IsSqlLanguage(value))
            .ToList();
        var visibilitySql = $"lower({GetSymbolColumnSql("visibility", "''")})";
        var signatureSql = $"lower({GetSymbolColumnSql("signature", "''")})";
        const string pathSql = "lower(f.path)";
        var isPublicOrExportedSql = $"{visibilitySql} IN ('public', 'open', 'pub', 'export')";
        var hasConfigContextSql = $@"(
                {pathSql} LIKE 'config/%'
                OR {pathSql} LIKE '%/config/%'
                OR {pathSql} LIKE 'settings/%'
                OR {pathSql} LIKE '%/settings/%'
                OR {pathSql} LIKE 'options/%'
                OR {pathSql} LIKE '%/options/%'
                OR {signatureSql} LIKE '%iconfiguration%'
                OR {signatureSql} LIKE '%configurationsection%'
                OR {signatureSql} LIKE '%ioptions%'
                OR {signatureSql} LIKE '%options<%'
            )";
        var isReflectionOrConfigSuspectSql = $@"(
                {isPublicOrExportedSql}
                AND s.kind = 'property'
                AND {hasConfigContextSql}
            )";
        var provisionalBucketOrderSql = $@"
            CASE
                WHEN {isReflectionOrConfigSuspectSql} THEN 3
                WHEN {isPublicOrExportedSql} THEN 2
                WHEN {visibilitySql} IN ('private', 'fileprivate') THEN 0
                ELSE 1
            END";

        var sql = $@"
            SELECT s.file_id, f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("container_qualified_name", GetSymbolColumnSql("container_name", "''"))} AS container_qualified_name,
                   CASE WHEN {isPublicOrExportedSql} THEN 1 ELSE 0 END AS is_public_or_exported,
                   CASE WHEN {isReflectionOrConfigSuspectSql} THEN 1 ELSE 0 END AS is_reflection_or_config_suspect,
                   {provisionalBucketOrderSql} AS provisional_bucket_order
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.kind NOT IN ('import', 'namespace')";
        sql += $"\n              AND {BuildAmbiguousCSharpEnumMemberExclusionSql("s", "f", pathPatterns, excludePathPatterns, excludeTests)}";
        sql += """
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbol_references sr
                  WHERE sr.symbol_name IS NOT NULL
                    AND sr.symbol_name <> ''
                    AND sr.symbol_name = s.name
              )
            """;

        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";

        if (kind != null)
            sql += " AND s.kind = @kind";

        sql += " AND (" + provisionalBucketOrderSql + ") = @provisionalBucketOrder";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        sql += " ORDER BY f.path, s.line, s.name";
        sql += " LIMIT @limit OFFSET @offset";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        else
        {
            for (int i = 0; i < graphLangs.Count; i++)
                SqliteCommandPolicy.Add(cmd, $"@gl{i}", graphLangs[i]);
        }
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        SqliteCommandPolicy.Add(cmd, "@provisionalBucketOrder", provisionalBucketOrder);
        SqliteCommandPolicy.Add(cmd, "@limit", fetchLimit);
        SqliteCommandPolicy.Add(cmd, "@offset", offset);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            yield return new UnusedCandidateSymbol
            {
                FileId = reader.GetInt64(0),
                Path = reader.GetString(1),
                Lang = GetNullableString(reader, 2),
                Kind = reader.GetString(3),
                Name = reader.GetString(4),
                Line = reader.GetInt32(5),
                StartLine = GetInt32OrFallback(reader, 6, 5),
                EndLine = GetInt32OrFallback(reader, 7, 5),
                Signature = GetNullableString(reader, 8),
                Visibility = GetNullableString(reader, 9),
                ReturnType = GetNullableString(reader, 10),
                ContainerKind = GetNullableString(reader, 11),
                ContainerName = GetNullableString(reader, 12),
                ContainerQualifiedName = GetNullableString(reader, 13),
                IsPublicOrExported = reader.GetInt32(14) != 0,
                IsReflectionOrConfigSuspect = reader.GetInt32(15) != 0,
                ProvisionalBucketOrder = reader.GetInt32(16),
            };
        }
    }

    private UnusedSymbolResult CreateUnusedSymbolResult(UnusedCandidateSymbol candidate)
    {
        var kind = NormalizeUnusedSymbolKind(candidate);
        var surfaceTags = BuildUnusedIntentionalSurfaceTags(candidate, kind);
        if (!surfaceTags.Contains("reflection_or_config_suspect", StringComparer.Ordinal)
            && candidate.IsPublicOrExported
            && HasReflectionAttributeContext(kind, candidate.Path, candidate.StartLine))
        {
            AddUnusedSurfaceTag(surfaceTags, "reflection_or_config_suspect");
        }

        var isIntentionalSurfaceSuspect = surfaceTags.Count > 0;
        var classification = ClassifyUnusedSymbol(candidate.IsPublicOrExported, isIntentionalSurfaceSuspect, candidate.Visibility);
        var reasonTags = BuildUnusedReasonTags(candidate.IsPublicOrExported, isIntentionalSurfaceSuspect, candidate.Visibility, surfaceTags);
        var contractDomain = ClassifyUnusedContractDomain(candidate, kind, classification.Bucket, surfaceTags);
        return new UnusedSymbolResult
        {
            Path = candidate.Path,
            Lang = candidate.Lang,
            Kind = kind,
            Name = candidate.Name,
            Line = candidate.Line,
            StartLine = candidate.StartLine,
            EndLine = candidate.EndLine,
            Signature = candidate.Signature,
            Visibility = candidate.Visibility,
            ReturnType = candidate.ReturnType,
            ContainerKind = candidate.ContainerKind,
            ContainerName = candidate.ContainerName,
            UnusedBucket = classification.Bucket,
            UnusedConfidence = classification.Confidence,
            UnusedReason = classification.Reason,
            UnusedReasonTags = reasonTags,
            UnusedContractDomain = contractDomain.Domain,
            UnusedContractDomainTags = contractDomain.Tags,
        };
    }

    private static string NormalizeUnusedSymbolKind(UnusedCandidateSymbol candidate)
        => NormalizeUnusedSymbolKind(candidate.Lang, candidate.Kind, candidate.Signature);

    private static string NormalizeUnusedSymbolKind(string? lang, string kind, string? signature)
    {
        if (!string.Equals(lang, "csharp", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(signature))
        {
            return kind;
        }

        var trimmed = signature.TrimStart();
        if (CSharpConstFieldSignatureRegex.IsMatch(trimmed))
            return "constant";

        if ((kind is "function" or "property")
            && !CSharpPropertyAccessorSignatureRegex.IsMatch(trimmed)
            && !LooksLikeCSharpCallableSignature(trimmed)
            && CSharpFieldLikeSignatureRegex.IsMatch(trimmed))
        {
            return "field";
        }

        return kind;
    }

    private static bool LooksLikeCSharpCallableSignature(string signature)
    {
        var declarationEnd = FindCSharpDeclarationBoundary(signature);
        var declaration = (declarationEnd >= 0 ? signature[..declarationEnd] : signature).TrimEnd();
        for (var i = 0; i < declaration.Length; i++)
        {
            if (declaration[i] != '(')
                continue;

            var close = FindMatchingCSharpSignatureParen(declaration, i);
            if (close < 0)
                return false;

            var next = SkipCSharpSignatureWhitespace(declaration, close + 1);
            if (next >= declaration.Length || StartsWithCSharpWhereConstraint(declaration, next))
                return true;

            i = close;
        }

        return false;
    }

    private static int FindCSharpDeclarationBoundary(string signature)
    {
        var parenDepth = 0;
        for (var i = 0; i < signature.Length; i++)
        {
            if (TrySkipCSharpSignatureLiteral(signature, ref i))
                continue;

            var ch = signature[i];
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                continue;
            }

            if (parenDepth == 0 && (ch == '{' || ch == ';' || ch == '='))
                return i;
        }

        return -1;
    }

    private static int FindMatchingCSharpSignatureParen(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (TrySkipCSharpSignatureLiteral(text, ref i))
                continue;

            if (text[i] == '(')
            {
                depth++;
                continue;
            }

            if (text[i] != ')')
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static bool TrySkipCSharpSignatureLiteral(string text, ref int index)
    {
        if (text[index] == '\'' && TrySkipCSharpCharacterLiteral(text, ref index))
            return true;

        if (text[index] == '@' && index + 1 < text.Length && text[index + 1] == '"')
        {
            index++;
            return TrySkipCSharpVerbatimStringLiteral(text, ref index);
        }

        if (text[index] != '"')
            return false;

        var quoteRunLength = CountCSharpQuoteRun(text, index);
        if (quoteRunLength >= 3)
            return TrySkipCSharpRawStringLiteral(text, ref index, quoteRunLength);

        return TrySkipCSharpRegularStringLiteral(text, ref index);
    }

    private static int CountCSharpQuoteRun(string text, int start)
    {
        var index = start;
        while (index < text.Length && text[index] == '"')
            index++;
        return index - start;
    }

    private static bool TrySkipCSharpCharacterLiteral(string text, ref int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '\'')
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipCSharpRegularStringLiteral(string text, ref int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipCSharpVerbatimStringLiteral(string text, ref int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;

            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            index = i;
            return true;
        }

        return false;
    }

    private static bool TrySkipCSharpRawStringLiteral(string text, ref int index, int quoteRunLength)
    {
        for (var i = index + quoteRunLength; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;

            if (CountCSharpQuoteRun(text, i) < quoteRunLength)
                continue;

            index = i + quoteRunLength - 1;
            return true;
        }

        return false;
    }

    private static int SkipCSharpSignatureWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static bool StartsWithCSharpWhereConstraint(string text, int index)
    {
        const string whereKeyword = "where";
        if (!text.AsSpan(index).StartsWith(whereKeyword, StringComparison.Ordinal))
            return false;

        var end = index + whereKeyword.Length;
        return end >= text.Length || char.IsWhiteSpace(text[end]);
    }

    private List<UnusedSymbolResult> FetchUnusedCandidates(int fetchLimit, int provisionalBucketOrder, int offset, string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var graphLangs = ReferenceExtractor.GetSupportedLanguages();
        var visibilitySql = $"lower({GetSymbolColumnSql("visibility", "''")})";
        var signatureSql = $"lower({GetSymbolColumnSql("signature", "''")})";
        const string pathSql = "lower(f.path)";
        var isPublicOrExportedSql = $"{visibilitySql} IN ('public', 'open', 'pub', 'export')";
        var hasConfigContextSql = $@"(
                {pathSql} LIKE 'config/%'
                OR {pathSql} LIKE '%/config/%'
                OR {pathSql} LIKE 'settings/%'
                OR {pathSql} LIKE '%/settings/%'
                OR {pathSql} LIKE 'options/%'
                OR {pathSql} LIKE '%/options/%'
                OR {signatureSql} LIKE '%iconfiguration%'
                OR {signatureSql} LIKE '%configurationsection%'
                OR {signatureSql} LIKE '%ioptions%'
                OR {signatureSql} LIKE '%options<%'
            )";
        var isReflectionOrConfigSuspectSql = $@"(
                {isPublicOrExportedSql}
                AND s.kind = 'property'
                AND {hasConfigContextSql}
            )";
        var provisionalBucketOrderSql = $@"
            CASE
                WHEN {isReflectionOrConfigSuspectSql} THEN 3
                WHEN {isPublicOrExportedSql} THEN 2
                WHEN {visibilitySql} IN ('private', 'fileprivate') THEN 0
                ELSE 1
            END";

        var sql = $@"
            WITH unused_candidates AS (
                SELECT s.file_id, f.path, f.lang, s.kind, s.name, s.line,
                       {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                       {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                       {GetSymbolColumnSql("signature")} AS signature,
                       {GetSymbolColumnSql("visibility")} AS visibility,
                       {GetSymbolColumnSql("return_type")} AS return_type,
                       {GetSymbolColumnSql("container_kind")} AS container_kind,
                       {GetSymbolColumnSql("container_name")} AS container_name,
                       {GetSymbolColumnSql("container_qualified_name", GetSymbolColumnSql("container_name", "''"))} AS container_qualified_name,
                       CASE WHEN {isPublicOrExportedSql} THEN 1 ELSE 0 END AS is_public_or_exported,
                       CASE WHEN {isReflectionOrConfigSuspectSql} THEN 1 ELSE 0 END AS is_reflection_or_config_suspect,
                       {provisionalBucketOrderSql} AS provisional_bucket_order
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.kind NOT IN ('import', 'namespace')
                  AND NOT EXISTS (
                      SELECT 1
                      FROM symbol_references sr
                      JOIN files rf ON rf.id = sr.file_id" + ReferenceLineJoinSql("sr") + @"
                      WHERE sr.symbol_name = s.name
                         OR (f.lang = 'sql' AND rf.lang = 'sql' AND (
                                (sql_resolve_reference_segment_count_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number) = sql_segment_count(s.name)
                                 AND sql_reference_matches_target_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number, s.name) = 1)
                         OR (sql_segment_count(sr.symbol_name) = 1
                            AND sql_allow_leaf_fallback_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number) = 1
                            AND sr.symbol_name = sql_leaf_name(s.name) COLLATE NOCASE
                            AND NOT EXISTS (
                                    SELECT 1
                                    FROM symbols s_exact
                                    JOIN files f_exact ON f_exact.id = s_exact.file_id
                                    WHERE f_exact.lang = 'sql'
                                      AND sql_segment_count(s_exact.name) = sql_resolve_reference_segment_count_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number)
                                     AND sql_reference_matches_target_at(sr.symbol_name, " + ReferenceContextSql("sr") + @", sr.container_name, sr.column_number, s_exact.name) = 1
                                ))
                         ))
                  )";
        if (_hasChunksTable && HasTable("chunks"))
        {
            sql += BuildSameFilePrivateUseExclusionSql(
                "s",
                "f",
                visibilitySql,
                GetSymbolColumnSql("start_line", "s.line"),
                GetSymbolColumnSql("end_line", "s.line"));
            sql += BuildCSharpPartialContainingTypeUseExclusionSql("s", "f", visibilitySql);
        }
        sql += $"\n              AND {BuildAmbiguousCSharpEnumMemberExclusionSql("s", "f", pathPatterns, excludePathPatterns, excludeTests)}";

        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";

        if (kind != null)
            sql += " AND s.kind = @kind";

        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        sql += @"
            )
            SELECT file_id, path, lang, kind, name, line, start_line, end_line, signature, visibility,
                   return_type, container_kind, container_name, container_qualified_name,
                   is_public_or_exported, is_reflection_or_config_suspect, provisional_bucket_order
            FROM unused_candidates
            WHERE provisional_bucket_order = @bucketOrder
            ORDER BY path, line, name
            LIMIT @limit OFFSET @offset";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        SqliteCommandPolicy.Add(cmd, "@bucketOrder", provisionalBucketOrder);
        SqliteCommandPolicy.Add(cmd, "@limit", fetchLimit);
        SqliteCommandPolicy.Add(cmd, "@offset", offset);
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        else
        {
            var langList = graphLangs.ToList();
            for (int i = 0; i < langList.Count; i++)
                SqliteCommandPolicy.Add(cmd, $"@gl{i}", langList[i]);
        }
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        var results = new List<UnusedSymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var candidate = new UnusedCandidateSymbol
            {
                FileId = reader.GetInt64(0),
                Path = reader.GetString(1),
                Lang = GetNullableString(reader, 2),
                Kind = reader.GetString(3),
                Name = reader.GetString(4),
                Line = reader.GetInt32(5),
                StartLine = GetInt32OrFallback(reader, 6, 5),
                EndLine = GetInt32OrFallback(reader, 7, 5),
                Signature = GetNullableString(reader, 8),
                Visibility = GetNullableString(reader, 9),
                ReturnType = GetNullableString(reader, 10),
                ContainerKind = GetNullableString(reader, 11),
                ContainerName = GetNullableString(reader, 12),
                ContainerQualifiedName = GetNullableString(reader, 13),
                IsPublicOrExported = reader.GetInt32(14) != 0,
                IsReflectionOrConfigSuspect = reader.GetInt32(15) != 0,
                ProvisionalBucketOrder = reader.GetInt32(16),
            };
            results.Add(CreateUnusedSymbolResult(candidate));
        }

        return results;
    }

    private static List<UnusedSymbolResult> DiversifyUnusedResults(List<UnusedSymbolResult> results, int limit)
    {
        if (results.Count == 0 || limit <= 0)
            return results;

        var targetCount = Math.Min(limit, results.Count);
        var buckets = OrderedUnusedBuckets
            .ToDictionary(
                bucket => bucket,
                bucket => new Queue<UnusedSymbolResult>(results.Where(result => result.UnusedBucket == bucket)),
                StringComparer.Ordinal);

        var limited = new List<UnusedSymbolResult>(targetCount);
        bool advanced;
        do
        {
            advanced = false;
            foreach (var bucket in OrderedUnusedBuckets)
            {
                var queue = buckets[bucket];
                if (queue.Count == 0)
                    continue;

                limited.Add(queue.Dequeue());
                advanced = true;
                if (limited.Count >= targetCount)
                    return limited;
            }
        } while (advanced);

        return limited;
    }

    private static List<UnusedSymbolResult> FilterUnusedResults(IEnumerable<UnusedSymbolResult> results, string? bucketFilter, string? minConfidence)
        => results
            .Where(result => MatchesUnusedFilters(result, bucketFilter, minConfidence))
            .ToList();

    private static bool HasEffectiveUnusedFilter(string? bucketFilter, string? minConfidence)
        => bucketFilter != null || string.Equals(minConfidence, "medium", StringComparison.Ordinal);

    private static Dictionary<string, List<UnusedSymbolResult>> CreateUnusedBucketResultLists()
        => OrderedUnusedBuckets.ToDictionary(
            bucket => bucket,
            _ => new List<UnusedSymbolResult>(),
            StringComparer.Ordinal);

    private static HashSet<string> GetTargetUnusedBuckets(string? bucketFilter, string? minConfidence)
    {
        var minConfidenceRank = minConfidence == null ? int.MinValue : GetUnusedConfidenceRank(minConfidence);
        return OrderedUnusedBuckets
            .Where(bucket => bucketFilter == null || string.Equals(bucket, bucketFilter, StringComparison.Ordinal))
            .Where(bucket => GetUnusedConfidenceRank(GetUnusedBucketConfidence(bucket)) >= minConfidenceRank)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<int> GetRelevantUnusedProvisionalBuckets(IReadOnlySet<string> targetBuckets)
    {
        var wantsIntentionalSurface = targetBuckets.Contains(UnusedBucketReflectionOrConfig);
        if (targetBuckets.Contains(UnusedBucketLikelyPrivate) || wantsIntentionalSurface)
            yield return 0;
        if (targetBuckets.Contains(UnusedBucketMaybeNonPublic) || wantsIntentionalSurface)
            yield return 1;
        if (targetBuckets.Contains(UnusedBucketPublicOrExported) || wantsIntentionalSurface)
            yield return 2;
        if (wantsIntentionalSurface)
            yield return 3;
    }

    private static bool AllTargetUnusedBucketsFilled(
        IReadOnlyDictionary<string, List<UnusedSymbolResult>> resultsByBucket,
        HashSet<string> targetBuckets,
        int targetCount)
    {
        foreach (var bucket in targetBuckets)
        {
            if (resultsByBucket[bucket].Count < targetCount)
                return false;
        }

        return true;
    }

    private static void AddFilteredUnusedResult(
        Dictionary<string, List<UnusedSymbolResult>> resultsByBucket,
        HashSet<string> targetBuckets,
        int targetCount,
        UnusedSymbolResult result,
        string? bucketFilter,
        string? minConfidence)
    {
        if (!MatchesUnusedFilters(result, bucketFilter, minConfidence))
            return;
        if (!targetBuckets.Contains(result.UnusedBucket))
            return;

        var bucketResults = resultsByBucket[result.UnusedBucket];
        if (bucketResults.Count < targetCount)
            bucketResults.Add(result);
    }

    private static bool MatchesUnusedFilters(UnusedSymbolResult result, string? bucketFilter, string? minConfidence)
    {
        if (bucketFilter != null && !string.Equals(result.UnusedBucket, bucketFilter, StringComparison.Ordinal))
            return false;

        if (minConfidence != null && GetUnusedConfidenceRank(result.UnusedConfidence) < GetUnusedConfidenceRank(minConfidence))
            return false;

        return true;
    }

    private static string GetUnusedBucketConfidence(string bucket)
        => string.Equals(bucket, UnusedBucketLikelyPrivate, StringComparison.Ordinal) ? "medium" : "low";

    private static int GetUnusedConfidenceRank(string confidence) => confidence switch
    {
        "medium" => 1,
        "low" => 0,
        _ => -1,
    };

    public UnusedCountResult CountUnusedSymbolsDetailed(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, string? bucketFilter = null, string? minConfidence = null)
        => CountUnusedSymbolsDetailedCore(
            kind,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            visibilityFilters,
            excludeVisibilityFilters,
            bucketFilter,
            minConfidence,
            resultFilter: null);

    public UnusedCountResult CountUnusedSymbolsDetailedFiltered(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters, string? bucketFilter, string? minConfidence, Func<UnusedSymbolResult, bool> resultFilter)
    {
        ArgumentNullException.ThrowIfNull(resultFilter);
        return CountUnusedSymbolsDetailedCore(
            kind,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            visibilityFilters,
            excludeVisibilityFilters,
            bucketFilter,
            minConfidence,
            resultFilter);
    }

    private UnusedCountResult CountUnusedSymbolsDetailedCore(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence, Func<UnusedSymbolResult, bool>? resultFilter)
    {
        if (!_hasReferencesTable)
            return EmptyUnusedCountResult();
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            return EmptyUnusedCountResult();
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return CountUnusedSymbolsDetailedWithoutSqlResolver(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence, resultFilter);

        return CountUnusedSymbolsDetailedWithSqlResolver(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence, resultFilter);
    }

    private UnusedCountResult CountUnusedSymbolsDetailedWithSqlResolver(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence, Func<UnusedSymbolResult, bool>? resultFilter)
    {
        var count = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var includesSql = false;
        var bucketCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var confidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var contractDomainCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        const int batchSize = UnusedPublicOverfetchMaximum;
        for (var bucket = 0; bucket <= 3; bucket++)
        {
            var offset = 0;
            while (true)
            {
                var batch = FetchUnusedCandidates(batchSize, bucket, offset, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var result in batch)
                {
                    if (!MatchesUnusedFilters(result, bucketFilter, minConfidence))
                        continue;
                    if (resultFilter != null && !resultFilter(result))
                        continue;

                    AddUnusedCountResult(result, paths, bucketCounts, confidenceCounts, contractDomainCounts, ref count, ref includesSql);
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        return CreateUnusedCountResult(count, paths.Count, includesSql, bucketCounts, confidenceCounts, contractDomainCounts);
    }

    private UnusedCountResult CountUnusedSymbolsDetailedWithoutSqlResolver(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence, Func<UnusedSymbolResult, bool>? resultFilter)
    {
        var count = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var includesSql = false;
        var bucketCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var confidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var contractDomainCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var fileContentByFileId = new Dictionary<long, string>();
        const int batchSize = UnusedPublicOverfetchMaximum;
        for (var bucket = 0; bucket <= 3; bucket++)
        {
            var offset = 0;
            while (true)
            {
                var batch = FetchUnusedCandidateSymbols(batchSize, offset, bucket, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var candidate in batch)
                {
                    if (HasPrivateCSharpUse(candidate, fileContentByFileId))
                        continue;

                    var result = CreateUnusedSymbolResult(candidate);
                    if (!MatchesUnusedFilters(result, bucketFilter, minConfidence))
                        continue;
                    if (resultFilter != null && !resultFilter(result))
                        continue;

                    AddUnusedCountResult(result, paths, bucketCounts, confidenceCounts, contractDomainCounts, ref count, ref includesSql);
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        return CreateUnusedCountResult(count, paths.Count, includesSql, bucketCounts, confidenceCounts, contractDomainCounts);
    }

    private static void AddUnusedCountResult(
        UnusedSymbolResult result,
        HashSet<string> paths,
        Dictionary<string, int> bucketCounts,
        Dictionary<string, int> confidenceCounts,
        Dictionary<string, int> contractDomainCounts,
        ref int count,
        ref bool includesSql)
    {
        count++;
        paths.Add(result.Path);
        if (IsSqlLanguage(result.Lang))
            includesSql = true;
        IncrementUnusedCount(bucketCounts, result.UnusedBucket);
        IncrementUnusedCount(confidenceCounts, result.UnusedConfidence);
        if (!string.IsNullOrWhiteSpace(result.UnusedContractDomain))
            IncrementUnusedCount(contractDomainCounts, result.UnusedContractDomain);
    }

    private static void IncrementUnusedCount(Dictionary<string, int> counts, string key)
    {
        if (counts.TryGetValue(key, out var count))
            counts[key] = count + 1;
        else
            counts[key] = 1;
    }

    private static UnusedCountResult CreateUnusedCountResult(
        int count,
        int fileCount,
        bool includesSql,
        Dictionary<string, int> bucketCounts,
        Dictionary<string, int> confidenceCounts,
        Dictionary<string, int> contractDomainCounts)
        => new(
            count,
            fileCount,
            includesSql,
            OrderUnusedBucketCounts(bucketCounts),
            OrderUnusedConfidenceCounts(confidenceCounts),
            OrderUnusedContractDomainCounts(contractDomainCounts));

    private static UnusedCountResult EmptyUnusedCountResult()
        => CreateUnusedCountResult(
            0,
            0,
            includesSql: false,
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal));

    private static QueryCountResult ToQueryCountResult(UnusedCountResult result)
        => new(result.Count, result.FileCount, result.IncludesSql);

    private static Dictionary<string, int> OrderUnusedBucketCounts(Dictionary<string, int> counts)
    {
        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bucket in OrderedUnusedBuckets)
        {
            if (counts.TryGetValue(bucket, out var count))
                ordered[bucket] = count;
        }

        return ordered;
    }

    private static Dictionary<string, int> OrderUnusedConfidenceCounts(Dictionary<string, int> counts)
    {
        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var confidence in new[] { "medium", "low" })
        {
            if (counts.TryGetValue(confidence, out var count))
                ordered[confidence] = count;
        }

        foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ordered.ContainsKey(pair.Key))
                ordered[pair.Key] = pair.Value;
        }

        return ordered;
    }

    private static Dictionary<string, int> OrderUnusedContractDomainCounts(Dictionary<string, int> counts)
    {
        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var domain in OrderedUnusedContractDomains)
        {
            if (counts.TryGetValue(domain, out var count))
                ordered[domain] = count;
        }

        foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ordered.ContainsKey(pair.Key))
                ordered[pair.Key] = pair.Value;
        }

        return ordered;
    }

    public QueryCountResult CountUnusedSymbols(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null, string? bucketFilter = null, string? minConfidence = null)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            return new QueryCountResult(0, 0);
        if (bucketFilter != null || minConfidence != null)
            return CountFilteredUnusedSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return CountUnusedSymbolsWithoutSqlResolver(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters);

        var graphLangs = ReferenceExtractor.GetSupportedLanguages();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("sr");
        var contextSql = ReferenceContextSql("sr");
        var sql = @"
            SELECT COUNT(*), COUNT(DISTINCT f.path), MAX(CASE WHEN f.lang = 'sql' THEN 1 ELSE 0 END)
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.kind NOT IN ('import', 'namespace')
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbol_references sr
                  JOIN files rf ON rf.id = sr.file_id" + referenceLineJoin + @"
                  WHERE sr.symbol_name = s.name
                     OR (f.lang = 'sql' AND rf.lang = 'sql' AND (
                            (sql_resolve_reference_segment_count_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number) = sql_segment_count(s.name)
                             AND sql_reference_matches_target_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number, s.name) = 1)
                         OR (sql_segment_count(sr.symbol_name) = 1
                            AND sql_allow_leaf_fallback_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number) = 1
                            AND sr.symbol_name = sql_leaf_name(s.name) COLLATE NOCASE
                            AND NOT EXISTS (
                                    SELECT 1
                                    FROM symbols s_exact
                                    JOIN files f_exact ON f_exact.id = s_exact.file_id
                                    WHERE f_exact.lang = 'sql'
                                      AND sql_segment_count(s_exact.name) = sql_resolve_reference_segment_count_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number)
                                      AND sql_reference_matches_target_at(sr.symbol_name, " + contextSql + @", sr.container_name, sr.column_number, s_exact.name) = 1
                                ))
                     ))
              )";
        if (_hasChunksTable && HasTable("chunks"))
        {
            sql += BuildSameFilePrivateUseExclusionSql(
                "s",
                "f",
                $"lower({GetSymbolColumnSql("visibility", "''")})",
                GetSymbolColumnSql("start_line", "s.line"),
                GetSymbolColumnSql("end_line", "s.line"));
            sql += BuildCSharpPartialContainingTypeUseExclusionSql(
                "s",
                "f",
                $"lower({GetSymbolColumnSql("visibility", "''")})");
        }
        sql += $"\n              AND {BuildAmbiguousCSharpEnumMemberExclusionSql("s", "f", pathPatterns, excludePathPatterns, excludeTests)}";

        if (lang != null)
            sql += SymbolLanguageFileIdFilter;
        else
            sql += $" AND f.lang IN ({string.Join(",", graphLangs.Select((_, i) => $"@gl{i}"))})";

        if (kind != null)
            sql += " AND s.kind = @kind";

        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        AppendVisibilityFilters(ref sql, visibilityFilters, excludeVisibilityFilters);
        cmd.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.Add(cmd, "@lang", lang);
        else
        {
            var langList = graphLangs.ToList();
            for (int i = 0; i < langList.Count; i++)
                SqliteCommandPolicy.Add(cmd, $"@gl{i}", langList[i]);
        }
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        AddVisibilityFilterParameters(cmd, visibilityFilters, excludeVisibilityFilters);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return new QueryCountResult(0, 0);
        return new QueryCountResult(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.FieldCount > 2 && !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) != 0);
    }

    private QueryCountResult CountFilteredUnusedSymbols(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        if (!ScopeMayIncludeSqlSymbols(kind, lang, pathPatterns, excludePathPatterns, excludeTests))
            return CountFilteredUnusedSymbolsWithoutSqlResolver(kind, lang, pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters, bucketFilter, minConfidence);

        return ToQueryCountResult(CountUnusedSymbolsDetailedWithSqlResolver(
            kind,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            visibilityFilters,
            excludeVisibilityFilters,
            bucketFilter,
            minConfidence,
            resultFilter: null));
    }

    private QueryCountResult CountFilteredUnusedSymbolsWithoutSqlResolver(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests,
        IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters,
        string? bucketFilter, string? minConfidence)
    {
        return ToQueryCountResult(CountUnusedSymbolsDetailedWithoutSqlResolver(
            kind,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            visibilityFilters,
            excludeVisibilityFilters,
            bucketFilter,
            minConfidence,
            resultFilter: null));
    }

    private QueryCountResult CountUnusedSymbolsWithoutSqlResolver(string? kind, string? lang,
        IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, IReadOnlyList<string>? visibilityFilters = null, IReadOnlyList<string>? excludeVisibilityFilters = null)
    {
        var count = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var fileContentByFileId = new Dictionary<long, string>();
        const int batchSize = UnusedPublicOverfetchMaximum;
        for (var bucket = 0; bucket <= 3; bucket++)
        {
            var offset = 0;
            while (true)
            {
                var batch = FetchUnusedCandidateSymbols(batchSize, offset, bucket, kind, lang,
                    pathPatterns, excludePathPatterns, excludeTests, visibilityFilters, excludeVisibilityFilters).ToList();
                if (batch.Count == 0)
                    break;

                offset += batch.Count;
                foreach (var candidate in batch)
                {
                    if (HasPrivateCSharpUse(candidate, fileContentByFileId))
                        continue;

                    count++;
                    paths.Add(candidate.Path);
                }

                if (batch.Count < batchSize)
                    break;
            }
        }

        return new QueryCountResult(count, paths.Count);
    }

    public bool ScopeMayIncludeSqlSymbols(string? kind, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (lang != null && !IsSqlLanguage(lang))
            return false;

        using var cmd = _conn.CreateCommand();
        var sql = """
            SELECT 1
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'sql'
              AND s.kind NOT IN ('import', 'namespace')
            """;
        if (kind != null)
            sql += " AND s.kind = @kind";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        if (kind != null)
            SqliteCommandPolicy.Add(cmd, "@kind", kind);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        return cmd.ExecuteScalar() != null;
    }

    private bool HasReflectionAttributeContext(string kind, string path, int startLine)
    {
        if (!_hasChunksTable || startLine <= 1)
            return false;

        var reflectionAttributeNames = GetReflectionAttributeNamesForKind(kind);
        if (reflectionAttributeNames == null)
            return false;

        var excerptStart = Math.Max(1, startLine - UnusedAttributeContextWindow);
        FileExcerptResult? excerpt;
        try
        {
            excerpt = GetExcerpt(path, excerptStart, startLine + UnusedAttributeContextWindow);
        }
        catch (SqliteException ex) when (IsMissingChunksTableUnavailable(ex))
        {
            return false;
        }
        if (excerpt == null)
            return false;

        var lines = excerpt.Content.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var currentIndex = startLine - excerptStart;
        if (currentIndex < 0 || currentIndex >= lines.Length)
            return false;

        // Sanitize the excerpt across lines so multi-line verbatim / raw /
        // raw-interpolated string literals that contain `[` or `]` in the
        // attribute argument do not leak reflection attribute context onto
        // adjacent symbols. Closes #409.
        // 複数行 verbatim / raw / raw 補間文字列リテラル内の `[` / `]` が
        // 隣接シンボルへ reflection 属性コンテキストを漏らさないよう、
        // 抜粋を行をまたいで sanitize する。#409 を修正。
        var sanitizedLines = SymbolExtractor.SanitizeCSharpLinesForCrossLineScan(lines);
        var triviaMask = BuildTriviaMask(sanitizedLines);
        var attributeBlock = GetAdjacentAttributeBlock(lines, sanitizedLines, triviaMask, currentIndex);
        if (attributeBlock.Count == 0)
            return false;

        var attributeNames = ExtractNormalizedAttributeNames(attributeBlock);
        if (attributeNames.Overlaps(ReflectionIgnoreAttributeNames))
            return false;

        return attributeNames.Overlaps(reflectionAttributeNames);
    }

    private static HashSet<string>? GetReflectionAttributeNamesForKind(string kind)
    {
        if (kind is "property" or "field")
            return ReflectionPropertyAttributeNames;

        if (kind is "class" or "struct" or "interface" or "enum")
            return ReflectionTypeAttributeNames;

        if (kind == "function")
            return ReflectionFunctionAttributeNames;

        return null;
    }

    private bool IsMissingChunksTableUnavailable(SqliteException ex)
    {
        if (ex.SqliteErrorCode != 1)
            return false;

        _schemaCache?.Refresh();
        return !HasTable("chunks");
    }

    private static List<string> GetAdjacentAttributeBlock(string[] lines, string[] sanitizedLines, bool[] triviaMask, int anchorIndex)
    {
        var anchorLine = lines[anchorIndex];
        // Run the inline `[attr] decl;` check against the sanitized anchor so that
        // a line whose first non-whitespace token is a block comment — e.g.
        // `/* note */ [JsonPropertyName("ok")] public string A { get; set; }` —
        // still registers as an inline-attribute-with-declaration. The sanitizer
        // blanks leading `/* ... */` bodies and delimiters to whitespace, leaving
        // `[JsonPropertyName(    )] public string A ...` which satisfies the
        // leading-`[` anchor in LineContainsInlineAttributeAndDeclaration. Using
        // the original line would miss this valid C# shape and drop the property
        // out of `reflection_or_config_suspect`. The #409 intent — refusing to
        // treat multi-line literal continuation tails like `]")] public string A ...`
        // as inline declarations — is preserved: sanitization cannot blank the
        // leading `)` into a `[`, so the leading-`[` anchor still rejects those
        // continuation rows. Closes #409.
        // anchor 行のインライン `[attr] decl;` 判定は sanitize 済み行に対して行う。
        // 行頭ブロックコメントの後ろに属性と宣言が並ぶ、例えば
        // `/* note */ [JsonPropertyName("ok")] public string A { get; set; }` も
        // sanitizer が先頭 `/* ... */` 本体と区切りを空白化するため、
        // `[JsonPropertyName(    )] public string A ...` として扱え、
        // LineContainsInlineAttributeAndDeclaration の先頭 `[` アンカーを満たす。
        // original 行で判定するとこの正しい C# 形を取りこぼし、対象プロパティが
        // `reflection_or_config_suspect` から外れてしまう。#409 の意図
        // （`]")] public string A ...` のような複数行リテラル tail を
        // インライン宣言と見なさない）は、sanitize で先頭 `)` が `[` に変わる
        // ことはないため維持される。#409 を修正。
        if (LineContainsInlineAttributeAndDeclaration(sanitizedLines[anchorIndex]))
            return [sanitizedLines[anchorIndex].Trim()];

        var declarationIndex = anchorIndex;
        if (LooksLikeAttributeBoundaryLine(anchorLine))
        {
            // A line like `]")] public string A ...` is itself both the tail of a
            // multi-line attribute literal AND the inline declaration. In that case
            // the declaration lives on the anchor line itself, so do not walk forward
            // looking for a separate declaration below (which would otherwise skip
            // past the real declaration and pick up an unrelated symbol). Closes #409.
            // `]")] public string A ...` のように複数行属性リテラルの末尾かつ
            // インライン宣言でもある行の場合、宣言は anchor 行自身にあるため、
            // 下方へ別の宣言を探しに行かない（探しに行くと本来の宣言を飛び越して
            // 別シンボルを拾ってしまう）。#409 を修正。
            if (!SanitizedLineHasInlineDeclarationTail(sanitizedLines[anchorIndex]))
            {
                declarationIndex = FindNextDeclarationLine(lines, triviaMask, anchorIndex + 1);
                if (declarationIndex < 0)
                    return [];
            }
        }

        var attributeBottom = FindPreviousNonTriviaLine(lines, triviaMask, declarationIndex - 1);
        if (attributeBottom < 0 || !LooksLikeAttributeBoundaryLine(lines[attributeBottom]))
            return [];

        // If the previous non-trivia line already has its own inline `[attr] decl`,
        // its attribute belongs to that line's declaration, not to the anchor below.
        // Without this guard, a line like `[JsonPropertyName("a[")] public string X { ... }`
        // would leak its reflection attribute onto the next plain property and flip
        // that unrelated symbol into the reflection_or_config_suspect bucket. Closes #375.
        // The same leak surfaces when the `[` and the declaration are split by a
        // multi-line verbatim / raw / raw-interpolated string literal: the line at
        // attributeBottom then starts with a continuation tail like `)]` before the
        // real declaration. LineContainsInlineAttributeAndDeclaration's leading-`[`
        // anchor cannot see that pattern, so we additionally check the sanitized
        // line for trailing declaration content after the last `]`. Both checks
        // run against the sanitized line so that a trailing `// comment` on the
        // attribute row (e.g. `[JsonPropertyName("ok")] // note`) does not count
        // as an inline declaration tail. Closes #409.
        // 直前の非 trivia 行がすでに `[attr] decl` のインライン宣言を持つ場合、
        // その属性はその行の宣言に属し、下の anchor 行には及ばない。
        // このガードが無いと `[JsonPropertyName("a[")] public string X { ... }` の属性が
        // 下の属性なしプロパティに漏れ、無関係なシンボルが reflection_or_config_suspect に
        // 誤分類される。#375 を修正。
        // `[` と宣言が複数行 verbatim / raw / raw 補間文字列リテラルで分断されると、
        // attributeBottom の行は `)]` 等の継続末尾で始まり LineContainsInlineAttributeAndDeclaration の
        // 先頭 `[` アンカーでは捕らえられない。sanitize 済み行の最後の `]` 以降に
        // 宣言本体が残っていないかを併せて確認する。判定はいずれも sanitize 済み行に対して行う。
        // これにより属性行末尾の `// コメント`（例: `[JsonPropertyName("ok")] // note`）が
        // 宣言末尾と誤判定されることも防ぐ。#409 を修正。
        if (attributeBottom != anchorIndex
            && (LineContainsInlineAttributeAndDeclaration(lines[attributeBottom])
                || SanitizedLineHasInlineDeclarationTail(sanitizedLines[attributeBottom])))
            return [];

        // Build the attribute block from the cross-line-sanitized lines so
        // comment bodies never bleed into downstream attribute-name parsing.
        // A multi-line block comment embedded inside an attribute list, e.g.
        //   [
        //       /* explanation
        //          [JsonIgnore] */
        //       JsonPropertyName("ok")
        //   ]
        // has `[JsonIgnore]` inside the comment body. Its original line
        // survives `BuildSingleLineTrivia`, and `ExtractNormalizedAttributeNames`
        // would otherwise parse a phantom `JsonIgnore` attribute that cancels
        // the real `JsonPropertyName`. Sanitized lines blank the comment body
        // (with lexer state carried across physical lines), so the phantom
        // attribute disappears while real identifiers like `JsonPropertyName`
        // are preserved. Closes #409 follow-up.
        // 属性ブロックは横断 sanitize 済み行から構築する。これにより、
        // 属性リスト内に埋め込まれた複数行ブロックコメント（上の例のように
        // `[JsonIgnore]` を本体に含むもの）のコメント本体がダウンストリームの
        // 属性名パースへ漏れることがない。元の行を渡すと `BuildSingleLineTrivia`
        // をすり抜けて `ExtractNormalizedAttributeNames` が幻の `JsonIgnore` を
        // 拾い、本物の `JsonPropertyName` を打ち消してしまっていた。
        // sanitize 済み行は物理行を跨ぐ lexer 状態でコメント本体を空白化するため、
        // 幻の属性は消え、`JsonPropertyName` のような本物の識別子だけが残る。
        // #409 追加修正。
        var block = new List<string>();
        var bracketDepth = 0;
        var sawBracket = false;

        for (int i = attributeBottom; i >= 0; i--)
        {
            var trimmed = sanitizedLines[i].Trim();
            if (triviaMask[i])
            {
                if (sawBracket)
                    block.Add(trimmed);
                continue;
            }

            var hasBracketToken = LooksLikeAttributeBoundaryLine(trimmed);
            if (!sawBracket)
            {
                if (!hasBracketToken)
                    return [];
                sawBracket = true;
            }
            else if (bracketDepth == 0 && !hasBracketToken)
            {
                break;
            }

            block.Add(trimmed);
            bracketDepth += CountBracketDeltaOutsideStrings(trimmed);
            if (bracketDepth < 0)
                bracketDepth = 0;
        }

        block.Reverse();
        return block;
    }

    // Count `] - [` on a C# line while skipping characters that appear inside
    // string or char literals, so standalone attribute rows like `[Obsolete("]")]`
    // do not leave one bracket of residual depth and swallow an unrelated
    // attribute block above them.
    // C# の 1 行について、文字列 / 文字リテラル内の文字を除外した上で `] - [` を数える。
    // `[Obsolete("]")]` のような standalone 属性行が 1 つ分の bracket depth を残して
    // 上の無関係な属性ブロックまで吸い込むのを防ぐ。
    private static int CountBracketDeltaOutsideStrings(string line)
    {
        if (string.IsNullOrEmpty(line))
            return 0;

        var delta = 0;
        var cursor = 0;
        while (cursor < line.Length)
        {
            if (TrySkipCSharpStringOrCharLiteral(line, ref cursor))
                continue;
            var ch = line[cursor++];
            if (ch == '[')
                delta--;
            else if (ch == ']')
                delta++;
        }
        return delta;
    }

    private static bool LineContainsInlineAttributeAndDeclaration(string line)
    {
        // Only a line that actually starts with `[` (after whitespace) can be an
        // inline `[attr] decl;` row. Without this anchor, continuation rows of a
        // multi-line attribute literal (e.g. the `]")]` tail of
        // `[JsonPropertyName(@"a[\n]")]`) pass LooksLikeAttributeBoundaryLine and
        // survive StripLeadingCSharpAttributeLists (which returns the input
        // unchanged when the line doesn't start with `[`), so the guard in
        // GetAdjacentAttributeBlock incorrectly treats them as standalone
        // inline-declaration rows and drops the real attribute block above —
        // flipping reflection-attributed properties out of
        // `reflection_or_config_suspect`. Closes #409.
        // 先頭が `[`（空白を除く）である行だけがインライン `[attr] decl;` 行として成立する。
        // このアンカーが無いと、複数行にまたがる属性リテラルの継続行（例:
        // `[JsonPropertyName(@"a[\n]")]` の末尾 `]")]`）が LooksLikeAttributeBoundaryLine を
        // 通過し、`[` 始まりでない入力をそのまま返す StripLeadingCSharpAttributeLists の
        // 挙動により、GetAdjacentAttributeBlock のガードがそれを単独の
        // インライン宣言行と誤判定して本来の属性ブロックを潰し、reflection 属性付き
        // プロパティが `reflection_or_config_suspect` から外れる。#409 を修正。
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || line[index] != '[')
            return false;

        var remainder = StripLeadingCSharpAttributeLists(line);
        if (string.IsNullOrWhiteSpace(remainder))
            return false;

        remainder = remainder.TrimStart();
        return remainder.Length > 0
            && remainder[0] != '/'
            && remainder[0] != '*'
            && remainder[0] != '#';
    }

    // Detect that the cross-line-sanitized line ends an attribute run with
    // trailing declaration content (single-line `[Foo] decl;` or the tail of a
    // multi-line literal attribute such as `)] decl;`). The sanitizer blanks
    // string / char / comment bodies AND their delimiters, so a genuine
    // declaration `public string X;` shows up as non-whitespace text after the
    // last `]`. A pure attribute line `[Foo]` has only whitespace after its
    // last `]` and returns false here. Closes #409.
    // 横断 sanitize 済みの行が、属性ブロックに続けて宣言本体を抱えているか
    // （単行 `[Foo] decl;` または複数行リテラル属性の末尾 `)] decl;`）を判定する。
    // sanitizer は文字列 / 文字 / コメントの本体と区切りを空白化するため、
    // 実宣言 `public string X;` は最後の `]` 以降に非空白として残る。
    // 属性単独行 `[Foo]` の場合は最後の `]` 以降が空白のみなので false を返す。#409 を修正。
    private static bool SanitizedLineHasInlineDeclarationTail(string sanitizedLine)
    {
        var lastBracket = sanitizedLine.LastIndexOf(']');
        if (lastBracket < 0)
            return false;
        for (var i = lastBracket + 1; i < sanitizedLine.Length; i++)
        {
            if (!char.IsWhiteSpace(sanitizedLine[i]))
                return true;
        }
        return false;
    }

    private static int FindNextDeclarationLine(string[] lines, bool[] triviaMask, int startIndex)
    {
        for (int i = startIndex; i < lines.Length; i++)
        {
            if (!triviaMask[i])
                return i;
        }

        return -1;
    }

    private static int FindPreviousNonTriviaLine(string[] lines, bool[] triviaMask, int startIndex)
    {
        for (int i = startIndex; i >= 0; i--)
        {
            if (!triviaMask[i])
                return i;
        }

        return -1;
    }

    // A line is trivia iff its cross-line-sanitized form is entirely whitespace.
    // `SanitizeCSharpLinesForCrossLineScan` already blanks strings, chars, `//`
    // line comments, and `/* ... */` block comments (with state carried across
    // physical lines), so any non-whitespace left over is real code. The previous
    // heuristic flagged any line that merely *contained* `*/` as trivia, which
    // wrongly skipped attribute rows with a trailing block comment such as
    // `[JsonPropertyName("ok")] /* note */` and made FindPreviousNonTriviaLine
    // overshoot past the real attribute block, dropping reflection context off
    // the following property. Closes #409 follow-up.
    // 行の trivia 判定は、横断 sanitize 済み形がすべて空白かどうかで決める。
    // `SanitizeCSharpLinesForCrossLineScan` は文字列 / 文字 / `//` 行コメント /
    // `/* ... */` ブロックコメント（物理行を跨ぐ状態保持付き）をすべて空白化するため、
    // 残った非空白は必ず本物のコード。以前のヒューリスティックは `*/` を含むだけで
    // trivia 判定していたため、`[JsonPropertyName("ok")] /* note */` のような末尾
    // ブロックコメント付き属性行を飛ばしてしまい、FindPreviousNonTriviaLine が
    // 本来の属性ブロックを越えて遡り、直下プロパティの reflection コンテキストを
    // 落としていた。#409 追加修正。
    private static bool[] BuildTriviaMask(string[] sanitizedLines)
    {
        var triviaMask = new bool[sanitizedLines.Length];
        for (int i = 0; i < sanitizedLines.Length; i++)
            triviaMask[i] = string.IsNullOrWhiteSpace(sanitizedLines[i]);
        return triviaMask;
    }

    private static HashSet<string> ExtractNormalizedAttributeNames(IReadOnlyList<string> attributeBlock)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var content = string.Join("\n", attributeBlock.Where(line => !BuildSingleLineTrivia(line.Trim())));
        var parenDepth = 0;

        for (int i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                continue;
            }

            if (parenDepth != 0 || (ch != '[' && ch != ','))
                continue;

            if (!TryReadAttributeIdentifier(content, ref i, out var identifier))
                continue;

            var normalized = NormalizeAttributeIdentifier(identifier);
            if (normalized != null)
                names.Add(normalized);
        }

        return names;
    }

    private static string StripLeadingCSharpAttributeLists(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index >= line.Length || line[index] != '[')
            return line;

        var cursor = index;
        while (cursor < line.Length && line[cursor] == '[')
        {
            var depth = 0;
            var sawBracket = false;
            var breakOnDepthZero = false;
            while (cursor < line.Length)
            {
                // Skip over string and char literals so `[` or `]` inside them do
                // not affect the bracket-depth counter. Without this, a line like
                // `[Foo("[")] public string X { get; set; }` runs to end of line
                // with depth > 0 and the scan returns `string.Empty`, which makes
                // `LineContainsInlineAttributeAndDeclaration` falsely report that
                // the line has no trailing declaration. Closes #375.
                // 文字列・文字リテラル中の `[` `]` が depth 計算を乱さないようスキップする。
                // これを怠ると `[Foo("[")] public string X { get; set; }` のような行で
                // depth が戻らず空文字が返り、`LineContainsInlineAttributeAndDeclaration`
                // が「宣言なし」と誤判定して隣接する別シンボルへ属性が誤帰属する。#375 を修正。
                if (TrySkipCSharpStringOrCharLiteral(line, ref cursor))
                    continue;

                var ch = line[cursor++];
                if (ch == '[')
                {
                    depth++;
                    sawBracket = true;
                }
                else if (ch == ']')
                {
                    depth--;
                    if (depth == 0 && sawBracket)
                    {
                        breakOnDepthZero = true;
                        break;
                    }
                }
            }

            if (!breakOnDepthZero)
                return string.Empty;

            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;
        }

        return cursor < line.Length ? line[cursor..] : string.Empty;
    }

    /// <summary>
    /// If <paramref name="cursor"/> is at the start of a C# string or char literal
    /// (regular, verbatim, raw, or char), advance <paramref name="cursor"/> past the
    /// closing delimiter and return true. Multi-line strings are clamped to end-of-line
    /// since this helper operates on a single line. Returns false if not at a literal.
    /// C# の文字列・文字リテラル（通常・verbatim・raw・char）先頭にあれば、終端直後まで
    /// <paramref name="cursor"/> を進めて true を返す。単一行前提のため、未終端リテラルは
    /// 行末で打ち切る。リテラル先頭でなければ false を返す。
    /// </summary>
    private static bool TrySkipCSharpStringOrCharLiteral(string line, ref int cursor)
    {
        var start = cursor;
        if (start >= line.Length)
            return false;

        var ch = line[start];

        // Verbatim string: @"..." with "" as escape
        // verbatim 文字列: @"..." で "" が escape
        if (ch == '@' && start + 1 < line.Length && line[start + 1] == '"')
        {
            var i = start + 2;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                i++;
            }
            cursor = i;
            return true;
        }

        // Interpolated verbatim string: $@"..." or @$"..."
        // 補間 verbatim 文字列: $@"..." または @$"..."
        if ((ch == '$' && start + 2 < line.Length && line[start + 1] == '@' && line[start + 2] == '"')
            || (ch == '@' && start + 2 < line.Length && line[start + 1] == '$' && line[start + 2] == '"'))
        {
            var i = start + 3;
            var braceDepth = 0;
            while (i < line.Length)
            {
                if (braceDepth == 0 && line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                if (line[i] == '{')
                {
                    if (i + 1 < line.Length && line[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }
                    braceDepth++;
                }
                else if (line[i] == '}' && braceDepth > 0)
                {
                    braceDepth--;
                }
                i++;
            }
            cursor = i;
            return true;
        }

        if (ch == '"')
        {
            // Raw string: """..."""  (C# 11) — match the opening run length
            // raw 文字列: """..."""（C# 11）— 開始クォート数に合わせて終端を探す
            var runLength = 0;
            while (start + runLength < line.Length && line[start + runLength] == '"')
                runLength++;

            if (runLength >= 3)
            {
                var i = start + runLength;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '"')
                            closeRun++;
                        if (closeRun == runLength)
                        {
                            i += closeRun;
                            break;
                        }
                        i += closeRun;
                        continue;
                    }
                    i++;
                }
                cursor = i;
                return true;
            }

            // Regular string: "..." with \ as escape
            // 通常文字列: "..." で \ が escape
            var k = start + 1;
            while (k < line.Length)
            {
                if (line[k] == '\\' && k + 1 < line.Length)
                {
                    k += 2;
                    continue;
                }
                if (line[k] == '"')
                {
                    k++;
                    break;
                }
                k++;
            }
            cursor = k;
            return true;
        }

        // Interpolated raw string: $"""..."""  and multi-$ form $$"""..."""  (C# 11)
        // — N consecutive `$` means N consecutive `{`/`}` are required to open/close
        // interpolation; fewer are treated as literal.
        // 補間 raw 文字列: $"""..."""、および multi-$ 形式 $$"""..."""（C# 11）—
        // `$` の連続数 N に対し、補間を開閉するには `{` / `}` も N 個連続している必要がある。
        if (ch == '$')
        {
            var dollarCount = 0;
            while (start + dollarCount < line.Length && line[start + dollarCount] == '$')
                dollarCount++;

            var quoteStart = start + dollarCount;
            var rawRunLength = 0;
            while (quoteStart + rawRunLength < line.Length && line[quoteStart + rawRunLength] == '"')
                rawRunLength++;

            if (dollarCount >= 1 && rawRunLength >= 3)
            {
                var i = quoteStart + rawRunLength;
                var braceDepth = 0;
                while (i < line.Length)
                {
                    if (braceDepth == 0 && line[i] == '"')
                    {
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '"')
                            closeRun++;
                        if (closeRun == rawRunLength)
                        {
                            i += closeRun;
                            break;
                        }
                        i += closeRun;
                        continue;
                    }
                    if (line[i] == '{')
                    {
                        if (braceDepth == 0)
                        {
                            var openRun = 0;
                            while (i + openRun < line.Length && line[i + openRun] == '{')
                                openRun++;
                            if (openRun >= dollarCount)
                            {
                                braceDepth = 1;
                                i += dollarCount;
                                continue;
                            }
                            // Fewer than $ count — literal in raw interpolated.
                            // $ の連続数より少ない `{` は raw 補間では literal 扱い。
                            i += openRun;
                            continue;
                        }
                        braceDepth++;
                        i++;
                        continue;
                    }
                    if (line[i] == '}')
                    {
                        if (braceDepth == 0)
                        {
                            i++;
                            continue;
                        }
                        if (braceDepth > 1)
                        {
                            braceDepth--;
                            i++;
                            continue;
                        }
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '}')
                            closeRun++;
                        if (closeRun >= dollarCount)
                        {
                            braceDepth = 0;
                            i += dollarCount;
                            continue;
                        }
                        i += closeRun;
                        continue;
                    }
                    i++;
                }
                cursor = i;
                return true;
            }
        }

        // Interpolated string: $"..." — treat braces as skipped so quotes inside
        // `{...}` expressions don't prematurely terminate the string.
        // 補間文字列: $"..." — `{...}` 中のクォートで早期終端しないよう波括弧を追跡する。
        if (ch == '$' && start + 1 < line.Length && line[start + 1] == '"')
        {
            var i = start + 2;
            var braceDepth = 0;
            while (i < line.Length)
            {
                if (braceDepth == 0)
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i += 2;
                        continue;
                    }
                    if (line[i] == '"')
                    {
                        i++;
                        break;
                    }
                }
                if (line[i] == '{')
                {
                    if (i + 1 < line.Length && line[i + 1] == '{')
                    {
                        i += 2;
                        continue;
                    }
                    braceDepth++;
                }
                else if (line[i] == '}' && braceDepth > 0)
                {
                    if (i + 1 < line.Length && line[i + 1] == '}')
                    {
                        i += 2;
                        continue;
                    }
                    braceDepth--;
                }
                i++;
            }
            cursor = i;
            return true;
        }

        if (ch == '\'')
        {
            var k = start + 1;
            while (k < line.Length)
            {
                if (line[k] == '\\' && k + 1 < line.Length)
                {
                    k += 2;
                    continue;
                }
                if (line[k] == '\'')
                {
                    k++;
                    break;
                }
                k++;
            }
            cursor = k;
            return true;
        }

        return false;
    }

    private static bool TryReadAttributeIdentifier(string content, ref int index, out string? identifier)
    {
        identifier = null;
        var i = index + 1;
        while (i < content.Length && char.IsWhiteSpace(content[i]))
            i++;

        var start = i;
        if (!TryConsumeAttributeName(content, ref i))
            return false;
        if (i == start)
            return false;

        while (i < content.Length && char.IsWhiteSpace(content[i]))
            i++;

        // Skip attribute targets like `[property: JsonPropertyName]`.
        var leadingIdentifier = content[start..i].Trim();
        if (i < content.Length && content[i] == ':' && (i + 1 >= content.Length || content[i + 1] != ':') && AttributeTargetNames.Contains(leadingIdentifier))
        {
            i++;
            while (i < content.Length && char.IsWhiteSpace(content[i]))
                i++;
            start = i;
            if (!TryConsumeAttributeName(content, ref i))
                return false;
            if (i == start)
                return false;
        }

        identifier = content[start..i];
        index = i - 1;
        return true;
    }

    private static bool TryConsumeAttributeName(string content, ref int index)
    {
        var consumed = false;
        while (index < content.Length)
        {
            var segmentStart = index;
            while (index < content.Length && (char.IsLetterOrDigit(content[index]) || content[index] == '_'))
                index++;
            if (index == segmentStart)
                break;

            consumed = true;
            if (index + 1 < content.Length && content[index] == ':' && content[index + 1] == ':')
            {
                index += 2;
                continue;
            }

            if (index < content.Length && content[index] == '.')
            {
                index++;
                continue;
            }

            break;
        }

        return consumed;
    }

    private static string? NormalizeAttributeIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        var qualifierIndex = identifier.LastIndexOf("::", StringComparison.Ordinal);
        if (qualifierIndex >= 0)
            identifier = identifier[(qualifierIndex + 2)..];

        var lastDot = identifier.LastIndexOf('.');
        var simpleName = lastDot >= 0 ? identifier[(lastDot + 1)..] : identifier;
        if (simpleName.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
            simpleName = simpleName[..^"Attribute".Length];

        return simpleName.Length == 0 ? null : simpleName.ToLowerInvariant();
    }

    // Pure-trivia classifier used by ExtractNormalizedAttributeNames to skip
    // comment-only rows picked up by the block walker (pure line/block comments
    // and javadoc-style continuation rows). The lone `*/` closing row is
    // already covered by the `StartsWith('*')` check, so we deliberately do
    // NOT flag any line that merely contains `*/` mid-line — that used to
    // discard attribute rows with a trailing block comment
    // (`[JsonPropertyName("ok")] /* note */`) and strip reflection context
    // off the next property. Closes #409 follow-up.
    // ExtractNormalizedAttributeNames がブロック walker に拾われたコメント専用行
    // （純粋な行 / ブロックコメント、javadoc スタイルの継続行）を除外するための
    // 純 trivia 判定。`*/` だけの閉じ行は `StartsWith('*')` で既に拾えるので、
    // 途中に `*/` を含むだけの行はここでは trivia 扱いしない。以前はそれを trivia 扱いして
    // 末尾ブロックコメント付き属性行 `[JsonPropertyName("ok")] /* note */` を落とし、
    // 直下のプロパティから reflection コンテキストを失わせていた。#409 追加修正。
    private static bool BuildSingleLineTrivia(string trimmed)
    {
        return trimmed.Length == 0
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*');
    }

    private static bool LooksLikeAttributeBoundaryLine(string line)
    {
        return line.IndexOf('[') >= 0 || line.IndexOf(']') >= 0;
    }

    private static List<string> BuildUnusedIntentionalSurfaceTags(UnusedCandidateSymbol candidate, string kind)
    {
        var tags = new List<string>();
        if (candidate.IsReflectionOrConfigSuspect)
            AddUnusedSurfaceTag(tags, "reflection_or_config_suspect");
        if (IsMarkdownHeadingSymbol(candidate, kind))
            AddUnusedSurfaceTag(tags, "documentation_heading");
        if (IsGeneratedSurface(candidate))
            AddUnusedSurfaceTag(tags, "generated_surface");
        if (IsSourceGeneratedJsonContext(candidate))
        {
            AddUnusedSurfaceTag(tags, "serialization_contract");
            AddUnusedSurfaceTag(tags, "source_generated_json_context");
        }
        if (IsUnusedContractType(candidate, kind))
            AddUnusedSurfaceTag(tags, "serialization_contract");
        if (IsUnusedContractMember(candidate, kind))
            AddUnusedSurfaceTag(tags, "contract_member");
        if (IsConfigOrManifestSurface(candidate, kind))
            AddUnusedSurfaceTag(tags, "config_or_metadata_surface");
        if (IsTestHookName(candidate.Name))
            AddUnusedSurfaceTag(tags, "test_hook");
        if (IsExceptionMetadataProperty(candidate, kind))
            AddUnusedSurfaceTag(tags, "exception_metadata");
        if (IsConfigOrMetadataMember(candidate, kind))
            AddUnusedSurfaceTag(tags, "config_or_metadata_member");
        return tags;
    }

    private static void AddUnusedSurfaceTag(List<string> tags, string tag)
    {
        if (!tags.Contains(tag, StringComparer.Ordinal))
            tags.Add(tag);
    }

    private static bool IsMarkdownHeadingSymbol(UnusedCandidateSymbol candidate, string kind)
    {
        return (string.Equals(candidate.Lang, "markdown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Lang, "md", StringComparison.OrdinalIgnoreCase))
            && (kind.Contains("heading", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("header", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGeneratedSurface(UnusedCandidateSymbol candidate)
        => ContainsAny(candidate.Path, UnusedGeneratedPathMarkers);

    private static bool IsSourceGeneratedJsonContext(UnusedCandidateSymbol candidate)
    {
        return EndsWithAny(candidate.Name, ["JsonContext"])
            || ContainsAny(candidate.Signature, ["JsonSerializerContext", "JsonSerializable", "JsonSourceGenerationOptions"]);
    }

    private static bool IsUnusedContractType(UnusedCandidateSymbol candidate, string kind)
    {
        if (!IsTypeLikeUnusedKind(kind) || IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        return ContainsAny(candidate.Path, UnusedContractPathSegments)
            || IsRecordContractType(candidate)
            || ContainsAny(candidate.Signature, ["DataContract", "Serializable", "MessagePackObject", "ProtoContract"]);
    }

    private static bool IsUnusedContractMember(UnusedCandidateSymbol candidate, string kind)
    {
        if (!IsDataMemberUnusedKind(kind) || IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        return ContainsAny(candidate.Path, UnusedContractPathSegments)
            || ContainsAny(candidate.Signature, ["JsonProperty", "JsonInclude", "DataMember", "XmlElement", "XmlAttribute"]);
    }

    private static bool IsConfigOrManifestSurface(UnusedCandidateSymbol candidate, string kind)
    {
        if (IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        if (!IsTypeLikeUnusedKind(kind) && !IsDataMemberUnusedKind(kind) && !IsFunctionLikeUnusedKind(kind))
            return false;
        if (EndsWithAny(candidate.Name, ["Options", "Settings", "Configuration", "Config", "Manifest", "Schema"])
            || ContainsAny(candidate.Name, ["Configuration", "IOptions"])
            || ContainsAny(candidate.Signature, ["IConfiguration", "ConfigurationSection", "IOptions", "Options<"]))
        {
            return true;
        }

        return IsDataMemberUnusedKind(kind)
            && (ContainsAny(candidate.Path, ["/config/", "/configuration/", "/options/", "/settings/", "/manifest/", "/manifests/"])
                || ContainsAny(candidate.Signature, ["IConfiguration", "ConfigurationSection", "IOptions", "Options<"]));
    }

    private static bool IsTestHookName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && (name.EndsWith("ForTests", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ForTest", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TestOnly", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExceptionMetadataProperty(UnusedCandidateSymbol candidate, string kind)
    {
        return string.Equals(kind, "property", StringComparison.OrdinalIgnoreCase)
            && UnusedExceptionMetadataNames.Contains(candidate.Name)
            && (EndsWithAny(candidate.ContainerName, ["Exception"])
                || ContainsAny(candidate.ContainerQualifiedName, ["Exception"])
                || ContainsAny(candidate.Signature, ["Exception"]));
    }

    private static bool IsConfigOrMetadataMember(UnusedCandidateSymbol candidate, string kind)
    {
        if (!IsDataMemberUnusedKind(kind) || IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        if (!ContainsAny(candidate.Name, UnusedConfigMemberTerms))
            return false;

        var hasContext = ContainsAny(candidate.Path, UnusedContractPathSegments)
            || ContainsAny(candidate.Path, ["/config/", "/configuration/", "/options/", "/settings/", "/manifest/", "/manifests/"])
            || ContainsAny(candidate.Signature, ["JsonProperty", "JsonInclude", "DataMember", "Diagnostic", "Metadata", "IConfiguration", "IOptions", "Options<"]);
        return hasContext;
    }

    private static bool IsTypeLikeUnusedKind(string kind)
    {
        return kind is "class" or "struct" or "record" or "interface" or "enum" or "type";
    }

    private static bool IsDataMemberUnusedKind(string kind)
    {
        return kind is "property" or "field" or "constant" or "enum_member";
    }

    private static bool IsFunctionLikeUnusedKind(string kind)
    {
        return kind is "function" or "method";
    }

    private static bool IsRecordContractType(UnusedCandidateSymbol candidate)
    {
        return ContainsCSharpRecordKeyword(candidate.Signature)
            && EndsWithAny(candidate.Name, UnusedRecordContractSuffixes);
    }

    private static bool ContainsCSharpRecordKeyword(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var trimmed = signature.TrimStart();
        return trimmed.StartsWith("record ", StringComparison.Ordinal)
            || trimmed.StartsWith("record(", StringComparison.Ordinal)
            || signature.Contains(" record ", StringComparison.Ordinal)
            || signature.Contains(" record(", StringComparison.Ordinal);
    }

    private static bool EndsWithAny(string? value, IReadOnlyList<string> suffixes)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ContainsAny(string? value, IReadOnlyList<string> fragments)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var fragment in fragments)
        {
            if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static (string Bucket, string Confidence, string Reason) ClassifyUnusedSymbol(bool isPublicOrExported, bool isIntentionalSurfaceSuspect, string? visibility)
    {
        if (isIntentionalSurfaceSuspect)
        {
            return (
                UnusedBucketReflectionOrConfig,
                "low",
                "symbol with attribute-driven reflection surface, serialization, config, metadata, test-hook, generated, documentation, or compatibility surface and no indexed references");
        }

        if (isPublicOrExported)
        {
            return (
                UnusedBucketPublicOrExported,
                "low",
                "public/exported symbol with no indexed references");
        }

        if (IsPrivateLikeVisibility(visibility))
        {
            return (
                UnusedBucketLikelyPrivate,
                "medium",
                "private/file-local symbol with no indexed references after same-file text validation");
        }

        return (
            UnusedBucketMaybeNonPublic,
            "low",
            "non-public symbol with no indexed references");
    }

    private static List<string> BuildUnusedReasonTags(bool isPublicOrExported, bool isIntentionalSurfaceSuspect, string? visibility, IReadOnlyList<string> surfaceTags)
    {
        var tags = new List<string> { "no_indexed_references" };
        if (isIntentionalSurfaceSuspect)
        {
            tags.Add("intentional_surface_suspect");
            tags.Add("reflection_or_config_suspect");
        }
        foreach (var surfaceTag in surfaceTags)
            AddUnusedSurfaceTag(tags, surfaceTag);
        if (isPublicOrExported)
            tags.Add("public_or_exported");
        else if (IsPrivateLikeVisibility(visibility))
            tags.Add("private_or_file_local");
        else
            tags.Add("non_public");
        return tags;
    }

    private static UnusedContractDomainClassification ClassifyUnusedContractDomain(
        UnusedCandidateSymbol candidate,
        string kind,
        string bucket,
        IReadOnlyList<string> surfaceTags)
    {
        var tags = new List<string>();
        if (string.Equals(bucket, UnusedBucketReflectionOrConfig, StringComparison.Ordinal))
            AddUnusedSurfaceTag(tags, "intentional_surface_suspect");
        foreach (var surfaceTag in surfaceTags)
            AddUnusedSurfaceTag(tags, surfaceTag);

        if (IsPrivateLikeVisibility(candidate.Visibility))
            return CreateUnusedContractDomain(UnusedContractDomainPrivate, tags, "private_or_file_local");

        if (!candidate.IsPublicOrExported && surfaceTags.Count == 0)
            return CreateUnusedContractDomain(UnusedContractDomainNonPublic, tags, "nonpublic_or_protected");

        AddUnusedSurfaceTag(tags, candidate.IsPublicOrExported ? "public_or_exported" : "nonpublic_or_protected");

        if (HasUnusedSurfaceTag(surfaceTags, "documentation_heading") || IsMarkdownHeadingSymbol(candidate, kind))
            return CreateUnusedContractDomain(UnusedContractDomainDocumentation, tags, "documentation_heading");

        if (IsUnusedTestContractSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainTest, tags, "test_surface");

        if (HasUnusedSurfaceTag(surfaceTags, "generated_surface") || IsGeneratedSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainGenerated, tags, "generated_surface");

        if (HasUnusedSurfaceTag(surfaceTags, "exception_metadata") || IsExceptionDiagnosticSurface(candidate, kind))
            return CreateUnusedContractDomain(UnusedContractDomainExceptionDiagnostic, tags, "exception_metadata");

        if (IsFrameworkOverrideSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainFrameworkOverride, tags, "framework_override");

        if (IsMcpContractSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainMcp, tags, "mcp_tool_contract");

        if (IsLspContractSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainLsp, tags, "lsp_protocol_contract");

        if (IsCliContractSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainCli, tags, "cli_option_or_result");

        if (IsConfigurationContractSurface(candidate, kind, surfaceTags))
            return CreateUnusedContractDomain(UnusedContractDomainConfig, tags, "configuration_or_metadata_contract");

        if (IsJsonContractSurface(candidate, surfaceTags))
            return CreateUnusedContractDomain(UnusedContractDomainJson, tags, "json_output_or_input_contract");

        if (HasUnusedSurfaceTag(surfaceTags, "reflection_or_config_suspect"))
            return CreateUnusedContractDomain(UnusedContractDomainSerialization, tags, "reflection_or_serialization_contract");

        return candidate.IsPublicOrExported
            ? CreateUnusedContractDomain(UnusedContractDomainPublicApi, tags, "public_api_surface")
            : CreateUnusedContractDomain(UnusedContractDomainNonPublic, tags, "nonpublic_or_protected");
    }

    private static UnusedContractDomainClassification CreateUnusedContractDomain(string domain, List<string> tags, params string[] domainTags)
    {
        AddUnusedSurfaceTag(tags, domain);
        foreach (var tag in domainTags)
            AddUnusedSurfaceTag(tags, tag);
        return new UnusedContractDomainClassification(domain, tags);
    }

    private static bool HasUnusedSurfaceTag(IReadOnlyList<string> tags, string tag)
        => tags.Contains(tag, StringComparer.Ordinal);

    private static bool IsUnusedTestContractSurface(UnusedCandidateSymbol candidate)
    {
        return IsUnusedTestPath(candidate.Path)
            || EndsWithAny(candidate.ContainerName, ["Test", "Tests", "Fixture"])
            || EndsWithAny(candidate.ContainerQualifiedName, ["Test", "Tests", "Fixture"])
            || IsTestHookName(candidate.Name);
    }

    private static bool IsUnusedTestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("test/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".tests/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExceptionDiagnosticSurface(UnusedCandidateSymbol candidate, string kind)
    {
        return IsExceptionMetadataProperty(candidate, kind)
            || (IsDataMemberUnusedKind(kind)
                && (EndsWithAny(candidate.ContainerName, ["Exception"])
                    || EndsWithAny(candidate.ContainerQualifiedName, ["Exception"])))
            || (IsTypeLikeUnusedKind(kind) && EndsWithAny(candidate.Name, ["Exception"]));
    }

    private static bool IsFrameworkOverrideSurface(UnusedCandidateSymbol candidate)
    {
        if (ContainsAny(candidate.Signature, [" override ", " override\t", " override\r", " override\n"]))
            return true;

        if (!UnusedFrameworkOverrideMemberNames.Contains(candidate.Name))
            return false;

        return EndsWithAny(candidate.ContainerName, ["Stream", "TextReader", "TextWriter"])
            || EndsWithAny(candidate.ContainerQualifiedName, ["Stream", "TextReader", "TextWriter"])
            || ContainsAny(candidate.Signature, ["Stream", "TextReader", "TextWriter"]);
    }

    private static bool IsMcpContractSurface(UnusedCandidateSymbol candidate)
    {
        return ContainsAny(candidate.Path, UnusedMcpPathMarkers)
            || ContainsAny(candidate.ContainerName, ["Mcp", "JsonRpc"])
            || ContainsAny(candidate.ContainerQualifiedName, ["Mcp", "JsonRpc"])
            || ContainsAny(candidate.Signature, ["Mcp", "JsonRpc"]);
    }

    private static bool IsLspContractSurface(UnusedCandidateSymbol candidate)
    {
        return ContainsAny(candidate.Path, UnusedLspPathMarkers)
            || ContainsAny(candidate.ContainerName, ["Lsp", "LanguageServer"])
            || ContainsAny(candidate.ContainerQualifiedName, ["Lsp", "LanguageServer"])
            || ContainsAny(candidate.Signature, ["Lsp", "LanguageServer"]);
    }

    private static bool IsCliContractSurface(UnusedCandidateSymbol candidate)
    {
        var hasCliContext = ContainsAny(candidate.Path, UnusedCliPathMarkers)
            || ContainsAny(candidate.ContainerQualifiedName, ["Cli", "CommandLine"])
            || ContainsAny(candidate.Signature, ["CommandLine", "System.CommandLine", "Option<", "Argument<"]);
        if (hasCliContext)
            return true;

        return EndsWithAny(candidate.Name, ["Command", "Flag", "Flags", "Usage", "ExitCode", "ErrorCode"])
            || EndsWithAny(candidate.ContainerName, ["Command", "Flag", "Flags", "Usage", "ExitCode", "ErrorCode"]);
    }

    private static bool IsConfigurationContractSurface(UnusedCandidateSymbol candidate, string kind, IReadOnlyList<string> surfaceTags)
    {
        return HasUnusedSurfaceTag(surfaceTags, "config_or_metadata_surface")
            || HasUnusedSurfaceTag(surfaceTags, "config_or_metadata_member")
            || IsConfigOrManifestSurface(candidate, kind)
            || IsConfigOrMetadataMember(candidate, kind);
    }

    private static bool IsJsonContractSurface(UnusedCandidateSymbol candidate, IReadOnlyList<string> surfaceTags)
    {
        return HasUnusedSurfaceTag(surfaceTags, "serialization_contract")
            || HasUnusedSurfaceTag(surfaceTags, "contract_member")
            || HasUnusedSurfaceTag(surfaceTags, "source_generated_json_context")
            || ContainsAny(candidate.Path, UnusedContractPathSegments)
            || EndsWithAny(candidate.Name, UnusedRecordContractSuffixes)
            || EndsWithAny(candidate.ContainerName, UnusedRecordContractSuffixes)
            || ContainsAny(candidate.Name, UnusedJsonContractTerms)
            || ContainsAny(candidate.ContainerName, UnusedJsonContractTerms)
            || ContainsAny(candidate.Signature, ["JsonProperty", "JsonInclude", "JsonSerializerContext", "DataContract", "DataMember", "XmlElement", "XmlAttribute", "YamlMember", "MessagePackObject", "ProtoContract"]);
    }

    private static bool IsPrivateLikeVisibility(string? visibility)
    {
        return string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, "fileprivate", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] OrderedUnusedBuckets =
    [
        UnusedBucketLikelyPrivate,
        UnusedBucketMaybeNonPublic,
        UnusedBucketPublicOrExported,
        UnusedBucketReflectionOrConfig,
    ];
}
