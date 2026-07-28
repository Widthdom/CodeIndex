using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Cli;

public static partial class ConsoleUi
{
    public static void PrintUsage(bool showBanner = true)
        => PrintUsageBrief(showBanner);

    public static void PrintUsageBrief(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx <projectPath>");
        Console.WriteLine("  cdidx <command> [options]");
        Console.WriteLine("  cdidx --help-all");
        Console.WriteLine("  cdidx --help-flags");
        Console.WriteLine();
        PrintCommandSummary();
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help-all` for every command and option, `cdidx --help-flags` for shared flags, or `cdidx <command> --help` for one command.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  cdidx ./myproject");
        Console.WriteLine("  cdidx search \"authenticate\"");
        Console.WriteLine("  cdidx inspect Run --body --exclude-tests");
    }

    public static void PrintUsageFull(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        var helpWidth = ShouldUseInteractiveConsole() ? Math.Min(GetWindowWidth(), 120) : 0;
        void WriteHelpLine(string line = "")
        {
            if (helpWidth <= 0)
            {
                Console.WriteLine(line);
                return;
            }

            foreach (var wrapped in WrapHelpLine(line, helpWidth))
                Console.WriteLine(wrapped);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx <projectPath>");
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (HiddenCommandUsageNames.Contains(name))
                continue;

            WriteHelpLine($"  {RenderUsageLineFromSchema(name, usage)}");
        }
        Console.WriteLine();
        PrintCommandSummary();
        Console.WriteLine();
        PrintFlagReference(WriteHelpLine);
        Console.WriteLine();
        PrintExamples();
    }

    public static void PrintFlagUsage(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        var helpWidth = ShouldUseInteractiveConsole() ? Math.Min(GetWindowWidth(), 120) : 0;
        void WriteHelpLine(string line = "")
        {
            if (helpWidth <= 0)
            {
                Console.WriteLine(line);
                return;
            }

            foreach (var wrapped in WrapHelpLine(line, helpWidth))
                Console.WriteLine(wrapped);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx --help-flags");
        Console.WriteLine();
        PrintFlagReference(WriteHelpLine);
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help-all` to show commands and examples.");
    }

    private static void PrintCommandSummary()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  help <command> [subcommand]  Show help without running the command");
        Console.WriteLine("  index <projectPath>        Build or update the index for a project");
        Console.WriteLine("  hooks                      Install, uninstall, or inspect git hook integration");
        Console.WriteLine("  backfill-fold              Upgrade folded-name columns in an existing index DB");
        Console.WriteLine("  optimize                   Optimize FTS5 segments in an existing index DB");
        Console.WriteLine("  vacuum                     Reclaim free SQLite pages from an existing index DB");
        Console.WriteLine("  search <query>             Full-text search across indexed chunks");
        Console.WriteLine("  recipes                    List built-in search audit recipes");
        Console.WriteLine("  audit <recipe>             Run a built-in search audit recipe");
        Console.WriteLine("  definition <query>         Resolve symbol definitions with extracted ranges");
        Console.WriteLine("  goto <query>               Return one best LSP Location for a definition");
        Console.WriteLine("  references <query>         Find indexed references for a symbol (--kind uses reference kind)");
        Console.WriteLine("  callers <query>            Find callers of a symbol (--kind uses reference kind)");
        Console.WriteLine("  callees <query>            Find callees used by a caller (--kind uses reference kind)");
        Console.WriteLine("  symbols [query]            Search symbols (functions, classes, imports)");
        Console.WriteLine("  files [query|glob]         List indexed files (* and ? positionals use path-glob semantics)");
        Console.WriteLine("  find <query>               Find literal substring matches inside known indexed files");
        Console.WriteLine("  excerpt <path>             Reconstruct a line-range excerpt from indexed chunks");
        Console.WriteLine("  map                        Show a repo-level overview for AI orientation");
        Console.WriteLine("  inspect <query>            Bundle definition, graph, and nearby symbol context");
        Console.WriteLine("  outline <path>             Show a file outline ordered by line, start column, kind, and name");
        Console.WriteLine("  status                     Show database statistics; add --check for freshness, --config for effective config, --explain <field> for field details, or --log-path for logs");
        Console.WriteLine("  workspace                  List manifest members and manage the active workspace");
        Console.WriteLine("  config show                Show resolved workspace config and precedence");
        Console.WriteLine("  upgrade                    Check for and install the latest release via install.sh");
        Console.WriteLine("  validate-config            Validate .cdidx/config.json or .cdidxrc.json");
        Console.WriteLine("  doctor                     Print a redacted environment summary or env-var inventory for bug reports");
        Console.WriteLine("  db --integrity-check       Run SQLite `PRAGMA integrity_check` and report findings");
        Console.WriteLine("  db schema                  Dump SQLite schema entries and PRAGMA user_version");
        Console.WriteLine("  db prune --dry-run|--apply Count or delete orphaned DB rows");
        Console.WriteLine("  diff <db1> <db2>           Compare two index databases; exit 0 identical, 1 drift, 2 schema mismatch, 3 unreadable");
        Console.WriteLine("  report --output <bundle.tgz> Build a redacted crash-repro tarball without replacing existing output; use --overwrite to opt in");
        Console.WriteLine("  validate                   Report encoding issues (U+FFFD origin/severity, BOM, null bytes, mixed line endings, UTF-16 BOM, likely non-UTF8)");
        Console.WriteLine("  impact <query>             Show transitive callers; type queries may return heuristic file-level dependency hints");
        Console.WriteLine("  deps                       Show file-level dependency edges from the reference graph");
        Console.WriteLine("  unused                     Find symbols defined but never referenced (dead code)");
        Console.WriteLine("  hotspots                   Find high-impact symbols; duplicate-name families may fall back conservatively");
        Console.WriteLine("  suggestions                Add, list, inspect, and export local suggestion history");
        Console.WriteLine("  export                     Export ctags or a portable CodeIndex archive");
        Console.WriteLine("  import                     Import a portable CodeIndex archive");
        Console.WriteLine("  languages                  List supported languages and their capabilities");
        Console.WriteLine("  batch                      Run newline-delimited JSON query commands with one DB connection");
        Console.WriteLine("  mcp                        Start MCP server (for AI tools: Claude, Cursor, etc.)");
        Console.WriteLine("  lsp                        Start LSP server over stdio (for LSP-native editors)");
        Console.WriteLine("  completions <shell>        Generate shell completions for bash, zsh, fish, or PowerShell");
        Console.WriteLine("  license                    Show licensing, trademark, and commercial-use summary");
    }

    private static void PrintFlagReference(Action<string> WriteHelpLine)
    {
        foreach (var (heading, flags) in CliFlagSchema.GetSharedHelpSections())
        {
            if (flags.Count == 0)
                continue;

            Console.WriteLine();
            Console.WriteLine(heading);
            foreach (var flag in flags)
            {
                WriteHelpLine($"  {FormatSharedFlagToken(flag)}");
                WriteHelpLine($"      {flag.Description}");
            }

            if (string.Equals(heading, "Index and update options:", StringComparison.Ordinal))
                WriteHelpLine("  .cdidxignore  Optional project-local ignore file; loaded after .gitignore in each directory");
            if (string.Equals(heading, "Query options:", StringComparison.Ordinal))
                WriteHelpLine($"  {FormatRelatedOptionTokens("search", "--limit", "--top", "--max-results")}  Equivalent result-limit options");
        }

        Console.WriteLine();
        Console.WriteLine("Built-in help options:");
        WriteHelpLine("  --help, -h  Show help");
        WriteHelpLine("  --help-all  Show every command, usage form, and shared option");
        WriteHelpLine("  --help-flags  Show shared options");
        WriteHelpLine("  --version, -V  Show version information");
        WriteHelpLine("  --license  Show licensing, trademark, and commercial-use summary");
        WriteHelpLine("  --completions <bash|zsh|fish|powershell>  Generate a shell completion script");

        Console.WriteLine();
        Console.WriteLine("Update workflows:");
        WriteHelpLine("  Use --commits with a project path after normal commits; git diff sees rename/delete paths too.");
        WriteHelpLine("  Use --changed-between <old-ref> <new-ref> after switching branches to refresh only changed files.");
        WriteHelpLine("  Use --files only for known in-place edits or new files; old rename/delete paths stay indexed unless also listed.");
        WriteHelpLine("  Incremental writes optimize FTS5 opportunistically after a small maintenance threshold; run `cdidx optimize` for manual maintenance.");
        Console.WriteLine();
        WriteHelpLine("  Note: if a query itself starts with '-', pass it with --query <query> or -- <query>; for option values that start with '--', use --opt=<value>.");
    }

    private static string FormatSharedFlagToken(CliFlag flag)
    {
        var names = flag.ShortName is null ? flag.Name : $"{flag.Name}, {flag.ShortName}";
        var placeholder = flag.CommandValueDomains.Count > 0
            ? "<command-specific-value>"
            : flag.GetValuePlaceholder(string.Empty);
        return placeholder is null ? names : $"{names} {placeholder}";
    }

    private static string FormatRelatedOptionTokens(string command, params string[] flagNames) =>
        string.Join(", ", flagNames.Select(flagName =>
            CliFlagSchema.GetUsageTokenForCommand(command, flagName)));

    private static void PrintExamples()
    {
        Console.WriteLine("Examples:");
        Console.WriteLine("  cdidx ./myproject                             Index a project");
        Console.WriteLine("  cdidx backfill-fold                           Upgrade folded-name columns in an existing DB");
        Console.WriteLine("  cdidx optimize --dry-run --json               Preview FTS5 optimization work without writing");
        Console.WriteLine("  cdidx optimize                                Optimize FTS5 segments in an existing DB");
        Console.WriteLine("  cdidx vacuum --dry-run --json                 Estimate DB free pages and maintenance guidance");
        Console.WriteLine("  cdidx index ./myproject --commits abc123      Update DB from one commit");
        Console.WriteLine("  cdidx index ./myproject --commits abc123 def456");
        Console.WriteLine("                                              Update DB from multiple commits");
        Console.WriteLine("  cdidx index ./myproject --changed-between main feature");
        Console.WriteLine("                                              Update DB from files changed between two refs");
        Console.WriteLine("  cdidx index ./myproject --files src/app.cs    Update specific files");
        Console.WriteLine("  cdidx index ./myproject --watch               Run an initial scan, then keep the index live as files change (Ctrl+C to stop)");
        Console.WriteLine("  cdidx export ctags --output tags              Export editor tags for Vim, Emacs, and Sublime");
        Console.WriteLine("  cdidx export codeindex.cdidx.zip              Export a portable CodeIndex archive");
        Console.WriteLine("  cdidx export codeindex.cdidx.zip --overwrite  Explicitly replace an existing portable archive");
        Console.WriteLine("  cdidx import codeindex.cdidx.zip              Import a portable CodeIndex archive");
        Console.WriteLine("  cdidx import codeindex.cdidx.zip --dry-run    Validate an archive without replacing the DB");
        Console.WriteLine("  cdidx search \"authenticate\"                    Full-text search");
        Console.WriteLine("  cdidx search \"auth*\"                          Prefix shorthand in literal-safe mode");
        Console.WriteLine("  cdidx search --query --path --path README.md   Search for a literal option token");
        Console.WriteLine("  cdidx search --named-query pack=\"dotnet pack\" --named-query push=\"nuget push\" --format compact");
        Console.WriteLine("                                              Run named ad hoc searches with compact snippets");
        Console.WriteLine("  cdidx search \"Run();\" --exact-substring        Case-sensitive exact substring search");
        Console.WriteLine("  cdidx search \"File.ReadAllText\" --exact-substring --reject-before \"Length\" --guard-window 8");
        Console.WriteLine("                                              Find calls without a nearby preceding size guard");
        Console.WriteLine("  cdidx search authenticate --json=array         Emit search results as one JSON array");
        Console.WriteLine("  cdidx search authenticate --profile            Append SQL profile JSON for slow-query debugging");
        Console.WriteLine("  cdidx search authenticate --verbose            Emit query debug diagnostics on stderr");
        Console.WriteLine("  cdidx definition ResolveGitCommonDir --body   Show a symbol definition and body");
        Console.WriteLine("  cdidx references ResolveGitCommonDir          Find indexed references");
        Console.WriteLine("  cdidx references DbContext --kind instantiate Filter constructor sites by reference kind");
        Console.WriteLine("  cdidx references e --path dist/app.js --max-line-width 120");
        Console.WriteLine("                                              Clamp a minified single-line context window");
        Console.WriteLine("  cdidx excerpt src/app.js --start 120 --focus-column 88 --max-line-width 120");
        Console.WriteLine("                                              Keep the requested token visible inside a long line");
        Console.WriteLine("  cdidx callers ResolveGitCommonDir             Find callers");
        Console.WriteLine("  cdidx callees AddToGitExclude                 Find callees used by a caller");
        Console.WriteLine("  cdidx symbols Run --exact-name                Exact symbol-name match");
        Console.WriteLine("  cdidx symbols UserService --kind class        Find class definitions");
        Console.WriteLine("  cdidx find guard --path src/Auth.cs --after 2 Find literal matches inside a known file");
        Console.WriteLine("  cdidx find --path README.md -- --path         Search a literal that starts with '-'");
        Console.WriteLine("  cdidx excerpt src/app.cs --start 10 --end 20  Reconstruct a file excerpt");
        Console.WriteLine("  cdidx map --path src/ --exclude-tests          Show a repo map for source code");
        Console.WriteLine("  cdidx inspect Run --body --exclude-tests       Inspect one symbol with bundled context");
        Console.WriteLine("  cdidx outline src/app.cs --json                Symbol outline of a single file");
        Console.WriteLine("  cdidx deps --path src/ --exclude-tests          Show file-level dependency edges");
        Console.WriteLine("  cdidx deps --reverse --path src/app.cs          Show what depends on a file");
        Console.WriteLine("  cdidx unused --lang csharp --actionable          Find private cleanup candidates");
        Console.WriteLine("  cdidx hotspots --lang csharp --exclude-tests    Find high-impact symbols with conservative duplicate fallback");
        Console.WriteLine("  cdidx hotspots --group-by=file --json           Compare hotspot volume by target file");
        Console.WriteLine("  cdidx hotspots --group-by-name --exclude-tests  Collapse same-name hotspots across files");
        Console.WriteLine("  cdidx impact Run --max-hops 0 --exclude-tests  Resolve a symbol without traversing callers");
        Console.WriteLine("  cdidx impact FolderDiffService --json           Type query may return heuristic file-level dependency hints");
        Console.WriteLine("  cdidx files --lang python                      List Python files");
        Console.WriteLine("  cdidx files --since 2024-01-01                 Files modified since a date");
        Console.WriteLine("  cdidx status --json                            DB stats as JSON");
        Console.WriteLine("  cdidx status --config                          Effective configuration as JSON");
        Console.WriteLine("  cdidx validate-config                          Validate checked-in config");
        Console.WriteLine("  cdidx languages                                Show supported languages");
        Console.WriteLine("  cdidx --completions zsh > ~/.zfunc/_cdidx      Generate a zsh completion script");
        Console.WriteLine("  cdidx license                                  Show licensing and commercial-use terms");
    }

    internal static IReadOnlyList<string> WrapHelpLine(string line, int maxWidth)
    {
        if (maxWidth <= 0 || line.Length <= maxWidth)
            return [line];

        var continuationIndent = GetHelpContinuationIndent(line);
        return WrapLineByWords(line, maxWidth, continuationIndent);
    }

    private static string GetHelpContinuationIndent(string line)
    {
        var leading = 0;
        while (leading < line.Length && line[leading] == ' ')
            leading++;

        for (var i = leading + 1; i < line.Length - 1; i++)
        {
            if (line[i] == ' ' && line[i + 1] == ' ')
            {
                while (i < line.Length && line[i] == ' ')
                    i++;
                if (i < line.Length)
                    return new string(' ', i);
                break;
            }
        }

        return new string(' ', Math.Min(leading + 2, 8));
    }

    private static IReadOnlyList<string> WrapLineByWords(string line, int maxWidth, string continuationIndent)
    {
        maxWidth = Math.Max(1, maxWidth);
        if (continuationIndent.Length >= maxWidth)
            continuationIndent = new string(' ', Math.Max(0, Math.Min(2, maxWidth - 1)));

        var lines = new List<string>();
        var current = line;
        while (current.Length > maxWidth)
        {
            var breakAt = current.LastIndexOf(' ', Math.Min(maxWidth, current.Length - 1));
            if (breakAt <= 0 || current[..breakAt].Trim().Length == 0)
                breakAt = maxWidth;

            lines.Add(current[..breakAt].TrimEnd());
            var nextStart = breakAt < current.Length && current[breakAt] == ' ' ? breakAt + 1 : breakAt;
            current = continuationIndent + current[nextStart..].TrimStart();
        }

        lines.Add(current);
        return lines;
    }

    public static void PrintLicenseSummary()
    {
        Console.WriteLine("cdidx / CodeIndex license");
        Console.WriteLine();
        Console.WriteLine("License: Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2)");
        Console.WriteLine("Copyright: Copyright 2026 Widthdom.");
        Console.WriteLine("Summary: use, modification, and distribution are allowed for non-competing purposes, including internal, commercial, AI, IDE, MCP, CI, and scripting integrations.");
        Console.WriteLine("Competing commercial products or services require a separate written agreement with Widthdom.");
        Console.WriteLine("Names and trademarks: CodeIndex and cdidx are not licensed for derivative product, package, service, or endorsement branding.");
        Console.WriteLine();
        Console.WriteLine("See LICENSE, LICENSES/FSL-1.1-ALv2.txt, LICENSES/Apache-2.0.txt, COMMERCIAL_LICENSE.md, INTEGRATION_POLICY.md, and TRADEMARKS.md for the controlling terms.");
    }

    internal static LicenseJsonResult BuildLicenseJsonResult() =>
        new(
            JsonOutputContract.ApiVersion,
            new LicenseTermsJsonResult(
                "FSL-1.1-ALv2",
                "Functional Source License, Version 1.1, ALv2 Future License",
                "Apache-2.0",
                "LICENSE"),
            "Copyright 2026 Widthdom.",
            new LicenseCommercialUseJsonResult(
                NonCompetingUseAllowed: true,
                CompetingProductsOrServicesRequireSeparateAgreement: true,
                "Use, modification, and distribution are allowed for non-competing purposes, including internal, commercial, AI, IDE, MCP, CI, and scripting integrations."),
            new LicenseTrademarkJsonResult(
                ["CodeIndex", "cdidx"],
                DerivativeBrandingAllowed: false,
                EndorsementBrandingAllowed: false,
                "CodeIndex and cdidx are not licensed for derivative product, package, service, or endorsement branding."),
            [
                "LICENSE",
                "LICENSES/FSL-1.1-ALv2.txt",
                "LICENSES/Apache-2.0.txt",
                "COMMERCIAL_LICENSE.md",
                "INTEGRATION_POLICY.md",
                "TRADEMARKS.md",
            ]);

    public static string? GetUsageLine(string command)
    {
        command = NormalizeCommandUsageName(command);
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (string.Equals(name, command, StringComparison.Ordinal))
                return RenderUsageLineFromSchema(command, usage);
        }

        return null;
    }

    public static bool PrintCommandUsage(string command)
    {
        command = NormalizeCommandUsageName(command);
        var usages = GetCommandUsageLines(command);
        if (usages.Count == 0)
            return false;

        Console.WriteLine("Usage:");
        foreach (var usage in usages)
            Console.WriteLine($"  {usage}");
        var schemaCommand = GetFlagSchemaCommandName(command);
        var helpFlags = string.Equals(command, schemaCommand, StringComparison.Ordinal)
            && CliFlagSchema.HasAuthoritativeHelpOptions(schemaCommand)
                ? CliFlagSchema.GetHelpFlagsForCommand(schemaCommand)
                : [];
        if (helpFlags.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Options:");
            foreach (var flag in helpFlags)
            {
                var names = flag.ShortName is null ? flag.Name : $"{flag.Name}, {flag.ShortName}";
                var projectionFields = string.Equals(flag.Name, "--fields", StringComparison.Ordinal)
                                       && ProjectionFieldRegistry.SupportsCommand(schemaCommand);
                var valuePlaceholder = projectionFields
                    ? ProjectionFieldRegistry.GetHelpValuePlaceholder(schemaCommand)
                    : flag.GetValuePlaceholder(schemaCommand);
                var description = projectionFields
                    ? ProjectionFieldRegistry.GetHelpDescription(schemaCommand)
                    : flag.GetDescription(schemaCommand);
                var token = valuePlaceholder is null ? names : $"{names} {valuePlaceholder}";
                Console.WriteLine($"  {token}");
                Console.WriteLine($"      {description}");
            }
        }
        var notes = GetCommandUsageNotes(command);
        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notes:");
            foreach (var note in notes)
                Console.WriteLine($"  {note}");
        }
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help` to show all commands and shared options.");
        return true;
    }

    private static IReadOnlyList<string> GetCommandUsageLines(string command)
    {
        command = NormalizeCommandUsageName(command);
        var usages = new List<string>();
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (string.Equals(name, command, StringComparison.Ordinal)
                || string.Equals(command, "index", StringComparison.Ordinal) && name.StartsWith("index-", StringComparison.Ordinal))
            {
                usages.Add(RenderUsageLineFromSchema(command, usage));
            }
        }

        return usages;
    }

    private static string RenderUsageLineFromSchema(string command, string usage)
    {
        var schemaCommand = GetFlagSchemaCommandName(command);
        foreach (var flag in CliFlagSchema.GetHelpFlagsForCommand(schemaCommand))
        {
            if (flag.ValueDomain is null
                && flag.CommandValueDomains.Count == 0
                && flag.CommandValuePlaceholders.Count == 0)
                continue;

            var placeholder = flag.GetValuePlaceholder(schemaCommand);
            if (placeholder is null)
                continue;

            usage = ReplaceUsagePlaceholder(usage, flag.Name, placeholder);
        }

        return usage;
    }

    private static string ReplaceUsagePlaceholder(string usage, string flagName, string replacement)
    {
        var searchStart = 0;
        var prefix = $"{flagName} ";
        while (searchStart < usage.Length)
        {
            var flagStart = usage.IndexOf(prefix, searchStart, StringComparison.Ordinal);
            if (flagStart < 0)
                break;

            var valueStart = flagStart + prefix.Length;
            if (valueStart >= usage.Length || usage[valueStart] != '<')
            {
                searchStart = valueStart;
                continue;
            }

            var valueEnd = usage.IndexOf('>', valueStart);
            if (valueEnd < 0)
                break;

            usage = usage[..valueStart] + replacement + usage[(valueEnd + 1)..];
            searchStart = valueStart + replacement.Length;
        }

        return usage;
    }

    private static IReadOnlyList<string> GetCommandUsageNotes(string command)
    {
        command = NormalizeCommandUsageName(command);
        var notes = new List<string>();
        foreach (var (name, note) in CommandUsageNotes)
        {
            if (string.Equals(name, command, StringComparison.Ordinal))
                notes.Add(note);
        }

        return notes;
    }

    private static string GetFlagSchemaCommandName(string command)
    {
        if (command.StartsWith("db-", StringComparison.Ordinal))
            return "db";
        if (command.StartsWith("hooks-", StringComparison.Ordinal))
            return "hooks";
        return command == "--completions" ? "completions" : command;
    }

    private static string NormalizeCommandUsageName(string command) =>
        CliCommandCatalog.NormalizePublicCommandName(command);

    // --- Did-you-mean / もしかして ---

    /// <summary>
    /// Find the closest matching command name using Damerau-Levenshtein distance.
    /// Short commands use a stricter threshold to avoid unrelated suggestions.
    /// Damerau-Levenshtein距離で最も近いコマンド名を返す。短いコマンドは無関係な推薦を避けるため閾値を厳しくする。
    /// </summary>
    public static string? FindClosestCommand(string input) =>
        FindClosestMatch(input, CliCommandCatalog.PublicCommandNames);

    /// <summary>
    /// Find the closest match for <paramref name="input"/> from <paramref name="candidates"/>
    /// using Damerau-Levenshtein distance with the same length-aware threshold the
    /// command suggester uses (#1582). Comparison is case-insensitive. Returns the original
    /// (cased) candidate string, or <c>null</c> when no candidate is within the threshold.
    /// 任意の候補集合に対して Damerau-Levenshtein 距離で最も近い候補を返す (#1582)。
    /// 短い入力には厳しめの距離閾値を適用し、無関係な推薦を避ける。比較は case-insensitive。
    /// </summary>
    public static string? FindClosestMatch(string? input, IEnumerable<string> candidates)
    {
        var normalized = NormalizeSuggestionInput(input);
        if (normalized == null)
            return null;

        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (candidate.Length > MaxSuggestionInputCharLength)
                continue;
            var candidateNormalized = candidate.ToLowerInvariant();
            if (string.Equals(normalized, candidateNormalized, StringComparison.Ordinal))
                return candidate;
            var dist = DamerauLevenshteinDistance(normalized, candidateNormalized);
            if (dist > GetSuggestionDistanceThreshold(normalized.Length, candidateNormalized.Length))
                continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Return up to <paramref name="maxResults"/> closest candidates for <paramref name="input"/>,
    /// ordered by Damerau-Levenshtein distance. Useful for structured suggestions in MCP
    /// error payloads (#1582). Returns an empty list when no candidate is within the threshold.
    /// Damerau-Levenshtein 距離で近い候補を最大 <paramref name="maxResults"/> 件まで返す。
    /// MCP の structured error payload で `similar_values` を返す用途を想定する (#1582)。
    /// </summary>
    public static IReadOnlyList<string> FindClosestMatches(string? input, IEnumerable<string> candidates, int maxResults = 3)
    {
        var normalized = NormalizeSuggestionInput(input);
        if (normalized == null || maxResults <= 0)
            return Array.Empty<string>();

        var matches = new List<(string Candidate, int Distance)>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (candidate.Length > MaxSuggestionInputCharLength)
                continue;
            var candidateNormalized = candidate.ToLowerInvariant();
            if (string.Equals(normalized, candidateNormalized, StringComparison.Ordinal))
                continue;
            var dist = DamerauLevenshteinDistance(normalized, candidateNormalized);
            if (dist > GetSuggestionDistanceThreshold(normalized.Length, candidateNormalized.Length))
                continue;
            matches.Add((candidate, dist));
        }
        return matches
            .OrderBy(m => m.Distance)
            .ThenBy(m => m.Candidate, StringComparer.Ordinal)
            .Select(m => m.Candidate)
            .Take(maxResults)
            .ToList();
    }

    private static string? NormalizeSuggestionInput(string? input)
    {
        if (input == null || input.Length > MaxSuggestionInputCharLength || string.IsNullOrWhiteSpace(input))
            return null;

        return input.ToLowerInvariant();
    }

    private static int GetSuggestionDistanceThreshold(int inputLength, int commandLength)
    {
        var shorter = Math.Min(inputLength, commandLength);
        return shorter switch
        {
            <= 4 => 1,
            <= 10 => 2,
            _ => 3,
        };
    }

    private static int DamerauLevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[n, m];
    }

    // --- Shell Completions / シェル補完 ---

    /// <summary>
    /// Print shell completion script. Returns false for unknown shells.
    /// シェル補完スクリプトを出力。不明なシェルの場合はfalseを返す。
    /// </summary>
    public static bool PrintCompletions(string shell)
    {
        try
        {
            Console.WriteLine(GetCompletionScript(shell));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static string GetCompletionScript(string shell) =>
        ConsoleCompletionRenderer.GetCompletionScript(shell);

    // --- Helpers / ヘルパー ---

    private static ColorMode _colorMode = ColorMode.Auto;
    private static ColorPalette? _explicitPalette;
    private static bool? _windowsVirtualTerminalProcessingEnabled;
    private static Func<bool>? _windowsVirtualTerminalProcessingDetectorForTests;
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// Set the active color-output mode. <see cref="ColorMode.Always"/> and
    /// <see cref="ColorMode.Never"/> short-circuit env / TTY checks in
    /// <see cref="ShouldUseColor"/>; <see cref="ColorMode.Auto"/> defers to
    /// the existing CLICOLOR_FORCE / NO_COLOR / CLICOLOR / TTY chain.
    /// 色出力モードを設定する。Always / Never は環境変数と TTY 判定を上書きする。
    /// </summary>
    public static void SetColorMode(ColorMode mode) => _colorMode = mode;

}
