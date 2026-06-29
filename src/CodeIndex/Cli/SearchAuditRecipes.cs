using CodeIndex;
using CodeIndex.Database;
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
    private static readonly string[] DefaultExecutableExcludeOriginsValue = [SearchMatchClassifier.HelpText];
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

    internal static IReadOnlyList<string> DefaultSourcePathPatterns => DefaultSourcePathPatternsValue;
    internal static IReadOnlyList<string> DefaultSourceExcludePaths => DefaultSourceExcludePathsValue;

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
                "positive: BoundedRegex aliases, explicit timeout overloads, or tightly bounded trusted inputs can make a hit intentional."
            ],
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
                "positive: BoundedRegex aliases and instance names ending in Regex are filtered out; remaining hits should be classified as timeout-backed, generated/precompiled, trusted small input, or non-matching helpers such as Escape."
            ],
            MatchOrigins = ["code"],
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
                    "False positives include bounded test fixtures and small files guarded by explicit size checks."),
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
                },
                new(
                    "cancellation-gap",
                    "CancellationToken.None",
                    "Find async or stream paths that may be ignoring caller cancellation.",
                    ["audit", "bug"],
                    "False positives include intentionally fire-and-forget work and APIs that have no meaningful caller cancellation token."),
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
                },
                new(
                    "process-start-direct",
                    "Process.Start",
                    "Find direct process launches that may need a shared safe-launch wrapper or explicit argument handling.",
                    ["audit", "security"],
                    "False positives include simple URL/document open helpers or test fixtures with trusted inputs."),
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
                    ExcludePaths = [BoundedRegexPath],
                    RiskEvidence =
                    [
                        "risk: raw System.Text.RegularExpressions.Regex construction should show an explicit timeout, non-backtracking mode, or bounded input.",
                        "positive: bounded-wrapper aliases are reported by bounded-regex-alias instead of this raw construction query."
                    ],
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
                },
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
                    "False positives include non-secret feature flags and documented public configuration."),
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
                    ExactSubstring: false),
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
            "auth-token-audit",
            "Audit credential and auth-token material without the parser, protocol, LSP, and cancellation-token noise from bare token searches.",
            [
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
            ]),
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
                        "risk: MaxValue sentinels can bypass practical bounds for allocation, traversal, timeout, or query limits.",
                        "positive: explicit clamping, saturation helper names, or test-only probes are safer evidence."
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
            [
                new(
                    "json-document-parse",
                    "JsonDocument.Parse",
                    "Find DOM parsing via JsonDocument.Parse that may need input-size limits or streaming alternatives.",
                    ["audit", "bug"],
                    "False positives include deliberately bounded callers and parsing of already-small generated payloads."),
                new(
                    "json-node-parse",
                    "JsonNode.Parse",
                    "Find mutable DOM parsing via JsonNode.Parse that may need input-size limits, depth limits, or streaming alternatives.",
                    ["audit", "bug"],
                    "False positives include tests, bounded configuration files, and already-size-limited payloads."),
                new(
                    "json-serializer-deserialize",
                    "JsonSerializer.Deserialize",
                    "Find serializer materialization paths that may need payload bounds, streaming, or explicit JsonSerializerOptions review.",
                    ["audit", "bug"],
                    "False positives include bounded local files, test fixtures, and deserialization of tiny protocol envelopes."),
                new(
                    "json-async-deserialize",
                    "DeserializeAsyncEnumerable",
                    "Find streaming JSON deserialization paths that may need cancellation, item limits, or backpressure review.",
                    ["audit", "performance"],
                    "False positives include already-cancelable readers with explicit item budgets.")
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
                    ExcludePaths = [BoundedRegexPath],
                    RiskEvidence =
                    [
                        "risk: raw System.Text.RegularExpressions.Regex construction should show an explicit timeout, non-backtracking mode, or bounded input.",
                        "positive: bounded-wrapper aliases are reported by bounded-regex-alias instead of this raw construction query."
                    ],
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
                },
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
            "xml-parser-security",
            "Audit XML parser APIs and DTD/entity settings for XXE and external-resolution regressions.",
            [
                new(
                    "xml-reader-settings",
                    "XmlReaderSettings",
                    "Find XML reader settings that should keep DtdProcessing disabled or ignored and avoid external entity resolution.",
                    ["audit", "security"],
                    "Expected safe settings include `DtdProcessing.Ignore` or `Prohibit` and no external resolver; tests and safe fixture parsers may be false positives."),
                new(
                    "dtd-processing",
                    "DtdProcessing",
                    "Find DTD handling changes that may re-enable entity expansion or unsafe external document access.",
                    ["audit", "security"],
                    "Review for `Ignore` or `Prohibit`; `Parse` requires strong justification, bounded input, and resolver controls."),
                new(
                    "xml-resolver",
                    "XmlResolver",
                    "Find XML resolver configuration that may allow network or filesystem entity resolution.",
                    ["audit", "security"],
                    "Safe paths usually set the resolver to null or use a tightly bounded resolver.")
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
            "bounded-read-evidence",
            "Positive audit searches for max-byte file-read helpers, explicit file-open policy, and bounded downstream accumulators.",
            [
                new(
                    "bounded-file-open-helper",
                    "BoundedFile.OpenRead",
                    "Find reads routed through the shared file-open helper so audits can see the explicit share mode, long-path normalization, and bounded read category.",
                    ["audit", "performance"],
                    "Expected positive evidence includes length-checked text reads, fixed-prefix probes, log tails, hash streams, and trusted archive sources that enforce their byte limits at the caller.",
                    ExactSubstring: false)
                {
                    MatchOrigins = ["code"],
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
                }
            ]),
        AllScopedRecipe(
            "phrase-risk-patterns",
            "Precision-focused audit searches for noisy code phrases, broad words, and configuration text that need semantic triage facets.",
            [
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
            ]),
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

            var root = JsonNode.Parse(
                text,
                documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
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
    public SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy { get; init; }
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
    [property: JsonPropertyName("broad_catch_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring);

internal sealed record SearchRecipeGuardFilterJsonResult(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("option")] string Option);

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
    [property: JsonPropertyName("broad_catch_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy,
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
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult Recipe,
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
    [property: JsonPropertyName("broad_catch_taxonomy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SearchRecipeBroadCatchTaxonomyJsonResult? BroadCatchTaxonomy,
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
