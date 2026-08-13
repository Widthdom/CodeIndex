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

    private static readonly string[] UnusedContractPathSegments = ["/contracts/", "/contract/", "/dtos/", "/dto/", "/models/", "/model/", "/schemas/", "/schema/"];
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

    private static readonly string[] UnusedJsonContextSuffixes = ["JsonContext"];
    private static readonly string[] UnusedJsonContextSignatureTerms = ["JsonSerializerContext", "JsonSerializable", "JsonSourceGenerationOptions"];
    private static readonly string[] UnusedContractTypeSignatureTerms = ["DataContract", "Serializable", "MessagePackObject", "ProtoContract"];
    private static readonly string[] UnusedContractMemberSignatureTerms = ["JsonProperty", "JsonInclude", "DataMember", "XmlElement", "XmlAttribute"];
    private static readonly string[] UnusedConfigNameSuffixes = ["Options", "Settings", "Configuration", "Config", "Manifest", "Schema"];
    private static readonly string[] UnusedConfigNameTerms = ["Configuration", "IOptions"];
    private static readonly string[] UnusedConfigSignatureTerms = ["IConfiguration", "ConfigurationSection", "IOptions", "Options<"];
    private static readonly string[] UnusedConfigPathSegments = ["/config/", "/configuration/", "/options/", "/settings/", "/manifest/", "/manifests/"];
    private static readonly string[] UnusedMetadataSignatureTerms = ["JsonProperty", "JsonInclude", "DataMember", "Diagnostic", "Metadata", "IConfiguration", "IOptions", "Options<"];
    private static readonly string[] UnusedExceptionSuffixes = ["Exception"];
    private static readonly string[] UnusedTestContainerSuffixes = ["Test", "Tests", "Fixture"];
    private static readonly string[] UnusedFrameworkOverrideSignatureTerms = [" override ", " override\t", " override\r", " override\n"];
    private static readonly string[] UnusedFrameworkContainerSuffixes = ["Stream", "TextReader", "TextWriter"];
    private static readonly string[] UnusedMcpContractTerms = ["Mcp", "JsonRpc"];
    private static readonly string[] UnusedLspContractTerms = ["Lsp", "LanguageServer"];
    private static readonly string[] UnusedCliContractTerms = ["Cli", "CommandLine"];
    private static readonly string[] UnusedCliSignatureTerms = ["CommandLine", "System.CommandLine", "Option<", "Argument<"];
    private static readonly string[] UnusedCliContractSuffixes = ["Command", "Flag", "Flags", "Usage", "ExitCode", "ErrorCode"];
    private static readonly string[] UnusedJsonSignatureTerms =
    [
        "JsonProperty",
        "JsonInclude",
        "JsonSerializerContext",
        "DataContract",
        "DataMember",
        "XmlElement",
        "XmlAttribute",
        "YamlMember",
        "MessagePackObject",
        "ProtoContract",
    ];

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

    private static readonly string[] OrderedUnusedBuckets =
    [
        UnusedBucketLikelyPrivate,
        UnusedBucketMaybeNonPublic,
        UnusedBucketPublicOrExported,
        UnusedBucketReflectionOrConfig,
    ];

    [Flags]
    private enum UnusedSurfaceFlags : ushort
    {
        None = 0,
        ReflectionOrConfigSuspect = 1 << 0,
        DocumentationHeading = 1 << 1,
        MarkdownFenceLanguageMarker = 1 << 2,
        GeneratedSurface = 1 << 3,
        SerializationContract = 1 << 4,
        SourceGeneratedJsonContext = 1 << 5,
        ContractMember = 1 << 6,
        ConfigOrMetadataSurface = 1 << 7,
        TestHook = 1 << 8,
        ExceptionMetadata = 1 << 9,
        ConfigOrMetadataMember = 1 << 10,
    }

    private readonly record struct UnusedSurfaceAnalysis(UnusedSurfaceFlags Flags, List<string> Tags);
    private readonly record struct UnusedContractDomainClassification(string Domain, List<string> Tags);
    private UnusedSymbolResult CreateUnusedSymbolResult(UnusedCandidateSymbol candidate)
    {
        var kind = NormalizeUnusedSymbolKind(candidate);
        var surface = AnalyzeUnusedSurfaces(candidate, kind);
        var classification = ClassifyUnusedSymbol(
            candidate.IsPublicOrExported,
            surface.Tags.Count > 0,
            candidate.Visibility);
        var reasonTags = BuildUnusedReasonTags(candidate, surface);
        var contractDomain = ClassifyUnusedContractDomain(
            candidate,
            kind,
            classification.Bucket,
            surface);
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

    private UnusedSurfaceAnalysis AnalyzeUnusedSurfaces(
        UnusedCandidateSymbol candidate,
        string kind)
    {
        var flags = UnusedSurfaceFlags.None;
        var tags = new List<string>();
        if (candidate.IsReflectionOrConfigSuspect)
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.ReflectionOrConfigSuspect, tags, "reflection_or_config_suspect");
        if (IsMarkdownHeadingSymbol(candidate, kind))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.DocumentationHeading, tags, "documentation_heading");
        if (IsMarkdownFenceSymbol(candidate, kind))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.MarkdownFenceLanguageMarker, tags, "markdown_fence_language_marker");
        if (IsGeneratedSurface(candidate))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.GeneratedSurface, tags, "generated_surface");
        AddUnusedSerializationSurfaces(candidate, kind, ref flags, tags);
        if (IsConfigOrManifestSurface(candidate, kind))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.ConfigOrMetadataSurface, tags, "config_or_metadata_surface");
        if (IsTestHookName(candidate.Name))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.TestHook, tags, "test_hook");
        if (IsExceptionMetadataProperty(candidate, kind))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.ExceptionMetadata, tags, "exception_metadata");
        if (IsConfigOrMetadataMember(candidate, kind))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.ConfigOrMetadataMember, tags, "config_or_metadata_member");
        if ((flags & UnusedSurfaceFlags.ReflectionOrConfigSuspect) == 0
            && candidate.IsPublicOrExported
            && HasReflectionAttributeContext(kind, candidate.Path, candidate.StartLine))
        {
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.ReflectionOrConfigSuspect, tags, "reflection_or_config_suspect");
        }
        return new UnusedSurfaceAnalysis(flags, tags);
    }

    private static void AddUnusedSerializationSurfaces(
        UnusedCandidateSymbol candidate,
        string kind,
        ref UnusedSurfaceFlags flags,
        List<string> tags)
    {
        if (IsSourceGeneratedJsonContext(candidate))
        {
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.SerializationContract, tags, "serialization_contract");
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.SourceGeneratedJsonContext, tags, "source_generated_json_context");
        }
        if (IsUnusedContractType(candidate, kind))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.SerializationContract, tags, "serialization_contract");
        if (IsUnusedContractMember(candidate, kind))
            AddUnusedSurface(ref flags, UnusedSurfaceFlags.ContractMember, tags, "contract_member");
    }

    private static void AddUnusedSurface(
        ref UnusedSurfaceFlags flags,
        UnusedSurfaceFlags flag,
        List<string> tags,
        string tag)
    {
        flags |= flag;
        AddUnusedSurfaceTag(tags, tag);
    }

    private static void AddUnusedSurfaceTag(List<string> tags, string tag)
    {
        if (!tags.Contains(tag, StringComparer.Ordinal))
            tags.Add(tag);
    }

    private static (string Bucket, string Confidence, string Reason) ClassifyUnusedSymbol(
        bool isPublicOrExported,
        bool isIntentionalSurfaceSuspect,
        string? visibility)
    {
        if (isIntentionalSurfaceSuspect)
        {
            return (
                UnusedBucketReflectionOrConfig,
                "low",
                "symbol with attribute-driven reflection surface, serialization, config, metadata, test-hook, generated, documentation, or compatibility surface and no indexed references");
        }
        if (isPublicOrExported)
            return (UnusedBucketPublicOrExported, "low", "public/exported symbol with no indexed references");
        if (IsPrivateLikeVisibility(visibility))
        {
            return (
                UnusedBucketLikelyPrivate,
                "medium",
                "private/file-local symbol with no indexed references after same-file text validation");
        }
        return (UnusedBucketMaybeNonPublic, "low", "non-public symbol with no indexed references");
    }

    private static List<string> BuildUnusedReasonTags(
        UnusedCandidateSymbol candidate,
        UnusedSurfaceAnalysis surface)
    {
        var tags = new List<string> { "no_indexed_references" };
        if (surface.Tags.Count > 0)
        {
            tags.Add("intentional_surface_suspect");
            tags.Add("reflection_or_config_suspect");
        }
        foreach (var surfaceTag in surface.Tags)
            AddUnusedSurfaceTag(tags, surfaceTag);
        if (candidate.IsPublicOrExported)
            tags.Add("public_or_exported");
        else if (IsPrivateLikeVisibility(candidate.Visibility))
            tags.Add("private_or_file_local");
        else
            tags.Add("non_public");
        return tags;
    }

    private static UnusedContractDomainClassification ClassifyUnusedContractDomain(
        UnusedCandidateSymbol candidate,
        string kind,
        string bucket,
        UnusedSurfaceAnalysis surface)
    {
        var tags = new List<string>();
        if (string.Equals(bucket, UnusedBucketReflectionOrConfig, StringComparison.Ordinal))
            AddUnusedSurfaceTag(tags, "intentional_surface_suspect");
        foreach (var surfaceTag in surface.Tags)
            AddUnusedSurfaceTag(tags, surfaceTag);

        if (IsPrivateLikeVisibility(candidate.Visibility))
            return CreateUnusedContractDomain(UnusedContractDomainPrivate, tags, "private_or_file_local");
        if (!candidate.IsPublicOrExported && surface.Tags.Count == 0)
            return CreateUnusedContractDomain(UnusedContractDomainNonPublic, tags, "nonpublic_or_protected");

        AddUnusedSurfaceTag(tags, candidate.IsPublicOrExported ? "public_or_exported" : "nonpublic_or_protected");
        return ClassifyExposedUnusedContractDomain(candidate, kind, surface, tags);
    }

    private static UnusedContractDomainClassification ClassifyExposedUnusedContractDomain(
        UnusedCandidateSymbol candidate,
        string kind,
        UnusedSurfaceAnalysis surface,
        List<string> tags)
    {
        if (HasUnusedSurface(surface, UnusedSurfaceFlags.DocumentationHeading))
            return CreateUnusedContractDomain(UnusedContractDomainDocumentation, tags, "documentation_heading");
        if (HasUnusedSurface(surface, UnusedSurfaceFlags.MarkdownFenceLanguageMarker))
            return CreateUnusedContractDomain(UnusedContractDomainDocumentation, tags, "markdown_fence_language_marker");
        if (IsUnusedTestContractSurface(candidate, surface))
            return CreateUnusedContractDomain(UnusedContractDomainTest, tags, "test_surface");
        if (HasUnusedSurface(surface, UnusedSurfaceFlags.GeneratedSurface))
            return CreateUnusedContractDomain(UnusedContractDomainGenerated, tags, "generated_surface");
        if (IsExceptionDiagnosticSurface(candidate, kind, surface))
            return CreateUnusedContractDomain(UnusedContractDomainExceptionDiagnostic, tags, "exception_metadata");
        if (IsFrameworkOverrideSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainFrameworkOverride, tags, "framework_override");
        if (IsMcpContractSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainMcp, tags, "mcp_tool_contract");
        if (IsLspContractSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainLsp, tags, "lsp_protocol_contract");
        if (IsCliContractSurface(candidate))
            return CreateUnusedContractDomain(UnusedContractDomainCli, tags, "cli_option_or_result");
        if (IsConfigurationContractSurface(surface))
            return CreateUnusedContractDomain(UnusedContractDomainConfig, tags, "configuration_or_metadata_contract");
        if (IsJsonContractSurface(candidate, surface))
            return CreateUnusedContractDomain(UnusedContractDomainJson, tags, "json_output_or_input_contract");
        if (HasUnusedSurface(surface, UnusedSurfaceFlags.ReflectionOrConfigSuspect))
            return CreateUnusedContractDomain(UnusedContractDomainSerialization, tags, "reflection_or_serialization_contract");
        return candidate.IsPublicOrExported
            ? CreateUnusedContractDomain(UnusedContractDomainPublicApi, tags, "public_api_surface")
            : CreateUnusedContractDomain(UnusedContractDomainNonPublic, tags, "nonpublic_or_protected");
    }

    private static UnusedContractDomainClassification CreateUnusedContractDomain(
        string domain,
        List<string> tags,
        string domainTag)
    {
        AddUnusedSurfaceTag(tags, domain);
        AddUnusedSurfaceTag(tags, domainTag);
        return new UnusedContractDomainClassification(domain, tags);
    }

    private static bool HasUnusedSurface(UnusedSurfaceAnalysis surface, UnusedSurfaceFlags flag)
        => (surface.Flags & flag) != 0;

    private static bool IsMarkdownHeadingSymbol(UnusedCandidateSymbol candidate, string kind)
    {
        return (string.Equals(candidate.Lang, "markdown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Lang, "md", StringComparison.OrdinalIgnoreCase))
            && (kind.Contains("heading", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("header", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMarkdownFenceSymbol(UnusedCandidateSymbol candidate, string kind)
    {
        if ((!string.Equals(candidate.Lang, "markdown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.Lang, "md", StringComparison.OrdinalIgnoreCase))
            || !string.Equals(kind, "code", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var signature = candidate.Signature?.TrimStart();
        return signature?.StartsWith("```", StringComparison.Ordinal) == true
            || signature?.StartsWith("~~~", StringComparison.Ordinal) == true;
    }

    private static bool IsGeneratedSurface(UnusedCandidateSymbol candidate)
        => ContainsAny(candidate.Path, UnusedGeneratedPathMarkers);

    private static bool IsSourceGeneratedJsonContext(UnusedCandidateSymbol candidate)
        => EndsWithAny(candidate.Name, UnusedJsonContextSuffixes)
            || ContainsAny(candidate.Signature, UnusedJsonContextSignatureTerms);

    private static bool IsUnusedContractType(UnusedCandidateSymbol candidate, string kind)
    {
        if (!IsTypeLikeUnusedKind(kind) || IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        return ContainsAny(candidate.Path, UnusedContractPathSegments)
            || IsRecordContractType(candidate)
            || ContainsAny(candidate.Signature, UnusedContractTypeSignatureTerms);
    }

    private static bool IsUnusedContractMember(UnusedCandidateSymbol candidate, string kind)
    {
        if (!IsDataMemberUnusedKind(kind) || IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        return ContainsAny(candidate.Path, UnusedContractPathSegments)
            || ContainsAny(candidate.Signature, UnusedContractMemberSignatureTerms);
    }

    private static bool IsConfigOrManifestSurface(UnusedCandidateSymbol candidate, string kind)
    {
        if (IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        if (!IsTypeLikeUnusedKind(kind) && !IsDataMemberUnusedKind(kind) && !IsFunctionLikeUnusedKind(kind))
            return false;
        if (EndsWithAny(candidate.Name, UnusedConfigNameSuffixes)
            || ContainsAny(candidate.Name, UnusedConfigNameTerms)
            || ContainsAny(candidate.Signature, UnusedConfigSignatureTerms))
        {
            return true;
        }
        return IsDataMemberUnusedKind(kind)
            && (ContainsAny(candidate.Path, UnusedConfigPathSegments)
                || ContainsAny(candidate.Signature, UnusedConfigSignatureTerms));
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
            && (EndsWithAny(candidate.ContainerName, UnusedExceptionSuffixes)
                || ContainsAny(candidate.ContainerQualifiedName, UnusedExceptionSuffixes)
                || ContainsAny(candidate.Signature, UnusedExceptionSuffixes));
    }

    private static bool IsConfigOrMetadataMember(UnusedCandidateSymbol candidate, string kind)
    {
        if (!IsDataMemberUnusedKind(kind) || IsPrivateLikeVisibility(candidate.Visibility))
            return false;
        if (!ContainsAny(candidate.Name, UnusedConfigMemberTerms))
            return false;
        return ContainsAny(candidate.Path, UnusedContractPathSegments)
            || ContainsAny(candidate.Path, UnusedConfigPathSegments)
            || ContainsAny(candidate.Signature, UnusedMetadataSignatureTerms);
    }

    private static bool IsTypeLikeUnusedKind(string kind)
        => kind is "class" or "struct" or "record" or "interface" or "enum" or "type";

    private static bool IsDataMemberUnusedKind(string kind)
        => kind is "property" or "field" or "constant" or "enum_member";

    private static bool IsFunctionLikeUnusedKind(string kind)
        => kind is "function" or "method";

    private static bool IsRecordContractType(UnusedCandidateSymbol candidate)
        => ContainsCSharpRecordKeyword(candidate.Signature)
            && EndsWithAny(candidate.Name, UnusedRecordContractSuffixes);

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

    private static bool IsUnusedTestContractSurface(
        UnusedCandidateSymbol candidate,
        UnusedSurfaceAnalysis surface)
    {
        return IsUnusedTestPath(candidate.Path)
            || EndsWithAny(candidate.ContainerName, UnusedTestContainerSuffixes)
            || EndsWithAny(candidate.ContainerQualifiedName, UnusedTestContainerSuffixes)
            || HasUnusedSurface(surface, UnusedSurfaceFlags.TestHook);
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

    private static bool IsExceptionDiagnosticSurface(
        UnusedCandidateSymbol candidate,
        string kind,
        UnusedSurfaceAnalysis surface)
    {
        return HasUnusedSurface(surface, UnusedSurfaceFlags.ExceptionMetadata)
            || (IsDataMemberUnusedKind(kind)
                && (EndsWithAny(candidate.ContainerName, UnusedExceptionSuffixes)
                    || EndsWithAny(candidate.ContainerQualifiedName, UnusedExceptionSuffixes)))
            || (IsTypeLikeUnusedKind(kind) && EndsWithAny(candidate.Name, UnusedExceptionSuffixes));
    }

    private static bool IsFrameworkOverrideSurface(UnusedCandidateSymbol candidate)
    {
        if (ContainsAny(candidate.Signature, UnusedFrameworkOverrideSignatureTerms))
            return true;
        if (!UnusedFrameworkOverrideMemberNames.Contains(candidate.Name))
            return false;
        return EndsWithAny(candidate.ContainerName, UnusedFrameworkContainerSuffixes)
            || EndsWithAny(candidate.ContainerQualifiedName, UnusedFrameworkContainerSuffixes)
            || ContainsAny(candidate.Signature, UnusedFrameworkContainerSuffixes);
    }

    private static bool IsMcpContractSurface(UnusedCandidateSymbol candidate)
        => ContainsAny(candidate.Path, UnusedMcpPathMarkers)
            || ContainsAny(candidate.ContainerName, UnusedMcpContractTerms)
            || ContainsAny(candidate.ContainerQualifiedName, UnusedMcpContractTerms)
            || ContainsAny(candidate.Signature, UnusedMcpContractTerms);

    private static bool IsLspContractSurface(UnusedCandidateSymbol candidate)
        => ContainsAny(candidate.Path, UnusedLspPathMarkers)
            || ContainsAny(candidate.ContainerName, UnusedLspContractTerms)
            || ContainsAny(candidate.ContainerQualifiedName, UnusedLspContractTerms)
            || ContainsAny(candidate.Signature, UnusedLspContractTerms);

    private static bool IsCliContractSurface(UnusedCandidateSymbol candidate)
    {
        var hasCliContext = ContainsAny(candidate.Path, UnusedCliPathMarkers)
            || ContainsAny(candidate.ContainerQualifiedName, UnusedCliContractTerms)
            || ContainsAny(candidate.Signature, UnusedCliSignatureTerms);
        if (hasCliContext)
            return true;
        return EndsWithAny(candidate.Name, UnusedCliContractSuffixes)
            || EndsWithAny(candidate.ContainerName, UnusedCliContractSuffixes);
    }

    private static bool IsConfigurationContractSurface(UnusedSurfaceAnalysis surface)
        => HasUnusedSurface(surface, UnusedSurfaceFlags.ConfigOrMetadataSurface)
            || HasUnusedSurface(surface, UnusedSurfaceFlags.ConfigOrMetadataMember);

    private static bool IsJsonContractSurface(
        UnusedCandidateSymbol candidate,
        UnusedSurfaceAnalysis surface)
    {
        return HasUnusedSurface(surface, UnusedSurfaceFlags.SerializationContract)
            || HasUnusedSurface(surface, UnusedSurfaceFlags.ContractMember)
            || HasUnusedSurface(surface, UnusedSurfaceFlags.SourceGeneratedJsonContext)
            || ContainsAny(candidate.Path, UnusedContractPathSegments)
            || EndsWithAny(candidate.Name, UnusedRecordContractSuffixes)
            || EndsWithAny(candidate.ContainerName, UnusedRecordContractSuffixes)
            || ContainsAny(candidate.Name, UnusedJsonContractTerms)
            || ContainsAny(candidate.ContainerName, UnusedJsonContractTerms)
            || ContainsAny(candidate.Signature, UnusedJsonSignatureTerms);
    }

    private static bool IsPrivateLikeVisibility(string? visibility)
        => string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, "fileprivate", StringComparison.OrdinalIgnoreCase);
}
