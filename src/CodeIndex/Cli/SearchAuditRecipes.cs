using CodeIndex;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeIndex.Cli;

internal static class SearchAuditRecipes
{
    internal const string DefaultAuditScope = "source";
    internal const string AllAuditScope = "all";
    internal const string DefaultQuerySeverity = "medium";
    internal const string RecipePathsEnvironmentVariable = "CDIDX_SEARCH_RECIPE_PATHS";
    private const string BoundedRegexAliasUsing = "using Regex = CodeIndex.Indexer.BoundedRegex";
    private const string BoundedRegexPath = "src/CodeIndex/Indexer/BoundedRegex.cs";
    private const string RegexRegistryPath = "src/CodeIndex/Indexer/RegexRegistry.cs";
    private const int MaxRecipeSourceFiles = 8;
    internal const int MaxRecipeSourceBytes = 128 * 1024;
    private const int MaxExternalRecipesPerFile = 32;
    private const int MaxExternalQueriesPerRecipe = 32;
    private const int MaxExternalNameLength = 80;
    private const int MaxExternalDescriptionLength = 512;
    private const int MaxExternalFalsePositiveGuidanceLength = 512;
    private const int MaxExternalLabelCount = 16;
    private const int MaxExternalLabelLength = 64;
    private const int MaxExternalPathPatternCount = 32;
    private const int MaxExternalPathPatternLength = 256;
    private const int MaxRecipeDiagnosticCount = 64;
    private const int MaxRecipeDiagnosticLength = 512;
    private static readonly string[] SupportedQuerySeverities = ["info", "low", "medium", "high", "critical"];
    private static readonly string[] DefaultSourcePathPatternsValue = ["src/**"];
    private static readonly string[] DefaultSourceExcludePathsValue =
    [
        "src/CodeIndex/Cli/SearchAuditRecipes.cs",
        "tests/**",
        "docs/**",
        "CHANGELOG.md",
        "changelog.d/**",
        "README.md",
        "USER_GUIDE.md",
        "DEVELOPER_GUIDE.md",
        "TESTING_GUIDE.md",
        "AGENT_GUIDE.md",
        ".agent_harness/**",
        ".claude/**",
        ".codex/**",
        ".github/**"
    ];
    private static readonly string[] DefaultExecutableExcludeOriginsValue =
        [SearchMatchClassifier.HelpText, SearchMatchClassifier.SchemaDescription];
    private static readonly SearchRecipeBroadCatchTaxonomyJsonResult BroadExceptionCatchTaxonomy = new(
        [
            new(
                "top_level_normalization",
                "CLI, MCP, LSP, or command-entry catch blocks that intentionally convert unexpected failures into stable user-facing errors.",
                "Normalize to a bounded, sanitized command error, JSON error, or protocol error without echoing raw paths or secrets."),
            new(
                "cleanup_best_effort",
                "Best-effort cleanup, deletion, rotation, or rollback paths where failure must not hide the original operation result.",
                "Emit a bounded warning only when users can act on it; otherwise keep the failure private and preserve the primary result."),
            new(
                "probe_fallback",
                "Capability probes for filesystem, platform, SDK, or environment behavior where failure selects a conservative fallback.",
                "Return a documented fallback value and, when surfaced, use a stable category rather than raw exception text."),
            new(
                "diagnostic_sanitization",
                "Diagnostic formatting or redaction paths whose job is to prevent sensitive exception data from escaping.",
                "Prefer stable categories and scrubbed messages; never let sanitizer failures re-expose the original raw diagnostic."),
            new(
                "worker_process_boundary",
                "Worker-process startup, command, and IPC boundaries where broad catches isolate a subprocess or protocol request.",
                "Translate failures into worker/protocol diagnostics with bounded payloads and clear recovery hints."),
            new(
                "unexpected_bug",
                "Non-boundary broad catches where recovery is not intentional or where the catch swallows actionable defects.",
                "Narrow to expected exception types, rethrow, or add an explicit stable diagnostic before suppressing the failure.")
        ],
        [
            new(
                "stable_sanitized_diagnostic",
                "The catch reports a stable category or known error code with redacted human text."),
            new(
                "bounded_best_effort_warning",
                "The catch preserves best-effort behavior while bounding any warning path and avoiding raw exception echo."),
            new(
                "documented_fallback",
                "The catch selects a documented conservative fallback without implying the operation succeeded."),
            new(
                "private_suppression",
                "The catch intentionally suppresses a secondary failure that would be noisier than useful to users."),
            new(
                "narrow_or_rethrow_required",
                "The catch is not a real boundary and should be narrowed, rethrown, or converted to stable diagnostics.")
        ],
        "Classify each broad catch by boundary first. Treat top-level and worker boundaries as intentional only when they normalize to stable sanitized diagnostics; cleanup and probe catches are acceptable only when best-effort behavior is documented and bounded; otherwise narrow the catch or surface a stable diagnostic.");
    private static readonly SearchRecipeStringComparisonTaxonomyJsonResult StringComparisonSemanticsTaxonomy = new(
        [
            new(
                "path_filesystem",
                "Filesystem path equality, prefix, dictionary, and sort decisions whose correctness depends on path normalization and filesystem case sensitivity.",
                "Require evidence from the indexed filesystem case-sensitivity signal or a centralized path comparer before accepting case-insensitive ordinal behavior."),
            new(
                "protocol_tokens",
                "Protocol and file-format tokens such as URI schemes, HTTP headers, MIME types, JSON member names, and other machine-specified ASCII domains.",
                "Ordinal or ordinal-ignore-case comparisons are usually expected when the protocol defines byte, ASCII, or invariant token semantics."),
            new(
                "cli_options",
                "CLI option names, command aliases, environment-variable names, labels, and other command/control tokens.",
                "Case-insensitive ordinal handling can be intentional for command ergonomics; keep these hits separate from path or human-text comparisons."),
            new(
                "environment_names",
                "Environment variable names and process environment inventory keys that are specified as stable machine tokens.",
                "Environment names should use stable ordinal semantics and should not inherit filesystem or human-text comparison rules."),
            new(
                "symbol_names",
                "Extracted symbol names, language identifiers, canonical names, and graph lookup keys.",
                "Symbol-name exactness uses the documented extracted-name contract; keep it separate from raw source text, paths, and display text."),
            new(
                "stable_identifiers",
                "Persisted keys, cache keys, database identifiers, symbol names, and index terms that need stable process- and culture-independent lookup semantics.",
                "Use ordinal comparers consistently with the persistence format and avoid culture-sensitive transforms unless the stored contract says otherwise."),
            new(
                "persisted_db_keys",
                "SQLite metadata keys, JSON field names, and schema identifiers persisted across cdidx versions.",
                "Persisted keys need stable ordinal semantics and migration compatibility rather than user-locale or path comparison rules."),
            new(
                "human_text",
                "User-facing text, localized messages, display names, search phrases, and documentation prose whose meaning can be culture-sensitive.",
                "Prefer explicit culture-aware comparison, casing, formatting, or parsing for human text; invariant or ordinal behavior needs a machine-token justification."),
            new(
                "docs_help_text",
                "Documentation, help text, prompts, and explanatory strings that users read rather than machines parse.",
                "Do not reuse protocol, path, or persisted-key comparison rules for text whose purpose is user comprehension or localization."),
            new(
                "machine_formatting",
                "Round-trippable numeric, date/time, diagnostic, serialization, and protocol formatting intended for machines instead of readers.",
                "InvariantCulture is usually expected for machine-readable formats, but it should not be reused as a blanket answer for human-facing text.")
        ],
        "Classify each string comparison by data domain before filing. Path hits need filesystem case-sensitivity and normalization evidence; protocol, CLI, environment, symbol-name, and persisted-key hits are commonly ordinal; human-facing docs/help text needs culture-sensitive review; invariant casing should show machine-token intent or be replaced by comparer overloads.");

    private static readonly SearchRecipeNullableContractTaxonomyJsonResult NullableContractTaxonomy = new(
        [
            new(
                "optional_lookup",
                "A lookup where absence is an expected data state, such as a missing file row, metadata key, symbol, or configured value.",
                "Keep the nullable return when the method name, XML docs, or caller handling makes absence explicit; otherwise prefer Try* or an option/result wrapper."),
            new(
                "parse_miss",
                "A parser, extractor, or classifier miss where the input is valid but no supported construct was recognized.",
                "Prefer TryParse/TryExtract shapes or a result that distinguishes a valid miss from malformed input."),
            new(
                "unsupported_language_capability",
                "A language, extractor, or query capability that is intentionally unavailable for the active file or symbol kind.",
                "Surface stable capability diagnostics for user-facing boundaries instead of returning null through CLI, JSON, MCP, or LSP output."),
            new(
                "legacy_schema_absence",
                "A compatibility path where older indexes or imported databases legitimately lack a column, table, or metadata stamp.",
                "Keep the legacy fallback explicit and covered by migration/read-compatibility tests."),
            new(
                "unexpected_invariant_violation",
                "A path where null would mean an internal invariant, required row, or post-validation state was broken.",
                "Fail with a typed diagnostic, assertion, or exception rather than silently returning null.")
        ],
        [
            new(
                "try_pattern_out_parameter",
                "A null-forgiving assignment to an out parameter that is only read when the Try* method returns true."),
            new(
                "reflection_or_serialization_boundary",
                "A suppression required because reflection, source generation, or serialization initializes members outside normal constructors."),
            new(
                "delayed_initialization_field",
                "A field assigned by an explicit Open/Initialize path before use."),
            new(
                "false_state_sentinel",
                "A placeholder assigned before returning false, where callers must not read the placeholder.")
        ],
        "Classify nullable returns by domain before changing behavior. Optional lookup and parse-miss nulls can remain when callers branch explicitly; unsupported capabilities and legacy schema absence need stable diagnostics or documented fallbacks at user-facing boundaries; invariant violations should not be nullable contracts. For null-forgiving suppressions, require nearby tests or contract evidence for reflection/serialization, delayed initialization, or false-state Try* sentinels.");

    internal static IReadOnlyList<string> DefaultSourcePathPatterns => DefaultSourcePathPatternsValue;
    internal static IReadOnlyList<string> DefaultSourceExcludePaths => DefaultSourceExcludePathsValue;

    private static readonly SearchRecipeClassifierJsonResult SourceOriginClassifier = new(
        "source_origin",
        "Classifies whether a textual hit came from production source, tests, docs, generated metadata, recipe definitions, comments, strings, or help text.",
        [
            new("source_code", "Runtime source-code hit in the selected source scope.", "Prioritize when the path and origin facets point to production code."),
            new("test_or_fixture", "Test file, test symbol, or fixture text.", "Usually lower priority unless the audit is explicitly about tests or fixture drift."),
            new("documentation_or_recipe", "Documentation, changelog, workflow, or recipe-definition text.", "Treat as guidance or examples instead of runtime evidence."),
            new("comment_or_string", "Comment, string literal, regex literal, or help text rather than executable code.", "Use match origin facets before filing runtime issues.")
        ],
        ["path", "match_origins", "match_facets.origin", "test_file", "test_symbol", "test_fixture", "result_kinds"],
        "Use origin and test/fixture facets before filing noisy lexical hits; prefer source-scoped recipes or --origin code/comment when the raw term appears in docs or metadata.");
    private static readonly SearchRecipeClassifierJsonResult TimestampBoundaryClassifier = new(
        "timestamp_boundary",
        "Classifies DateTime and DateTimeOffset hits by timestamp boundary before changing UTC, offset, cache-expiry, display, or elapsed-time behavior.",
        [
            new("persisted_utc_or_offset", "Persisted database or file metadata timestamp that must round-trip as UTC or with an explicit offset.", "Treat offsetless persisted values as a documented UTC contract or migrate to an offset-aware representation."),
            new("filesystem_timestamp", "Filesystem last-write or metadata timestamp crossing OS and repository boundaries.", "Keep filesystem timestamps in UTC at the boundary and avoid local-time comparisons in freshness decisions."),
            new("runtime_wall_clock", "Process wall-clock timestamp used for audit logs, diagnostics, metadata, or user-facing status.", "Use TimeProvider or UTC/offset-aware types when the timestamp leaves the process or enters JSON."),
            new("network_api_timestamp", "HTTP, GitHub, or protocol timestamp such as Retry-After, rate-limit reset, or release metadata.", "Normalize API timestamps to UTC/offset-aware values before comparing with process clock values."),
            new("cache_expiry", "Cache freshness or expiry calculation based on checked-at or retry-after timestamps.", "Use a single UTC/offset contract for both the cached stamp and the current clock."),
            new("support_json", "Machine-facing status, support bundle, or diagnostics JSON field.", "Emit UTC or an explicit offset so consumers do not infer local time."),
            new("human_display", "Formatting path intended only for people reading CLI or log text.", "Display formatting may be local or contextual, but should not feed persistence or comparison logic."),
            new("monotonic_elapsed", "Elapsed-time, timeout, retry-delay, or duration measurement.", "Prefer Stopwatch, timeout budgets, or monotonic clock helpers instead of wall-clock subtraction.")
        ],
        ["path", "enclosing_symbol_name", "risk_evidence", "match_origins", "result_kinds"],
        "Classify the timestamp boundary first. Persisted and machine-facing values need explicit UTC or offset semantics; cache expiry must compare like-with-like clocks; elapsed-time and timeout logic should use monotonic duration primitives rather than DateTime wall-clock subtraction.");
    private static readonly SearchRecipeClassifierJsonResult GuardEvidenceClassifier = new(
        "guard_evidence",
        "Classifies whether nearby guard checks explain why a risky API call is already bounded, filtered, or intentionally rejected.",
        [
            new("bounded_positive_evidence", "A required guard appears near the primary match.", "Use guard_evidence and guard_checks to decide whether the hit is already bounded."),
            new("missing_guard", "No required guard was found near the primary match.", "Prioritize review when the query describes an API that needs bounds or policy."),
            new("reject_guard_excluded", "A reject guard intentionally removed an otherwise noisy hit.", "Use --show-excluded or a narrower child query when auditing recipe precision.")
        ],
        ["guard_filters", "guard_evidence", "guard_checks", "risk_evidence"],
        "Guard evidence is query-local context, not proof of safety; verify that the guard applies to the matched operation and not an unrelated nearby call.");
    private static readonly SearchRecipeClassifierJsonResult SecretOriginClassifier = new(
        "secret_origin",
        "Classifies token/auth hits by likely sensitive runtime material versus structural, SQL, protocol, docs, or placeholder text.",
        [
            new("runtime_secret_material", "Code that loads, stores, logs, forwards, or serializes token material.", "Prioritize redaction, scope, retention, and outbound-boundary review."),
            new("placeholder_or_redacted_example", "Fixture, documentation, or example text that does not carry live credentials.", "Usually keep as low-priority evidence unless examples can be copied unsafely."),
            new("structural_token_domain", "Parser, syntax, LSP, cancellation, SQL, or protocol-token domains.", "Use source/auth-token recipes to avoid confusing lexical token uses with credentials.")
        ],
        ["match_origins", "match_facets.origin", "path", "test_file", "risk_evidence"],
        "Use the source-scoped auth-token recipe for credential material and the broad-token-audit recipe only when lexical token coverage is intentional.");
    private static readonly SearchRecipeClassifierJsonResult ParserGuardClassifier = new(
        "parser_guard_evidence",
        "Classifies parser and deserializer hits by payload bounds, streaming/cancellation, and guard evidence.",
        [
            new("bounded_payload", "A byte, depth, item, or file-size bound is near the parse operation.", "Review whether the bound covers the actual input consumed by the parser."),
            new("streaming_or_cancelable", "The parser path is streaming, async, or cancellation-aware.", "Verify item budgets and cancellation are wired to the caller."),
            new("unbounded_materialization", "DOM or serializer materialization appears without nearby bounds.", "Prioritize size/depth limits, streaming, or bounded readers.")
        ],
        ["guard_filters", "guard_evidence", "guard_checks", "risk_evidence", "match_origins"],
        "Parser guard classifiers are triage hints; keep input-size, depth, and cancellation checks close to the parse boundary when possible.");
    private static readonly SearchRecipeClassifierJsonResult ProcessLaunchClassifier = new(
        "process_launch_boundary",
        "Classifies process-launch hits by shell use, ArgumentList use, working directory, environment forwarding, and shared launch wrappers.",
        [
            new("safe_wrapper", "Launch configuration flows through a shared policy wrapper.", "Check that the wrapper disables shell execution, bounds output, and scrubs environment variables."),
            new("adhoc_launch", "ProcessStartInfo or Process.Start is configured inline.", "Prioritize ArgumentList, UseShellExecute=false, working-directory validation, and timeout/cancellation review."),
            new("environment_or_cwd_boundary", "The launch mutates environment variables or working directory.", "Review inherited secrets, prompt suppression, and path trust boundaries.")
        ],
        ["path", "enclosing_symbol_name", "risk_evidence", "guard_evidence", "guard_checks"],
        "Treat process-launch results as trust-boundary evidence; prefer ProcessLaunchPolicy/SubprocessEnvironmentPolicy or nearby purpose-specific wrappers.");
    private static readonly SearchRecipeClassifierJsonResult CancellationIntentClassifier = new(
        "cancellation_intent",
        "Classifies cancellation-token hits by compatibility wrapper, short-lived probe, or long-running operation risk.",
        [
            new("compatibility_wrapper", "CancellationToken.None is used because an upstream API has no caller token.", "Document why cancellation cannot be propagated."),
            new("short_lived_probe", "A bounded local probe intentionally omits cancellation.", "Keep timeout or size evidence near the probe."),
            new("long_running_operation", "Indexing, I/O, process, network, or stream work ignores caller cancellation.", "Prioritize token propagation or a clear timeout path.")
        ],
        ["path", "enclosing_symbol_name", "risk_evidence", "match_origins"],
        "Classify CancellationToken.None by operation lifetime before changing behavior; long-running work should normally accept caller cancellation.");
    private static readonly SearchRecipeClassifierJsonResult TaskResultIntentClassifier = new(
        "task_result_intent",
        "Classifies .Result hits as sync-over-async risks or ordinary DTO/result-wrapper properties.",
        [
            new("task_blocking", "The receiver is Task, ValueTask, or an async operation converted to a blocking wait.", "Prioritize async flow, cancellation, and timeout review."),
            new("dto_result_property", "The receiver is a command, parse, query, or DTO result object.", "Usually a false positive for sync-over-async audits."),
            new("unclear_receiver", "The receiver type is not clear from the indexed snippet.", "Inspect the enclosing symbol before filing.")
        ],
        ["enclosing_symbol_name", "enclosing_symbol_kind", "result_kinds", "match_origins", "path"],
        "Use the receiver domain to separate true Task/ValueTask blocking from result-wrapper properties named Result.");
    private static readonly SearchRecipeClassifierJsonResult ActiveSkipClassifier = new(
        "active_skip_governance",
        "Classifies Skip assignments by active disabled tests versus examples, fixtures, or documented platform-specific skips.",
        [
            new("active_skip", "An active test attribute or metadata assignment disables coverage.", "Track the reason, issue link, and platform/runtime condition."),
            new("platform_or_external_dependency_skip", "The skip is a documented platform or external-dependency guard.", "Keep the condition narrow and covered by an alternate test when practical."),
            new("fixture_or_example", "The skip text appears in fixture code or documentation.", "Usually lower priority unless it masks an active test.")
        ],
        ["path", "match_origins", "test_file", "test_symbol", "test_fixture"],
        "Review active Skip assignments as test governance metadata, not just lexical matches.");
    private static readonly SearchRecipeClassifierJsonResult BroadCatchBoundaryClassifier = new(
        "broad_catch_boundary",
        "Classifies broad catch clauses by intentional boundary type before deciding whether the catch should be narrowed, rethrown, or documented.",
        [.. BroadExceptionCatchTaxonomy.BoundaryCategories.Select(category => new SearchRecipeClassifierCategoryJsonResult(
            category.Name,
            category.Description,
            category.ExpectedDiagnosticBehavior))],
        ["path", "enclosing_symbol_name", "guard_evidence", "risk_evidence", "match_origins"],
        BroadExceptionCatchTaxonomy.TriageGuidance);
    private static readonly SearchRecipeClassifierJsonResult DiagnosticRedactionClassifier = new(
        "diagnostic_redaction",
        "Classifies exception-message and broad-catch diagnostic paths by sanitized output, bounded private suppression, debug logging, or raw echo risk.",
        [
            new("sanitized_user_visible", "The path emits a stable error code, bounded user message, or redacted diagnostic.", "Prefer DiagnosticRedactor, CommandErrorWriter.FormatSanitizedException, or protocol-specific bounded error payloads."),
            new("private_or_best_effort_suppression", "The exception stays private because cleanup/probe failure should not replace the primary result.", "Keep comments or tests near the boundary explaining why suppression is intentional."),
            new("debug_or_support_bundle", "The diagnostic is limited to debug logging, local traces, or support-bundle material.", "Verify the path is opt-in, scoped, and redacted before it leaves the local trust boundary."),
            new("raw_exception_echo", "Raw exception text can cross CLI, JSON, MCP, LSP, support-bundle, or GitHub issue output.", "Route through the existing diagnostic/error formatting policy or add a stable sanitized wrapper.")
        ],
        ["path", "enclosing_symbol_name", "risk_evidence", "guard_evidence", "match_origins", "result_kinds"],
        "Trace raw exception text to its output boundary; user-visible diagnostics should be bounded and redacted, while private cleanup/probe suppression needs explicit intent.");

    private static List<SearchGuardFilter> BoundedRegexEvidenceGuardFilters() =>
    [
        new(SearchGuardRole.Reject, SearchGuardDirection.Before, "RegexOptions.NonBacktracking", SearchGuardScope.Window),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "RegexOptions.NonBacktracking", SearchGuardScope.Window),
        new(SearchGuardRole.Reject, SearchGuardDirection.Before, "RegexOptions.NonBacktracking", SearchGuardScope.SameLine),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "RegexOptions.NonBacktracking", SearchGuardScope.SameLine),
        new(SearchGuardRole.Reject, SearchGuardDirection.Before, "TimeSpan.", SearchGuardScope.Window),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "TimeSpan.", SearchGuardScope.Window),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "TimeSpan.", SearchGuardScope.SameLine),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "matchTimeout:", SearchGuardScope.Window),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "matchTimeout:", SearchGuardScope.SameLine),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "MatchTimeout(", SearchGuardScope.Window),
        new(SearchGuardRole.Reject, SearchGuardDirection.After, "MatchTimeout(", SearchGuardScope.SameLine)
    ];

    private static List<SearchAuditRecipeQuery> AddClassifiers(
        List<SearchAuditRecipeQuery> queries,
        params SearchRecipeClassifierJsonResult[] classifiers)
        => queries
            .Select(query => query with
            {
                Classifiers = [.. query.Classifiers, .. classifiers]
            })
            .ToList();

    private static SearchAuditRecipeQuery RegexTimeoutPolicyReferenceQuery() =>
        new(
            "regex-timeout-policy-reference",
            "RegexTimeoutPolicy",
            "Find shared regex timeout policy references that separate timeout-positive paths from raw regex calls.",
            ["audit", "performance"],
            "This is positive evidence; pair it with the matching Regex construction or static API hit to verify the timeout applies to the searched pattern.",
            ExactSubstring: true)
        {
            Severity = "info",
            RiskEvidence =
            [
                "positive: RegexTimeoutPolicy references are timeout-positive evidence for a documented repository regex timeout boundary.",
                "risk: timeout policy constants must still be wired into the actual Regex overload, wrapper, or registry factory that evaluates the pattern."
            ],
            MatchOrigins = ["code"],
        };

    private static SearchAuditRecipeQuery RegexTimeoutTimespanEvidenceQuery() =>
        new(
            "regex-timeout-timespan-evidence",
            "TimeSpan.From",
            "Find explicit TimeSpan timeout values that can explain why nearby regex construction is bounded.",
            ["audit", "performance"],
            "False positives include non-regex timeouts; require nearby Regex construction, static Regex API usage, or a registry factory before treating this as regex evidence.",
            ExactSubstring: true)
        {
            Severity = "info",
            RiskEvidence =
            [
                "positive: TimeSpan.From* near a Regex construction or static Regex call is timeout-positive evidence.",
                "risk: unrelated retry, process, or database timeouts do not prove regex evaluation is bounded."
            ],
            MatchOrigins = ["code"],
        };

    private static SearchAuditRecipeQuery RegexRegistryFactoryQuery() =>
        new(
            "regex-registry-factory",
            "RegexRegistry.Create",
            "Find centralized regex factory use so registry-backed patterns are classified apart from ad hoc raw construction.",
            ["audit", "performance"],
            "False positives include factory declarations; callers should still confirm the chosen registry method matches the input trust boundary.",
            ExactSubstring: true)
        {
            Severity = "info",
            RiskEvidence =
            [
                "positive: RegexRegistry.Create* is the centralized regex factory path with named timeout and option policy.",
                "risk: each caller still needs trust-boundary review for user input, config/env input, repository-controlled patterns, test fixtures, or generated diagnostics."
            ],
            MatchOrigins = ["code"],
        };

    private static SearchAuditRecipeQuery GeneratedRegexAttributeQuery() =>
        new(
            "generated-regex-attribute",
            "GeneratedRegex",
            "Find source-generated regex patterns so generated code can be reviewed separately from runtime construction.",
            ["audit", "performance"],
            "False positives include classifier strings and documentation; generated regex attributes still need timeout, culture, case, and trust-boundary review.",
            ExactSubstring: true)
        {
            Severity = "info",
            RiskEvidence =
            [
                "positive: GeneratedRegex can move stable hot-path patterns out of runtime construction.",
                "risk: generated patterns still need explicit timeout and culture/case choices that match path, symbol, environment, protocol, or human-text domains."
            ],
            MatchOrigins = ["code", "regex_literal"],
        };

    private static SearchAuditRecipeQuery RegexCultureInvariantOptionQuery() =>
        new(
            "regex-culture-invariant-option",
            "RegexOptions.CultureInvariant",
            "Find regex culture options so path, symbol, environment, protocol, and human-text matching choices can be classified.",
            ["audit", "bug", "portability"],
            "False positives include non-regex option documentation; verify the pattern domain before changing culture or case behavior.",
            ExactSubstring: true)
        {
            Severity = "info",
            RiskEvidence =
            [
                "positive: RegexOptions.CultureInvariant is usually expected for machine-token, syntax, protocol, and repository-controlled pattern matching.",
                "risk: human-facing text matching may need culture-aware behavior, while path matching still needs filesystem case-sensitivity evidence."
            ],
            MatchOrigins = ["code"],
        };

    private static SearchAuditRecipeQuery RegexNonBacktrackingOptionQuery() =>
        new(
            "regex-nonbacktracking-option",
            "RegexOptions.NonBacktracking",
            "Find regex non-backtracking evidence that can explain why nearby construction is bounded against backtracking blowups.",
            ["audit", "performance"],
            "False positives include recipe guard documentation; verify the option is applied to the regex that evaluates untrusted or variable input.",
            ExactSubstring: true)
        {
            Severity = "info",
            RiskEvidence =
            [
                "positive: RegexOptions.NonBacktracking is evidence that a pattern has a non-backtracking execution policy.",
                "risk: non-backtracking does not replace timeout review for every trust boundary, input size, or unsupported pattern feature."
            ],
            MatchOrigins = ["code"],
        };

    private static SearchAuditRecipeQuery RegexInfiniteTimeoutJustificationQuery() =>
        new(
            "regex-infinite-timeout-justification",
            "Regex.InfiniteMatchTimeout",
            "Find explicit no-timeout regex policy that needs nearby bounded-input or documented no-timeout justification.",
            ["audit", "performance"],
            "This is not safe evidence by itself; accept it only with documented trusted small input, generated diagnostics, or another bounded execution argument.",
            ExactSubstring: true)
        {
            RiskEvidence =
            [
                "risk: Regex.InfiniteMatchTimeout disables timeout enforcement and needs a documented no-timeout justification.",
                "positive: fixed repository-controlled patterns over tiny trusted inputs may justify no-timeout behavior when the reason is documented."
            ],
            MatchOrigins = ["code"],
        };

    private static SearchAuditRecipeQuery StaticRegexApiQuery(string name, string query, string apiName) =>
        new(
            name,
            query,
            $"Find static Regex.{apiName} calls that may need BoundedRegex or an explicit timeout overload.",
            ["audit", "performance"],
            "False positives include bounded wrapper aliases, prevalidated small inputs, and tests that intentionally exercise raw Regex behavior.")
        {
            RejectFileQueries =
            [
                BoundedRegexAliasUsing
            ],
            ExcludePaths = [BoundedRegexPath],
            RiskEvidence =
            [
                $"risk: static System.Text.RegularExpressions.Regex.{apiName} can run without the shared timeout policy.",
                "risk: classify each pattern by trust boundary: user input, config/env input, repository-controlled patterns, test fixtures, or generated diagnostics.",
                "positive: BoundedRegex aliases, explicit timeout overloads, GeneratedRegex attributes, registry factories, or tightly bounded trusted inputs can make a hit intentional."
            ],
            GuardFilters = BoundedRegexEvidenceGuardFilters(),
            MatchOrigins = ["code"],
        };

    private static SearchAuditRecipeQuery DogfoodStaticRegexApiQuery(string name, string query, string shape) =>
        new(
            name,
            query,
            $"Find raw static Regex API usage candidates with {shape} so bounded instance names are not counted.",
            ["audit", "performance", "security"],
            "False positives include Regex.Escape/Unescape, explicit timeout overloads, generated/precompiled patterns, trusted small inputs, and tests that intentionally exercise raw Regex behavior.")
        {
            RejectFileQueries =
            [
                BoundedRegexAliasUsing
            ],
            ExcludePaths = [BoundedRegexPath],
            RiskEvidence =
            [
                "risk: raw System.Text.RegularExpressions.Regex static APIs can run without explicit timeout or shared bounded-regex policy.",
                "risk: classify each pattern by trust boundary: user input, config/env input, repository-controlled patterns, test fixtures, or generated diagnostics.",
                "positive: BoundedRegex aliases and instance names ending in Regex are filtered out; remaining hits should be classified as timeout-backed, generated/precompiled, trusted small input, or non-matching helpers such as Escape."
            ],
            GuardFilters = BoundedRegexEvidenceGuardFilters(),
            MatchOrigins = ["code"],
        };

    private static readonly string[] TimestampBoundaryRiskEvidence =
    [
        "risk: timestamp hits must be classified as persisted database/file metadata, filesystem clock, process wall clock, network/API timestamp, human display, support JSON, cache expiry, or monotonic elapsed-time before changing behavior.",
        "risk: persisted and machine-facing timestamps should be UTC or carry an explicit offset; do not compare offsetless local wall time with UTC freshness fields.",
        "positive: Stopwatch, TimeProvider.GetUtcNow, DateTimeOffset, and round-trip O formatting are useful evidence only when they match the boundary being measured or serialized."
    ];

    private static SearchAuditRecipeQuery TimestampBoundaryQuery(
        string name,
        string query,
        string description,
        string falsePositiveGuidance,
        params string[] riskEvidence)
        => new(
            name,
            query,
            description,
            ["audit", "bug"],
            falsePositiveGuidance)
        {
            RiskEvidence = [.. TimestampBoundaryRiskEvidence, .. riskEvidence],
            MatchOrigins = ["code"],
            Classifiers = [TimestampBoundaryClassifier, SourceOriginClassifier],
        };

    private static readonly List<SearchAuditRecipe> BuiltInRecipes =
    [
        SourceScopedRecipe(
            "risky-code",
            "Reusable audit searches for risky code patterns that often need manual triage.",
            [
                new(
                    "unbounded-json-parse",
                    "JsonDocument.Parse",
                    "Find direct JSON parsing calls that may need input size limits or streaming alternatives.",
                    ["audit", "bug"],
                    "False positives include tests, deliberately bounded callers, and parsing of already-small generated payloads.")
                {
                    RiskEvidence =
                    [
                        "risk: DOM parsing can materialize an entire payload and should have an upstream byte or depth bound.",
                        "positive: generated payloads, fixed literals, and callers with explicit byte caps are usually lower risk."
                    ],
                    MatchOrigins = ["code"],
                    ExcludePaths = ["src/CodeIndex/Diagnostics/BoundedJson.cs"],
                },
                new(
                    "full-materialization",
                    "ReadToEnd",
                    "Find full stream/string materialization that may need bounded reads or incremental processing.",
                    ["audit", "performance"],
                    "False positives include bounded in-memory test fixtures and tiny diagnostic payloads.")
                {
                    RiskEvidence =
                    [
                        "risk: whole stream or string content may be buffered before size or cancellation checks.",
                        "positive: nearby bounded reader, byte cap, line cap, or tiny trusted source can explain intentional materialization."
                    ],
                },
                new(
                    "file-read-all-text",
                    "File.ReadAllText",
                    "Find whole-file text reads that may need size caps, sharing policy, or streaming alternatives.",
                    ["audit", "performance"],
                    "False positives include bounded test fixtures and small files guarded by explicit size checks."),
                new(
                    "file-read-all-bytes",
                    "File.ReadAllBytes",
                    "Find whole-file byte reads that may need size caps, sharing policy, or streaming alternatives.",
                    ["audit", "performance"],
                    "False positives include bounded test fixtures and small files guarded by explicit size checks.")
                {
                    MatchOrigins = ["code"],
                    ExcludePaths = ["src/CodeIndex/BoundedFile.cs"],
                },
                new(
                    "max-value-probe",
                    "int.MaxValue",
                    "Find sentinel or unbounded limit probes that may hide huge allocation or traversal paths.",
                    ["audit", "bug"],
                    "False positives include defensive upper-bound constants that are never passed to allocation or query limits.")
                {
                    RiskEvidence =
                    [
                        "risk: sentinel limits can bypass practical allocation, traversal, or query bounds.",
                        "positive: saturation helpers, explicit cap comments, or test-only ceiling probes often make the hit non-actionable."
                    ],
                },
                new(
                    "raw-diagnostic-echo",
                    "ex.Message",
                    "Find raw exception-message echoes that may need redaction before CLI, JSON, MCP, or GitHub output.",
                    ["audit", "security"],
                    "False positives include messages that are already sanitized by the surrounding writer.")
                {
                    RiskEvidence =
                    [
                        "risk: raw exception messages can carry absolute paths, command lines, SQL, or secret-like values into user-visible output.",
                        "positive: DiagnosticRedactor, CommandErrorWriter.FormatSanitizedException, or a dedicated sanitizer nearby is strong safe evidence."
                    ],
                    MatchOrigins = ["code"],
                    ExcludePaths = ["src/CodeIndex/Diagnostics/DiagnosticRedactor.cs"],
                    GuardFilters =
                    [
                        new(SearchGuardRole.Reject, SearchGuardDirection.Before, "DiagnosticRedactor", SearchGuardScope.Window),
                        new(SearchGuardRole.Reject, SearchGuardDirection.After, "DiagnosticRedactor", SearchGuardScope.Window),
                        new(SearchGuardRole.Reject, SearchGuardDirection.Before, "FormatSanitizedException", SearchGuardScope.Window),
                        new(SearchGuardRole.Reject, SearchGuardDirection.After, "FormatSanitizedException", SearchGuardScope.Window),
                    ],
                    Classifiers = [DiagnosticRedactionClassifier],
                },
                new(
                    "cancellation-gap",
                    "CancellationToken.None",
                    "Find async or stream paths that may be ignoring caller cancellation.",
                    ["audit", "bug"],
                    "False positives include intentionally fire-and-forget work and APIs that have no meaningful caller cancellation token.")
                {
                    Classifiers = [CancellationIntentClassifier],
                },
                new(
                    "empty-catch-review",
                    "catch",
                    "Find C# catch clauses that may be empty, overly broad, or swallowing diagnostic context.",
                    ["audit", "bug"],
                    "False positives include catch blocks that rethrow, translate exceptions safely, or intentionally ignore best-effort cleanup failures.",
                    ExactSubstring: false)
                {
                    MatchOrigins = ["code"],
                    GuardFilters =
                    [
                        new(SearchGuardRole.Require, SearchGuardDirection.Before, "}"),
                        new(SearchGuardRole.Require, SearchGuardDirection.After, "{")
                    ],
                    RiskEvidence =
                    [
                        "risk: broad or empty catch clauses can swallow recovery diagnostics or hide unexpected failures.",
                        "positive: explicit rethrow, translation to a stable error contract, or documented best-effort cleanup can make a catch intentional."
                    ],
                    Classifiers = [BroadCatchBoundaryClassifier],
                },
                new(
                    "broad-exception-catch",
                    "catch (Exception",
                    "Find broad C# exception catches that may need narrower exception types or explicit recovery boundaries.",
                    ["audit", "bug"],
                    "False positives include top-level command, worker/process, cleanup best-effort, probe fallback, and diagnostic-sanitization boundaries when they emit bounded stable diagnostics or intentionally private suppression.")
                {
                    MatchOrigins = ["code"],
                    BroadCatchTaxonomy = BroadExceptionCatchTaxonomy,
                    Classifiers = [BroadCatchBoundaryClassifier, DiagnosticRedactionClassifier],
                },
                new(
                    "process-start-info",
                    "ProcessStartInfo",
                    "Find external process launch configuration that may need argument, environment, cwd, and shell-use review.",
                    ["audit", "security"],
                    "False positives include tests and launch wrappers that already validate arguments and disable shell expansion.")
                {
                    RiskEvidence =
                    [
                        "risk: launch sites need review for UseShellExecute, WorkingDirectory, Environment mutation, and ArgumentList usage.",
                        "positive: shared safe-launch wrappers and explicit ArgumentList setup usually lower risk compared with ad hoc Process.Start calls."
                    ],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-start-direct",
                    "Process.Start",
                    "Find direct process launches that may need a shared safe-launch wrapper or explicit argument handling.",
                    ["audit", "security"],
                    "False positives include simple URL/document open helpers or test fixtures with trusted inputs.")
                {
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "recursive-delete",
                    "Directory.Delete",
                    "Find recursive or broad delete operations that may need path-boundary and symlink/reparse-point review.",
                    ["audit", "security"],
                    "False positives include isolated temporary-directory cleanup guarded by test helpers or workspace-root containment checks."),
                new(
                    "infinite-timeout",
                    "Timeout.InfiniteTimeSpan",
                    "Find infinite waits that may need bounded timeouts, cancellation, or liveness reporting.",
                    ["audit", "bug"],
                    "False positives include deliberate sentinel values that are never passed to blocking waits."),
                new(
                    "thread-sleep",
                    "Thread.Sleep",
                    "Find blocking sleeps that may need cancellation-aware waits, bounded retry policy, or test-only isolation.",
                    ["audit", "bug"],
                    "False positives include tiny test synchronization probes and documented compatibility waits."),
                new(
                    "path-case-heuristic",
                    "OrdinalIgnoreCase",
                    "Find case-insensitive path or identifier comparisons that may need filesystem case-sensitivity awareness.",
                    ["audit", "portability"],
                    "False positives include non-path protocol tokens, CLI option names, labels, and other intentionally case-insensitive domains.")
                {
                    RiskEvidence =
                    [
                        "risk: path equality and path dictionaries may need the indexed filesystem case-sensitivity signal.",
                        "positive: protocol tokens, option names, labels, header names, or language keywords are non-path domains."
                    ],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "regex-construction",
                    "new Regex(",
                    "Find direct regex construction that may need a timeout, non-backtracking mode, or bounded input review.",
                    ["audit", "performance"],
                    "False positives include precompiled bounded patterns with explicit timeouts or tiny trusted inputs.")
                {
                    RejectFileQueries =
                    [
                        "using Regex = CodeIndex.Indexer.BoundedRegex"
                    ],
                    ExcludePaths = [BoundedRegexPath, RegexRegistryPath],
                    RiskEvidence =
                    [
                        "risk: raw System.Text.RegularExpressions.Regex construction should show an explicit timeout, non-backtracking mode, or bounded input.",
                        "risk: classify each pattern by trust boundary: user input, config/env input, repository-controlled patterns, test fixtures, or generated diagnostics.",
                        "positive: bounded-wrapper aliases are reported by bounded-regex-alias instead of this raw construction query.",
                        "positive: shared regex factories in RegexRegistry.cs are the centralized raw-construction exception."
                    ],
                    GuardFilters = BoundedRegexEvidenceGuardFilters(),
                },
                new(
                    "bounded-regex-alias",
                    "using Regex = CodeIndex.Indexer.BoundedRegex",
                    "Find files where `new Regex(...)` is backed by the repository bounded regex wrapper alias.",
                    ["audit", "performance"],
                    "This is positive evidence for regex-construction hits; still review whether the bounded wrapper receives trusted patterns and inputs.",
                    ExactSubstring: true)
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: the Regex identifier aliases CodeIndex.Indexer.BoundedRegex, separating wrapper construction from raw BCL Regex.",
                        "risk: alias evidence does not prove every regex input is small; check the matching construction site when the same file also appears in regex-construction."
                    ],
                },
                new(
                    "fully-qualified-regex-construction",
                    "new System.Text.RegularExpressions.Regex",
                    "Find fully qualified raw BCL regex construction that bypasses a bounded wrapper alias.",
                    ["audit", "performance"],
                    "False positives include tests and code that supplies explicit timeouts or RegexOptions.NonBacktracking.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "risk: fully qualified BCL Regex construction bypasses local aliases and should carry timeout/non-backtracking evidence.",
                        "positive: explicit timeout arguments or RegexOptions.NonBacktracking can make the construction bounded."
                    ],
                    GuardFilters = BoundedRegexEvidenceGuardFilters(),
                },
                RegexTimeoutPolicyReferenceQuery(),
                RegexTimeoutTimespanEvidenceQuery(),
                RegexRegistryFactoryQuery(),
                GeneratedRegexAttributeQuery(),
                RegexCultureInvariantOptionQuery(),
                RegexNonBacktrackingOptionQuery(),
                RegexInfiniteTimeoutJustificationQuery(),
                StaticRegexApiQuery(
                    "static-regex-is-match",
                    " Regex.IsMatch(",
                    "IsMatch"),
                StaticRegexApiQuery(
                    "static-regex-is-match-negated",
                    "!Regex.IsMatch(",
                    "IsMatch"),
                StaticRegexApiQuery(
                    "static-regex-is-match-parenthesized",
                    "(Regex.IsMatch(",
                    "IsMatch"),
                StaticRegexApiQuery(
                    "static-regex-match",
                    " Regex.Match(",
                    "Match"),
                StaticRegexApiQuery(
                    "static-regex-match-negated",
                    "!Regex.Match(",
                    "Match"),
                StaticRegexApiQuery(
                    "static-regex-match-parenthesized",
                    "(Regex.Match(",
                    "Match"),
                StaticRegexApiQuery(
                    "static-regex-matches",
                    " Regex.Matches(",
                    "Matches"),
                StaticRegexApiQuery(
                    "static-regex-matches-negated",
                    "!Regex.Matches(",
                    "Matches"),
                StaticRegexApiQuery(
                    "static-regex-matches-parenthesized",
                    "(Regex.Matches(",
                    "Matches"),
                StaticRegexApiQuery(
                    "static-regex-replace",
                    " Regex.Replace(",
                    "Replace"),
                StaticRegexApiQuery(
                    "static-regex-replace-negated",
                    "!Regex.Replace(",
                    "Replace"),
                StaticRegexApiQuery(
                    "static-regex-replace-parenthesized",
                    "(Regex.Replace(",
                    "Replace"),
                StaticRegexApiQuery(
                    "static-regex-split",
                    " Regex.Split(",
                    "Split"),
                StaticRegexApiQuery(
                    "static-regex-split-negated",
                    "!Regex.Split(",
                    "Split"),
                StaticRegexApiQuery(
                    "static-regex-split-parenthesized",
                    "(Regex.Split(",
                    "Split"),
                new(
                    "regex-timeout-handling",
                    "RegexMatchTimeoutException",
                    "Find regex timeout handling boundaries that may need consistent diagnostics and recovery behavior.",
                    ["audit", "bug"],
                    "False positives include tests and already-normalized parse/validation errors."),
                new(
                    "environment-secret-source",
                    "GetEnvironmentVariable",
                    "Find environment-variable reads that may source tokens, secrets, credentials, or operational policy.",
                    ["audit", "security"],
                    "False positives include non-secret feature flags and documented public configuration.")
                {
                    MatchOrigins = ["code"],
                    ExcludePaths =
                    [
                        "src/CodeIndex/Diagnostics/SensitiveNameClassifier.cs",
                        "src/CodeIndex/Processes/SubprocessEnvironmentPolicy.cs",
                    ],
                },
                new(
                    "authorization-handling",
                    "Authorization",
                    "Find authorization header or auth-boundary handling that may need redaction and egress review.",
                    ["audit", "security"],
                    "False positives include documentation, tests, and already-redacted header-name-only handling.")
                {
                    RiskEvidence =
                    [
                        "risk: HTTP Authorization header values and outbound auth boundaries need storage, redaction, and egress review.",
                        "positive: SQL ALTER AUTHORIZATION, parser grammar, and header-name-only constants are usually structural false positives."
                    ],
                },
                new(
                    "http-client-construction",
                    "new HttpClient",
                    "Find direct HTTP client construction that may need lifetime, timeout, and outbound-boundary review.",
                    ["audit", "security"],
                    "False positives include tests, short-lived CLI probes with explicit timeouts, and shared factory wrappers.")
                {
                    RiskEvidence =
                    [
                        "risk: ad hoc clients can miss timeout, handler lifetime, proxy, auth, or egress-boundary policy.",
                        "positive: shared factories with explicit timeout and handler policy are lower-risk construction sites."
                    ],
                },
                new(
                    "bearer-token-handling",
                    "Bearer",
                    "Find bearer token handling that may need storage, logging, and outbound request review.",
                    ["audit", "security"],
                    "False positives include examples, tests, and redacted token placeholders."),
                new(
                    "credential-term",
                    "credential",
                    "Find credential-related code paths that may need source, persistence, and redaction boundary review.",
                    ["audit", "security"],
                    "False positives include natural-language documentation or non-secret credential-type names.",
                    ExactSubstring: false)
                {
                    MatchOrigins = ["code"],
                    ExcludePaths =
                    [
                        "src/CodeIndex/Diagnostics/DiagnosticRedactor.cs",
                        "src/CodeIndex/Diagnostics/SensitiveNameClassifier.cs",
                    ],
                },
                new(
                    "secret-term",
                    "secret",
                    "Find secret-related code paths that may need source, persistence, and redaction boundary review.",
                    ["audit", "security"],
                    "False positives include documentation, labels, and comments that do not touch secret material.",
                    ExactSubstring: false),
                new(
                    "token-term",
                    "auth token",
                    "Find auth-token contexts without the broad parser, syntax, LSP, and cancellation-token noise from the bare token term.",
                    ["audit", "security"],
                    "False positives include documentation and tests; use the broad-token-audit recipe or an ad hoc `token` search when you intentionally need lexical-token coverage.",
                    ExactSubstring: false)
                {
                    RiskEvidence =
                    [
                        "risk: auth-token material can be logged, persisted, or forwarded across trust boundaries without redaction.",
                        "positive: placeholders, documentation, or explicit redaction helpers are usually lower-risk token mentions."
                    ],
                }
            ],
            DefaultExecutableExcludeOriginsValue),
        SourceScopedRecipe(
            "string-comparison-semantics",
            "Audit string comparison, casing, and culture choices by path, protocol, CLI, identifier, and human-text semantics.",
            [
                new(
                    "ordinal-ignore-case",
                    "OrdinalIgnoreCase",
                    "Find case-insensitive ordinal comparisons that need path/protocol/CLI/identifier/human-text classification.",
                    ["audit", "bug", "portability"],
                    "False positives include protocol tokens, CLI options, labels, headers, and stable machine identifiers where ordinal-ignore-case is the intended contract.")
                {
                    RiskEvidence =
                    [
                        "risk: path equality, path-prefix checks, and path dictionaries need filesystem case-sensitivity and normalization evidence instead of unconditional case-insensitive ordinal checks.",
                        "positive: protocol tokens, CLI options, headers, labels, and persisted machine keys usually need ordinal semantics and should be separated from path/culture findings."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "string-comparer-ordinal-family",
                    "StringComparer.Ordinal",
                    "Find StringComparer.Ordinal* comparer use in dictionaries, sets, and ordering so key domains can be classified.",
                    ["audit", "bug", "portability"],
                    "This intentionally includes StringComparer.OrdinalIgnoreCase; use ordinal-ignore-case for the case-insensitive cross-cutting bucket, then classify the key domain here.")
                {
                    RiskEvidence =
                    [
                        "risk: dictionary and set comparers define lookup semantics; classify keys as paths, protocol tokens, CLI names, stable identifiers, or human text.",
                        "positive: protocol tokens, command names, generated IDs, and database/cache keys often require stable ordinal or ordinal-ignore-case comparers."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "string-comparison-ordinal-family",
                    "StringComparison.Ordinal",
                    "Find StringComparison.Ordinal* overloads so equality, contains, prefix, and sort semantics can be classified by domain.",
                    ["audit", "bug", "portability"],
                    "This intentionally includes StringComparison.OrdinalIgnoreCase; use ordinal-ignore-case for the case-insensitive cross-cutting bucket, then classify whether the comparison is path-sensitive, protocol/CLI-sensitive, identifier-sensitive, or human-facing.")
                {
                    RiskEvidence =
                    [
                        "risk: ordinal-family overloads on StartsWith, Contains, Equals, Compare, or EndsWith can be wrong for human text and can miss filesystem case-sensitivity policy for paths.",
                        "positive: protocol tokens, CLI switches, enum-like identifiers, and persisted keys often require ordinal-family overloads for repeatable behavior."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "path-case-sensitivity-signal",
                    "path_case_sensitive",
                    "Find path comparison readiness signals that should govern path/glob case-sensitivity review.",
                    ["audit", "bug", "portability"],
                    "False positives include documentation of the status field; use this as positive evidence when classifying path comparison hits.",
                    ExactSubstring: true)
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: path_case_sensitive is the indexed filesystem case-sensitivity signal; path comparisons should cite it instead of assuming host OS behavior.",
                        "risk: path/glob comparisons that ignore this signal can collapse distinct files on case-sensitive filesystems or miss aliases on case-insensitive filesystems."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "uri-protocol-token-domain",
                    "Uri.TryCreate",
                    "Find URI parsing and scheme/host classification paths that usually need protocol-token ordinal semantics.",
                    ["audit", "bug"],
                    "False positives include file URI conversion paths where the post-parse local path still needs filesystem path semantics.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "positive: URI schemes and hosts are protocol tokens; ordinal or ordinal-ignore-case checks are usually intended where the protocol specifies ASCII token behavior.",
                        "risk: after a URI is converted to a local path, path comparison must switch back to filesystem path semantics."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "cli-option-domain",
                    "CliFlag",
                    "Find CLI flag schema and completion paths where option names are command tokens rather than human text.",
                    ["audit", "bug"],
                    "False positives include help text descriptions; option identity should be classified separately from descriptions shown to users.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "positive: CLI flag names and aliases are command/control tokens that usually need stable ordinal semantics.",
                        "risk: flag descriptions and help prose are human-facing text and should not inherit option-name comparison rules."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "symbol-name-domain",
                    "symbol name",
                    "Find symbol-name contract surfaces so language identifier comparisons stay separate from raw source or display text.",
                    ["audit", "bug"],
                    "False positives include user-facing messages that mention symbol names without performing symbol lookup.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "positive: extracted symbol names and canonical name fields need the documented exact-name/folded-name contract rather than ad hoc culture rules.",
                        "risk: user-facing messages that only display a symbol name should be classified as human text instead of lookup semantics."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "environment-name-domain",
                    "EnvironmentVariable",
                    "Find environment-variable name surfaces that need stable machine-token comparison semantics.",
                    ["audit", "bug"],
                    "False positives include user-facing environment diagnostics; classify the variable name separately from the displayed explanation.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "positive: environment variable names are machine/control tokens and should stay culture-independent.",
                        "risk: displayed environment diagnostics may be human text even when the key name is ordinal."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "db-key-domain",
                    "codeindex_meta",
                    "Find persisted database metadata key comparisons that need stable DB-key semantics.",
                    ["audit", "bug"],
                    "False positives include schema documentation; persisted keys need migration-compatible ordinal semantics.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "positive: codeindex_meta keys are persisted DB identifiers and should be compared as stable machine keys.",
                        "risk: migration and diff paths must not treat persisted keys as localized or path-like text."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "current-culture-human-text",
                    "CurrentCulture",
                    "Find explicit current-culture formatting or casing paths that are likely intended for human-facing text.",
                    ["audit", "bug"],
                    "False positives include diagnostics that intentionally record the active locale as machine-readable metadata.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "positive: CurrentCulture is appropriate evidence for user-facing formatting and locale diagnostics.",
                        "risk: machine formats, persisted keys, and protocol tokens should not inherit CurrentCulture behavior unless the contract says so."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "docs-help-text-domain",
                    "help_text",
                    "Find help/documentation-text classification paths so user prose stays separate from protocol, path, and key domains.",
                    ["audit", "bug"],
                    "False positives include enum-like origin labels; verify whether the match is classifying prose or comparing machine tokens.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "positive: help_text marks explanatory text intended for users, not protocol/path/key identity.",
                        "risk: help prose should not be normalized with protocol-token or persisted-key comparison rules unless the compared value is an actual command token."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "invariant-culture",
                    "InvariantCulture",
                    "Find invariant culture formatting, parsing, or comparison paths that need machine-format versus human-text classification.",
                    ["audit", "bug"],
                    "False positives include serialization, diagnostics, protocol fields, and round-trip numeric or date/time formatting intended for machines.")
                {
                    RiskEvidence =
                    [
                        "risk: InvariantCulture used on user-facing text can ignore user locale, casing, and collation expectations.",
                        "positive: machine-readable formatting, parsing, protocol serialization, and stable diagnostics usually require invariant culture."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "lower-invariant-casing",
                    "ToLowerInvariant",
                    "Find invariant lowercasing that may need comparer overloads or human-culture-aware casing instead of string normalization.",
                    ["audit", "bug"],
                    "False positives include machine tokens normalized for protocol, CLI, cache-key, or persisted-key contracts.")
                {
                    RiskEvidence =
                    [
                        "risk: invariant lowercasing can allocate, lose original spelling, and be wrong for human-facing text or path semantics.",
                        "positive: protocol tokens, CLI switches, and stable machine keys can justify invariant normalization when the stored contract is documented."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                },
                new(
                    "upper-invariant-casing",
                    "ToUpperInvariant",
                    "Find invariant uppercasing that may need comparer overloads or human-culture-aware casing instead of string normalization.",
                    ["audit", "bug"],
                    "False positives include machine tokens normalized for protocol, CLI, cache-key, or persisted-key contracts.")
                {
                    RiskEvidence =
                    [
                        "risk: invariant uppercasing can allocate, lose original spelling, and be wrong for human-facing text or path semantics.",
                        "positive: protocol tokens, CLI switches, and stable machine keys can justify invariant normalization when the stored contract is documented."
                    ],
                    MatchOrigins = ["code"],
                    StringComparisonTaxonomy = StringComparisonSemanticsTaxonomy,
                }
            ]),
        SourceScopedRecipe(
            "auth-token-audit",
            "Audit credential and auth-token material without the parser, protocol, LSP, and cancellation-token noise from bare token searches.",
            AddClassifiers([
                new(
                    "bearer-token",
                    "Bearer",
                    "Find bearer token handling that may need source, storage, logging, and outbound request review.",
                    ["audit", "security"],
                    "False positives include examples, tests, and redacted token placeholders.")
                {
                    RiskEvidence =
                    [
                        "risk: bearer tokens often authorize outbound requests and should not be logged, cached, or persisted without policy.",
                        "positive: redacted placeholders, sanitized diagnostics, and isolated test fixtures are usually lower risk."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                },
                new(
                    "authorization-header",
                    "Authorization",
                    "Find authorization header construction and forwarding paths that may carry token material.",
                    ["audit", "security"],
                    "False positives include non-secret authorization enum names and documentation-only references.")
                {
                    RiskEvidence =
                    [
                        "risk: Authorization headers can propagate bearer or API tokens into logs, telemetry, redirects, or unintended hosts.",
                        "positive: shared outbound clients with redaction, host allowlists, and sanitized diagnostics are safer evidence."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                },
                new(
                    "github-token",
                    "github token",
                    "Find GitHub token handling without matching generic parser or cancellation token domains.",
                    ["audit", "security"],
                    "False positives include docs and examples that do not load, store, log, or transmit real tokens.",
                    ExactSubstring: false)
                {
                    RiskEvidence =
                    [
                        "risk: GitHub tokens can grant repository or workflow access and need storage, scope, and logging review.",
                        "positive: token-scope validation, secret providers, and redaction boundaries are useful safe evidence."
                    ],
                },
                new(
                    "api-token",
                    "api token",
                    "Find API token handling without broad lexical token noise.",
                    ["audit", "security"],
                    "False positives include documentation or placeholder examples that do not touch runtime secret material.",
                    ExactSubstring: false)
                {
                    RiskEvidence =
                    [
                        "risk: API tokens can cross process, network, or persistence boundaries if not scoped and redacted.",
                        "positive: secret-store loading, explicit scope validation, and sanitized output paths lower the risk."
                    ],
                },
                new(
                    "access-token",
                    "access token",
                    "Find access-token contexts that may need expiration, refresh, storage, or logging review.",
                    ["audit", "security"],
                    "False positives include auth protocol docs and redacted example payloads.",
                    ExactSubstring: false)
                {
                    RiskEvidence =
                    [
                        "risk: access tokens usually have expiry and scope semantics that can be mishandled in caches or logs.",
                        "positive: expiry-aware caches, refresh policy, and redacted serializers are useful safe evidence."
                    ],
                },
                new(
                    "token-secret",
                    "token secret",
                    "Find token-secret contexts where credential material may be produced, stored, or redacted.",
                    ["audit", "security"],
                    "False positives include labels or documentation that do not reference runtime token values.",
                    ExactSubstring: false)
                {
                    RiskEvidence =
                    [
                        "risk: token secret paths often need source-of-truth, retention, and redaction review.",
                        "positive: secret providers, short-lived values, and sanitized diagnostics are safer evidence."
                    ],
                }
            ], SecretOriginClassifier, SourceOriginClassifier)),
        SourceScopedRecipe(
            "dogfood-risk-patterns",
            "Focused audit searches for recurring risk patterns found while dogfooding cdidx.",
            [
                new(
                    "exception-message-classifier",
                    ".Message.Contains",
                    "Find exception-message substring classifiers that may be brittle across runtimes, locales, and providers.",
                    ["audit", "bug"],
                    "False positives include test assertions and code that classifies already-normalized diagnostic codes.")
                {
                    RiskEvidence =
                    [
                        "risk: substring checks on exception messages can break across runtimes, localization, or provider versions.",
                        "positive: typed exception properties, error codes, or normalized diagnostic classifiers are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [DiagnosticRedactionClassifier],
                },
                DogfoodStaticRegexApiQuery(
                    "static-regex-api",
                    " Regex.",
                    "a whitespace prefix"),
                DogfoodStaticRegexApiQuery(
                    "static-regex-api-negated",
                    "!Regex.",
                    "a negation prefix"),
                DogfoodStaticRegexApiQuery(
                    "static-regex-api-parenthesized",
                    "(Regex.",
                    "an opening-parenthesis prefix"),
                new(
                    "relaxed-json-encoder",
                    "UnsafeRelaxedJsonEscaping",
                    "Find relaxed JSON encoder usage that may need HTML/script embedding and downstream consumer review.",
                    ["audit", "security"],
                    "False positives include payloads that are never embedded in HTML, script, logs, or browser-visible contexts.")
                {
                    RiskEvidence =
                    [
                        "risk: relaxed escaping can expose JSON to HTML/script or log-injection contexts if reused outside trusted boundaries.",
                        "positive: machine-only payloads with explicit content-type and no HTML/script embedding are lower risk."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "temp-file-name",
                    "GetTempFileName",
                    "Find deterministic or pre-created temporary file names that may need race, retention, and overwrite review.",
                    ["audit", "security"],
                    "False positives include isolated test fixtures and immediately-opened handles with exclusive access.")
                {
                    RiskEvidence =
                    [
                        "risk: deterministic or pre-created temp names can create race, retention, or stale-file overwrite hazards.",
                        "positive: random names opened atomically with exclusive access and cleanup policy are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "overwrite-file-move",
                    "File.Move",
                    "Find file moves that may overwrite or replace outputs without atomicity and destination policy review.",
                    ["audit", "bug"],
                    "False positives include test-only moves and callers that validate destination ownership, overwrite intent, and rollback behavior.")
                {
                    RiskEvidence =
                    [
                        "risk: overwrite moves can clobber user data or leave partial state without atomic replacement and rollback policy.",
                        "positive: explicit destination validation, backup/rollback, and same-volume atomic replace are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "suppressed-cleanup-diagnostics",
                    "catch (Exception",
                    "Find broad cleanup catches that may suppress diagnostics during best-effort cleanup.",
                    ["audit", "bug"],
                    "False positives include cleanup paths that intentionally log, aggregate, or surface suppressed failures.")
                {
                    RiskEvidence =
                    [
                        "risk: best-effort cleanup can hide root-cause failures when broad catches suppress diagnostics.",
                        "positive: logging, aggregation, retry policy, or explicit non-critical cleanup comments reduce filing priority."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [BroadCatchBoundaryClassifier, DiagnosticRedactionClassifier],
                },
                new(
                    "wall-clock-deadline",
                    "DateTime.UtcNow",
                    "Find wall-clock time used in deadline or duration logic that may need monotonic time review.",
                    ["audit", "bug"],
                    "False positives include timestamps used only for display, logging, serialization, or durable metadata.")
                {
                    RiskEvidence =
                    [
                        "risk: wall-clock time can move backward or jump across clock adjustments, breaking deadlines and durations.",
                        "positive: TimeProvider, Stopwatch, or monotonic clock helpers are safer evidence for elapsed-time logic."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "local-wall-clock-deadline",
                    "DateTime.Now",
                    "Find local wall-clock time used in deadline or duration logic that may need timezone and monotonicity review.",
                    ["audit", "bug"],
                    "False positives include display-only timestamps and UI formatting paths.")
                {
                    RiskEvidence =
                    [
                        "risk: local wall-clock time includes timezone and daylight-saving shifts in addition to clock jumps.",
                        "positive: display-only formatting or TimeProvider-backed elapsed-time logic is lower risk."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "max-value-sentinel",
                    "MaxValue",
                    "Find sentinel maximum limits that may hide unbounded allocation, traversal, or query behavior.",
                    ["audit", "bug"],
                    "False positives include pure constants, saturation helpers, and tests that do not feed allocation or traversal limits.")
                {
                    RiskEvidence =
                    [
                        "risk: MaxValue sentinels can bypass practical bounds for allocation, traversal, database pagination, JSON/output byte budgets, timeout, rate, or query limits.",
                        "positive: explicit clamping, paged query/output contracts, saturation helper names, or test-only probes are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "recipe-output-contract",
                    "SearchRecipe",
                    "Find search recipe output contract paths that may need schema, compact output, and issue-draft compatibility review.",
                    ["audit"],
                    "False positives include recipe metadata definitions and tests that intentionally assert contract behavior.")
                {
                    Severity = "low",
                    RiskEvidence =
                    [
                        "risk: recipe contract changes can break JSON, compact, issue-draft, or downstream automation consumers.",
                        "positive: source-generated JSON contracts and focused snapshot tests are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "raw-sql-command-text",
                    "CommandText",
                    "Find raw SQL command construction that may need parameterization and identifier interpolation review.",
                    ["audit", "security"],
                    "False positives include constant SQL text with parameterized values and trusted migration scripts.")
                {
                    RiskEvidence =
                    [
                        "risk: raw SQL command text can interpolate identifiers, table names, or values without parameterization.",
                        "positive: parameters for values and allowlisted identifier helpers are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "pragma-command",
                    "PRAGMA",
                    "Find SQLite PRAGMA usage that may need helper, transaction, and identifier policy review.",
                    ["audit", "security"],
                    "False positives include read-only PRAGMA probes with constant names and bounded diagnostics.")
                {
                    RiskEvidence =
                    [
                        "risk: PRAGMA helpers can bypass normal parameterization and alter connection or database-wide behavior.",
                        "positive: constant PRAGMA names, allowlisted values, and isolated connection setup are safer evidence."
                    ],
                },
                new(
                    "environment-variable-parser",
                    "GetEnvironmentVariable",
                    "Find environment-variable option parsing that may silently fall back instead of warning on invalid values.",
                    ["audit", "bug"],
                    "False positives include required variables that fail closed and callers that report parse diagnostics.")
                {
                    RiskEvidence =
                    [
                        "risk: silent fallback can hide misspelled or invalid environment options in automation.",
                        "positive: explicit warnings, parse diagnostics, or fail-closed behavior are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "process-launch-policy",
                    "ProcessLaunchPolicy",
                    "Find shared process launch policy wrappers used as positive evidence for subprocess trust boundaries.",
                    ["audit", "security"],
                    "Use this with process-start-info and process-start-direct to distinguish shared launch policy from ad hoc process setup.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: ProcessLaunchPolicy centralizes no-shell process configuration, argument-list setup, and redirected stream defaults.",
                        "risk: wrapper call sites still need review for executable path trust, working directory selection, timeout/cancellation, and diagnostics."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "subprocess-environment-policy",
                    "SubprocessEnvironmentPolicy",
                    "Find shared subprocess environment scrubbing used as positive evidence for inherited-environment boundaries.",
                    ["audit", "security"],
                    "Use this with process launch hits to verify whether subprocesses inherit only allowlisted environment variables.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: SubprocessEnvironmentPolicy makes inherited environment handling explicit for worker, git, and child CLI launches.",
                        "risk: launch sites without this evidence may inherit prompts, credentials, or tool-specific state unintentionally."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-start-info",
                    "ProcessStartInfo",
                    "Find external process launch configuration that may need argument, environment, cwd, and shell-use review.",
                    ["audit", "security"],
                    "False positives include tests and launch wrappers that already validate arguments, scrub environment variables, and disable shell expansion.")
                {
                    RiskEvidence =
                    [
                        "risk: launch sites need review for UseShellExecute, ArgumentList, WorkingDirectory, environment mutation, stdout/stderr drain, timeout, cancellation, and redacted diagnostics.",
                        "positive: ProcessLaunchPolicy, SubprocessEnvironmentPolicy, ArgumentList, UseShellExecute=false, redirected stream draining, and bounded WaitForExitAsync are useful guard evidence."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-start-direct",
                    "Process.Start",
                    "Find direct process launches that may need a shared safe-launch wrapper or explicit argument handling.",
                    ["audit", "security"],
                    "False positives include simple URL/document open helpers or test fixtures with trusted inputs.")
                {
                    RiskEvidence =
                    [
                        "risk: direct Process.Start calls can bypass no-shell defaults, argument-list construction, environment scrubbing, timeout, and output-drain policy.",
                        "positive: passing a ProcessStartInfo produced by a shared wrapper is safer than string command interpolation."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-argument-list",
                    "ArgumentList",
                    "Find process argument-list construction as positive evidence against shell or command-line interpolation.",
                    ["audit", "security"],
                    "Review whether every untrusted argument flows through ArgumentList rather than a shell-expanded command string.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: ArgumentList avoids shell parsing for individual arguments when UseShellExecute is false.",
                        "risk: argument-list evidence does not validate executable path trust, working directory, environment, or timeout behavior."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-shell-execute",
                    "UseShellExecute",
                    "Find shell-execution toggles that decide whether the platform shell participates in process launch.",
                    ["audit", "security"],
                    "False positives include assertions that verify UseShellExecute is false.")
                {
                    RiskEvidence =
                    [
                        "risk: UseShellExecute=true can reintroduce shell expansion, file association behavior, and inherited shell state.",
                        "positive: UseShellExecute=false with ArgumentList and redirected stream handling is preferred for subprocess boundaries."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-working-directory",
                    "WorkingDirectory",
                    "Find process working-directory choices that may cross workspace, plugin, or installer trust boundaries.",
                    ["audit", "security"],
                    "False positives include assertions over already-normalized temporary directories.")
                {
                    RiskEvidence =
                    [
                        "risk: cwd controls relative path resolution for child processes and can drift across plugin, installer, or test-helper boundaries.",
                        "positive: normalized workspace containment checks or explicit trusted system directories reduce risk."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-redirect-output",
                    "RedirectStandardOutput",
                    "Find stdout redirection choices that should be paired with bounded draining and cancellation.",
                    ["audit", "bug"],
                    "False positives include tests that assert process-launch defaults.")
                {
                    RiskEvidence =
                    [
                        "risk: redirected stdout must be drained without unbounded buffering or deadlocking the child process.",
                        "positive: bounded readers, concurrent stderr draining, cancellation, and timeout handling are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-redirect-error",
                    "RedirectStandardError",
                    "Find stderr redirection choices that should be paired with bounded draining and sanitized diagnostics.",
                    ["audit", "bug"],
                    "False positives include tests that assert process-launch defaults.")
                {
                    RiskEvidence =
                    [
                        "risk: redirected stderr can deadlock or leak command diagnostics if it is not drained and sanitized deliberately.",
                        "positive: bounded concurrent stderr draining plus CommandErrorWriter or DiagnosticSanitizer evidence lowers risk."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-wait-for-exit",
                    "WaitForExit",
                    "Find process waits that may need timeout, cancellation, and output-drain review.",
                    ["audit", "bug"],
                    "False positives include bounded WaitForExitAsync calls with caller cancellation and explicit timeout handling.")
                {
                    RiskEvidence =
                    [
                        "risk: process waits can hang indefinitely or race output draining when cancellation and timeout policy are unclear.",
                        "positive: WaitForExitAsync with a caller token, bounded timeout, and post-drain handling is safer evidence."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "process-kill",
                    "Kill(",
                    "Find child-process termination paths that may need process-tree and cleanup review.",
                    ["audit", "bug"],
                    "False positives include tests that intentionally exercise termination behavior.")
                {
                    RiskEvidence =
                    [
                        "risk: child termination can leave process trees, temp files, or partial diagnostics behind without explicit cleanup policy.",
                        "positive: bounded timeout branches, kill-entire-tree intent, and cleanup diagnostics make termination behavior easier to audit."
                    ],
                    MatchOrigins = ["code"],
                    Classifiers = [ProcessLaunchClassifier],
                },
                new(
                    "current-directory-boundary",
                    "Environment.CurrentDirectory",
                    "Find current-directory dependencies that may affect command, plugin, or embedded-host boundaries.",
                    ["audit", "bug"],
                    "False positives include tests that intentionally assert cwd behavior.")
                {
                    RiskEvidence =
                    [
                        "risk: relying on process cwd can make command behavior depend on host launch context or plugin side effects.",
                        "positive: explicit project-root resolution and cwd drift diagnostics reduce ambiguity."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "set-current-directory",
                    "SetCurrentDirectory",
                    "Find process cwd mutations that may need restore, isolation, and concurrency review.",
                    ["audit", "bug"],
                    "False positives include isolated tests with try/finally restoration.")
                {
                    RiskEvidence =
                    [
                        "risk: mutating process cwd is process-wide and can race other command, plugin, or test-helper work.",
                        "positive: try/finally restoration, serial test isolation, or avoiding cwd mutation altogether lowers risk."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "plugin-activator",
                    "Activator.CreateInstance",
                    "Find plugin constructor paths that may need constructor side-effect and lifecycle review.",
                    ["audit", "bug"],
                    "False positives include trusted test fixtures and tightly controlled type allowlists.")
                {
                    RiskEvidence =
                    [
                        "risk: reflective construction can run plugin constructors with unexpected side effects or missing lifecycle hooks.",
                        "positive: allowlisted types, explicit constructor contracts, and disposal/lifecycle handling are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "plugin-term",
                    "plugin",
                    "Find broad plugin terminology that helps catch tokenization gaps around concrete plugin type names.",
                    ["audit", "security"],
                    "This is an intentionally broad discovery query; triage with match origins, file paths, and nearby concrete type names.")
                {
                    Severity = "low",
                    RiskEvidence =
                    [
                        "risk: plugin discovery, trust gates, constructors, and load contexts execute extension code and need explicit boundaries.",
                        "positive: broad plugin hits make CamelCase or concrete-type naming gaps visible during dogfood audits."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "hook-term",
                    "hook",
                    "Find broad hook terminology that helps catch tokenization gaps around post-extraction and git hook surfaces.",
                    ["audit", "security"],
                    "This is an intentionally broad discovery query; triage with match origins, hook type names, and callback budget evidence.")
                {
                    Severity = "low",
                    RiskEvidence =
                    [
                        "risk: hooks can execute extension or git automation code and need clear trust, timeout, and diagnostics boundaries.",
                        "positive: broad hook hits make CamelCase or concrete-type naming gaps visible during dogfood audits."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "trust-overrides-contract",
                    "trust_overrides",
                    "Find machine-readable trust override output contracts for plugin and hook trust-boundary review.",
                    ["audit", "security"],
                    "False positives include tests that only assert stable JSON field names.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "risk: trust override output must identify the opt-in surface without leaking raw local sensitive paths or secret-like values.",
                        "positive: sanitized value, sanitized effective_path, environment variable, and reason fields make trust decisions auditable."
                    ],
                },
                new(
                    "assembly-load-context",
                    "AssemblyLoadContext",
                    "Find plugin assembly load contexts that may need unloadability, retention, and dependency isolation review.",
                    ["audit", "bug"],
                    "False positives include tests that intentionally exercise load-context retention behavior.")
                {
                    RiskEvidence =
                    [
                        "risk: retained AssemblyLoadContext references can prevent plugin unload or cross-plugin dependency isolation.",
                        "positive: collectible contexts, weak-reference unload checks, and explicit disposal are safer evidence."
                    ],
                    MatchOrigins = ["code"],
                }
            ]),
        SourceScopedRecipe(
            "timestamp-timezone-boundaries",
            "Audit timestamp and elapsed-time boundaries so persisted database values, filesystem stamps, process clocks, network/API metadata, support JSON, cache expiry, human display, and monotonic timing keep explicit UTC or offset semantics.",
            [
                TimestampBoundaryQuery(
                    "datetime-now-local-wall-clock",
                    "DateTime.Now",
                    "Find local wall-clock timestamps that can drift across timezones, daylight-saving transitions, or host settings.",
                    "False positives include display-only formatting paths that never feed persistence, cache expiry, support JSON, or elapsed-time decisions.",
                    "risk: DateTime.Now is local wall-clock time and should not feed persisted database stamps, freshness comparisons, retry scheduling, or machine-facing JSON without an explicit conversion boundary."),
                TimestampBoundaryQuery(
                    "datetime-utcnow-wall-clock",
                    "DateTime.UtcNow",
                    "Find UTC wall-clock timestamps that still need classification apart from monotonic elapsed-time measurement.",
                    "False positives include durable audit metadata and support JSON fields that are intentionally UTC and covered by tests.",
                    "risk: DateTime.UtcNow is wall-clock time; use it for timestamps, not elapsed-time measurement or timeout deadlines."),
                TimestampBoundaryQuery(
                    "datetimeoffset-utcnow-offset-clock",
                    "DateTimeOffset.UtcNow",
                    "Find offset-aware UTC timestamps used for persistence, API metadata, or support JSON.",
                    "False positives include fields that already serialize as UTC or explicit offsets with round-trip tests.",
                    "positive: DateTimeOffset.UtcNow is a good boundary type when the value crosses JSON, API, or cache contracts that need an explicit offset."),
                TimestampBoundaryQuery(
                    "timeprovider-utcnow-injected-clock",
                    "GetUtcNow",
                    "Find injectable UTC clock boundaries used for deterministic tests, cache expiry, or process diagnostics.",
                    "False positives include wrappers that already normalize the returned timestamp before persistence or JSON output.",
                    "positive: TimeProvider.GetUtcNow keeps wall-clock timestamps testable, but callers still need UTC/offset and monotonic-vs-wall-clock classification."),
                TimestampBoundaryQuery(
                    "datetime-kind-contract",
                    "DateTimeKind",
                    "Find DateTime kind checks and contracts that decide whether values are UTC, local, or offsetless.",
                    "False positives include tests that intentionally construct all DateTimeKind variants.",
                    "risk: DateTimeKind.Unspecified needs an explicit boundary contract; it must not silently inherit the host local timezone in persisted or support JSON paths."),
                TimestampBoundaryQuery(
                    "specifykind-utc-relabel",
                    "DateTime.SpecifyKind",
                    "Find timestamp relabeling sites that may need conversion rather than kind replacement.",
                    "False positives include legacy offsetless values that are explicitly documented and tested as UTC.",
                    "risk: DateTime.SpecifyKind can relabel local wall-clock values as UTC without conversion; verify the source value is truly offsetless UTC before accepting it."),
                TimestampBoundaryQuery(
                    "to-universal-time-conversion",
                    "ToUniversalTime",
                    "Find local-to-UTC conversion boundaries used before persistence, comparison, or serialization.",
                    "False positives include conversions immediately before human-only formatting or tests that assert local-time behavior.",
                    "risk: ToUniversalTime is correct for local instants but can mis-handle offsetless values unless the unspecified-kind contract is explicit."),
                TimestampBoundaryQuery(
                    "roundtrip-timestamp-format",
                    "ToString(\"O\"",
                    "Find round-trip timestamp formatting for persisted metadata, support JSON-adjacent diagnostics, or API detail strings.",
                    "False positives include tests and non-timestamp string formatting fixtures.",
                    "positive: the O format is appropriate for machine timestamps when the input kind or offset has already been normalized."),
                TimestampBoundaryQuery(
                    "datetime-tryparse-boundary",
                    "DateTime.TryParse",
                    "Find DateTime parsers that need invariant culture, UTC/offset assumptions, and local-time drift tests.",
                    "False positives include parsers whose input is human-entered display text and remains human-facing.",
                    "risk: DateTime.TryParse defaults can infer local time; machine timestamp parsers should specify invariant culture and UTC or offset behavior."),
                TimestampBoundaryQuery(
                    "datetimeoffset-tryparse-boundary",
                    "DateTimeOffset.TryParse",
                    "Find offset-aware timestamp parsers for persisted metadata, API values, and support diagnostics.",
                    "False positives include tests that only validate rejected malformed timestamps.",
                    "positive: DateTimeOffset.TryParse can preserve explicit offsets, but offsetless inputs still need a documented UTC or local-time assumption."),
                TimestampBoundaryQuery(
                    "unix-epoch-timestamp",
                    "FromUnixTimeSeconds",
                    "Find Unix epoch conversions that define network/API or cache reset timestamp boundaries.",
                    "False positives include examples and tests that do not feed retry or freshness decisions.",
                    "positive: Unix epoch conversion is UTC by contract, but downstream comparisons should still stay UTC or offset-aware."),
                TimestampBoundaryQuery(
                    "stopwatch-monotonic-elapsed",
                    "Stopwatch",
                    "Find monotonic elapsed-time measurement sites to separate duration logic from wall-clock timestamps.",
                    "False positives include diagnostics that only report elapsed durations and do not compare against timestamps.",
                    "positive: Stopwatch is the expected primitive for elapsed-time measurement, timeout diagnostics, and performance durations."),
                TimestampBoundaryQuery(
                    "timeout-duration-boundary",
                    "Timeout",
                    "Find timeout and retry-delay boundaries that should be duration-based rather than wall-clock timestamp comparisons.",
                    "False positives include constant names or diagnostics that do not schedule, compare, or enforce durations.",
                    "risk: timeout paths should compare duration budgets or monotonic elapsed time, not mixed local and UTC wall-clock timestamps.")
            ]),
        SourceScopedRecipe(
            "sqlite-query-policy-surfaces",
            "Audit SQLite raw SQL, PRAGMA, schema, transaction, metadata, and read-only compatibility surfaces under the shared command/query policy.",
            [
                new(
                    "sqlite-policy-command-text",
                    "CommandText",
                    "Find SQLite command text construction that may need parameterization, identifier quoting, timeout, and cancellation review.",
                    ["audit", "security"],
                    "False positives include constant SQL text with all values bound through SqliteCommandPolicy helpers.")
                {
                    RiskEvidence =
                    [
                        "risk: raw CommandText can mix trusted SQL, dynamic identifiers, and user values without a single policy checkpoint.",
                        "positive: typed SqliteCommandPolicy parameters and SqliteIdentifier quoting are safer evidence."
                    ],
                },
                new(
                    "sqlite-policy-create-command",
                    "CreateCommand",
                    "Find SQLite command creation sites that may need the shared command policy before executing SQL.",
                    ["audit", "security"],
                    "False positives include isolated tests or commands immediately populated by a shared policy helper.")
                {
                    RiskEvidence =
                    [
                        "risk: ad hoc commands can skip typed parameters, timeout setup, cancellation boundaries, or read-only checks.",
                        "positive: command wrappers that centralize timeout, typed parameter, and SQL text policy reduce the risk."
                    ],
                },
                new(
                    "sqlite-policy-execute-reader",
                    "ExecuteReader",
                    "Find SQLite reader execution surfaces that may need timeout, cancellation, and result-bounding review.",
                    ["audit", "bug"],
                    "False positives include bounded internal queries and reader loops with explicit cancellation or row limits."),
                new(
                    "sqlite-policy-execute-non-query",
                    "ExecuteNonQuery",
                    "Find SQLite mutation execution surfaces that may need transaction, timeout, cancellation, and read-only compatibility review.",
                    ["audit", "security"],
                    "False positives include schema setup or maintenance operations already guarded by mode checks and shared SQL helpers."),
                new(
                    "sqlite-policy-execute-scalar",
                    "ExecuteScalar",
                    "Find SQLite scalar execution surfaces that may need type conversion, timeout, and PRAGMA compatibility review.",
                    ["audit", "bug"],
                    "False positives include constant bounded probes whose result conversion is centralized."),
                new(
                    "sqlite-policy-add-with-value",
                    "AddWithValue",
                    "Find SQLite parameter binding that may need explicit type or size review instead of provider inference.",
                    ["audit", "bug"],
                    "False positives include tests and intentionally unconstrained values that cannot affect SQL shape."),
                new(
                    "sqlite-policy-shared-create-command",
                    "SqliteConnectionPolicy.CreateCommand",
                    "Find SQLite command creation routed through the shared connection policy helper so audits can separate policy-compliant command construction from raw CreateCommand sites.",
                    ["audit", "security"],
                    "Expected safe hits create commands through SqliteConnectionPolicy; still verify the subsequent SQL text and parameters.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: SqliteConnectionPolicy.CreateCommand centralizes command timeout and connection policy for SQLite commands.",
                        "review: surrounding CommandText still needs value parameterization, bounded dynamic identifiers, and cancellation review."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "sqlite-policy-typed-parameter",
                    "SqliteCommandPolicy.Add",
                    "Find SQLite command paths using typed parameter helpers so audits can separate explicit binding from provider-inferred AddWithValue usage.",
                    ["audit", "bug"],
                    "Expected safe hits use AddText/AddInt64/AddDouble/AddBlob/AddLimit/AddOffset-style helpers; still verify parameter values are bounded for their query shape.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: SqliteCommandPolicy typed helpers avoid AddWithValue provider inference and keep parameter binding auditable.",
                        "review: ensure every data value in nearby SQL is bound through a typed helper rather than interpolated into CommandText."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "sqlite-policy-identifier-quoting",
                    "SqliteIdentifier.Quote",
                    "Find dynamic SQLite identifier construction that uses shared identifier quoting so audits can separate quoted identifiers from value interpolation.",
                    ["audit", "security"],
                    "Expected safe hits quote table, column, index, or PRAGMA identifiers; still verify user data remains parameterized.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: SqliteIdentifier.Quote constrains dynamic SQL identifier interpolation to quoted SQLite identifiers.",
                        "review: quoted identifiers are not value parameters; nearby user values should still use typed SqliteCommandPolicy parameters."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "sqlite-policy-pragma-helper",
                    "DbPragmaPolicy.",
                    "Find SQLite PRAGMA construction routed through the shared allowlisted PRAGMA helper policy.",
                    ["audit", "security"],
                    "Expected safe hits use DbPragmaPolicy constants or bounded builders for PRAGMA statements.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: DbPragmaPolicy keeps PRAGMA names and values allowlisted or bounded where SQLite cannot use ordinary parameters.",
                        "review: raw PRAGMA CommandText outside the helper remains a higher-risk surface."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "sqlite-policy-pragma",
                    "PRAGMA",
                    "Find SQLite PRAGMA surfaces that may need allowlisted names, bounded values, and read-only fallback review.",
                    ["audit", "security"],
                    "False positives include constant read-only PRAGMA probes and calls routed through DbPragmaPolicy or SqliteCommandPolicy helpers.")
                {
                    RiskEvidence =
                    [
                        "risk: PRAGMA syntax is commonly string-built because SQLite cannot bind every pragma value like a normal parameter.",
                        "positive: allowlisted pragma names, bounded numeric builders, and fixed values are safer evidence."
                    ],
                },
                new(
                    "sqlite-policy-create-table",
                    "CREATE TABLE",
                    "Find SQLite table DDL that may need schema compatibility, identifier, and migration review.",
                    ["audit", "security"],
                    "False positives include static schema statements covered by migration tests."),
                new(
                    "sqlite-policy-alter-table",
                    "ALTER TABLE",
                    "Find SQLite schema evolution statements that may need legacy database compatibility review.",
                    ["audit", "bug"],
                    "False positives include static migrations guarded by schema-version checks."),
                new(
                    "sqlite-policy-create-index",
                    "CREATE INDEX",
                    "Find SQLite index DDL that may need identifier quoting, uniqueness, and migration compatibility review.",
                    ["audit", "bug"],
                    "False positives include static index definitions covered by migration tests."),
                new(
                    "sqlite-policy-drop-table",
                    "DROP TABLE",
                    "Find SQLite destructive DDL that may need transaction and legacy compatibility review.",
                    ["audit", "security"],
                    "False positives include temporary-table cleanup in isolated maintenance paths."),
                new(
                    "sqlite-policy-delete-from",
                    "DELETE FROM",
                    "Find SQLite delete statements that may need parameterization, transaction, and read-only mode review.",
                    ["audit", "security"],
                    "False positives include bounded maintenance cleanup with typed parameters and explicit mode checks."),
                new(
                    "sqlite-policy-begin-transaction",
                    "BeginTransaction",
                    "Find SQLite transaction boundaries that may need isolation, busy timeout, WAL, and cancellation review.",
                    ["audit", "bug"],
                    "False positives include short-lived schema transactions covered by rollback tests."),
                new(
                    "sqlite-policy-codeindex-meta",
                    "codeindex_meta",
                    "Find SQLite metadata stamping and reads that may need migration, downgrade, and read-only compatibility review.",
                    ["audit", "bug"],
                    "False positives include constant metadata keys covered by schema compatibility tests."),
                new(
                    "sqlite-policy-user-version",
                    "user_version",
                    "Find SQLite user_version reads and writes that may need migration and read-only fallback review.",
                    ["audit", "bug"],
                    "False positives include constant version probes with explicit writable-mode checks."),
                new(
                    "sqlite-policy-check-constraint",
                    "CHECK (",
                    "Find SQLite CHECK constraints that may encode enum or state allowlists needing compatibility review.",
                    ["audit", "bug"],
                    "False positives include static constraints whose allowed values are covered by round-trip tests."),
                new(
                    "sqlite-policy-immutable-uri",
                    "immutable=1",
                    "Find SQLite immutable read-only URI handling that may need WAL, side-file, and migration compatibility review.",
                    ["audit", "bug"],
                    "False positives include diagnostic text and tests that only assert the documented fallback path."),
                new(
                    "sqlite-policy-read-only",
                    "read-only",
                    "Find SQLite read-only fallback text and control flow that may need write avoidance and compatibility review.",
                    ["audit", "bug"],
                    "False positives include user-facing help text and tests that intentionally exercise read-only failures."),
                new(
                    "sqlite-policy-migration",
                    "Migration",
                    "Find SQLite migration code paths that may need legacy schema, metadata, and downgrade compatibility review.",
                    ["audit", "bug"],
                    "False positives include stable diagnostic constants and tests that only assert migration messages."),
                new(
                    "sqlite-policy-maintenance-progress",
                    "ReportMaintenanceProgress",
                    "Find SQLite maintenance status surfaces that may need PRAGMA, transaction, and read-only compatibility review.",
                    ["audit", "bug"],
                    "False positives include progress-only emission that does not affect database state.")
            ],
            DefaultExecutableExcludeOriginsValue),
        SourceScopedRecipe(
            "json-parse-apis",
            "Audit JSON parse and deserialize API families that may need payload bounds, streaming, or serializer-option review.",
            AddClassifiers([
                new(
                    "json-document-parse",
                    "JsonDocument.Parse",
                    "Find DOM parsing via JsonDocument.Parse that may need input-size limits or streaming alternatives.",
                    ["audit", "bug"],
                    "False positives include deliberately bounded callers and parsing of already-small generated payloads.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: JsonDocument.Parse builds a full DOM and should show byte, depth, and item-count limits before user-controlled payloads reach it.",
                        "positive: BoundedJson.ParseDocument or a size-gated structured-data fallback is upstream guard evidence for intentional DOM parsing."
                    ],
                },
                new(
                    "json-node-parse",
                    "JsonNode.Parse",
                    "Find mutable DOM parsing via JsonNode.Parse that may need input-size limits, depth limits, or streaming alternatives.",
                    ["audit", "bug"],
                    "False positives include tests, bounded configuration files, and already-size-limited payloads.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: JsonNode.Parse materializes a mutable DOM and should be paired with payload and depth bounds for API, config, or protocol inputs.",
                        "positive: BoundedJson.ParseNode, bounded frame readers, or fixed-size local metadata files make the materialization auditable."
                    ],
                },
                new(
                    "json-serializer-deserialize",
                    "JsonSerializer.Deserialize",
                    "Find serializer materialization paths that may need payload bounds, streaming, or explicit JsonSerializerOptions review.",
                    ["audit", "bug"],
                    "False positives include bounded local files, test fixtures, and deserialization of tiny protocol envelopes.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: JsonSerializer.Deserialize can materialize an entire object graph before callers enforce semantic item limits.",
                        "positive: BoundedJson.Deserialize, MaxDepth options, and fixed protocol frame byte caps show upstream parse bounds."
                    ],
                },
                new(
                    "json-async-deserialize",
                    "DeserializeAsyncEnumerable",
                    "Find streaming JSON deserialization paths that may need cancellation, item limits, or backpressure review.",
                    ["audit", "performance"],
                    "False positives include already-cancelable readers with explicit item budgets.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: streaming deserialization still needs cancellation, per-item limits, and a bounded source stream.",
                        "positive: WithCancellation, explicit record caps, and max-byte snapshot reads show streaming backpressure evidence."
                    ],
                },
                new(
                    "json-serializer-options",
                    "JsonSerializerOptions",
                    "Find serializer option construction and reuse sites that should show MaxDepth, naming, encoder, and case-insensitive property rationale.",
                    ["audit", "bug"],
                    "False positives include generated contexts and shared option declarations whose callers already enforce byte and item limits.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: inconsistent JsonSerializerOptions can silently change depth, case sensitivity, encoder, or naming behavior across CLI, MCP, and local-state payloads.",
                        "positive: shared option instances with explicit MaxDepth, encoder scope, and documented case-insensitive token domains reduce parser triage risk."
                    ],
                },
                new(
                    "json-case-insensitive-properties",
                    "PropertyNameCaseInsensitive",
                    "Find case-insensitive JSON property handling that should be justified by a compatibility or protocol boundary.",
                    ["audit", "bug"],
                    "False positives include compatibility aliases and user-authored config formats where case-insensitive names are intentional.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: case-insensitive JSON properties can hide duplicate-key or compatibility drift when the payload contract is machine-authored.",
                        "positive: explicit compatibility alias lifecycle, user-authored config rationale, or tests for duplicate/canonical names make the setting intentional."
                    ],
                },
                new(
                    "json-serializer-serialize",
                    "JsonSerializer.Serialize",
                    "Find JSON serialization sites that may need output-size, streaming, or redaction review.",
                    ["audit", "performance"],
                    "False positives include tiny protocol envelopes, bounded diagnostics, and test fixtures.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: JsonSerializer.Serialize can materialize unbounded output when result sets scale with workspace size.",
                        "positive: bounded result limits, Utf8JsonWriter streaming, output caps, or small fixed DTOs make serialization size explicit."
                    ],
                },
                new(
                    "utf8-json-writer",
                    "Utf8JsonWriter",
                    "Find streaming JSON writers so audits can verify flush, destination ownership, and output-size policy.",
                    ["audit", "performance"],
                    "False positives include bounded local writers and one-shot small diagnostics.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: streaming writers still need bounded destinations, cancellation or flush ownership, and redaction policy at user-facing boundaries.",
                        "positive: writing directly to a caller-owned stream, LocalJsonlJsonWriterOptions, or fixed-size diagnostic payloads can explain the writer."
                    ],
                }
            ], ParserGuardClassifier, GuardEvidenceClassifier)),
        SourceScopedRecipe(
            "text-encoding-boundaries",
            "Audit text encoding, BOM detection, stream reader/writer ownership, and Unicode normalization boundaries.",
            [
                new(
                    "utf8-encoding-boundary",
                    "Encoding.UTF8",
                    "Find UTF-8 encoding boundaries so generated JSON, NDJSON, SARIF, ctags, reports, and protocol outputs can confirm stable UTF-8 behavior.",
                    ["audit", "bug"],
                    "False positives include constants and tests; prioritize file, stream, process, and protocol boundaries.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: implicit or inconsistent text encodings can corrupt generated artifacts or hide replacement-character behavior across platforms.",
                        "positive: explicit UTF-8 policy, shared JsonWriterOptions, or boundary tests for invalid bytes and replacement characters make the site auditable."
                    ],
                },
                new(
                    "utf8-encoding-constructor",
                    "UTF8Encoding",
                    "Find custom UTF8Encoding construction so BOM emission and invalid-byte fallback behavior are explicit.",
                    ["audit", "bug"],
                    "False positives include fixture encodings and tests that intentionally vary fallback or BOM settings.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: UTF8Encoding constructor flags control BOM emission and throw-on-invalid-byte behavior, which can drift between readers and generated outputs.",
                        "positive: explicit encoderShouldEmitUTF8Identifier and throwOnInvalidBytes arguments with tests make the contract clear."
                    ],
                },
                new(
                    "stream-reader-bom-policy",
                    "detectEncodingFromByteOrderMarks",
                    "Find StreamReader BOM-detection policy so input boundaries can distinguish fixture compatibility from stable UTF-8 contracts.",
                    ["audit", "bug"],
                    "False positives include tests and local compatibility probes.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: BOM auto-detection can make input behavior differ from generated-output UTF-8 contracts unless the boundary is intentional.",
                        "positive: named compatibility readers, fixture tests, or explicit UTF-8-only readers make the boundary easier to classify."
                    ],
                },
                new(
                    "stream-reader-encoding-boundary",
                    "StreamReader",
                    "Find StreamReader boundaries that should show encoding, BOM detection, leave-open ownership, cancellation, and max-character behavior.",
                    ["audit", "performance"],
                    "False positives include tiny trusted test helpers and fixed in-memory protocol snippets.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: StreamReader can hide implicit encoding choices, replacement fallback, and ownership transfer of the underlying stream.",
                        "positive: explicit encoding, detectEncodingFromByteOrderMarks choice, leaveOpen intent, and bounded line/byte readers make the boundary auditable."
                    ],
                },
                new(
                    "stream-writer-encoding-boundary",
                    "StreamWriter",
                    "Find StreamWriter boundaries that should show UTF-8/no-BOM policy, flush behavior, leave-open ownership, and output-size behavior.",
                    ["audit", "performance"],
                    "False positives include fixed small local files and test fixtures.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: StreamWriter can emit platform- or constructor-dependent encodings and close caller-owned streams unexpectedly.",
                        "positive: explicit UTF-8/no-BOM choices, using scopes, leaveOpen intent, and bounded DTO/result emission explain safe writer use."
                    ],
                },
                new(
                    "default-encoding-boundary",
                    "Encoding.Default",
                    "Find platform-default encoding usage that should usually be replaced with an explicit boundary encoding.",
                    ["audit", "portability"],
                    "False positives include compatibility shims that intentionally mirror a legacy platform default.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Encoding.Default varies by runtime and platform, making generated or parsed text non-reproducible.",
                        "positive: legacy compatibility wrappers should document the source format and keep generated outputs on explicit UTF-8."
                    ],
                },
                new(
                    "code-page-encoding-boundary",
                    "Encoding.GetEncoding",
                    "Find code-page lookup sites so non-UTF-8 compatibility boundaries stay isolated from generated output contracts.",
                    ["audit", "portability"],
                    "False positives include tests and legacy importers with explicit source-format coverage.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: code-page lookup can introduce platform registration requirements and inconsistent fallback behavior.",
                        "positive: isolated import paths, EncodingProvider setup, and tests for invalid bytes reduce portability risk."
                    ],
                },
                new(
                    "unicode-normalization-boundary",
                    "NormalizationForm",
                    "Find Unicode normalization decisions that should be tied to path, identifier, or user-text semantics.",
                    ["audit", "bug", "portability"],
                    "False positives include tests and shared normalization helpers whose domain is already documented.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: normalization can change identifier, path, or human-text equality semantics if applied outside its intended domain.",
                        "positive: domain-specific helpers and tests for composed/decomposed forms make normalization intent auditable."
                    ],
                }
            ]),
        SourceScopedRecipe(
            "dotnet-risk-patterns",
            "Audit common .NET reliability and security patterns that regularly need manual review.",
            [
                new(
                    "sqlite-addwithvalue",
                    "AddWithValue",
                    "Find SQLite parameter binding that may need explicit DbType or size review instead of AddWithValue inference.",
                    ["audit", "bug"],
                    "False positives include test-only SQL snippets and values whose inferred SQLite type is intentionally unconstrained."),
                new(
                    "sqlite-quoted-identifier",
                    "SqliteIdentifier.Quote",
                    "Find dynamic SQLite command construction that uses shared identifier quoting so audits can separate identifier interpolation from value interpolation.",
                    ["audit", "security"],
                    "Expected safe hits quote table, column, index, or pragma identifiers; still verify user values are parameterized."),
                new(
                    "sqlite-typed-parameter",
                    "SqliteCommandPolicy.Add",
                    "Find SQLite command paths using the shared typed parameter helpers instead of AddWithValue inference.",
                    ["audit", "bug"],
                    "False positives include helper declarations; callers should prefer AddText/AddInt64/AddLimit/AddOffset wrappers for concrete value types."),
                new(
                    "regex-construction",
                    "new Regex(",
                    "Find direct regex construction that may need a timeout, non-backtracking mode, or bounded input review.",
                    ["audit", "performance"],
                    "False positives include precompiled bounded patterns with explicit timeouts or tiny trusted inputs.")
                {
                    RejectFileQueries =
                    [
                        "using Regex = CodeIndex.Indexer.BoundedRegex"
                    ],
                    ExcludePaths = [BoundedRegexPath, RegexRegistryPath],
                    RiskEvidence =
                    [
                        "risk: raw System.Text.RegularExpressions.Regex construction should show an explicit timeout, non-backtracking mode, or bounded input.",
                        "risk: classify each pattern by trust boundary: user input, config/env input, repository-controlled patterns, test fixtures, or generated diagnostics.",
                        "positive: bounded-wrapper aliases are reported by bounded-regex-alias instead of this raw construction query.",
                        "positive: shared regex factories in RegexRegistry.cs are the centralized raw-construction exception."
                    ],
                    GuardFilters = BoundedRegexEvidenceGuardFilters(),
                },
                new(
                    "bounded-regex-alias",
                    "using Regex = CodeIndex.Indexer.BoundedRegex",
                    "Find files where `new Regex(...)` is backed by the repository bounded regex wrapper alias.",
                    ["audit", "performance"],
                    "This is positive evidence for regex-construction hits; still review whether the bounded wrapper receives trusted patterns and inputs.",
                    ExactSubstring: true)
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: the Regex identifier aliases CodeIndex.Indexer.BoundedRegex, separating wrapper construction from raw BCL Regex.",
                        "risk: alias evidence does not prove every regex input is small; check the matching construction site when the same file also appears in regex-construction."
                    ],
                },
                new(
                    "fully-qualified-regex-construction",
                    "new System.Text.RegularExpressions.Regex",
                    "Find fully qualified raw BCL regex construction that bypasses a bounded wrapper alias.",
                    ["audit", "performance"],
                    "False positives include tests and code that supplies explicit timeouts or RegexOptions.NonBacktracking.",
                    ExactSubstring: true)
                {
                    RiskEvidence =
                    [
                        "risk: fully qualified BCL Regex construction bypasses local aliases and should carry timeout/non-backtracking evidence.",
                        "positive: explicit timeout arguments or RegexOptions.NonBacktracking can make the construction bounded."
                    ],
                    GuardFilters = BoundedRegexEvidenceGuardFilters(),
                },
                RegexTimeoutPolicyReferenceQuery(),
                RegexTimeoutTimespanEvidenceQuery(),
                RegexRegistryFactoryQuery(),
                GeneratedRegexAttributeQuery(),
                RegexCultureInvariantOptionQuery(),
                RegexNonBacktrackingOptionQuery(),
                RegexInfiniteTimeoutJustificationQuery(),
                StaticRegexApiQuery(
                    "static-regex-is-match",
                    " Regex.IsMatch(",
                    "IsMatch"),
                StaticRegexApiQuery(
                    "static-regex-is-match-negated",
                    "!Regex.IsMatch(",
                    "IsMatch"),
                StaticRegexApiQuery(
                    "static-regex-is-match-parenthesized",
                    "(Regex.IsMatch(",
                    "IsMatch"),
                StaticRegexApiQuery(
                    "static-regex-match",
                    " Regex.Match(",
                    "Match"),
                StaticRegexApiQuery(
                    "static-regex-match-negated",
                    "!Regex.Match(",
                    "Match"),
                StaticRegexApiQuery(
                    "static-regex-match-parenthesized",
                    "(Regex.Match(",
                    "Match"),
                StaticRegexApiQuery(
                    "static-regex-matches",
                    " Regex.Matches(",
                    "Matches"),
                StaticRegexApiQuery(
                    "static-regex-matches-negated",
                    "!Regex.Matches(",
                    "Matches"),
                StaticRegexApiQuery(
                    "static-regex-matches-parenthesized",
                    "(Regex.Matches(",
                    "Matches"),
                StaticRegexApiQuery(
                    "static-regex-replace",
                    " Regex.Replace(",
                    "Replace"),
                StaticRegexApiQuery(
                    "static-regex-replace-negated",
                    "!Regex.Replace(",
                    "Replace"),
                StaticRegexApiQuery(
                    "static-regex-replace-parenthesized",
                    "(Regex.Replace(",
                    "Replace"),
                StaticRegexApiQuery(
                    "static-regex-split",
                    " Regex.Split(",
                    "Split"),
                StaticRegexApiQuery(
                    "static-regex-split-negated",
                    "!Regex.Split(",
                    "Split"),
                StaticRegexApiQuery(
                    "static-regex-split-parenthesized",
                    "(Regex.Split(",
                    "Split"),
                new(
                    "cancellation-token-none",
                    "CancellationToken.None",
                    "Find production paths that ignore caller cancellation and may need a propagated token.",
                    ["audit", "bug"],
                    "False positives include intentionally detached background work and APIs without a meaningful caller token."),
                new(
                    "cancellation-token-source",
                    "CancellationTokenSource",
                    "Find cancellation-token source ownership boundaries that may need linked-token, timeout, or disposal review.",
                    ["audit", "bug"],
                    "False positives include small local using scopes and tests; prioritize command, MCP, LSP, worker, HTTP, and database lifetimes.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: CancellationTokenSource ownership can leak timers, registrations, or shutdown signals when disposal and cancellation ordering are unclear.",
                        "positive: using/await using scopes, linked-token ownership comments, and explicit timeout budgets make source lifetime intentional."
                    ],
                },
                new(
                    "cancellation-registration",
                    "Register(",
                    "Find registration callbacks that may need cancellation-token disposal, lock-free callback bodies, or teardown ordering review.",
                    ["audit", "bug"],
                    "False positives include DI/service registration and event registration; prioritize CancellationToken.Register and callbacks that touch shared state.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: CancellationToken.Register callbacks can run during shutdown and should not hold locks, call user/plugin code, or outlive the owning operation.",
                        "positive: disposing the registration, isolating callback state, or using static callbacks with bounded work reduces cancellation-lifetime risk."
                    ],
                },
                new(
                    "task-run-scheduling",
                    "Task.Run",
                    "Find background scheduling boundaries where cancellation, ExecutionContext flow, exception observation, and finally-block ownership need review.",
                    ["audit", "bug"],
                    "False positives include tiny CPU offloads and tests; prioritize command, MCP, worker, index, and transport scheduling.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Task.Run can detach work from caller cancellation and hide exceptions unless the returned task is observed or deliberately owned.",
                        "positive: captured cancellation tokens, observed tasks, explicit background-task observers, and documented fire-and-forget ownership make scheduling intentional."
                    ],
                },
                new(
                    "task-delay-backoff",
                    "Task.Delay",
                    "Find timer, debounce, retry, and polling delays that should carry cancellation, monotonic time, and disposal semantics.",
                    ["audit", "bug", "performance"],
                    "False positives include bounded tests and deliberate retry sleeps; prioritize runtime debounce, retry/backoff, watch-loop, and shutdown delays.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Task.Delay without a caller token can stall cancellation or leave retry/backoff loops alive during disposal.",
                        "positive: cancellation-token overloads, explicit timeout budgets, TimeProvider usage, and deterministic teardown tests make delays auditable."
                    ],
                },
                new(
                    "wait-for-exit-boundary",
                    "WaitForExit",
                    "Find process-drain boundaries where synchronous waits need timeout, cancellation, and process-tree cleanup review.",
                    ["audit", "bug"],
                    "False positives include already-bounded process cleanup and tests that assert timeout behavior.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: WaitForExit can block shutdown or cancellation if process output, timeout, and kill-tree behavior are not coordinated.",
                        "positive: bounded waits, cancellation-aware process runners, output-drain ordering, and timeout diagnostics make process cleanup intentional."
                    ],
                },
                new(
                    "semaphore-slim-boundary",
                    "SemaphoreSlim",
                    "Find async and sync gate boundaries where fairness, cancellation, release-on-exception, and disposal ordering need review.",
                    ["audit", "bug"],
                    "False positives include small local throttles; prioritize shared command, DB writer, MCP, HTTP transport, and event-stream gates.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: SemaphoreSlim gates can leak permits or block cancellation when Wait/WaitAsync and Release are not paired across exception paths.",
                        "positive: try/finally Release, WaitAsync with caller tokens, and bounded disposal/shutdown paths make gate ownership explicit."
                    ],
                },
                new(
                    "task-completion-source",
                    "TaskCompletionSource",
                    "Find completion-signal boundaries that may need RunContinuationsAsynchronously, cancellation, and completion-race review.",
                    ["audit", "bug"],
                    "False positives include test-only signals; prioritize production shutdown, protocol, worker, and transport completions.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: TaskCompletionSource can inline continuations or race completion/cancellation when ownership is not centralized.",
                        "positive: TaskCreationOptions.RunContinuationsAsynchronously, TrySet* usage, and deterministic cancellation tests make completion boundaries safer."
                    ],
                },
                new(
                    "http-listener-lifetime",
                    "HttpListener",
                    "Find HTTP listener lifetimes that need cancellation-aware accept loops, deterministic Close/Stop ordering, and bounded disposal.",
                    ["audit", "bug"],
                    "False positives include platform guards and tests; prioritize MCP HTTP transport and support-server loops.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: HttpListener accept loops can hang shutdown or surface ObjectDisposedException inconsistently unless cancellation and disposal ordering are explicit.",
                        "positive: cancellation-aware accept loops, close-before-await ordering, and bounded disposal tests make listener lifetime intentional."
                    ],
                },
                new(
                    "sync-wait-call",
                    ".Wait(",
                    "Find synchronous Wait calls that may block cancellation, async continuations, or shutdown paths.",
                    ["audit", "bug"],
                    "False positives include Monitor.Wait, SemaphoreSlim.Wait(0) admission checks, and bounded disposal or cleanup waits that intentionally bridge synchronous APIs.")
                {
                    RiskEvidence =
                    [
                        "risk: Task.Wait, unbounded waits, and waits without a caller cancellation token can hide cancellation and deadlock async flows.",
                        "positive: Monitor.Wait, SemaphoreSlim admission checks, and bounded best-effort shutdown waits should be classified separately from Task blocking."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "sync-over-async",
                    "GetAwaiter().GetResult",
                    "Find Task or ValueTask GetAwaiter().GetResult bridges that may deadlock or hide cancellation and timeout behavior.",
                    ["audit", "bug"],
                    "False positives include process-exit boundaries, compatibility sync wrappers, and completed-task observation; `.Result` property accesses are intentionally kept out of this query because many project DTOs expose Result-named properties.")
                {
                    RiskEvidence =
                    [
                        "risk: Task/ValueTask GetAwaiter().GetResult can block async continuations and bypass caller cancellation if used on live asynchronous work.",
                        "positive: compatibility wrappers, process-exit boundaries, and already-completed task observation should be reviewed as sync API bridges rather than automatic defects."
                    ],
                    MatchOrigins = ["code"],
                }
            ]),
        SourceScopedRecipe(
            "unsupported-operation-boundaries",
            "Audit unsupported-operation exceptions and messages for stable command, protocol, and capability diagnostics.",
            [
                new(
                    "not-supported-exception",
                    "NotSupportedException",
                    "Find generic unsupported-operation exception handling that may need typed diagnostics at user-facing boundaries.",
                    ["audit", "bug"],
                    "False positives include internal stream capability overrides, path API catch filters, and tests that intentionally assert framework exception behavior.")
                {
                    RiskEvidence =
                    [
                        "risk: generic NotSupportedException can escape command, MCP, LSP, or installer boundaries as inconsistent diagnostics or exit codes.",
                        "positive: CodeIndexException, CommandErrorWriter.WriteJsonOrHuman, MCP protocol errors, or bounded diagnostic categories make unsupported operations machine-readable."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                },
                new(
                    "platform-not-supported-exception",
                    "PlatformNotSupportedException",
                    "Find platform-specific unsupported paths that may need stable capability guidance or graceful degradation.",
                    ["audit", "bug", "portability"],
                    "False positives include guarded platform probes that degrade silently after confirming an alternate supported path.")
                {
                    RiskEvidence =
                    [
                        "risk: platform unsupported errors can become surprising command failures without recovery guidance or capability metadata.",
                        "positive: OperatingSystem guards, documented fallback behavior, and fixed recovery hints are safer evidence."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                },
                new(
                    "unsupported-message",
                    "unsupported",
                    "Find unsupported-operation messages that may need the same taxonomy as exception-based unsupported paths.",
                    ["audit", "bug"],
                    "False positives include capability documentation, field names such as unsupported_symbol_kind, and internal recipe metadata.")
                {
                    RiskEvidence =
                    [
                        "risk: free-form unsupported messages can diverge across CLI, JSON, MCP, and LSP surfaces.",
                        "positive: stable error codes, capability fields, supported-value allowlists, and fixed next-step hints make unsupported states actionable."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                },
                new(
                    "not-supported-message",
                    "not supported",
                    "Find phrase-based not-supported diagnostics that may need structured unsupported-operation classification.",
                    ["audit", "bug"],
                    "False positives include docs, comments, and internal capability explanations that are not emitted as command or protocol errors.")
                {
                    RiskEvidence =
                    [
                        "risk: phrase-only not-supported diagnostics can force users and automation to parse prose instead of stable categories.",
                        "positive: CodeIndexException categories, command-specific usage errors, MCP protocol errors, or typed capability metadata reduce triage risk."
                    ],
                    MatchOrigins = ["code", "string_literal"],
                }
            ]),
        SourceScopedRecipe(
            "nullable-contracts",
            "Audit nullable return contracts, null-forgiving suppressions, and guard/diagnostic evidence by domain.",
            [
                new(
                    "return-null-contract",
                    "return null",
                    "Find nullable return sites that should be classified as optional lookup, parse miss, unsupported capability, legacy schema absence, or invariant violation.",
                    ["audit", "bug"],
                    "False positives include explicit optional lookup or parser-miss contracts whose callers already branch on null.")
                {
                    RiskEvidence =
                    [
                        "risk: user-facing null returns can conflate optional lookup, parse miss, unsupported capability, legacy schema absence, and unexpected invariant violations.",
                        "positive: Try* methods, explicit nullable return docs, typed result wrappers, and stable diagnostics show the null contract has been classified."
                    ],
                    MatchOrigins = ["code"],
                    NullableContractTaxonomy = NullableContractTaxonomy,
                },
                new(
                    "null-forgiving-suppression",
                    "null!",
                    "Find null-forgiving suppressions that need false-state, delayed-initialization, reflection, or serialization evidence.",
                    ["audit", "bug"],
                    "False positives include Try* out-parameter placeholders, delayed initialization that is enforced before use, and reflection/serialization members covered by tests.")
                {
                    RiskEvidence =
                    [
                        "risk: null-forgiving suppressions can hide invariant bugs when the value is later read on a successful path.",
                        "positive: tests or nearby contracts for Try* false-state placeholders, delayed initialization, and reflection_or_serialization_boundary suppressions make the suppression intentional."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "default-forgiving-suppression",
                    "default!",
                    "Find default-forgiving suppressions that may hide an unclassified value-state contract.",
                    ["audit", "bug"],
                    "False positives include generic false-state placeholders whose callers cannot observe the value when the operation reports failure.")
                {
                    RiskEvidence =
                    [
                        "risk: default! can bypass nullable analysis without documenting which state makes the placeholder safe.",
                        "positive: a Try* false return, immediate error assignment, and tests that assert callers ignore the placeholder reduce risk."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "argument-null-guard",
                    "ArgumentNullException.ThrowIfNull",
                    "Find positive evidence that a public or boundary-facing API rejects null before nullable contracts reach deeper code.",
                    ["audit"],
                    "This is positive evidence; still verify user-facing errors are typed and do not replace domain-specific parse or lookup misses.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: explicit null guards clarify required inputs before optional return contracts are evaluated.",
                        "risk: guards do not classify nullable return domains by themselves; pair them with result contracts or diagnostics where absence is expected."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "argument-string-guard",
                    "ArgumentException.ThrowIfNullOrWhiteSpace",
                    "Find positive evidence that string inputs reject null or blank values before lookup, parse, or capability code runs.",
                    ["audit"],
                    "This is positive evidence; still verify empty/missing domain states use result contracts or typed diagnostics instead of raw nulls.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: explicit null-or-whitespace guards separate invalid input from optional lookup or parse-miss nulls.",
                        "risk: string guards do not replace stable diagnostics for unsupported language capability or legacy schema absence."
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "typed-diagnostic-evidence",
                    "CodeIndexException",
                    "Find typed diagnostic evidence that user-facing failures use stable codes instead of raw nullable sentinels.",
                    ["audit"],
                    "This is positive evidence; false positives include catch filters and comments that mention typed diagnostics without creating or emitting one.")
                {
                    Severity = "info",
                    RiskEvidence =
                    [
                        "positive: CodeIndexException carries stable code, category, path, and hint fields for user-facing failures.",
                        "risk: catch filters or comments alone do not prove nullable returns at the same boundary were classified."
                    ],
                    MatchOrigins = ["code"],
                }
            ]),
        SourceScopedRecipe(
            "xml-parser-security",
            "Audit XML parser APIs and DTD/entity settings for XXE and external-resolution regressions.",
            [
                new(
                    "xml-reader-settings",
                    "XmlReaderSettings",
                    "Find XML reader settings that should keep DtdProcessing disabled or ignored and avoid external entity resolution.",
                    ["audit", "security"],
                    "Expected safe settings include `DtdProcessing.Ignore` or `Prohibit` and no external resolver; tests and safe fixture parsers may be false positives.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: XML reader settings must keep DTD/entity behavior explicit and disable external resolver access for user-controlled manifests.",
                        "positive: SymbolExtractor.CreateExtractionXmlReaderSettings centralizes DtdProcessing, XmlResolver = null, and shared document/entity character limits."
                    ],
                },
                new(
                    "dtd-processing",
                    "DtdProcessing",
                    "Find DTD handling changes that may re-enable entity expansion or unsafe external document access.",
                    ["audit", "security"],
                    "Review for `Ignore` or `Prohibit`; `Parse` requires strong justification, bounded input, and resolver controls.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: DtdProcessing.Parse can re-enable entity expansion unless resolver, entity characters, and payload size are tightly bounded.",
                        "positive: DtdProcessing.Prohibit rejects project/dependency manifests; DtdProcessing.Ignore is acceptable only with XmlResolver = null and shared XML size limits."
                    ],
                },
                new(
                    "xml-resolver",
                    "XmlResolver",
                    "Find XML resolver configuration that may allow network or filesystem entity resolution.",
                    ["audit", "security"],
                    "Safe paths usually set the resolver to null or use a tightly bounded resolver.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: non-null XML resolvers can resolve external filesystem or network entities from otherwise small XML payloads.",
                        "positive: XmlResolver = null blocks external entity resolution and should be paired with DtdProcessing.Ignore or Prohibit."
                    ],
                }
            ]),
        SourceScopedRecipe(
            "filesystem-traversal",
            "Audit directory traversal and enumeration APIs for cancellation, budget, long-path, and exception-taxonomy behavior.",
            [
                new(
                    "enumerate-files",
                    "Directory.EnumerateFiles",
                    "Find lazy file enumeration paths that may need cancellation checks, traversal budgets, long-path handling, and permission error taxonomy.",
                    ["audit", "performance", "security"],
                    "False positives include tiny fixed directories and traversal already bounded by project-root containment."),
                new(
                    "enumerate-directories",
                    "Directory.EnumerateDirectories",
                    "Find directory enumeration paths that may need depth limits, cancellation, symlink/reparse handling, and permission recovery.",
                    ["audit", "performance", "security"],
                    "False positives include shallow temp fixture setup and already-budgeted traversal helpers."),
                new(
                    "enumerate-file-system-entries",
                    "Directory.EnumerateFileSystemEntries",
                    "Find broad filesystem entry traversal that may need explicit exception handling and pruning policy.",
                    ["audit", "performance", "security"],
                    "False positives include isolated test cleanup and known-small directories."),
                new(
                    "enumerate-without-options",
                    "Directory.Enumerate",
                    "Find direct Directory.Enumerate* calls that do not have nearby EnumerationOptions evidence and may need traversal policy review.",
                    ["audit", "performance", "security"],
                    "False positives include known-small directories, already-budgeted traversal helpers, and wrappers that enforce cancellation or reparse-point policy.")
                {
                    RiskEvidence =
                    [
                        "risk: direct Directory.Enumerate* calls without nearby EnumerationOptions can inherit default recursion, inaccessible-path, and reparse-point behavior.",
                        "positive: known-small directories, cancellation/budget checks, and shared traversal wrappers can explain intentional direct enumeration."
                    ],
                    GuardFilters =
                    [
                        new(SearchGuardRole.Reject, SearchGuardDirection.Before, "EnumerationOptions"),
                        new(SearchGuardRole.Reject, SearchGuardDirection.After, "EnumerationOptions")
                    ],
                    MatchOrigins = ["code"],
                },
                new(
                    "enumeration-options",
                    "EnumerationOptions",
                    "Find traversal option configuration for recurse behavior, inaccessible paths, attributes, and reparse-point policy.",
                    ["audit", "performance", "security"],
                    "Review option combinations against cancellation, budget, long-path, and permission behavior.")
            ]),
        SourceScopedRecipe(
            "filesystem-mutation-boundaries",
            "Audit path normalization, destructive file operations, temp paths, symlink policy, and path-filter guard evidence together.",
            [
                new(
                    "path-full-normalization",
                    "Path.GetFullPath",
                    "Find path canonicalization sites that should be classified by trust boundary and filesystem case-sensitivity assumptions.",
                    ["audit", "security"],
                    "False positives include display-only normalization and tests; prioritize user input, archive import/export, DB paths, and cleanup targets.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: full-path normalization alone does not prove root containment, symlink/reparse policy, or case-sensitivity correctness.",
                        "positive: pair with PathCasing, root containment checks, LongPath normalization, or typed diagnostics before filesystem mutation."
                    ],
                },
                new(
                    "long-path-normalization",
                    "LongPath.EnsureWindowsPrefix",
                    "Find long-path normalization boundaries that should stay paired with containment checks and platform-specific filesystem probes.",
                    ["audit", "security"],
                    "False positives include wrappers whose only job is centralizing Windows long-path behavior.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "positive: LongPath helpers centralize Windows prefix handling before IO APIs.",
                        "risk: long-path normalization must not replace root containment, symlink/reparse rejection, or path redaction."
                    ],
                },
                new(
                    "file-delete-boundary",
                    "File.Delete",
                    "Find file deletion call sites that require owned-state, caller-approved output, or bounded best-effort cleanup justification.",
                    ["audit", "security"],
                    "False positives include test cleanup and intentionally scoped temp fixtures.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: deletion must be protected from path traversal, symlink/reparse redirection, and TOCTOU-sensitive target swaps.",
                        "positive: FileSystemBoundary validation, .cdidx ownership, AtomicFileWriter rollback, or explicit temp-root containment can justify the mutation."
                    ],
                },
                new(
                    "directory-delete-boundary",
                    "Directory.Delete",
                    "Find recursive and non-recursive directory deletions that need root containment, symlink policy, retry bounds, and ownership evidence.",
                    ["audit", "security"],
                    "False positives include tests and known-owned fixture directories.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: recursive directory deletion can cross a symlink/reparse boundary or race with target replacement unless guarded.",
                        "positive: TryValidateDirectoryCleanupTarget, owned .cdidx roots, and bounded retry loops are expected safe evidence."
                    ],
                },
                new(
                    "file-move-boundary",
                    "File.Move",
                    "Find file move and overwrite boundaries that need atomicity, destination containment, and replacement-policy review.",
                    ["audit", "security"],
                    "False positives include same-root staging moves with explicit overwrite/rollback policy.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: move/overwrite operations can replace caller-controlled or symlinked destinations without root containment.",
                        "positive: AtomicFileWriter, FileSystemBoundary validation, and temp-file ownership explain intentional moves."
                    ],
                },
                new(
                    "file-copy-boundary",
                    "File.Copy",
                    "Find copy operations that need source/destination trust classification, size limits, and overwrite-policy review.",
                    ["audit", "security", "performance"],
                    "False positives include fixed test resources and already-bounded archive snapshot helpers.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: copying from untrusted or symlinked paths can bypass size, containment, or overwrite expectations.",
                        "positive: bounded stream copy, manifest length checks, and explicit destination ownership are expected evidence."
                    ],
                },
                new(
                    "temp-path-boundary",
                    "Path.GetTempPath",
                    "Find system temp-root usage that should create per-run owned subdirectories and validate cleanup targets.",
                    ["audit", "security"],
                    "False positives include tests and wrappers that immediately create a private random child directory.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: shared temp roots are attacker-writable on many systems; cleanup and overwrite code must operate inside owned children only.",
                        "positive: random scoped temp directories, FileSystemBoundary cleanup validation, and bounded cleanup retries reduce risk."
                    ],
                },
                new(
                    "temp-file-name-boundary",
                    "Path.GetTempFileName",
                    "Find temp-file allocation sites that should avoid shared predictable names and document ownership before mutation.",
                    ["audit", "security"],
                    "False positives include tests; production code should usually prefer owned temp directories plus random names."),
                new(
                    "symlink-reparse-policy",
                    "IsSymlinkOrReparsePoint",
                    "Find symlink/reparse-point detection surfaces that should guard traversal, cleanup, import/export, and plugin loading boundaries.",
                    ["audit", "security"],
                    "Review both positive and negative checks; tests and platform probes may be intentional.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "positive: central symlink/reparse detection keeps traversal and mutation code auditable.",
                        "risk: direct IO after a stale probe can still race; prefer validation immediately before mutation."
                    ],
                },
                new(
                    "cleanup-target-guard",
                    "TryValidateDirectoryCleanupTarget",
                    "Find positive cleanup guard evidence for recursive deletion and temp-root pruning.",
                    ["audit", "security"],
                    "This is positive evidence; still verify the caller passes the correct trusted root and handles false results.")
                {
                    Severity = "info",
                    MatchOrigins = ["code"],
                },
                new(
                    "watcher-boundary",
                    "FileSystemWatcher",
                    "Find filesystem watcher surfaces where path normalization, symlink policy, debounce, and rename/delete races need review.",
                    ["audit", "security"],
                    "False positives include disabled watcher setup and tests.")
                {
                    MatchOrigins = ["code"],
                },
                new(
                    "posix-mode-boundary",
                    "UnixFileMode",
                    "Find POSIX mode reads/writes that should be paired with platform guards and non-fatal diagnostics.",
                    ["audit", "security"],
                    "False positives include tests and status-only reporting."),
                new(
                    "path-filter-boundary",
                    "PathPattern",
                    "Find glob/path filter policy surfaces where normalization, case sensitivity, and exclusion semantics need review.",
                    ["audit", "security"],
                    "False positives include recipe metadata and test-only path pattern definitions.")
            ]),
        SourceScopedRecipe(
            "bounded-read-evidence",
            "Positive audit searches for max-byte file-read helpers, explicit file-open policy, and bounded downstream accumulators.",
            [
                new(
                    "bounded-file-open-helper",
                    "BoundedFile.OpenReadFor",
                    "Find reads routed through the shared file-open helper so audits can see the explicit share mode, long-path normalization, and bounded read category.",
                    ["audit", "performance"],
                    "Expected positive evidence includes BoundedFile.OpenReadFor* length-checked text reads, fixed-prefix probes, log tails, hash streams, and trusted archive sources that enforce their byte limits at the caller.",
                    ExactSubstring: true)
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "positive: BoundedFile.OpenReadFor* helpers centralize FileShare.ReadWrite/Delete, long-path normalization, and caller-provided byte budgets.",
                        "risk: callers must still preserve the max-byte contract and avoid transferring the stream beyond the bounded read boundary."
                    ],
                },
                new(
                    "bounded-memory-accumulator",
                    "MemoryStream",
                    "Find MemoryStream accumulators that are downstream of a max-byte helper and should be treated as positive evidence instead of an unbounded materialization by default.",
                    ["audit", "performance"],
                    "Expected positive evidence is limited to BoundedHttpContentReader, BoundedJsonUtf8Stream, BoundedLineReader.TryReadUtf8File, FileContentLoader.ReadStreamBytesAfterGrowth, DataDirectorySecurity, and SuggestionStore.ReadFilteredSnapshotAsync.",
                    ExactSubstring: false)
                {
                    PathPatterns =
                    [
                        "src/CodeIndex/BoundedLineReader.cs",
                        "src/CodeIndex/Cli/BoundedHttpContentReader.cs",
                        "src/CodeIndex/Mcp/BoundedJsonUtf8Stream.cs",
                        "src/CodeIndex/Indexer/Scanning/FileContentLoader.cs",
                        "src/CodeIndex/Cli/DataDirectorySecurity.cs",
                        "src/CodeIndex/Cli/SuggestionStore.cs",
                    ],
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "positive: these MemoryStream hits sit behind max-byte readers, bounded HTTP content, bounded JSON streams, or capped suggestion snapshots.",
                        "risk: MemoryStream remains eager materialization; verify any new caller keeps byte limits before writing to the accumulator."
                    ],
                },
                new(
                    "bounded-full-byte-read-helper",
                    "ReadRawBytesWithSizeLimit",
                    "Find whole-file byte reads that are intentionally routed through the indexer's max-file-size helper.",
                    ["audit", "performance"],
                    "Expected positive evidence is FileContentLoader.ReadRawBytesWithSizeLimit and direct callers that preserve the helper's max-byte and grow-after-length-check contract.",
                    ExactSubstring: false)
                {
                    PathPatterns = ["src/CodeIndex/Indexer/Scanning/FileContentLoader.cs"],
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "positive: ReadRawBytesWithSizeLimit records a full-byte-read helper with a max-file-byte gate before allocation.",
                        "risk: direct callers must preserve grow-after-length-check behavior so files cannot expand past the checked size before reading."
                    ],
                }
            ]),
        SourceScopedRecipe(
            "resource-materialization-audit",
            "Classify resource lifetime, stream ownership, file-open policy, and eager materialization hotspots by subsystem.",
            [
                new(
                    "disposable-boundary",
                    "IDisposable",
                    "Find disposable ownership boundaries that should have explicit teardown, signal-unregistration, or lifetime-transfer review.",
                    ["audit", "performance"],
                    "False positives include small scope helpers and test-only leases; prioritize command, MCP, LSP, worker, and database boundaries.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: disposable ownership can cross subsystem boundaries and leak handles, registrations, processes, or SQLite objects when teardown is ambiguous.",
                        "positive: sealed scopes with deterministic Dispose ordering, ownership-transfer comments, or using/await using call sites usually explain intentional lifetime management."
                    ],
                },
                new(
                    "async-dispose-boundary",
                    "DisposeAsync",
                    "Find async disposal paths where cancellation, stream flush, process drain, or transport shutdown ordering needs review.",
                    ["audit", "performance"],
                    "False positives include thin interface implementations and tests; prioritize long-running command, DB, MCP, and transport paths.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: async disposal can lose flush/drain failures or race with shutdown cancellation when ownership is unclear.",
                        "positive: await using, explicit ConfigureAwait, bounded drain, or best-effort diagnostic handling can make the boundary intentional."
                    ],
                },
                new(
                    "db-command-reader-ownership",
                    "CreateCommand(",
                    "Find database command creation sites so DB command, reader, and transaction ownership can be reviewed together.",
                    ["audit", "performance"],
                    "False positives include tiny scalar probes; prioritize command/reader pairs, prepared command cache reuse, and transaction lifetime boundaries.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: SQLite commands and readers should have clear ownership so prepared statements, transactions, and connections are not retained longer than intended.",
                        "positive: using/await using, ExecuteTrackedReader ownership, or prepared-command cache policy nearby can explain the site."
                    ],
                },
                new(
                    "file-stream-ownership",
                    "FileStream",
                    "Find explicit FileStream ownership boundaries that should document sharing mode, length checks, and disposal scope.",
                    ["audit", "performance", "security"],
                    "False positives include bounded helper internals and fixed test archives; prioritize production streams that cross subsystem or async boundaries.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: raw FileStream ownership can hide platform-specific sharing, long-path, locking, or max-byte assumptions.",
                        "positive: BoundedFile helpers, explicit FileShare, max-byte enforcement, or short local using scopes are strong safe evidence."
                    ],
                },
                new(
                    "file-open-sharing-policy",
                    "File.Open(",
                    "Find File.Open call sites that should show explicit FileShare, access mode, long-path, and bounded-read policy.",
                    ["audit", "performance", "security"],
                    "False positives include wrappers whose purpose is centralizing file-open policy.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: File.Open defaults and overload choices can create cross-platform locking, symlink, or unbounded-read surprises.",
                        "positive: explicit FileMode, FileAccess, FileShare, LongPath normalization, or BoundedFile routing makes the policy auditable."
                    ],
                },
                new(
                    "openread-sharing-policy",
                    "OpenRead",
                    "Find OpenRead-style helpers and call sites that should be classified by ownership transfer and sharing policy.",
                    ["audit", "performance", "security"],
                    "False positives include the shared BoundedFile helper and trusted archive entry readers.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: OpenRead call sites can inherit default sharing or transfer stream ownership across subsystem boundaries.",
                        "positive: BoundedFile.OpenRead, archive-entry ownership, or immediate using scopes generally explain the site."
                    ],
                },
                new(
                    "stream-reader-ownership",
                    "StreamReader",
                    "Find StreamReader ownership boundaries that should show encoding, leave-open, cancellation, and max-character behavior.",
                    ["audit", "performance"],
                    "False positives include tiny trusted test helpers and fixed in-memory protocol snippets.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: StreamReader can hide whole-stream text materialization, implicit encoding choices, or ownership transfer of the underlying stream.",
                        "positive: bounded source streams, explicit encoding, leaveOpen intent, and line-by-line capped readers make the boundary auditable."
                    ],
                },
                new(
                    "stream-writer-ownership",
                    "StreamWriter",
                    "Find StreamWriter ownership boundaries that should show flush, leave-open, encoding, and output-size behavior.",
                    ["audit", "performance"],
                    "False positives include fixed small local files and test fixtures.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: StreamWriter can buffer unbounded output or close caller-owned streams unexpectedly when ownership is ambiguous.",
                        "positive: using scopes, explicit UTF-8/no-BOM choices, leaveOpen intent, and bounded DTO/result emission explain safe writer use."
                    ],
                },
                new(
                    "read-to-end-materialization",
                    "ReadToEnd",
                    "Find read-to-end materialization sites that should prove payload size was bounded before text allocation.",
                    ["audit", "performance"],
                    "False positives include already-bounded StringReader fixtures and small process output snippets.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: ReadToEnd materializes all remaining text and can bypass line, byte, or cancellation budgets.",
                        "positive: bounded in-memory readers, pre-capped process output, or prior max-byte file reads make the call intentional."
                    ],
                },
                new(
                    "memory-stream-materialization",
                    "MemoryStream",
                    "Find MemoryStream materialization sites so bounded accumulators, serialization buffers, and in-memory payload copies can be separated.",
                    ["audit", "performance"],
                    "False positives include bounded JSON writers, tiny protocol envelopes, and test fixtures with fixed input sizes.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: MemoryStream can hide eager payload materialization before byte limits or cancellation are enforced.",
                        "positive: max-byte helpers, capped initial capacity, streaming JSON writers, or bounded downstream consumers make the allocation intentional."
                    ],
                },
                new(
                    "string-builder-materialization",
                    "StringBuilder",
                    "Find StringBuilder accumulation sites so bounded output builders and unbounded protocol/result buffers can be separated.",
                    ["audit", "performance"],
                    "False positives include fixed-format diagnostics, tiny local formatting helpers, and builders capped by a nearby byte or item limit.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: StringBuilder can accumulate workspace-sized output before JSON, NDJSON, SARIF, ctags, report, or MCP response caps are enforced.",
                        "positive: fixed initial capacity, explicit item/byte caps, streaming writers, or small local formatting scope make the accumulation auditable."
                    ],
                },
                new(
                    "query-mcp-toarray-materialization",
                    "ToArray()",
                    "Find eager ToArray conversions in query and MCP paths where result materialization can scale with workspace or protocol size.",
                    ["audit", "performance"],
                    "False positives include small option lists and stable protocol field snapshots; prioritize search results, module lists, path sets, and JSON arrays.")
                {
                    PathPatterns =
                    [
                        "src/CodeIndex/Cli/QueryCommandRunner*.cs",
                        "src/CodeIndex/Mcp/**",
                    ],
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: eager ToArray in query/MCP paths can materialize large result sets before limit, pagination, or JSON-size policy is applied.",
                        "positive: bounded list sizes, option metadata, or immutable snapshot requirements can make the conversion intentional."
                    ],
                },
                new(
                    "query-mcp-tolist-materialization",
                    "ToList()",
                    "Find eager ToList conversions in query and MCP paths where result materialization can scale with workspace or protocol size.",
                    ["audit", "performance"],
                    "False positives include small option lists and fixed protocol metadata; prioritize search results, module lists, path sets, and JSON arrays.")
                {
                    PathPatterns =
                    [
                        "src/CodeIndex/Cli/QueryCommandRunner*.cs",
                        "src/CodeIndex/Mcp/**",
                    ],
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: eager ToList in query/MCP paths can materialize large result sets before limit, pagination, or JSON-size policy is applied.",
                        "positive: bounded list sizes, option metadata, or immutable snapshot requirements can make the conversion intentional."
                    ],
                }
            ]),
        SourceScopedRecipe(
            "memory-allocation-boundaries",
            "Audit pooled-buffer ownership, sensitive buffer return policy, stack allocation bounds, and MemoryMarshal boundaries.",
            [
                new(
                    "array-pool-usage",
                    "ArrayPool",
                    "Find ArrayPool usage so Rent, ownership transfer, and Return paths can be reviewed together.",
                    ["audit", "performance"],
                    "False positives include tests and fixed helpers whose try/finally return policy is already covered nearby.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: rented buffers can leak or retain sensitive bytes when Return is not paired on every exception path.",
                        "positive: local try/finally, ownership-transfer comments, or shared return helpers make pooled-buffer lifetime auditable."
                    ],
                },
                new(
                    "array-pool-return",
                    ".Shared.Return",
                    "Find direct ArrayPool.Shared.Return calls so clearing policy and exception-safe pairing can be reviewed.",
                    ["audit", "performance", "security"],
                    "False positives include non-sensitive protocol buffers and tests that intentionally assert return behavior.",
                    ExactSubstring: true)
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: returning a sensitive buffer without clearing can retain token, payload, or path material in the shared pool.",
                        "positive: clearArray:true, Array.Clear over used bytes, or SensitiveBufferPolicy helpers document the intended clearing policy."
                    ],
                },
                new(
                    "sensitive-buffer-return-policy",
                    "SensitiveBufferPolicy.Return",
                    "Find centralized sensitive/non-sensitive buffer return helpers so clear-on-return contracts remain visible.",
                    ["audit", "security"],
                    "False positives include the policy implementation itself and tests that assert policy behavior.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "positive: SensitiveBufferPolicy helpers make clear-on-return decisions explicit for token, payload, copy, and protocol buffers.",
                        "risk: call sites still need review for correct used-byte counts and ownership transfer before returning a pooled buffer."
                    ],
                },
                new(
                    "stackalloc-buffer",
                    "stackalloc",
                    "Find stackalloc buffers so size bounds and sensitive-data clearing can be verified.",
                    ["audit", "performance", "security"],
                    "False positives include fixed tiny spans and tests that intentionally cover stack thresholds.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: input-derived stackalloc sizes can overflow the stack or retain sensitive bytes unless bounded and cleared.",
                        "positive: small constants, named stack thresholds, fallback to ArrayPool, and try/finally clearing make stack allocation intentional."
                    ],
                },
                new(
                    "memory-marshal-boundary",
                    "MemoryMarshal",
                    "Find MemoryMarshal boundaries where span reinterpretation, pinning, or layout assumptions need review.",
                    ["audit", "bug", "performance"],
                    "False positives include tests and isolated low-level helpers with documented layout contracts.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: MemoryMarshal can bypass type, lifetime, or alignment checks and should stay inside small documented helpers.",
                        "positive: fixed layout tests, scoped spans, and no pooled-buffer ownership transfer reduce review risk."
                    ],
                }
            ]),
        SourceScopedRecipe(
            "concurrency-state-audit",
            "Audit shared-state, locking, cancellation-registration, background-worker, and cache-ownership boundaries.",
            [
                new(
                    "semaphore-slim-gate",
                    "SemaphoreSlim",
                    "Find shared async/sync gates where wait cancellation, fairness, release pairing, and disposal order need review.",
                    ["audit", "bug"],
                    "False positives include small local throttles; prioritize command, DB writer, MCP, HTTP transport, and event-stream gates.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: SemaphoreSlim gates can leak permits or block shutdown when Wait/WaitAsync, Release, and Dispose ownership are split.",
                        "positive: WaitAsync with caller tokens, try/finally Release, and deterministic shutdown tests make gate ownership explicit."
                    ],
                },
                new(
                    "lock-statement-scope",
                    "lock (",
                    "Find lock scopes that should stay small, ordered, and free of callbacks, blocking I/O, or user/plugin code.",
                    ["audit", "bug"],
                    "False positives include tiny private-state guards; prioritize global caches, transport/session state, and callback-adjacent locks.",
                    ExactSubstring: true)
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: lock scopes that invoke callbacks, perform blocking I/O, or call plugin/user code can deadlock or stall unrelated work.",
                        "positive: narrow private-state locks with no awaits, no callbacks, and documented ordering are usually intentional."
                    ],
                },
                new(
                    "cancellation-registration-callback",
                    "Register(",
                    "Find registration callbacks that may need deterministic disposal and isolation from lock-held shared state.",
                    ["audit", "bug"],
                    "False positives include DI/service registration and event registration; prioritize CancellationToken.Register callbacks and teardown hooks.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: cancellation registrations can fire on disposal paths and should not acquire contested locks or outlive the owning operation.",
                        "positive: disposed registrations, static callbacks with captured immutable state, and lock-free callback bodies reduce teardown risk."
                    ],
                },
                new(
                    "concurrent-dictionary-cache",
                    "ConcurrentDictionary",
                    "Find shared concurrent caches whose ownership, eviction, and shutdown behavior should be documented.",
                    ["audit", "bug", "performance"],
                    "False positives include tiny immutable lookup tables; prioritize unbounded caches, prepared-command state, and session/transport maps.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: ConcurrentDictionary can hide unbounded growth, value factory races, or stale state when ownership and eviction are unclear.",
                        "positive: bounded capacity, explicit Clear/Dispose ownership, immutable values, and documented lifecycle reduce shared-cache risk."
                    ],
                },
                new(
                    "lazy-cache-ownership",
                    "Lazy<",
                    "Find lazy initialization and cache ownership boundaries that may hide exception caching or shutdown-order assumptions.",
                    ["audit", "bug"],
                    "False positives include static immutable metadata; prioritize lazy state that owns handles, tasks, cancellation sources, or process-global values.",
                    ExactSubstring: true)
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Lazy<T> can cache initialization exceptions or retain process-global state beyond the intended owner lifetime.",
                        "positive: immutable metadata, explicit LazyThreadSafetyMode, and no disposable/task payload make lazy ownership easier to justify."
                    ],
                },
                new(
                    "async-local-context",
                    "AsyncLocal",
                    "Find ambient context scopes that may leak across async boundaries or nested operations.",
                    ["audit", "bug"],
                    "False positives include test-only context probes; prioritize command/session correlation, trace, and diagnostic scopes.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: AsyncLocal state can leak into child tasks or survive nested operations unless scope restoration is deterministic.",
                        "positive: disposable scope guards, try/finally restoration, and tests for nested async operations make ambient state safer."
                    ],
                },
                new(
                    "task-run-background-work",
                    "Task.Run",
                    "Find background worker boundaries where cancellation, exception observation, and drain/flush ordering need review.",
                    ["audit", "bug"],
                    "False positives include small CPU offloads; prioritize fire-and-forget, transport, worker, and cache-maintenance paths.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Task.Run can detach work from cancellation and hide exceptions when no owner observes or drains the task.",
                        "positive: BackgroundTaskObserver, captured cancellation, awaited drains, and documented fire-and-forget ownership make scheduling intentional."
                    ],
                },
                new(
                    "task-completion-source-signal",
                    "TaskCompletionSource",
                    "Find completion-signal ownership boundaries that may need asynchronous continuations and completion-race review.",
                    ["audit", "bug"],
                    "False positives include test-only coordination; prioritize production protocol, transport, worker, and shutdown signals.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: TaskCompletionSource can inline continuations under locks or race Set/Cancellation paths when ownership is unclear.",
                        "positive: RunContinuationsAsynchronously, TrySet* calls, and single-owner completion helpers reduce signaling risk."
                    ],
                },
                new(
                    "manual-reset-event-slim",
                    "ManualResetEventSlim",
                    "Find blocking signal boundaries that should be limited to tests or have explicit timeout and disposal ownership.",
                    ["audit", "bug"],
                    "False positives include test fixtures; production hits should justify sync blocking and teardown ordering.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: ManualResetEventSlim blocks threads and can stall shutdown if waits are unbounded or disposal races with signalers.",
                        "positive: test-only usage, bounded waits, and deterministic owner disposal make the signal boundary intentional."
                    ],
                },
                new(
                    "interlocked-state",
                    "Interlocked",
                    "Find lock-free state transitions that need a clear invariant and pairing with volatile reads or higher-level ownership.",
                    ["audit", "bug"],
                    "False positives include simple counters; prioritize state machines, shutdown flags, and shared cache mutations.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Interlocked operations can become ad hoc memory-order assumptions when the invariant and matching reads are not documented.",
                        "positive: single-purpose counters, documented state transitions, and paired Volatile reads/writes make lock-free state easier to review."
                    ],
                },
                new(
                    "volatile-state",
                    "Volatile",
                    "Find explicit memory-order reads and writes that should have documented invariants and pairing.",
                    ["audit", "bug"],
                    "False positives include simple stop flags; prioritize multi-field state machines and cache visibility assumptions.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Volatile reads/writes only protect visibility for the accessed location and can hide multi-field invariant races.",
                        "positive: single-flag ownership, documented happens-before relationships, and paired Interlocked transitions reduce memory-order risk."
                    ],
                },
                new(
                    "channel-boundary",
                    "Channel",
                    "Find channel and queue boundaries where cancellation, completion, backpressure, and drain ordering need review.",
                    ["audit", "bug", "performance"],
                    "False positives include namespace/type declarations; prioritize runtime queues, protocol streams, and background workers.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: Channel producers and consumers can deadlock or drop work when completion, cancellation, and drain ordering are split.",
                        "positive: bounded channels, TryComplete ownership, cancellation-aware readers, and flush/drain tests make queue behavior explicit."
                    ],
                },
                new(
                    "blocking-collection-boundary",
                    "BlockingCollection",
                    "Find blocking collection boundaries that need timeout, cancellation, and disposal review.",
                    ["audit", "bug"],
                    "False positives include legacy tests; production use should justify synchronous blocking and bounded shutdown.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: BlockingCollection can hide thread blocking and shutdown hangs without bounded Take/Add cancellation and CompleteAdding ownership.",
                        "positive: bounded capacity, cancellation-aware consuming loops, and deterministic CompleteAdding/Dispose ordering reduce blocking risk."
                    ],
                },
                new(
                    "threading-timer-lifetime",
                    "System.Threading.Timer",
                    "Find timer lifetimes where callback ownership, cancellation, and Dispose/DisposeAsync ordering need review.",
                    ["audit", "bug"],
                    "False positives include documentation strings and tests; prioritize runtime timers whose callbacks mutate shared state.")
                {
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: System.Threading.Timer callbacks can overlap disposal or mutate shared state after the owner begins shutdown.",
                        "positive: deterministic Dispose/DisposeAsync, callback serialization, and cancellation-aware owner teardown make timer lifetime intentional."
                    ],
                }
            ]),
        AllScopedRecipe(
            "phrase-risk-patterns",
            "Precision-focused audit searches for noisy code phrases, broad words, and configuration text that need semantic triage facets.",
            AddClassifiers([
                new(
                    "async-void-code",
                    "async void",
                    "Find exact async void declarations in production source instead of broad async/void lexical coincidences.",
                    ["audit", "bug"],
                    "False positives include required event handlers, framework callbacks, and intentionally fire-and-forget boundaries.")
                {
                    PathPatterns = [.. DefaultSourcePathPatternsValue],
                    ExcludePaths = [.. DefaultSourceExcludePathsValue],
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: async void hides exceptions and cancellation from normal Task-based callers.",
                        "positive: UI/event-handler or host callback signatures can require async void; verify the boundary and exception handling."
                    ],
                },
                new(
                    "throw-new-exception-code",
                    "throw new Exception",
                    "Find exact generic Exception construction in production source instead of broad throw/exception lexical matches.",
                    ["audit", "bug"],
                    "False positives include top-level compatibility shims and temporary placeholders already tracked for typed exception cleanup.")
                {
                    PathPatterns = [.. DefaultSourcePathPatternsValue],
                    ExcludePaths = [.. DefaultSourceExcludePathsValue],
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: generic Exception loses typed recovery, category, and diagnostic-contract information.",
                        "positive: boundary normalization or compatibility wrappers may intentionally translate to a generic exception only after preserving context."
                    ],
                },
                new(
                    "task-result-property-review",
                    ".Result",
                    "Find exact Result property accesses in production source so reviewers can separate Task blocking from ordinary DTO Result properties.",
                    ["audit", "bug"],
                    "False positives include DTO, command-result, parse-result, and search-result property access; prioritize hits whose receiver is Task or ValueTask.")
                {
                    PathPatterns = [.. DefaultSourcePathPatternsValue],
                    ExcludePaths = [.. DefaultSourceExcludePathsValue],
                    MatchOrigins = ["code"],
                    ResultKinds = ["identifier"],
                    RiskEvidence =
                    [
                        "risk: Task.Result and ValueTask.AsTask().Result can block async continuations and hide cancellation or timeout policy.",
                        "positive: DTOs and result-wrapper properties named Result should be classified separately from sync-over-async blocking."
                    ],
                    Classifiers = [TaskResultIntentClassifier],
                },
                new(
                    "unsafe-keyword-code",
                    "unsafe ",
                    "Find exact unsafe keyword usage in production source without documentation, installer text, or compatibility-note matches.",
                    ["audit", "security"],
                    "False positives include comments about unsafe APIs and safe-handle names; code-origin matches should be reviewed for pointer and buffer safety.")
                {
                    PathPatterns = [.. DefaultSourcePathPatternsValue],
                    ExcludePaths = [.. DefaultSourceExcludePathsValue],
                    MatchOrigins = ["code"],
                    ResultKinds = ["identifier"],
                    RiskEvidence =
                    [
                        "risk: unsafe code can bypass runtime memory safety and needs pointer lifetime, bounds, and pinning review.",
                        "positive: isolated interop boundaries with SafeHandle, fixed-size buffers, and focused tests reduce triage priority."
                    ],
                },
                new(
                    "active-test-skip-assignment",
                    "Skip =",
                    "Find exact active test skip annotations without broad skip prose, changelog, or documentation matches.",
                    ["audit", "test"],
                    "False positives include fixture text that demonstrates skip syntax; active attributes and test-case metadata are the primary review target.")
                {
                    Severity = "info",
                    PathPatterns = ["tests/**"],
                    MatchOrigins = ["code"],
                    RiskEvidence =
                    [
                        "risk: active Skip assignments can hide disabled coverage in the test suite.",
                        "positive: documented platform-specific skips or intentionally external-dependency tests may be acceptable when tracked."
                    ],
                    Classifiers = [ActiveSkipClassifier],
                },
                new(
                    "readalltext-call-site",
                    "ReadAllText",
                    "Find production ReadAllText call sites while excluding documentation, project files, and config text.",
                    ["audit", "performance"],
                    "False positives include bounded helpers or tiny trusted files; prefer this call-site query when bare ReadAllText is noisy.")
                {
                    PathPatterns = [.. DefaultSourcePathPatternsValue],
                    ExcludePaths = [.. DefaultSourceExcludePathsValue],
                    MatchOrigins = ["code"],
                    ResultKinds = ["call_site"],
                    RiskEvidence =
                    [
                        "risk: whole-file text reads can materialize unbounded input without sharing or size policy.",
                        "positive: nearby length checks, BoundedFile helpers, or tiny trusted files can make a hit intentional."
                    ],
                },
                new(
                    "version-project-config",
                    "Version=\"",
                    "Find exact XML Version attributes in project and package configuration instead of documentation or prose matches.",
                    ["audit"],
                    "False positives include generated fixture projects and examples; this query is for configuration/version-surface inventory, not source-code risk.")
                {
                    Severity = "info",
                    PathPatterns =
                    [
                        "*.csproj",
                        "*.props",
                        "*.targets",
                        "src/**/*.csproj",
                        "tests/**/*.csproj",
                        "Directory.Packages.props",
                        "Directory.Build.props",
                        "Directory.Build.targets"
                    ],
                    RiskEvidence =
                    [
                        "risk: dependency and package Version attributes belong to configuration review rather than source-code vulnerability sweeps.",
                        "positive: exact XML attribute matching keeps version prose, docs, and changelog examples out of this inventory."
                    ],
                },
                new(
                    "todo-production-comment",
                    "TODO",
                    "Find TODO comments in production source without fixture, documentation, and changelog examples dominating the result set.",
                    ["audit"],
                    "False positives include intentionally tracked follow-up markers; broad TODO inventory should be requested separately when docs and tests are in scope.")
                {
                    Severity = "info",
                    PathPatterns = [.. DefaultSourcePathPatternsValue],
                    ExcludePaths = [.. DefaultSourceExcludePathsValue],
                    MatchOrigins = ["comment"],
                    ResultKinds = ["comment"],
                    RiskEvidence =
                    [
                        "risk: production TODO comments can mark incomplete behavior that deserves explicit issue tracking.",
                        "positive: comments that reference an existing issue or explain non-actionable compatibility work are lower risk."
                    ],
                },
                new(
                    "obsolete-production-code",
                    "Obsolete",
                    "Find Obsolete usage in production source without documentation, fixture, and changelog examples dominating the result set.",
                    ["audit", "bug"],
                    "False positives include compatibility shims and deliberate API lifecycle annotations; prioritize call sites or declarations that affect runtime paths.")
                {
                    PathPatterns = [.. DefaultSourcePathPatternsValue],
                    ExcludePaths = [.. DefaultSourceExcludePathsValue],
                    MatchOrigins = ["code"],
                    ResultKinds = ["identifier"],
                    RiskEvidence =
                    [
                        "risk: Obsolete APIs or attributes in production code can hide compatibility debt or unsupported runtime behavior.",
                        "positive: explicit migration comments, compatibility guards, or attribute declarations with planned removal can make the hit intentional."
                    ],
                }
            ], SourceOriginClassifier)),
        AllScopedRecipe(
            "broad-token-audit",
            "Opt-in broad token search for audits that intentionally need lexical, parser, LSP, cancellation, and auth-token coverage.",
            [
                new(
                    "token-term-broad",
                    "token",
                    "Find every token mention when a broad token audit is explicitly requested.",
                    ["audit", "security"],
                    "This intentionally includes parser/tokenizer code, syntax tokens, LSP tokens, cancellation tokens, docs, and tests.",
                    ExactSubstring: false),
                new(
                    "auth-token",
                    "auth token",
                    "Facet broad token audits to credential/auth-token material.",
                    ["audit", "security"],
                    "Use auth-token-audit for a source-scoped review that avoids parser, LSP, and cancellation-token domains.",
                    ExactSubstring: false)
                {
                    RiskEvidence =
                    [
                        "risk: auth-token material can cross logging, persistence, or outbound request boundaries.",
                        "positive: redaction helpers and secret providers are strong safe evidence."
                    ],
                },
                new(
                    "parser-token",
                    "SyntaxToken",
                    "Facet broad token audits to parser or syntax-token domains that are usually not credential material.",
                    ["audit"],
                    "This is a negative-domain facet for separating parser/tokenizer noise from credential-token review.")
                {
                    Severity = "info",
                },
                new(
                    "cancellation-token",
                    "CancellationToken",
                    "Facet broad token audits to cancellation-token domains that are usually control-flow, not credentials.",
                    ["audit"],
                    "This is a negative-domain facet for separating cancellation plumbing from credential-token review.")
                {
                    Severity = "info",
                },
                new(
                    "lsp-token",
                    "SemanticToken",
                    "Facet broad token audits to LSP semantic-token domains that are usually protocol data, not credentials.",
                    ["audit"],
                    "This is a negative-domain facet for separating LSP protocol token data from credential-token review.")
                {
                    Severity = "info",
                }
            ])
    ];

    private static SearchAuditRecipe SourceScopedRecipe(
        string name,
        string description,
        List<SearchAuditRecipeQuery> queries,
        IReadOnlyList<string>? defaultExcludeOrigins = null) => new(name, description, ApplyDefaultQueryExcludeOrigins(queries, defaultExcludeOrigins))
        {
            DefaultPathPatterns = [.. DefaultSourcePathPatternsValue],
            DefaultExcludePaths = [.. DefaultSourceExcludePathsValue],
        };

    private static List<SearchAuditRecipeQuery> ApplyDefaultQueryExcludeOrigins(
        List<SearchAuditRecipeQuery> queries,
        IReadOnlyList<string>? defaultExcludeOrigins)
    {
        if (defaultExcludeOrigins is null || defaultExcludeOrigins.Count == 0)
            return queries;

        return queries
            .Select(query => query.MatchOrigins.Count == 0 && query.ExcludeOrigins.Count == 0
                ? query with { ExcludeOrigins = [.. defaultExcludeOrigins] }
                : query)
            .ToList();
    }

    private static SearchAuditRecipe AllScopedRecipe(
        string name,
        string description,
        List<SearchAuditRecipeQuery> queries) => new(name, description, queries)
        {
            DefaultScope = AllAuditScope,
        };

    internal static IReadOnlyList<SearchAuditRecipe> All => Load().Recipes;

    internal static SearchAuditRecipeRegistry Load()
    {
        var recipes = BuiltInRecipes.ToList();
        var diagnostics = new List<string>();
        var knownNames = new HashSet<string>(recipes.Select(recipe => recipe.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in ReadConfiguredRecipeSourcePaths(diagnostics))
        {
            if (!TryLoadExternalRecipes(sourcePath.Path, sourcePath.Label, diagnostics, out var externalRecipes))
                continue;

            foreach (var recipe in externalRecipes)
            {
                if (!knownNames.Add(recipe.Name))
                {
                    AddDiagnostic(diagnostics, $"{sourcePath.Label} defines duplicate recipe '{recipe.Name}'; keeping the first definition.");
                    continue;
                }

                recipes.Add(recipe);
            }
        }

        return new SearchAuditRecipeRegistry(recipes, diagnostics);
    }

    internal static bool TryGet(string name, out SearchAuditRecipe recipe)
    {
        var registry = Load();
        recipe = registry.Recipes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return recipe != null;
    }

    private static List<RecipeSourcePath> ReadConfiguredRecipeSourcePaths(List<string> diagnostics)
    {
        var raw = CdidxEnvironment.GetEnvironmentVariable(RecipePathsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var paths = new List<RecipeSourcePath>();
        foreach (var part in raw.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (paths.Count >= MaxRecipeSourceFiles)
            {
                AddDiagnostic(diagnostics, $"{RecipePathsEnvironmentVariable} lists more than {MaxRecipeSourceFiles} recipe sources; extra entries are ignored.");
                break;
            }

            paths.Add(new RecipeSourcePath(part, $"recipe source #{paths.Count + 1}"));
        }

        return paths;
    }

    private static bool TryLoadExternalRecipes(string sourcePath, string sourceLabel, List<string> diagnostics, out List<SearchAuditRecipe> recipes)
    {
        recipes = [];
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} is not a valid path ({SafeDiagnosticFormatter.FormatExceptionCategory("invalid_recipe_path", ex)}).");
            return false;
        }

        try
        {
            var text = DataDirectorySecurity.ReadTextWithinLimit(
                fullPath,
                MaxRecipeSourceBytes,
                FileShare.ReadWrite | FileShare.Delete);
            if (text is null)
            {
                AddDiagnostic(diagnostics, $"{sourceLabel} is too large (max {MaxRecipeSourceBytes} bytes).");
                return false;
            }

            var root = BoundedJson.ParseNode(
                text,
                MaxRecipeSourceBytes,
                maxDepth: 16);
            var recipeArray = root as JsonArray ?? root?["recipes"] as JsonArray;
            if (recipeArray is null)
            {
                AddDiagnostic(diagnostics, $"{sourceLabel} must be a JSON array or an object with a 'recipes' array.");
                return false;
            }

            for (var i = 0; i < recipeArray.Count && i < MaxExternalRecipesPerFile; i++)
            {
                if (TryParseRecipe(recipeArray[i], sourceLabel, i, diagnostics, out var recipe))
                    recipes.Add(recipe);
            }

            if (recipeArray.Count > MaxExternalRecipesPerFile)
                AddDiagnostic(diagnostics, $"{sourceLabel} has more than {MaxExternalRecipesPerFile} recipes; extra entries are ignored.");
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} does not exist.");
            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} could not be loaded ({SafeDiagnosticFormatter.FormatExceptionCategory("recipe_load", ex)}).");
            return false;
        }
    }

    private static bool TryParseRecipe(
        JsonNode? node,
        string sourceLabel,
        int recipeIndex,
        List<string> diagnostics,
        out SearchAuditRecipe recipe)
    {
        recipe = null!;
        if (node is not JsonObject obj)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe #{recipeIndex + 1} must be an object.");
            return false;
        }

        if (!TryReadRequiredString(obj, "name", MaxExternalNameLength, sourceLabel, recipeIndex, diagnostics, out var name)
            || !TryReadRequiredString(obj, "description", MaxExternalDescriptionLength, sourceLabel, recipeIndex, diagnostics, out var description)
            || !TryReadOptionalScope(obj, sourceLabel, recipeIndex, name, diagnostics, out var defaultScope)
            || !TryReadPathPatterns(obj, "defaultPathPatterns", "default_path_patterns", sourceLabel, recipeIndex, name, diagnostics, out var defaultPathPatterns)
            || !TryReadPathPatterns(obj, "defaultExcludePaths", "default_exclude_paths", sourceLabel, recipeIndex, name, diagnostics, out var defaultExcludePaths))
        {
            return false;
        }

        if (obj["queries"] is not JsonArray queryArray)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{name}' must include a 'queries' array.");
            return false;
        }

        var queries = new List<SearchAuditRecipeQuery>();
        for (var i = 0; i < queryArray.Count && i < MaxExternalQueriesPerRecipe; i++)
        {
            if (TryParseRecipeQuery(queryArray[i], sourceLabel, name, i, diagnostics, out var query))
                queries.Add(query);
        }

        if (queryArray.Count > MaxExternalQueriesPerRecipe)
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{name}' has more than {MaxExternalQueriesPerRecipe} queries; extra entries are ignored.");
        if (queries.Count == 0)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{name}' has no valid queries and was ignored.");
            return false;
        }

        recipe = new SearchAuditRecipe(name, description, queries)
        {
            DefaultScope = defaultScope,
            DefaultPathPatterns = defaultPathPatterns,
            DefaultExcludePaths = defaultExcludePaths
        };
        return true;
    }

    private static bool TryParseRecipeQuery(
        JsonNode? node,
        string sourceLabel,
        string recipeName,
        int queryIndex,
        List<string> diagnostics,
        out SearchAuditRecipeQuery query)
    {
        query = null!;
        if (node is not JsonObject obj)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' query #{queryIndex + 1} must be an object.");
            return false;
        }

        if (!TryReadRequiredString(obj, "name", MaxExternalNameLength, sourceLabel, queryIndex, diagnostics, out var name)
            || !TryReadRequiredString(obj, "query", QueryLimits.MaxQueryLength, sourceLabel, queryIndex, diagnostics, out var queryText)
            || !TryReadRequiredString(obj, "description", MaxExternalDescriptionLength, sourceLabel, queryIndex, diagnostics, out var description)
            || !TryReadOptionalSeverity(obj, sourceLabel, recipeName, queryIndex, name, diagnostics, out var severity)
            || !TryReadPathPatterns(obj, "pathPatterns", "path_patterns", sourceLabel, queryIndex, name, diagnostics, out var pathPatterns)
            || !TryReadPathPatterns(obj, "excludePaths", "exclude_paths", sourceLabel, queryIndex, name, diagnostics, out var excludePaths))
        {
            return false;
        }

        var labels = ReadLabels(obj, sourceLabel, recipeName, name, diagnostics);
        var falsePositiveGuidance = TryReadString(obj["falsePositiveGuidance"] ?? obj["false_positive_guidance"], out var guidance)
            && !string.IsNullOrWhiteSpace(guidance)
            ? guidance.Trim()
            : "Review surrounding context before filing an issue.";
        if (falsePositiveGuidance.Length > MaxExternalFalsePositiveGuidanceLength)
            falsePositiveGuidance = falsePositiveGuidance[..MaxExternalFalsePositiveGuidanceLength].TrimEnd();
        var exactSubstring = TryReadBool(obj["exactSubstring"] ?? obj["exact_substring"], out var exactValue)
            ? exactValue
            : true;

        query = new SearchAuditRecipeQuery(name, queryText, description, labels, falsePositiveGuidance, exactSubstring)
        {
            Severity = severity,
            PathPatterns = pathPatterns,
            ExcludePaths = excludePaths
        };
        return true;
    }

    private static bool TryReadRequiredString(
        JsonObject obj,
        string propertyName,
        int maxLength,
        string sourceLabel,
        int itemIndex,
        List<string> diagnostics,
        out string value)
    {
        value = string.Empty;
        if (!TryReadString(obj[propertyName], out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} item #{itemIndex + 1} must include a non-empty '{propertyName}' string.");
            return false;
        }

        value = raw.Trim();
        if (value.Length <= maxLength)
            return true;

        AddDiagnostic(diagnostics, $"{sourceLabel} item #{itemIndex + 1} field '{propertyName}' exceeds {maxLength} characters.");
        value = string.Empty;
        return false;
    }

    private static bool TryReadOptionalScope(
        JsonObject obj,
        string sourceLabel,
        int recipeIndex,
        string recipeName,
        List<string> diagnostics,
        out string scope)
    {
        scope = DefaultAuditScope;
        var node = obj["defaultScope"] ?? obj["default_scope"];
        if (node is null)
            return true;

        if (!TryReadString(node, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' item #{recipeIndex + 1} has an invalid default scope.");
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (StringComparer.Ordinal.Equals(normalized, DefaultAuditScope) || StringComparer.Ordinal.Equals(normalized, AllAuditScope))
        {
            scope = normalized;
            return true;
        }

        AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' item #{recipeIndex + 1} has unsupported default scope '{normalized}'.");
        return false;
    }

    private static bool TryReadPathPatterns(
        JsonObject obj,
        string camelCasePropertyName,
        string snakeCasePropertyName,
        string sourceLabel,
        int recipeIndex,
        string recipeName,
        List<string> diagnostics,
        out List<string> patterns)
    {
        patterns = [];
        var node = obj[camelCasePropertyName] ?? obj[snakeCasePropertyName];
        if (node is null)
            return true;

        if (node is not JsonArray array)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' item #{recipeIndex + 1} field '{snakeCasePropertyName}' must be an array.");
            return false;
        }

        if (array.Count > MaxExternalPathPatternCount)
        {
            AddDiagnostic(
                diagnostics,
                $"{sourceLabel} recipe '{recipeName}' item #{recipeIndex + 1} field '{snakeCasePropertyName}' has more than {MaxExternalPathPatternCount} entries.");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
        {
            if (!TryReadString(array[i], out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' item #{recipeIndex + 1} field '{snakeCasePropertyName}' has an invalid entry.");
                return false;
            }

            var pattern = raw.Trim();
            if (pattern.Length > MaxExternalPathPatternLength)
            {
                AddDiagnostic(
                    diagnostics,
                    $"{sourceLabel} recipe '{recipeName}' item #{recipeIndex + 1} field '{snakeCasePropertyName}' entry exceeds {MaxExternalPathPatternLength} characters.");
                return false;
            }

            if (seen.Add(pattern))
                patterns.Add(pattern);
        }

        return true;
    }

    private static bool TryReadOptionalSeverity(
        JsonObject obj,
        string sourceLabel,
        string recipeName,
        int queryIndex,
        string queryName,
        List<string> diagnostics,
        out string severity)
    {
        severity = DefaultQuerySeverity;
        var node = obj["severity"];
        if (node is null)
            return true;

        if (!TryReadString(node, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' query '{queryName}' item #{queryIndex + 1} has an invalid severity.");
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (SupportedQuerySeverities.Contains(normalized, StringComparer.Ordinal))
        {
            severity = normalized;
            return true;
        }

        AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' query '{queryName}' item #{queryIndex + 1} has unsupported severity '{normalized}'.");
        return false;
    }

    private static List<string> ReadLabels(
        JsonObject obj,
        string sourceLabel,
        string recipeName,
        string queryName,
        List<string> diagnostics)
    {
        var labelsNode = obj["recommendedLabels"] ?? obj["recommended_labels"];
        if (labelsNode is null)
            return [];
        if (labelsNode is not JsonArray labelArray)
        {
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' query '{queryName}' labels must be an array.");
            return [];
        }

        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < labelArray.Count && i < MaxExternalLabelCount; i++)
        {
            if (!TryReadString(labelArray[i], out var label) || string.IsNullOrWhiteSpace(label))
            {
                AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' query '{queryName}' label #{i + 1} must be a non-empty string.");
                continue;
            }
            label = label.Trim();
            if (label.Length > MaxExternalLabelLength)
            {
                AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' query '{queryName}' label #{i + 1} exceeds {MaxExternalLabelLength} characters.");
                continue;
            }
            if (seen.Add(label))
                labels.Add(label);
        }

        if (labelArray.Count > MaxExternalLabelCount)
            AddDiagnostic(diagnostics, $"{sourceLabel} recipe '{recipeName}' query '{queryName}' has more than {MaxExternalLabelCount} labels; extra entries are ignored.");
        return labels;
    }

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count >= MaxRecipeDiagnosticCount)
        {
            if (diagnostics.Count == MaxRecipeDiagnosticCount)
                diagnostics.Add($"recipe source diagnostics were truncated after {MaxRecipeDiagnosticCount} entries.");
            return;
        }

        if (message.Length > MaxRecipeDiagnosticLength)
            message = message[..MaxRecipeDiagnosticLength].TrimEnd() + " ... [truncated]";
        diagnostics.Add(message);
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is null)
            return false;
        try
        {
            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is null)
            return false;
        try
        {
            value = node.GetValue<bool>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record RecipeSourcePath(string Path, string Label);
}

internal sealed record SearchAuditRecipeRegistry(
    IReadOnlyList<SearchAuditRecipe> Recipes,
    IReadOnlyList<string> Diagnostics);

internal sealed record SearchAuditRecipe(
    string Name,
    string Description,
    List<SearchAuditRecipeQuery> Queries)
{
    public string DefaultScope { get; init; } = SearchAuditRecipes.DefaultAuditScope;
    public List<string> DefaultPathPatterns { get; init; } = [];
    public List<string> DefaultExcludePaths { get; init; } = [];

    public List<string> RecommendedLabels =>
        Queries
            .SelectMany(query => query.RecommendedLabels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

internal sealed record SearchAuditRecipeQuery(
    string Name,
    string Query,
    string Description,
    List<string> RecommendedLabels,
    string FalsePositiveGuidance,
    bool ExactSubstring = true)
{
    public string Severity { get; init; } = SearchAuditRecipes.DefaultQuerySeverity;
    public List<string> RiskEvidence { get; init; } = [];
    public List<SearchGuardFilter> GuardFilters { get; init; } = [];
    public List<string> RejectFileQueries { get; init; } = [];
    public List<string> PathPatterns { get; init; } = [];
    public List<string> ExcludePaths { get; init; } = [];
    public List<string> MatchOrigins { get; init; } = [];
    public List<string> ExcludeOrigins { get; init; } = [];
    public List<string> ResultKinds { get; init; } = [];
    public List<SearchRecipeClassifierJsonResult> Classifiers { get; init; } = [];
    public SearchRecipeStringComparisonTaxonomyJsonResult? StringComparisonTaxonomy { get; init; }
    public SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy { get; init; }
    public SearchRecipeNullableContractTaxonomyJsonResult? NullableContractTaxonomy { get; init; }
}

internal sealed record SearchRecipeListJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("recipes")] List<SearchRecipeListItemJsonResult> Recipes);

internal sealed record SearchRecipeNameListJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("names")] List<string> Names);

internal sealed record SearchRecipeCompactListJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("recipes")] List<SearchRecipeCompactListItemJsonResult> Recipes);

internal sealed record SearchRecipeCompactListItemJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("default_scope")] string DefaultScope,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("recommended_labels")] List<string> RecommendedLabels,
    [property: JsonPropertyName("default_path_patterns")] List<string> DefaultPathPatterns,
    [property: JsonPropertyName("default_exclude_paths")] List<string> DefaultExcludePaths);

internal sealed record SearchRecipeListItemJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("recommended_labels")] List<string> RecommendedLabels,
    [property: JsonPropertyName("default_scope")] string DefaultScope,
    [property: JsonPropertyName("default_path_patterns")] List<string> DefaultPathPatterns,
    [property: JsonPropertyName("default_exclude_paths")] List<string> DefaultExcludePaths,
    [property: JsonPropertyName("supported_formats")] List<string> SupportedFormats,
    [property: JsonPropertyName("filter_support")] SearchRecipeFilterSupportJsonResult FilterSupport,
    [property: JsonPropertyName("limit_semantics")] SearchRecipeLimitSemanticsJsonResult LimitSemantics,
    [property: JsonPropertyName("queries")] List<SearchRecipeQueryListItemJsonResult> Queries);

internal sealed record SearchRecipeFilterSupportJsonResult(
    [property: JsonPropertyName("lang")] bool Lang,
    [property: JsonPropertyName("path")] bool Path,
    [property: JsonPropertyName("exclude_path")] bool ExcludePath,
    [property: JsonPropertyName("exclude_tests")] bool ExcludeTests,
    [property: JsonPropertyName("since")] bool Since,
    [property: JsonPropertyName("dedup")] bool Dedup,
    [property: JsonPropertyName("visibility_rank")] bool VisibilityRank,
    [property: JsonPropertyName("guard_filters")] bool GuardFilters,
    [property: JsonPropertyName("snippet_controls")] bool SnippetControls,
    [property: JsonPropertyName("exact_mode_override")] bool ExactModeOverride);

internal sealed record SearchRecipeLimitSemanticsJsonResult(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("default")] int Default,
    [property: JsonPropertyName("description")] string Description);

internal sealed record SearchRecipeQueryListItemJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("recommended_labels")] List<string> RecommendedLabels,
    [property: JsonPropertyName("false_positive_guidance")] string FalsePositiveGuidance,
    [property: JsonPropertyName("risk_evidence")] List<string> RiskEvidence,
    [property: JsonPropertyName("guard_filters")] List<SearchRecipeGuardFilterJsonResult> GuardFilters,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("path_patterns")] List<string> PathPatterns,
    [property: JsonPropertyName("exclude_paths")] List<string> ExcludePaths,
    [property: JsonPropertyName("match_origins")] List<string> MatchOrigins,
    [property: JsonPropertyName("exclude_origins")] List<string> ExcludeOrigins,
    [property: JsonPropertyName("result_kinds")] List<string> ResultKinds,
    [property: JsonPropertyName("classifiers")] List<SearchRecipeClassifierJsonResult> Classifiers,
    [property: JsonPropertyName("string_comparison_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeStringComparisonTaxonomyJsonResult? StringComparisonTaxonomy,
    [property: JsonPropertyName("broad_catch_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy,
    [property: JsonPropertyName("nullable_contract_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeNullableContractTaxonomyJsonResult? NullableContractTaxonomy,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring);

internal sealed record SearchRecipeGuardFilterJsonResult(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("option")] string Option,
    [property: JsonPropertyName("scope")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Scope);

internal sealed record SearchRecipeClassifierJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("categories")] List<SearchRecipeClassifierCategoryJsonResult> Categories,
    [property: JsonPropertyName("evidence_fields")] List<string> EvidenceFields,
    [property: JsonPropertyName("triage_guidance")] string TriageGuidance);

internal sealed record SearchRecipeClassifierCategoryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("review_guidance")] string ReviewGuidance);

internal sealed record SearchRecipeClassifierCountJsonResult(
    [property: JsonPropertyName("classifier")] string Classifier,
    [property: JsonPropertyName("categories")] List<SearchRecipeClassifierCategoryCountJsonResult> Categories);

internal sealed record SearchRecipeClassifierCategoryCountJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("review_guidance")] string ReviewGuidance);

internal sealed record SearchRecipeBroadCatchTaxonomyJsonResult(
    [property: JsonPropertyName("boundary_categories")] List<SearchRecipeBroadCatchBoundaryJsonResult> BoundaryCategories,
    [property: JsonPropertyName("diagnostic_behaviors")] List<SearchRecipeBroadCatchDiagnosticBehaviorJsonResult> DiagnosticBehaviors,
    [property: JsonPropertyName("triage_guidance")] string TriageGuidance);

internal sealed record SearchRecipeBroadCatchBoundaryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("expected_diagnostic_behavior")] string ExpectedDiagnosticBehavior);

internal sealed record SearchRecipeBroadCatchDiagnosticBehaviorJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description);

internal sealed record SearchRecipeStringComparisonTaxonomyJsonResult(
    [property: JsonPropertyName("domain_categories")] List<SearchRecipeStringComparisonDomainJsonResult> DomainCategories,
    [property: JsonPropertyName("triage_guidance")] string TriageGuidance);

internal sealed record SearchRecipeStringComparisonDomainJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("review_guidance")] string ReviewGuidance);

internal sealed record SearchRecipeNullableContractTaxonomyJsonResult(
    [property: JsonPropertyName("return_domains")] List<SearchRecipeNullableReturnDomainJsonResult> ReturnDomains,
    [property: JsonPropertyName("suppression_evidence")] List<SearchRecipeNullableSuppressionEvidenceJsonResult> SuppressionEvidence,
    [property: JsonPropertyName("triage_guidance")] string TriageGuidance);

internal sealed record SearchRecipeNullableReturnDomainJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("preferred_contract")] string PreferredContract);

internal sealed record SearchRecipeNullableSuppressionEvidenceJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description);

internal sealed record SearchRecipeRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult Recipe,
    [property: JsonPropertyName("scope")] SearchRecipeScopeJsonResult Scope,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("summary")] SearchRecipeRunSummaryJsonResult Summary,
    [property: JsonPropertyName("queries")] List<SearchRecipeQueryResultJsonResult> Queries);

internal sealed record SearchRecipeRunSummaryJsonResult(
    [property: JsonPropertyName("limit_per_query")] int LimitPerQuery,
    [property: JsonPropertyName("total_limit")] int? TotalLimit,
    [property: JsonPropertyName("emitted_result_count")] int EmittedResultCount,
    [property: JsonPropertyName("truncated_query_count")] int TruncatedQueryCount,
    [property: JsonPropertyName("minimum_omitted_result_count")] int MinimumOmittedResultCount,
    [property: JsonPropertyName("query_freshness")] SearchRecipeQueryFreshnessJsonResult QueryFreshness,
    [property: JsonPropertyName("cursoring_available")] bool CursoringAvailable,
    [property: JsonPropertyName("cursoring_hint")] string CursoringHint);

internal sealed record SearchRecipeQueryFreshnessJsonResult(
    [property: JsonPropertyName("positive_evidence_query_count")] int PositiveEvidenceQueryCount,
    [property: JsonPropertyName("zero_result_query_count")] int ZeroResultQueryCount,
    [property: JsonPropertyName("stale_query_names")] List<string> StaleQueryNames);

internal sealed record SearchNamedBatchRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("queries")] List<SearchNamedBatchQueryResultJsonResult> Queries);

internal sealed record SearchNamedBatchQueryResultJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("top_files")] List<SearchRecipeTopFileJsonResult> TopFiles,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("results")] List<CompactSearchResult> Results);

internal sealed record SearchRecipeQueryResultJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("recommended_labels")] List<string> RecommendedLabels,
    [property: JsonPropertyName("false_positive_guidance")] string FalsePositiveGuidance,
    [property: JsonPropertyName("risk_evidence")] List<string> RiskEvidence,
    [property: JsonPropertyName("guard_filters")] List<SearchRecipeGuardFilterJsonResult> GuardFilters,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("path_patterns")] List<string> PathPatterns,
    [property: JsonPropertyName("exclude_paths")] List<string> ExcludePaths,
    [property: JsonPropertyName("match_origins")] List<string> MatchOrigins,
    [property: JsonPropertyName("exclude_origins")] List<string> ExcludeOrigins,
    [property: JsonPropertyName("result_kinds")] List<string> ResultKinds,
    [property: JsonPropertyName("classifiers")] List<SearchRecipeClassifierJsonResult> Classifiers,
    [property: JsonPropertyName("string_comparison_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeStringComparisonTaxonomyJsonResult? StringComparisonTaxonomy,
    [property: JsonPropertyName("broad_catch_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy,
    [property: JsonPropertyName("nullable_contract_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeNullableContractTaxonomyJsonResult? NullableContractTaxonomy,
    [property: JsonPropertyName("classifier_counts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<SearchRecipeClassifierCountJsonResult>? ClassifierCounts,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("emitted_count")] int EmittedCount,
    [property: JsonPropertyName("minimum_matched_count")] int MinimumMatchedCount,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("result_limit")] int ResultLimit,
    [property: JsonPropertyName("minimum_omitted_result_count")] int MinimumOmittedResultCount,
    [property: JsonPropertyName("top_files")] List<SearchRecipeTopFileJsonResult> TopFiles,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("results")] List<CompactSearchResult> Results);

internal sealed record SearchNamedBatchCountSummaryRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("query_freshness")] SearchRecipeQueryFreshnessJsonResult QueryFreshness,
    [property: JsonPropertyName("queries")] List<SearchNamedBatchCountSummaryQueryJsonResult> Queries);

internal sealed record SearchNamedBatchCountSummaryQueryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("file_count")] int FileCount);

internal sealed record SearchRecipeCountRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult Recipe,
    [property: JsonPropertyName("scope")] SearchRecipeScopeJsonResult Scope,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("queries")] List<SearchRecipeCountQueryJsonResult> Queries);

internal sealed record SearchRecipeCountSummaryRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] string Recipe,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("query_freshness")] SearchRecipeQueryFreshnessJsonResult QueryFreshness,
    [property: JsonPropertyName("queries")] List<SearchRecipeCountSummaryQueryJsonResult> Queries);

internal sealed record SearchRecipeCountSummaryQueryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("file_count")] int FileCount);

internal sealed record SearchRecipeCountQueryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("matched_count")] int MatchedCount,
    [property: JsonPropertyName("emitted_count")] int EmittedCount,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("classifier_counts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<SearchRecipeClassifierCountJsonResult>? ClassifierCounts,
    [property: JsonPropertyName("top_files")] List<SearchRecipeTopFileJsonResult> TopFiles);

internal sealed record SearchRecipeAggregationRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult Recipe,
    [property: JsonPropertyName("scope")] SearchRecipeScopeJsonResult Scope,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("group_by")] string GroupBy,
    [property: JsonPropertyName("unique")] bool Unique,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("queries")] List<SearchRecipeAggregationQueryJsonResult> Queries);

internal sealed record SearchRecipeAggregationQueryJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("returned_groups")] int ReturnedGroups,
    [property: JsonPropertyName("total_groups")] int TotalGroups,
    [property: JsonPropertyName("groups_truncated")] bool GroupsTruncated,
    [property: JsonPropertyName("group_limit")] int GroupLimit,
    [property: JsonPropertyName("groups")] List<SearchGroupedCountItemJsonResult> Groups);

internal sealed record SearchRecipeCompactRunJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeCompactListItemJsonResult Recipe,
    [property: JsonPropertyName("scope")] SearchRecipeScopeJsonResult Scope,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("summary")] SearchRecipeRunSummaryJsonResult Summary,
    [property: JsonPropertyName("queries")] List<SearchRecipeCompactQueryResultJsonResult> Queries);

internal sealed record SearchRecipeCompactQueryResultJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("risk_evidence")] List<string> RiskEvidence,
    [property: JsonPropertyName("guard_filters")] List<SearchRecipeGuardFilterJsonResult> GuardFilters,
    [property: JsonPropertyName("path_patterns")] List<string> PathPatterns,
    [property: JsonPropertyName("exclude_paths")] List<string> ExcludePaths,
    [property: JsonPropertyName("match_origins")] List<string> MatchOrigins,
    [property: JsonPropertyName("exclude_origins")] List<string> ExcludeOrigins,
    [property: JsonPropertyName("result_kinds")] List<string> ResultKinds,
    [property: JsonPropertyName("classifiers")] List<SearchRecipeClassifierJsonResult> Classifiers,
    [property: JsonPropertyName("string_comparison_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeStringComparisonTaxonomyJsonResult? StringComparisonTaxonomy,
    [property: JsonPropertyName("broad_catch_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy,
    [property: JsonPropertyName("classifier_counts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<SearchRecipeClassifierCountJsonResult>? ClassifierCounts,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("emitted_count")] int EmittedCount,
    [property: JsonPropertyName("minimum_matched_count")] int MinimumMatchedCount,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("result_limit")] int ResultLimit,
    [property: JsonPropertyName("minimum_omitted_result_count")] int MinimumOmittedResultCount,
    [property: JsonPropertyName("top_files")] List<SearchRecipeTopFileJsonResult> TopFiles,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("results")] List<SearchRecipeCompactResultJsonResult> Results);

internal sealed record SearchRecipeTopFileJsonResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("count")] int Count);

internal sealed record SearchRecipeCompactResultJsonResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("lang")] string? Lang,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("risk_evidence")] List<string> RiskEvidence,
    [property: JsonPropertyName("chunk_start_line")] int ChunkStartLine,
    [property: JsonPropertyName("chunk_end_line")] int ChunkEndLine,
    [property: JsonPropertyName("match_lines")] List<int> MatchLines,
    [property: JsonPropertyName("enclosing_symbol_name")] string? EnclosingSymbolName,
    [property: JsonPropertyName("enclosing_symbol_kind")] string? EnclosingSymbolKind);

internal sealed record SearchIssueDraftExportJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult? Recipe,
    [property: JsonPropertyName("recipe_summary")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeCompactListItemJsonResult? RecipeSummary,
    [property: JsonPropertyName("metadata_mode")] string MetadataMode,
    [property: JsonPropertyName("scope")] SearchRecipeScopeJsonResult? Scope,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("query_freshness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeQueryFreshnessJsonResult? QueryFreshness,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftPreflightSummaryJsonResult DuplicatePreflight,
    [property: JsonPropertyName("drafts")] List<SearchIssueDraftJsonResult> Drafts);

internal sealed record SearchIssueDraftJsonResult(
    [property: JsonPropertyName("draft_id")] string DraftId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("labels")] List<string> Labels,
    [property: JsonPropertyName("missing_labels")] List<string> MissingLabels,
    [property: JsonPropertyName("label_warning")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LabelWarning,
    [property: JsonPropertyName("evidence_paths")] List<string> EvidencePaths,
    [property: JsonPropertyName("evidence")] List<SearchIssueDraftEvidenceJsonResult> Evidence,
    [property: JsonPropertyName("triage")] IssueDraftTriageMetadataJsonResult Triage,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("source")] SearchIssueDraftSourceJsonResult Source,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftDuplicatePreflightJsonResult DuplicatePreflight);

internal sealed record SearchIssueDraftEvidenceJsonResult(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("snippet")] string Snippet);

internal sealed record SearchIssueDraftSourceJsonResult(
    [property: JsonPropertyName("recipe")] string? Recipe,
    [property: JsonPropertyName("query_name")] string? QueryName,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("false_positive_guidance")] string FalsePositiveGuidance,
    [property: JsonPropertyName("risk_evidence")] List<string> RiskEvidence,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("result_limit")] int ResultLimit,
    [property: JsonPropertyName("omitted_count")] int OmittedCount,
    [property: JsonPropertyName("minimum_omitted_result_count")] int MinimumOmittedResultCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next_cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);

internal sealed record SearchRecipeScopeJsonResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path_patterns")] List<string> PathPatterns,
    [property: JsonPropertyName("exclude_paths")] List<string> ExcludePaths,
    [property: JsonPropertyName("exclude_tests")] bool ExcludeTests,
    [property: JsonPropertyName("recipe_default_path_patterns")] List<string> RecipeDefaultPathPatterns,
    [property: JsonPropertyName("recipe_default_exclude_paths")] List<string> RecipeDefaultExcludePaths,
    [property: JsonPropertyName("excluded_diagnostics")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<SearchRecipeExcludedDiagnosticJsonResult>? ExcludedDiagnostics);

internal sealed record SearchRecipeExcludedDiagnosticJsonResult(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("patterns")] List<string> Patterns,
    [property: JsonPropertyName("description")] string Description);
