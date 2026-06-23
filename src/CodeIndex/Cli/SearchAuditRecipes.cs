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
    private static readonly string[] DefaultSourcePathPatterns = ["src/**"];
    private static readonly string[] DefaultSourceExcludePaths =
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
        ".codex/**",
        ".github/**"
    ];

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
                    "catch (",
                    "Find C# catch clauses that may be empty, overly broad, or swallowing diagnostic context.",
                    ["audit", "bug"],
                    "False positives include catch blocks that rethrow, translate exceptions safely, or intentionally ignore best-effort cleanup failures.")
                {
                    MatchOrigins = ["code"],
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
                    "False positives include top-level command boundaries that intentionally normalize all recoverable failures.")
                {
                    MatchOrigins = ["code"],
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
                    RiskEvidence =
                    [
                        "risk: raw System.Text.RegularExpressions.Regex construction should show an explicit timeout, non-backtracking mode, or bounded input.",
                        "positive: files with a BoundedRegex alias are likely using the repository wrapper rather than raw BCL Regex."
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
            ]),
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
                    "regex-construction",
                    "new Regex(",
                    "Find direct regex construction that may need a timeout, non-backtracking mode, or bounded input review.",
                    ["audit", "performance"],
                    "False positives include precompiled bounded patterns with explicit timeouts or tiny trusted inputs.")
                {
                    RiskEvidence =
                    [
                        "risk: raw System.Text.RegularExpressions.Regex construction should show an explicit timeout, non-backtracking mode, or bounded input.",
                        "positive: files with a BoundedRegex alias are likely using the repository wrapper rather than raw BCL Regex."
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
                    "cancellation-token-none",
                    "CancellationToken.None",
                    "Find production paths that ignore caller cancellation and may need a propagated token.",
                    ["audit", "bug"],
                    "False positives include intentionally detached background work and APIs without a meaningful caller token."),
                new(
                    "sync-over-async",
                    "GetAwaiter().GetResult",
                    "Find sync-over-async waits that may deadlock or hide cancellation and timeout behavior.",
                    ["audit", "bug"],
                    "False positives include process-exit boundaries and test helpers that intentionally bridge sync APIs.")
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
        List<SearchAuditRecipeQuery> queries) => new(name, description, queries)
        {
            DefaultPathPatterns = [.. DefaultSourcePathPatterns],
            DefaultExcludePaths = [.. DefaultSourceExcludePaths],
        };

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
    public List<string> PathPatterns { get; init; } = [];
    public List<string> ExcludePaths { get; init; } = [];
    public List<string> MatchOrigins { get; init; } = [];
    public List<string> ExcludeOrigins { get; init; } = [];
    public List<string> ResultKinds { get; init; } = [];
}

internal sealed record SearchRecipeListJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("recipes")] List<SearchRecipeListItemJsonResult> Recipes);

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
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring);

internal sealed record SearchRecipeGuardFilterJsonResult(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("option")] string Option);

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
    [property: JsonPropertyName("emitted_result_count")] int EmittedResultCount,
    [property: JsonPropertyName("truncated_query_count")] int TruncatedQueryCount,
    [property: JsonPropertyName("minimum_omitted_result_count")] int MinimumOmittedResultCount,
    [property: JsonPropertyName("cursoring_available")] bool CursoringAvailable,
    [property: JsonPropertyName("cursoring_hint")] string CursoringHint);

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
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("result_limit")] int ResultLimit,
    [property: JsonPropertyName("minimum_omitted_result_count")] int MinimumOmittedResultCount,
    [property: JsonPropertyName("top_files")] List<SearchRecipeTopFileJsonResult> TopFiles,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("results")] List<CompactSearchResult> Results);

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
    [property: JsonPropertyName("count")] int Count,
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
    [property: JsonPropertyName("chunk_start_line")] int ChunkStartLine,
    [property: JsonPropertyName("chunk_end_line")] int ChunkEndLine,
    [property: JsonPropertyName("match_lines")] List<int> MatchLines,
    [property: JsonPropertyName("enclosing_symbol_name")] string? EnclosingSymbolName,
    [property: JsonPropertyName("enclosing_symbol_kind")] string? EnclosingSymbolKind);

internal sealed record SearchIssueDraftExportJsonResult(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("recipe")] SearchRecipeListItemJsonResult? Recipe,
    [property: JsonPropertyName("scope")] SearchRecipeScopeJsonResult? Scope,
    [property: JsonPropertyName("query_count")] int QueryCount,
    [property: JsonPropertyName("result_count")] int ResultCount,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftPreflightSummaryJsonResult DuplicatePreflight,
    [property: JsonPropertyName("drafts")] List<SearchIssueDraftJsonResult> Drafts);

internal sealed record SearchIssueDraftJsonResult(
    [property: JsonPropertyName("draft_id")] string DraftId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("labels")] List<string> Labels,
    [property: JsonPropertyName("evidence_paths")] List<string> EvidencePaths,
    [property: JsonPropertyName("triage")] IssueDraftTriageMetadataJsonResult Triage,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("source")] SearchIssueDraftSourceJsonResult Source,
    [property: JsonPropertyName("duplicate_preflight")] SuggestionIssueDraftDuplicatePreflightJsonResult DuplicatePreflight);

internal sealed record SearchIssueDraftSourceJsonResult(
    [property: JsonPropertyName("recipe")] string? Recipe,
    [property: JsonPropertyName("query_name")] string? QueryName,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("false_positive_guidance")] string FalsePositiveGuidance,
    [property: JsonPropertyName("risk_evidence")] List<string> RiskEvidence,
    [property: JsonPropertyName("exact_substring")] bool ExactSubstring,
    [property: JsonPropertyName("result_count")] int ResultCount);

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
