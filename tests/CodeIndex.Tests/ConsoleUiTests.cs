using System.Reflection;
using System.Reflection.Emit;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for console usage output.
/// コンソールの使い方出力のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class ConsoleUiTests
{
    private static readonly Dictionary<short, OpCode> SingleByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 1)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    private static readonly Dictionary<short, OpCode> MultiByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.GetValue(null) is OpCode opCode && opCode.Size == 2)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => (short)(opCode.Value & 0xff));

    [Fact]
    public void TryWriteErrorLine_IgnoresClosedErrorWriter()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            var closedError = new StringWriter();
            closedError.Dispose();
            Console.SetError(closedError);
            try
            {
                ConsoleUi.TryWriteErrorLine("diagnostic");
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void TryWriteErrorLine_IgnoresErrorWriterIoFailures()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            Console.SetError(new ThrowingTextWriter());
            try
            {
                ConsoleUi.TryWriteErrorLine("diagnostic");
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("closed pipe");
    }

    [Fact]
    public void StartSpinner_BackgroundLoop_DoesNotBlockOnTaskWait()
    {
        var methods = typeof(ConsoleUi).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Concat(typeof(ConsoleUi)
                .GetNestedTypes(BindingFlags.NonPublic)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)))
            .Where(method => method.Name.Contains("StartSpinner", StringComparison.Ordinal)
                || (method.DeclaringType?.Name.Contains("StartSpinner", StringComparison.Ordinal) ?? false))
            .ToArray();

        Assert.NotEmpty(methods);
        Assert.DoesNotContain(methods, CallsTaskWait);
    }

    [Fact]
    public void PrintUsage_WithBanner_IncludesAsciiArt()
    {
        var output = CaptureFullUsageOutput();

        Assert.Contains("██████╗", output);
        Assert.Contains("Usage:", output);
        Assert.Contains("cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]", output);
    }

    [Fact]
    public void PrintUsage_WithoutBanner_HidesAsciiArtAndEasterEggFlags()
    {
        var output = CaptureFullUsageOutput(showBanner: false);

        Assert.DoesNotContain("██████╗", output);
        Assert.Contains("Usage:", output);
        Assert.Contains("cdidx index <projectPath> [--db <path>] [--rebuild] [--optimize] [--verbose] [--dry-run] [--force] [--quiet] [--json] [--memory-trace] [--duration-format <auto|seconds|hms>] [--notify <auto|bell|osc9|desktop|none>] [--max-file-bytes <bytes>] [--max-symbols-per-file <n>] [--follow-symlinks <none|internal|all>]", output);
        Assert.Contains("cdidx hooks <install|uninstall|status> [--project <path>] [--force] [--json]", output);
        Assert.Contains("cdidx index <projectPath> --commits <commit-ref> [commit-ref ...] [--db <path>] [--verbose] [--dry-run] [--json] [--memory-trace] [--duration-format <auto|seconds|hms>]", output);
        Assert.Contains("cdidx index <projectPath> --files <path> [path ...] [--db <path>] [--verbose] [--dry-run] [--json] [--memory-trace] [--duration-format <auto|seconds|hms>]", output);
        Assert.Contains("cdidx backfill-fold [--db <path>] [--dry-run] [--no-checkpoint] [--json]", output);
        Assert.Contains("cdidx optimize [--db <path>] [--json]", output);
        Assert.Contains("cdidx license", output);
        Assert.Contains("cdidx completions <shell>", output);
        Assert.Contains("cdidx --completions <shell>", output);
        Assert.Contains("cdidx references <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx callers <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx callees <query>|--query <query>|-- <query>", output);
        Assert.Contains("cdidx search <query>|--query <query>|-- <query>|--recipe <name>|--list-recipes [--db <path>] [--json[=ndjson|array]] [--pretty] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif|issue-drafts>] [--open-issues <path>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--snippet-focus <leftmost|quality|proximity>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--prefix] [--count] [--since <datetime>] [--no-dedup] [--no-visibility-rank] [--require-before <query>] [--require-after <query>] [--reject-before <query>] [--reject-after <query>] [--guard-window <n>]", output);
        Assert.Contains("cdidx definition <query>|--query <query>|-- <query> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx references <query>|--query <query>|-- <query> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--exact|--exact-name] [--count]", output);
        Assert.Contains("cdidx inspect <query>|--query <query>|-- <query> [--db <path>] [--json] [--pretty] [--compact] [--fields <csv>] [--body-only] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--max-line-width <n>] [--exact|--exact-name]", output);
        Assert.Contains("--snippet-lines <n>        search/find snippet length (1-20, default: search 8; find 1)", output);
        Assert.Contains("--snippet-focus <mode>     search only: long-line focus mode (leftmost|quality|proximity, default: quality)", output);
        Assert.Contains("--max-line-width <n>       search/references/callers/callees/find/excerpt/impact/inspect only: clamp very long single-line snippet/context/excerpt payloads (`0` disables clamping; default: 512)", output);
        Assert.Contains("cdidx find <query> --path <glob>", output);
        Assert.Contains("--fts                      Use raw FTS5 query syntax for search (content:term, NEAR(a b, 5), OR, NOT, groups, prefix*, \"phrase\"; search query max 1000 chars; raw FTS parser max 2000 chars, 64 boolean ops, 16 NEAR ops", output);
        Assert.Contains("--exact                    Backward-compatible shorthand.", output);
        Assert.Contains("                              Prefer --exact-substring for search,", output);
        Assert.Contains("                              --exact for find,", output);
        Assert.Contains("                              and --exact-name for symbol/graph lookups.", output);
        Assert.Contains("                              Combining exact-match flags is rejected.", output);
        Assert.Contains("--exact-substring          Search only: case-sensitive exact substring", output);
        Assert.Contains("                              (no FTS5)", output);
        Assert.Contains("--exact-name               Exact name match for symbols, definition,", output);
        Assert.Contains("                              references, callers, callees, and inspect.", output);
        Assert.Contains("                              Uses NFKC + Unicode CaseFold when ready.", output);
        Assert.Contains("                              Legacy/stale-fold DBs fall back to ASCII NOCASE;", output);
        Assert.Contains("                              run `cdidx backfill-fold` or check fold_ready.", output);
        Assert.Contains("--kind <kind>              definition/symbols/hotspots/unused: symbol kind; references: reference kind (call/instantiate/subscribe/attribute/annotation); callers/callees: call-graph kinds only (call/instantiate/subscribe — metadata kinds rejected, use references instead); validate: issue kind", output);
        Assert.Contains("--severity <s>             validate only: filter issues by severity: info, warning, error", output);
        Assert.Contains("--count                    Count only; search/definition/references/callers/callees/symbols/files/find/unused ignore --limit, impact/hotspots still use visible page counts", output);
        Assert.Contains("--no-dedup                 search only: return every raw overlapping chunk hit (debug/density)", output);
        Assert.Contains("--commits <commit-ref> [commit-ref ...]", output);
        Assert.Contains("Update only files changed in the specified git commits (preferred after commits)", output);
        Assert.Contains("--files <path> [path ...]  Update only the specified files; old rename/delete paths are not purged unless also listed", output);
        Assert.Contains("--optimize                 index only: optimize the existing FTS5 table for this project's DB without scanning files", output);
        Assert.Contains("--duration-format <format> Index elapsed time format: `auto` (default), `seconds`, or `hms`; JSON keeps raw elapsed_ms", output);
        Assert.Contains("--ascii                    Use ASCII spinner/progress glyphs", output);
        Assert.Contains("cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--max-line-width <n>] [--focus-line <line>] [--focus-column <n>] [--focus-length <n>] [--db <path>] [--json] [--verbose]", output);
        Assert.Contains("--focus-column <n>         find/excerpt: focus a specific 1-based column", output);
        Assert.Contains("--focus-line <line>        find/excerpt: focus a specific line", output);
        Assert.Contains("cdidx map [--db <path>] [--json] [--pretty] [--compact] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--bytes] [--sections <tree,languages,hotspots,metrics>] [--depth <n>] [--min-entrypoint-confidence <0.0..1.0>]", output);
        Assert.Contains("cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json[=ndjson|array]] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>] [--bytes]", output);
        Assert.Contains("cdidx validate [--db <path>] [--json[=array]] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--severity <info|warning|error>] [--path <glob>]", output);
        Assert.Contains("Note: if a query itself starts with '-', pass it with --query <query> or -- <query>", output);
        Assert.DoesNotContain("cdidx validate [--db <path>] [--json] [--limit <n>] [--lang <lang>]", output);
        Assert.Contains("cdidx unused [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--bucket <bucket>] [--min-confidence <medium|low>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx hotspots [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by <symbol|file|statement>] [--group-by-name]", output);
        Assert.Contains("--json                     Output as JSON (search/files stream ndjson by", output);
        Assert.Contains("search/files/validate accept --json=array for one array)", output);
        Assert.Contains("--lang <lang>              Filter by language (aliases: bat, cmd, cshtml, razor, ts, tsx, cts, mts)", output);
        Assert.Contains("--bytes                    files: sort by size and show raw byte counts in human", output);
        Assert.Contains("map: show raw byte counts; JSON always keeps raw", output);
        Assert.Contains("--group-by-name            hotspots: collapse rows sharing (name, kind) across files; JSON count is the number of name/kind groups, not reference, file, or definition-site count", output);
        Assert.Contains("cdidx search \"Run();\" --exact-substring        Case-sensitive exact substring search", output);
        Assert.Contains("cdidx search --query --path --path README.md   Search for a literal option token", output);
        Assert.Contains("cdidx hotspots --group-by-name --exclude-tests", output);
        Assert.Contains("Collapse same-name hotspots across files", output);
        Assert.Contains("cdidx symbols Run --exact-name                Exact symbol-name match", output);
        Assert.Contains("backfill-fold", output);
        Assert.Contains("optimize                   Optimize FTS5 segments in an existing index DB", output);
        Assert.Contains("find <query>               Find literal substring matches inside known indexed files", output);
        Assert.Contains("Prefer --exact-substring for search", output);
        Assert.Contains("--exact for find", output);
        Assert.Contains("impact <query>             Show transitive callers; type queries may return heuristic file-level dependency hints", output);
        Assert.Contains("hotspots                   Find high-impact symbols; duplicate-name families may fall back conservatively", output);
        Assert.Contains("cdidx find guard --path src/Auth.cs --after 2", output);
        Assert.Contains("cdidx references DbContext --kind instantiate Filter constructor sites by reference kind", output);
        Assert.Contains("cdidx hotspots --lang csharp --exclude-tests    Find high-impact symbols with conservative duplicate fallback", output);
        Assert.Contains("cdidx impact FolderDiffService --json           Type query may return heuristic file-level dependency hints", output);
        Assert.Contains("license                    Show licensing, trademark, and commercial-use summary", output);
        Assert.Contains("--license                  Show licensing, trademark, and commercial-use summary", output);
        Assert.Contains("completions <shell>        Generate shell completions for bash, zsh, fish, or PowerShell", output);
        Assert.Contains("--completions <shell>      Generate shell completions (bash, zsh, fish, powershell)", output);
        Assert.Contains("cdidx --completions zsh > ~/.zfunc/_cdidx      Generate a zsh completion script", output);
        Assert.Contains("cdidx license                                  Show licensing and commercial-use terms", output);
        Assert.DoesNotContain("Easter eggs", output);
        Assert.DoesNotContain("--sushi", output);
        Assert.DoesNotContain("--random-spinner", output);
    }

    [Fact]
    public void PrintUsage_ExactMatchOptionLines_FitWithinEightyColumns()
    {
        var output = CaptureFullUsageOutput(showBanner: false);
        var lines = output.Split(Environment.NewLine);
        var exactStart = Array.FindIndex(lines, line => line.StartsWith("  --exact ", StringComparison.Ordinal));
        var exactSubstringStart = Array.FindIndex(lines, line => line.StartsWith("  --exact-substring", StringComparison.Ordinal));
        var exactNameStart = Array.FindIndex(lines, line => line.StartsWith("  --exact-name", StringComparison.Ordinal));
        var kindStart = Array.FindIndex(lines, line => line.StartsWith("  --kind <kind>", StringComparison.Ordinal));

        Assert.True(exactStart >= 0);
        Assert.True(exactSubstringStart > exactStart);
        Assert.True(exactNameStart > exactSubstringStart);
        Assert.True(kindStart > exactNameStart);

        var exactMatchLines = lines[exactStart..kindStart]
            .Where(line => line.Length > 0)
            .ToArray();

        Assert.NotEmpty(exactMatchLines);
        Assert.All(exactMatchLines, line => Assert.True(line.Length <= 80, $"Line exceeds 80 columns ({line.Length}): {line}"));
    }

    [Fact]
    public void PrintUsage_ShowsCommitUpdateWorkflowClearly()
    {
        var output = CaptureFullUsageOutput(showBanner: false);

        Assert.Contains("Update workflows:", output);
        Assert.Contains("Use --commits with a project path after normal commits", output);
        Assert.Contains("Use --changed-between <old-ref> <new-ref> after switching branches", output);
        Assert.Contains("Use --files only for known in-place edits or new files", output);
        Assert.Contains("cdidx index ./myproject --commits abc123", output);
        Assert.Contains("cdidx index ./myproject --changed-between main feature", output);
    }

    [Fact]
    public void WrapHelpLine_LongOptionDescription_PreservesOptionColumn()
    {
        const string line = "  --max-line-width <n>       search/references/find/excerpt/inspect only: clamp very long single-line snippet/context/excerpt payloads";

        var lines = ConsoleUi.WrapHelpLine(line, maxWidth: 80);

        Assert.All(lines, wrapped => Assert.True(wrapped.Length <= 80, wrapped));
        Assert.StartsWith("  --max-line-width <n>       search/references/find/excerpt/inspect", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("                             very long", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void WrapHelpLine_LongUsageLine_UsesDetectedWidth()
    {
        const string line = "  cdidx search <query>|--query <query>|-- <query> [--db <path>] [--json[=ndjson|array]] [--verbose] [--limit <n>|--top <n>]";

        var lines = ConsoleUi.WrapHelpLine(line, maxWidth: 72);

        Assert.True(lines.Count > 1);
        Assert.All(lines, wrapped => Assert.True(wrapped.Length <= 72, wrapped));
    }

    [Fact]
    public void WrapHelpLine_VeryNarrowWidth_DoesNotEmitEmptyOrOverwideLines()
    {
        const string line = "  --include-symbol-kind <kind>[,<kind>]  Keep only matching symbol kinds during indexing";

        var lines = ConsoleUi.WrapHelpLine(line, maxWidth: 12);

        Assert.All(lines, wrapped =>
        {
            Assert.NotEmpty(wrapped);
            Assert.True(wrapped.Length <= 12, wrapped);
        });
    }

    [Fact]
    public void PrintUsage_QueryLinesMatchImplementedOptions()
    {
        var output = CaptureFullUsageOutput(showBanner: false);

        Assert.Contains("cdidx search <query>|--query <query>|-- <query>|--recipe <name>|--list-recipes [--db <path>] [--json[=ndjson|array]] [--pretty] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif|issue-drafts>] [--open-issues <path>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--snippet-focus <leftmost|quality|proximity>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--prefix] [--count] [--since <datetime>] [--no-dedup] [--no-visibility-rank] [--require-before <query>] [--require-after <query>] [--reject-before <query>] [--reject-after <query>] [--guard-window <n>]", output);
        Assert.Contains("cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]", output);
        Assert.Contains("cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json[=ndjson|array]] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>] [--bytes]", output);
        Assert.Contains("cdidx hotspots [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx unused [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--bucket <bucket>] [--min-confidence <medium|low>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]", output);
        Assert.Contains("cdidx license", output);
        Assert.Contains("cdidx completions <shell>", output);
        Assert.Contains("cdidx --completions <shell>", output);
    }

    [Theory]
    [InlineData(0, "0 results", "No results found.")]
    [InlineData(1, "1 result", "Found 1 result.")]
    [InlineData(5, "5 results", "Found 5 results.")]
    public void CountedAndFoundSummary_UseNaturalPluralization(int count, string counted, string summary)
    {
        Assert.Equal(counted, ConsoleUi.Counted(count, "result"));
        Assert.Equal(summary, ConsoleUi.FoundSummary(count, "result"));
    }

    [Fact]
    public void Counted_AllowsIrregularPluralAndNumberFormat()
    {
        Assert.Equal("2 in-file matches", ConsoleUi.Counted(2, "in-file match", "in-file matches"));
        Assert.Equal("1,234 files", ConsoleUi.Counted(1234, "file", format: "N0"));
    }

    [Fact]
    public void FormatProgressLine_UsesInvariantNumberFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var line = ConsoleUi.FormatProgressLine(1234, 2000, windowWidth: 80, useUnicodeGlyphs: false);

            Assert.Contains("61.7%", line);
            Assert.Contains("[1,234/2,000]", line);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void GetSpinnerFrames_Default_UsesRotatingBrailleSequence()
    {
        WithUnicodeEnvironment(cdidxAscii: null, lang: "en_US.UTF-8", () =>
        {
            var frames = ConsoleUi.GetSpinnerFrames(null);

            Assert.Equal(["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"], frames);
        });
    }

    [Fact]
    public void GetSpinnerFrames_CdidxAsciiEnvVar_UsesAsciiSequence()
    {
        WithUnicodeEnvironment(cdidxAscii: "1", lang: "en_US.UTF-8", () =>
        {
            Assert.Equal(["|", "/", "-", "\\"], ConsoleUi.GetSpinnerFrames(null));
        });
    }

    [Fact]
    public void GetSpinnerFrames_PosixLang_UsesAsciiSequence()
    {
        WithUnicodeEnvironment(cdidxAscii: null, lang: "C", () =>
        {
            Assert.Equal(["|", "/", "-", "\\"], ConsoleUi.GetSpinnerFrames(null));
        });
    }

    [Fact]
    public void GetSpinnerFrames_AsciiOverrideWinsOverThemedSpinner()
    {
        WithUnicodeEnvironment(cdidxAscii: "1", lang: "en_US.UTF-8", () =>
        {
            Assert.Equal(["|", "/", "-", "\\"], ConsoleUi.GetSpinnerFrames("--sushi"));
        });
    }

    [Fact]
    public void FormatProgressLine_UnicodeEnabled_UsesBlockGlyphBar()
    {
        var line = ConsoleUi.FormatProgressLine(25, 100, windowWidth: 80, useUnicodeGlyphs: true);

        Assert.Contains('█', line);
        Assert.Contains('░', line);
        Assert.DoesNotContain('#', line);
        Assert.Contains("25.0%  [25/100]", line);
    }

    [Fact]
    public void FormatProgressLine_AsciiFallback_UsesAsciiBarAndSpinner()
    {
        var line = ConsoleUi.FormatProgressLine(25, 100, windowWidth: 80, useUnicodeGlyphs: false);

        Assert.StartsWith("- [########", line);
        Assert.Contains("------------------------]", line);
        Assert.DoesNotContain('█', line);
        Assert.DoesNotContain('░', line);
    }

    [Fact]
    public void FormatProgressLine_ProgressAnimationDisabled_UsesStaticPrefix()
    {
        var line = ConsoleUi.FormatProgressLine(
            25,
            100,
            windowWidth: 80,
            useUnicodeGlyphs: false,
            useProgressAnimation: false);

        Assert.StartsWith("  [########", line);
        Assert.DoesNotContain("- [", line);
    }

    [Fact]
    public void FormatProgressLine_NarrowUnicodeTerminal_UsesPercentageOnly()
    {
        var line = ConsoleUi.FormatProgressLine(2, 4, windowWidth: 39, useUnicodeGlyphs: true);

        Assert.Equal(" 50.0%  [2/4]", line);
    }

    [Fact]
    public void PrintWarning_FlushesBothConsoleStreams()
    {
        using var output = new FlushCountingTextWriter();
        using var error = new FlushCountingTextWriter();
        using var capture = ConsoleCapture.Start(output, error);

        ConsoleUi.PrintWarning("watch out");

        Assert.Contains("[WARN] watch out", error.ToString());
        Assert.True(error.FlushCount > 0);
        Assert.True(output.FlushCount > 0);
    }

    [Fact]
    public void EnsureConsoleWritersSynchronized_SerializesConcurrentWholeStringWrites()
    {
        using var output = new SlowChunkingTextWriter();
        using var capture = ConsoleCapture.Start(output, error: null);
        ConsoleUi.EnsureConsoleWritersSynchronized();

        const int iterations = 40;
        var left = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
                Console.Write("[spinner-frame]\n");
        });
        var right = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
                Console.Write("[progress-line]\n");
        });

        left.Start();
        right.Start();
        left.Join();
        right.Join();

        var lines = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(iterations * 2, lines.Length);
        Assert.All(lines, line => Assert.True(
            line is "[spinner-frame]" or "[progress-line]",
            $"Unexpected interleaved line: {line}"));
    }

    [Fact]
    public void StartSpinner_RedirectedOutput_UsesSynchronizedWriterBeforeFallbackLine()
    {
        using var output = new StringWriter();
        using var capture = ConsoleCapture.Start(output, error: null);

        var cts = ConsoleUi.StartSpinner("Indexing...", ["|"]);

        Assert.Null(cts);
        Assert.Contains("Indexing...", output.ToString());
    }

    [Fact]
    public void ShouldUseProgressAnimation_EnvOptOutDisablesAnimation()
    {
        using var env = new ProgressAnimationEnvironmentScope();
        Environment.SetEnvironmentVariable(ConsoleUi.DisableProgressEnvironmentVariable, "1");

        Assert.False(ConsoleUi.ShouldUseProgressAnimation());
    }

    [Fact]
    public void ShouldUseProgressAnimation_ReducedMotionDisablesAnimation()
    {
        using var env = new ProgressAnimationEnvironmentScope();
        Environment.SetEnvironmentVariable(ConsoleUi.PrefersReducedMotionEnvironmentVariable, "1");

        Assert.False(ConsoleUi.ShouldUseProgressAnimation());
    }

    [Fact]
    public void TryConsumeNoProgressFlag_RemovesGlobalFlagAndDisablesAnimation()
    {
        using var env = new ProgressAnimationEnvironmentScope();
        string[] args = ["index", ".", "--no-progress", "--json"];

        ProgramRunner.TryConsumeNoProgressFlag(ref args);

        Assert.Equal(["index", ".", "--json"], args);
        Assert.False(ConsoleUi.ShouldUseProgressAnimation());
    }

    [Fact]
    public void GetWindowWidth_ColumnsEnvVarSet_UsesColumnsValue()
    {
        WithColumnsEnvironment("200", () =>
        {
            Assert.Equal(200, ConsoleUi.GetWindowWidth());
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("wide")]
    public void GetWindowWidth_InvalidColumnsEnvVar_FallsBackToConsoleOrDefault(string? columns)
    {
        WithColumnsEnvironment(columns, () =>
        {
            Assert.True(ConsoleUi.GetWindowWidth() > 0);
        });
    }

    [Fact]
    public void ShouldUseUnicodeGlyphs_CdidxAsciiEnvVarDisablesUnicode()
    {
        WithUnicodeEnvironment(cdidxAscii: "1", lang: "en_US.UTF-8", () =>
        {
            Assert.False(ConsoleUi.ShouldUseUnicodeGlyphs());
        });
    }

    [Fact]
    public void ShouldUseUnicodeGlyphs_PosixLangDisablesUnicode()
    {
        WithUnicodeEnvironment(cdidxAscii: null, lang: "C", () =>
        {
            Assert.False(ConsoleUi.ShouldUseUnicodeGlyphs());
        });
    }

    [Fact]
    public void ShouldUseUnicodeGlyphs_NonUtf8LangDisablesUnicode()
    {
        WithUnicodeEnvironment(cdidxAscii: null, lang: "en_US", () =>
        {
            Assert.False(ConsoleUi.ShouldUseUnicodeGlyphs());
        });
    }

    [Fact]
    public void ShouldUseUnicodeGlyphs_DumbTerminalDisablesUnicode()
    {
        WithUnicodeEnvironment(cdidxAscii: null, lang: "en_US.UTF-8", term: "dumb", action: () =>
        {
            Assert.False(ConsoleUi.ShouldUseUnicodeGlyphs());
        });
    }

    [Fact]
    public void ShouldUseUnicodeGlyphs_NoUnicodeEnvVarDisablesUnicode()
    {
        WithUnicodeEnvironment(cdidxAscii: null, lang: "en_US.UTF-8", noUnicode: "1", action: () =>
        {
            Assert.False(ConsoleUi.ShouldUseUnicodeGlyphs());
        });
    }

    [Fact]
    public void ShouldUseUnicodeGlyphs_AccessibilityEnvVarDisablesUnicode()
    {
        WithUnicodeEnvironment(cdidxAscii: null, lang: "en_US.UTF-8", accessibilityEnabled: "1", action: () =>
        {
            Assert.False(ConsoleUi.ShouldUseUnicodeGlyphs());
        });
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1023L, "1,023 bytes")]
    [InlineData(1024L, "1.0 KiB")]
    [InlineData(1048575L, "1,024.0 KiB")]
    [InlineData(1048576L, "1.0 MiB")]
    [InlineData(5368709120L, "5.0 GiB")]
    [InlineData(1099511627776L, "1.0 TiB")]
    public void FormatBytes_UsesBinaryUnitsForHumanOutput(long bytes, string expected)
    {
        Assert.Equal(expected, ConsoleUi.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(999, "999ms")]
    [InlineData(1_000, "1.0s")]
    [InlineData(59_900, "59.9s")]
    [InlineData(60_000, "1m 0s")]
    [InlineData(3_599_000, "59m 59s")]
    [InlineData(3_600_000, "1h 0m 0s")]
    [InlineData(86_400_000, "24h 0m 0s")]
    [InlineData(360_061_000, "100h 1m 1s")]
    public void FormatDuration_Auto_UsesUnitLabels(int milliseconds, string expected)
    {
        Assert.Equal(expected, ConsoleUi.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void FormatDuration_ExplicitFormats_RespectUserPreference()
    {
        var duration = TimeSpan.FromMilliseconds(65_432);

        Assert.Equal("65.4s", ConsoleUi.FormatDuration(duration, DurationOutputFormat.Seconds));
        Assert.Equal("00:01:05", ConsoleUi.FormatDuration(duration, DurationOutputFormat.Hms));
    }

    [Fact]
    public void PrintProgress_InitialRender_ShowsZeroPercent()
    {
        using var capture = ConsoleCapture.Start(captureOut: true);

        ConsoleUi.PrintProgress(0, 10);

        var output = capture.Out!.ToString();

        Assert.Contains("0.0%", output);
        Assert.Contains("[0/10]", output);
    }

    [Fact]
    public void PrintLicenseSummary_DescribesFslAndCommercialRestriction()
    {
        using var capture = ConsoleCapture.Start(captureOut: true);

        ConsoleUi.PrintLicenseSummary();
        var output = capture.Out!.ToString();

        Assert.Contains("Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2)", output);
        Assert.Contains("non-competing purposes", output);
        Assert.Contains("Competing commercial products or services require a separate written agreement", output);
        Assert.Contains("separate written agreement", output);
        Assert.Contains("LICENSES/Apache-2.0.txt", output);
        Assert.Contains("INTEGRATION_POLICY.md", output);
    }

    [Fact]
    public void LoadVersion_ReturnsActualVersion_NotFallback()
    {
        // In the test environment the built version.json should be present.
        // Verify the returned value is a real version, not the "0.0.0" fallback.
        // テスト環境ではビルド済みの version.json が存在するはず。
        // フォールバックの "0.0.0" ではなく実際のバージョンが返ることを検証。
        var version = ConsoleUi.LoadVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("0.0.0", version);
        Assert.Contains('.', version);
    }

    [Fact]
    public void LoadBuildMetadata_PopulatesAllFields()
    {
        // #1550: build metadata (commit SHA, build date, dirty flag) is stamped
        // into the assembly so `cdidx --version` can distinguish dev builds
        // from tagged releases. All four fields must be non-empty — when the
        // MSBuild target cannot resolve a value it falls back to "unknown".
        // #1550 で導入したアセンブリメタデータ。MSBuild ターゲットが値を解決
        // できない場合は "unknown" フォールバックが入るため、いずれのフィールド
        // も空にはならない。
        var metadata = ConsoleUi.LoadBuildMetadata();
        Assert.False(string.IsNullOrWhiteSpace(metadata.Version));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Commit));
        Assert.False(string.IsNullOrWhiteSpace(metadata.BuildDate));
        Assert.False(string.IsNullOrWhiteSpace(metadata.Dirty));
        Assert.Equal(ConsoleUi.LoadVersion(), metadata.Version);
        Assert.Contains(metadata.Dirty, new[] { "clean", "dirty", "unknown" });
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    public void PrintCompletions_KnownShell_ReturnsTrue(string shell)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        Assert.True(ConsoleUi.PrintCompletions(shell));
        var output = capture.Out!.ToString();
        var exactSubstringToken = shell == "fish" ? "exact-substring" : "--exact-substring";
        var exactNameToken = shell == "fish" ? "exact-name" : "--exact-name";
        var groupByNameToken = shell == "fish" ? "group-by-name" : "--group-by-name";
        var licenseToken = shell == "fish" ? "-l license" : shell == "bash" ? "--license" : "license:license command";
        // #1570: the schema-driven generators emit one canonical description per flag
        // across every branch — the pre-refactor per-branch wording ("snippets" /
        // "contexts" / "excerpts") collapses to a single "payloads" tooltip.
        // #1570 後はスキーマ駆動なので、ブランチごとに違う旧文言ではなく統一の "payloads" 表記。
        var maxLineWidthToken = shell == "fish"
            ? "Clamp long single-line payloads (0 disables clamping)"
            : shell == "bash"
                ? "--max-line-width"
                : "--max-line-width[Clamp long single-line payloads (0 disables clamping)]:number";
        Assert.Contains(exactSubstringToken, output);
        Assert.Contains(exactNameToken, output);
        Assert.Contains(groupByNameToken, output);
        Assert.Contains(licenseToken, output);
        Assert.Contains(maxLineWidthToken, output);
        Assert.Contains("cshtml", output);
        Assert.Contains("razor", output);
        if (shell is "bash" or "zsh")
        {
            // Should contain dynamically generated languages, including newly added ones
            // 動的生成の言語リストに新しく追加した言語が含まれているか検証
            Assert.Contains("msbuild", output);
            Assert.Contains("elixir", output);
            Assert.Contains("graphql", output);
            Assert.Contains("protobuf", output);
        }
    }

    [Fact]
    public void PrintCompletions_BashCompletesFlagValues()
    {
        var output = ConsoleUi.GetCompletionScript("bash");

        Assert.Contains("--db|--path|--exclude-path|--output|-o|--metrics) COMPREPLY=($(compgen -f -- \"$cur\"))", output);
        Assert.Contains("--lang) COMPREPLY=($(compgen -W \"", output);
        Assert.Contains("csharp", output);
        Assert.Contains("python", output);
        Assert.Contains("--kind) COMPREPLY=($(compgen -W \"", output);
        Assert.Contains("function", output);
        Assert.Contains("type_reference", output);
        Assert.Contains("razor_event_binding", output);
    }

    [Fact]
    public void PrintCompletions_TopLevelGlobalLogFlagsAcrossShells()
    {
        var bash = ConsoleUi.GetCompletionScript("bash");
        var zsh = ConsoleUi.GetCompletionScript("zsh");
        var fish = ConsoleUi.GetCompletionScript("fish");
        var powershell = ConsoleUi.GetCompletionScript("powershell");

        foreach (var flag in new[] { "--log-format", "--log-retain-count", "--log-max-size-mb" })
        {
            Assert.Contains(flag, bash);
            Assert.Contains(flag, zsh);
            Assert.Contains(flag, powershell);
            Assert.Contains($"-l {flag.TrimStart('-')}", fish);
        }

        Assert.Contains("--log-format) COMPREPLY=($(compgen -W \"text json\" -- \"$cur\"))", bash);
        Assert.Contains("'--log-format[Persistent stderr log format]:format:(text json)'", zsh);
        Assert.Contains("-l log-format -r -a 'text json'", fish);
        Assert.Contains("'--log-format' { $logFormats", powershell);
    }

    [Fact]
    public void PrintCompletions_ZshAndFishCompleteKindValuesFromSharedSet()
    {
        var zsh = ConsoleUi.GetCompletionScript("zsh");
        var fish = ConsoleUi.GetCompletionScript("fish");

        Assert.Contains(":language:(", zsh);
        Assert.Contains(":kind:(", zsh);
        Assert.Contains("type_reference", zsh);
        Assert.Contains("razor_event_binding", zsh);
        Assert.Contains("-a '", fish);
        Assert.Contains("type_reference", fish);
        Assert.Contains("razor_event_binding", fish);
    }

    [Fact]
    public void PrintCompletions_PowerShellRegistersNativeCompleter()
    {
        var output = ConsoleUi.GetCompletionScript("powershell");

        Assert.Contains("generated for version", output);
        Assert.Contains("Regenerate this script after upgrading cdidx.", output);
        Assert.Contains("Register-ArgumentCompleter -Native -CommandName cdidx", output);
        Assert.Contains("$commands = @('index', 'backfill-fold'", output);
        Assert.Contains("'--help', '--version', '--license'", output);
        Assert.Contains("'search' { $flags = @(", output);
        Assert.Contains("'--lang' { $langs", output);
        Assert.Contains("'--kind' { $kinds", output);
        Assert.Contains("Get-ChildItem -Name \"$wordToComplete*\"", output);
        Assert.Contains("CompletionResult", output);
        Assert.Contains("[string]::IsNullOrEmpty($wordToComplete) -and $tokens.Count -ge 1", output);
        Assert.Contains("$afterLastToken = $lastElement -and $cursorPosition -gt $lastElement.Extent.EndOffset", output);
        Assert.Contains("$tokens.Count -le 2 -and -not ([string]::IsNullOrEmpty($wordToComplete)) -and -not $afterLastToken", output);
    }

    [Theory]
    [InlineData("bash", "# cdidx bash completions generated for version")]
    [InlineData("zsh", "# cdidx zsh completions generated for version")]
    [InlineData("fish", "# cdidx fish completions generated for version")]
    [InlineData("powershell", "# cdidx PowerShell completions generated for version")]
    public void PrintCompletions_EmbedsVersionMarker(string shell, string marker)
    {
        var output = ConsoleUi.GetCompletionScript(shell);

        Assert.Contains(marker, output);
        Assert.Contains("Regenerate this script after upgrading cdidx.", output);
    }

    [Fact]
    public void PrintCompletions_PowerShellIncludesSharedFlagValues()
    {
        var output = ConsoleUi.GetCompletionScript("pwsh");

        Assert.Contains("'csharp'", output);
        Assert.Contains("'python'", output);
        Assert.Contains("'type_reference'", output);
        Assert.Contains("'razor_event_binding'", output);
        Assert.Contains("'--max-line-width'", output);
        Assert.Contains("'--no-dedup'", output);
        Assert.Contains("'--group-by-name'", output);
        Assert.Contains("default { $flags = @(", output);
    }

    [Theory]
    [InlineData("bash", "if [ \"$cmd\" = \"hotspots\" ]", "--group-by-name", "--exact-name")]
    [InlineData("zsh", "elif [[ $subcmd == hotspots ]]; then", "--group-by-name[Hotspots: collapse same-name rows; count is name/kind groups]", "--exact-name[Exact symbol-name equality]")]
    public void PrintCompletions_BashAndZshScopeGroupByNameToHotspots(string shell, string hotspotsBranchMarker, string groupedFlagToken, string genericExactNameToken)
    {
        var output = ConsoleUi.GetCompletionScript(shell);
        Assert.Contains(hotspotsBranchMarker, output);
        Assert.Contains(groupedFlagToken, output);

        var hotspotsIndex = output.IndexOf(hotspotsBranchMarker, StringComparison.Ordinal);
        var genericIndex = output.LastIndexOf(genericExactNameToken, StringComparison.Ordinal);
        Assert.True(hotspotsIndex >= 0);
        Assert.True(genericIndex > hotspotsIndex);
        Assert.DoesNotContain("--group-by-name --exact-name", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintCompletions_FishIncludesFindOptions()
    {
        // #1570: fish completion is now emitted from `CliFlagSchema`. The hand-written
        // `__fish_seen_subcommand_from <list>` strings change to the canonical
        // command-ordering used by `CliFlagSchema.AllCommands`, and descriptions use the
        // schema's single source of truth (e.g. `--exact` → "Backward-compatible exact
        // shorthand"). These assertions intentionally check the schema-ordered groupings
        // (`--query` and `--before`/`--after` predicates) and key flag invariants while
        // accepting the unified wording.
        // #1570 によりスキーマ駆動。`__fish_seen_subcommand_from` の並びは `CliFlagSchema.AllCommands`
        // 順、`--exact` の説明は統一表記 (`Backward-compatible exact shorthand`)。
        var output = ConsoleUi.GetCompletionScript("fish");
        Assert.Contains("__fish_seen_subcommand_from search definition goto references callers callees symbols files find inspect impact", output);
        Assert.Contains("__fish_seen_subcommand_from find excerpt", output);
        // `--exact` schema membership: search + find + the name-resolution commands.
        // 旧手書きが `search find` だけだった所を、スキーマ準拠の正規列で確認する。
        Assert.Contains("__fish_seen_subcommand_from search definition references callers callees symbols find inspect' -l exact ", output);
        Assert.Contains("-l query -r -d 'Literal query'", output);
        Assert.Contains("-l before -r -d 'Context lines before'", output);
        Assert.Contains("-l after -r -d 'Context lines after'", output);
        Assert.Contains("-l exact -d 'Backward-compatible exact shorthand'", output);
        Assert.Contains("__fish_seen_subcommand_from hotspots", output);
        Assert.Contains("-l group-by-name -d 'Collapse same-name rows across files'", output);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    public void PrintCompletions_BashAndZshIncludeFindFocusOptions(string shell)
    {
        var output = ConsoleUi.GetCompletionScript(shell);
        Assert.Contains("find", output);
        Assert.Contains("--before", output);
        Assert.Contains("--after", output);
        Assert.Contains("--max-line-width", output);
        Assert.Contains("--exact", output);
        Assert.Contains("--query", output);
        if (shell == "bash")
        {
            Assert.Contains("if [ \"$cmd\" = \"find\" ]", output);
            Assert.Contains("elif [ \"$cmd\" = \"excerpt\" ]; then", output);
            var findBranch = ExtractBetween(output, "if [ \"$cmd\" = \"find\" ]", "elif [ \"$cmd\" = \"excerpt\" ]; then");
            var excerptBranch = ExtractBetween(output, "elif [ \"$cmd\" = \"excerpt\" ]; then", "elif [ \"$cmd\" = \"references\" ]; then");
            Assert.Contains("--focus-column", findBranch);
            Assert.Contains("--focus-column", excerptBranch);
        }
        else
        {
            Assert.Contains("if [[ $subcmd == find ]]; then", output);
            Assert.Contains("elif [[ $subcmd == excerpt ]]; then", output);
            var findBranch = ExtractBetween(output, "if [[ $subcmd == find ]]; then", "elif [[ $subcmd == excerpt ]]; then");
            var excerptBranch = ExtractBetween(output, "elif [[ $subcmd == excerpt ]]; then", "elif [[ $subcmd == references ]]; then");
            Assert.Contains("focus-column", findBranch);
            Assert.Contains("focus-column", excerptBranch);
        }
    }

    [Fact]
    public void PrintCompletions_ExcerptFlagSetsMatchAcrossShells()
    {
        var bashExcerpt = ExtractBashSubcommandFlags(ConsoleUi.GetCompletionScript("bash"), "excerpt", "references");
        var zshExcerpt = ExtractZshSubcommandFlags(ConsoleUi.GetCompletionScript("zsh"), "excerpt", "references");
        var fishExcerpt = ExtractFishSubcommandFlags(ConsoleUi.GetCompletionScript("fish"), "excerpt");

        // --help is universal in bash but is not enumerated by the zsh/fish scripts;
        // exclude it from the parity comparison so the flag sets line up cleanly.
        bashExcerpt.Remove("help");

        Assert.Equal(bashExcerpt, zshExcerpt);
        Assert.Equal(bashExcerpt, fishExcerpt);
        // Sanity check: required excerpt flags are present in every shell.
        foreach (var flag in new[] { "db", "json", "start", "end", "before", "after", "max-line-width", "focus-line", "focus-column", "focus-length" })
        {
            Assert.Contains(flag, bashExcerpt);
            Assert.Contains(flag, zshExcerpt);
            Assert.Contains(flag, fishExcerpt);
        }
    }

    [Fact]
    public void PrintCompletions_ReportFlagSetsMatchAcrossShells()
    {
        var bashScript = ConsoleUi.GetCompletionScript("bash");
        var zshScript = ConsoleUi.GetCompletionScript("zsh");
        var fishScript = ConsoleUi.GetCompletionScript("fish");
        var bashReport = ExtractBashSubcommandFlags(bashScript, "report", "search");
        var zshReport = ExtractZshSubcommandFlags(zshScript, "report", "search");
        var fishReport = ExtractFishSubcommandFlags(fishScript, "report");

        // --help is universal in bash but is not enumerated by the zsh/fish scripts.
        bashReport.Remove("help");

        var expected = new SortedSet<string>(StringComparer.Ordinal)
        {
            "db", "json", "pretty", "quiet", "silent", "no-progress", "output", "log-lines", "no-log", "include-args",
        };
        Assert.Equal(expected, bashReport);
        Assert.Equal(expected, zshReport);
        Assert.Equal(expected, fishReport);

        Assert.Contains("-o", ExtractBetween(bashScript, "[ \"$cmd\" = \"report\" ]; then", "[ \"$cmd\" = \"search\" ]; then"));
        Assert.Contains("'-o[Output bundle path]:file:_files'", ExtractBetween(zshScript, "[[ $subcmd == report ]]; then", "[[ $subcmd == search ]]; then"));
        Assert.Contains("-l output -s o -r", fishScript);
    }

    private static SortedSet<string> ExtractBashSubcommandFlags(string script, string subcommand, string nextSubcommand)
    {
        var startMarker = $"[ \"$cmd\" = \"{subcommand}\" ]; then";
        var endMarker = $"[ \"$cmd\" = \"{nextSubcommand}\" ]; then";
        var branch = ExtractBetween(script, startMarker, endMarker);
        var quoted = Regex.Match(branch, "compgen -W \"(?<flags>[^\"]*)\"");
        Assert.True(quoted.Success, $"bash branch for {subcommand} did not contain a compgen list");
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in quoted.Groups["flags"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
                flags.Add(token[2..]);
        }
        return flags;
    }

    private static SortedSet<string> ExtractZshSubcommandFlags(string script, string subcommand, string nextSubcommand)
    {
        var startMarker = $"[[ $subcmd == {subcommand} ]]; then";
        var endMarker = $"[[ $subcmd == {nextSubcommand} ]]; then";
        var branch = ExtractBetween(script, startMarker, endMarker);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(branch, @"'--(?<name>[a-z][a-z0-9-]*)\["))
            flags.Add(match.Groups["name"].Value);
        return flags;
    }

    private static SortedSet<string> ExtractFishSubcommandFlags(string script, string subcommand)
    {
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        var pattern = new Regex($@"__fish_seen_subcommand_from\s+(?<list>[^']+)'\s+-l\s+(?<flag>[a-z][a-z0-9-]*)\b");
        foreach (var line in script.Split('\n'))
        {
            var match = pattern.Match(line);
            if (!match.Success)
                continue;
            var subcmds = match.Groups["list"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (Array.IndexOf(subcmds, subcommand) < 0)
                continue;
            flags.Add(match.Groups["flag"].Value);
        }
        return flags;
    }

    [Fact]
    public void GetCompletionLangs_IncludesWindowsBatchAndYamlAliases()
    {
        var method = typeof(ConsoleUi).GetMethod("GetCompletionLangs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var value = method!.Invoke(null, []);

        var langs = value?.ToString() ?? string.Empty;
        var tokens = langs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("bat", tokens);
        Assert.Contains("batch", tokens);
        Assert.Contains("cmd", tokens);
        Assert.Contains("yml", tokens);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    public void PrintCompletions_BashAndZshScopeMaxLineWidthToSearchBranch(string shell)
    {
        var output = ConsoleUi.GetCompletionScript(shell).Replace("\r\n", "\n");
        if (shell == "bash")
        {
            Assert.Contains("elif [ \"$cmd\" = \"search\" ]; then", output);
            var searchBranch = ExtractBetween(output, "elif [ \"$cmd\" = \"search\" ]; then", "else\n");
            var genericBranch = ExtractBetween(output, "else\n                COMPREPLY=($(compgen -W \"", "\" -- \"$cur\"))\n            fi");
            Assert.Contains("--max-line-width", searchBranch);
            Assert.Contains("--top", searchBranch);
            Assert.Contains("--no-dedup", searchBranch);
            Assert.DoesNotContain("--exact-name", searchBranch);
            Assert.DoesNotContain("--max-line-width", genericBranch);
        }
        else
        {
            Assert.Contains("elif [[ $subcmd == search ]]; then", output);
            var searchBranch = ExtractBetween(output, "elif [[ $subcmd == search ]]; then", "else\n");
            var genericBranch = ExtractBetween(output, "else\n                _arguments", "fi\n            ;;");
            Assert.Contains("--max-line-width", searchBranch);
            Assert.Contains("'--top", searchBranch);
            Assert.Contains("'--no-dedup", searchBranch);
            Assert.DoesNotContain("--exact-name", searchBranch);
            Assert.DoesNotContain("--max-line-width", genericBranch);
        }
    }

    [Fact]
    public void PrintUsage_ShowsWorkingFindDashedLiteralExample()
    {
        using var capture = ConsoleCapture.Start(captureOut: true);

        ConsoleUi.PrintUsageFull();
        var output = capture.Out!.ToString();

        Assert.Contains("cdidx find --path README.md -- --path", output);
        Assert.DoesNotContain("cdidx find -- --path --path README.md", output);
    }

    [Fact]
    public void PrintUsageBrief_IsConciseAndPointsToExtendedHelp()
    {
        var output = CaptureBriefUsageOutput(showBanner: false);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length < 80, $"Brief help should stay under 80 non-empty lines but had {lines.Length}.");
        Assert.Contains("cdidx --help-all", output);
        Assert.Contains("cdidx --help-flags", output);
        Assert.Contains("validate                   Report encoding issues", output);
        Assert.DoesNotContain("Index and update options:", output);
        Assert.DoesNotContain("cdidx index <projectPath> --commits <commit-ref>", output);
    }

    [Fact]
    public void PrintFlagUsage_ShowsFlagsWithoutCommands()
    {
        using var capture = ConsoleCapture.Start(captureOut: true);

        ConsoleUi.PrintFlagUsage(showBanner: false);
        var output = capture.Out!.ToString();

        Assert.Contains("Index and update options:", output);
        Assert.Contains("Query options:", output);
        Assert.Contains("--log-format <text|json>", output);
        Assert.Contains("--log-retain-count <n>", output);
        Assert.Contains("--log-max-size-mb <n>", output);
        Assert.Contains("--limit <n>, --top <n>", output);
        Assert.DoesNotContain("Commands:", output);
        Assert.DoesNotContain("Examples:", output);
    }

    [Fact]
    public void PrintCompletions_UnknownShell_ReturnsFalse()
    {
        using var capture = ConsoleCapture.Start(captureError: true);

        Assert.False(ConsoleUi.PrintCompletions("nu"));
        Assert.Contains("Unknown shell", capture.Error!.ToString());
        Assert.Contains("powershell", capture.Error!.ToString());
    }

    [Fact]
    public void ColorizeKind_NoColorEnvVar_DisablesAnsiEscapes()
    {
        WithColorEnvironment(noColor: "1", cliColor: null, cliColorForce: null, () =>
        {
            Assert.False(ConsoleUi.ShouldUseColor());
            var output = ConsoleUi.ColorizeKind("function");
            Assert.Equal("function", output);
            Assert.DoesNotContain('\x1b', output);
        });
    }

    [Fact]
    public void ColorizeKind_NoColorEmpty_LeavesDefaultBehavior()
    {
        // Per https://no-color.org, only a non-empty NO_COLOR disables color.
        // 規約に従い、空の NO_COLOR は色を抑制せず、TTY 判定にフォールバックする。
        WithColorEnvironment(noColor: string.Empty, cliColor: null, cliColorForce: null, () =>
        {
            Assert.Equal(ConsoleUi.ShouldUseInteractiveConsole(), ConsoleUi.ShouldUseColor());
        });
    }

    [Fact]
    public void ColorizeKind_CliColorZero_DisablesAnsiEscapes()
    {
        WithColorEnvironment(noColor: null, cliColor: "0", cliColorForce: null, () =>
        {
            Assert.False(ConsoleUi.ShouldUseColor());
            var output = ConsoleUi.ColorizeKind("class", padWidth: 6);
            Assert.Equal("class ", output);
            Assert.DoesNotContain('\x1b', output);
        });
    }

    [Fact]
    public void ColorizeKind_CliColorOne_DoesNotOverrideTtyDetection()
    {
        // CLICOLOR=1 is the conventional default and must not override TTY detection.
        // CLICOLOR=1 は慣習的なデフォルトであり、TTY 判定を上書きしない。
        WithColorEnvironment(noColor: null, cliColor: "1", cliColorForce: null, () =>
        {
            Assert.Equal(ConsoleUi.ShouldUseInteractiveConsole(), ConsoleUi.ShouldUseColor());
        });
    }

    [Fact]
    public void ColorizeKind_CliColorForce_ForcesAnsiEvenWithoutTty()
    {
        WithColorEnvironment(noColor: null, cliColor: null, cliColorForce: "1", () =>
        {
            Assert.True(ConsoleUi.ShouldUseColor());
            var output = ConsoleUi.ColorizeKind("function");
            Assert.StartsWith("\x1b[33m", output);
            Assert.EndsWith("\x1b[0m", output);
        });
    }

    [Fact]
    public void ColorizeKind_CliColorForceZero_DoesNotForceColor()
    {
        // CLICOLOR_FORCE=0 must not force color on; behavior falls back to TTY detection.
        // CLICOLOR_FORCE=0 は色を強制せず、TTY 判定にフォールバックする。
        WithColorEnvironment(noColor: null, cliColor: null, cliColorForce: "0", () =>
        {
            Assert.Equal(ConsoleUi.ShouldUseInteractiveConsole(), ConsoleUi.ShouldUseColor());
        });
    }

    [Fact]
    public void ColorizeKind_CliColorForceBeatsNoColor()
    {
        // CLICOLOR_FORCE has the highest precedence so users can override a
        // global NO_COLOR for a single command.
        // CLICOLOR_FORCE は NO_COLOR より優先され、単発で色を有効化できる。
        WithColorEnvironment(noColor: "1", cliColor: "0", cliColorForce: "1", () =>
        {
            Assert.True(ConsoleUi.ShouldUseColor());
        });
    }

    [Fact]
    public void ShouldUseInteractiveConsole_Utf8WindowsTerminal_IsInteractive()
    {
        Assert.True(ConsoleUi.ShouldUseInteractiveConsole(
            isOutputRedirected: false,
            outputEncoding: Encoding.UTF8,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: true,
            isTerminalEnvironmentDisabled: false,
            isWindows: true));
    }

    [Fact]
    public void ShouldUseInteractiveConsole_StringWriterUtf16Capture_IsNotInteractive()
    {
        Assert.False(ConsoleUi.ShouldUseInteractiveConsole(
            isOutputRedirected: false,
            outputEncoding: Encoding.Unicode,
            isTextWriterCapture: true,
            hasTerminalEnvironmentHint: false,
            isTerminalEnvironmentDisabled: false,
            isWindows: false));
    }

    [Fact]
    public void ShouldUseInteractiveConsole_StringWriterCaptureWinsOverTerminalHint()
    {
        Assert.False(ConsoleUi.ShouldUseInteractiveConsole(
            isOutputRedirected: false,
            outputEncoding: Encoding.Unicode,
            isTextWriterCapture: true,
            hasTerminalEnvironmentHint: true,
            isTerminalEnvironmentDisabled: false,
            isWindows: true));
    }

    [Fact]
    public void ShouldUseInteractiveConsole_WindowsUtf16TerminalHint_IsInteractiveWhenNotCaptured()
    {
        Assert.True(ConsoleUi.ShouldUseInteractiveConsole(
            isOutputRedirected: false,
            outputEncoding: Encoding.Unicode,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: true,
            isTerminalEnvironmentDisabled: false,
            isWindows: true));
    }

    [Fact]
    public void ShouldUseInteractiveConsole_DumbOrCiEnvironment_DisablesInteractiveOutput()
    {
        Assert.False(ConsoleUi.ShouldUseInteractiveConsole(
            isOutputRedirected: false,
            outputEncoding: Encoding.UTF8,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: true,
            isTerminalEnvironmentDisabled: true,
            isWindows: false));
    }

    [Fact]
    public void ShouldUseInteractiveConsole_UnixWithoutTerminalHint_DisablesInteractiveOutput()
    {
        Assert.False(ConsoleUi.ShouldUseInteractiveConsole(
            isOutputRedirected: false,
            outputEncoding: Encoding.UTF8,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: false,
            isTerminalEnvironmentDisabled: false,
            isWindows: false));
    }

    [Fact]
    public void ShouldUseAnsiOutput_StringWriterCaptureWinsOverTerminalHint()
    {
        Assert.False(ConsoleUi.ShouldUseAnsiOutput(
            isOutputRedirected: false,
            outputEncoding: Encoding.Unicode,
            isTextWriterCapture: true,
            hasTerminalEnvironmentHint: true,
            isTerminalEnvironmentDisabled: false,
            isWindows: true,
            windowsVirtualTerminalProcessingEnabled: true));
    }

    [Fact]
    public void ShouldUseAnsiOutput_WindowsUtf16VirtualTerminal_IsAnsiWhenNotCaptured()
    {
        Assert.True(ConsoleUi.ShouldUseAnsiOutput(
            isOutputRedirected: false,
            outputEncoding: Encoding.Unicode,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: false,
            isTerminalEnvironmentDisabled: false,
            isWindows: true,
            windowsVirtualTerminalProcessingEnabled: true));
    }

    [Fact]
    public void ShouldUseAnsiOutput_WindowsRequiresVirtualTerminalOrTerminalHint()
    {
        Assert.True(ConsoleUi.ShouldUseAnsiOutput(
            isOutputRedirected: false,
            outputEncoding: Encoding.UTF8,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: false,
            isTerminalEnvironmentDisabled: false,
            isWindows: true,
            windowsVirtualTerminalProcessingEnabled: true));

        Assert.True(ConsoleUi.ShouldUseAnsiOutput(
            isOutputRedirected: false,
            outputEncoding: Encoding.UTF8,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: true,
            isTerminalEnvironmentDisabled: false,
            isWindows: true,
            windowsVirtualTerminalProcessingEnabled: false));

        Assert.False(ConsoleUi.ShouldUseAnsiOutput(
            isOutputRedirected: false,
            outputEncoding: Encoding.UTF8,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: false,
            isTerminalEnvironmentDisabled: false,
            isWindows: true,
            windowsVirtualTerminalProcessingEnabled: false));
    }

    [Fact]
    public void ShouldUseAnsiOutput_RedirectedOutput_DisablesAnsiEvenWithTerminalHint()
    {
        Assert.False(ConsoleUi.ShouldUseAnsiOutput(
            isOutputRedirected: true,
            outputEncoding: Encoding.UTF8,
            isTextWriterCapture: false,
            hasTerminalEnvironmentHint: true,
            isTerminalEnvironmentDisabled: false,
            isWindows: true,
            windowsVirtualTerminalProcessingEnabled: true));
    }

    private static void WithColorEnvironment(string? noColor, string? cliColor, string? cliColorForce, Action action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
            var originalCliColor = Environment.GetEnvironmentVariable("CLICOLOR");
            var originalCliColorForce = Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
            var originalColorTerm = Environment.GetEnvironmentVariable("COLORTERM");
            var originalTerm = Environment.GetEnvironmentVariable("TERM");
            var originalPaletteEnv = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
            var originalPalette = ConsoleUi.GetExplicitColorPalette();
            try
            {
                Environment.SetEnvironmentVariable("NO_COLOR", noColor);
                Environment.SetEnvironmentVariable("CLICOLOR", cliColor);
                Environment.SetEnvironmentVariable("CLICOLOR_FORCE", cliColorForce);
                // These ANSI-code assertions predate the palette feature and assume
                // the 8-color basic palette; pin it explicitly so the host's
                // COLORTERM/TERM cannot upgrade the palette mid-test.
                Environment.SetEnvironmentVariable("COLORTERM", null);
                Environment.SetEnvironmentVariable("TERM", null);
                Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", null);
                ConsoleUi.SetColorPalette(ColorPalette.Basic);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);
                Environment.SetEnvironmentVariable("CLICOLOR", originalCliColor);
                Environment.SetEnvironmentVariable("CLICOLOR_FORCE", originalCliColorForce);
                Environment.SetEnvironmentVariable("COLORTERM", originalColorTerm);
                Environment.SetEnvironmentVariable("TERM", originalTerm);
                Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", originalPaletteEnv);
                ConsoleUi.SetColorPalette(originalPalette);
            }
        }
    }

    private static void WithUnicodeEnvironment(
        string? cdidxAscii,
        string? lang,
        Action action,
        string? term = null,
        string? noUnicode = null,
        string? atBridgeType = null,
        string? accessibilityEnabled = null)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalAscii = Environment.GetEnvironmentVariable("CDIDX_ASCII");
            var originalLang = Environment.GetEnvironmentVariable("LANG");
            var originalLcAll = Environment.GetEnvironmentVariable("LC_ALL");
            var originalLcCType = Environment.GetEnvironmentVariable("LC_CTYPE");
            var originalTerm = Environment.GetEnvironmentVariable("TERM");
            var originalNoUnicode = Environment.GetEnvironmentVariable("NO_UNICODE");
            var originalAtBridgeType = Environment.GetEnvironmentVariable("AT_BRIDGE_TYPE");
            var originalAccessibilityEnabled = Environment.GetEnvironmentVariable("ACCESSIBILITY_ENABLED");
            var originalOutputEncoding = Console.OutputEncoding;
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Environment.SetEnvironmentVariable("CDIDX_ASCII", cdidxAscii);
                Environment.SetEnvironmentVariable("LANG", lang);
                Environment.SetEnvironmentVariable("LC_ALL", null);
                Environment.SetEnvironmentVariable("LC_CTYPE", null);
                Environment.SetEnvironmentVariable("TERM", term);
                Environment.SetEnvironmentVariable("NO_UNICODE", noUnicode);
                Environment.SetEnvironmentVariable("AT_BRIDGE_TYPE", atBridgeType);
                Environment.SetEnvironmentVariable("ACCESSIBILITY_ENABLED", accessibilityEnabled);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("CDIDX_ASCII", originalAscii);
                Environment.SetEnvironmentVariable("LANG", originalLang);
                Environment.SetEnvironmentVariable("LC_ALL", originalLcAll);
                Environment.SetEnvironmentVariable("LC_CTYPE", originalLcCType);
                Environment.SetEnvironmentVariable("TERM", originalTerm);
                Environment.SetEnvironmentVariable("NO_UNICODE", originalNoUnicode);
                Environment.SetEnvironmentVariable("AT_BRIDGE_TYPE", originalAtBridgeType);
                Environment.SetEnvironmentVariable("ACCESSIBILITY_ENABLED", originalAccessibilityEnabled);
                Console.OutputEncoding = originalOutputEncoding;
            }
        }
    }

    private static void WithColumnsEnvironment(string? columns, Action action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalColumns = Environment.GetEnvironmentVariable("COLUMNS");
            try
            {
                Environment.SetEnvironmentVariable("COLUMNS", columns);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("COLUMNS", originalColumns);
            }
        }
    }

    [Theory]
    [InlineData("serach", "search")]
    [InlineData("statu", "status")]
    [InlineData("defnition", "definition")]
    [InlineData("refernces", "references")]
    [InlineData("indx", "index")]
    [InlineData("mpa", "map")]
    [InlineData("backfillfold", "backfill-fold")]
    public void FindClosestCommand_Typo_ReturnsCorrectSuggestion(string input, string expected)
    {
        Assert.Equal(expected, ConsoleUi.FindClosestCommand(input));
    }

    [Theory]
    [InlineData("xyzabc")]
    [InlineData("foobarqux")]
    [InlineData("fold")]
    public void FindClosestCommand_GarbageInput_ReturnsNull(string input)
    {
        Assert.Null(ConsoleUi.FindClosestCommand(input));
    }

    [Theory]
    [InlineData("--paht", "--path")]
    [InlineData("--exclud-path", "--exclude-path")]
    [InlineData("--limti", "--limit")]
    [InlineData("--lnag", "--lang")]
    public void FindClosestMatch_FlagTypo_SuggestsClosestFlag(string input, string expected)
    {
        var flags = new[] { "--path", "--exclude-path", "--limit", "--lang", "--kind", "--json", "--query" };

        Assert.Equal(expected, ConsoleUi.FindClosestMatch(input, flags));
    }

    [Theory]
    [InlineData("pythno", "python")]
    [InlineData("csarp", "csharp")]
    [InlineData("typescritp", "typescript")]
    public void FindClosestMatch_LanguageTypo_SuggestsClosestLanguage(string input, string expected)
    {
        var languages = new[] { "python", "csharp", "typescript", "javascript", "go", "rust" };

        Assert.Equal(expected, ConsoleUi.FindClosestMatch(input, languages));
    }

    [Fact]
    public void FindClosestMatch_BlankInput_ReturnsNull()
    {
        Assert.Null(ConsoleUi.FindClosestMatch(null, new[] { "csharp" }));
        Assert.Null(ConsoleUi.FindClosestMatch("", new[] { "csharp" }));
        Assert.Null(ConsoleUi.FindClosestMatch("  ", new[] { "csharp" }));
    }

    [Fact]
    public void FindClosestMatches_ReturnsRankedSuggestions()
    {
        var candidates = new[] { "added", "changed", "fixed", "removed", "security", "docs" };

        var matches = ConsoleUi.FindClosestMatches("addd", candidates, maxResults: 2);

        Assert.Contains("added", matches);
        Assert.True(matches.Count <= 2);
    }

    [Fact]
    public void FindClosestMatches_NoMatchesWithinThreshold_ReturnsEmpty()
    {
        var candidates = new[] { "added", "changed" };

        var matches = ConsoleUi.FindClosestMatches("absolutelynotrelated", candidates);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindClosestMatches_ZeroMaxResults_ReturnsEmpty()
    {
        var candidates = new[] { "added", "changed" };

        var matches = ConsoleUi.FindClosestMatches("addd", candidates, maxResults: 0);

        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("auto", ColorMode.Auto)]
    [InlineData("Always", ColorMode.Always)]
    [InlineData("NEVER", ColorMode.Never)]
    [InlineData(" always ", ColorMode.Always)]
    public void TryParseColorMode_KnownValue_ReturnsTrue(string value, ColorMode expected)
    {
        Assert.True(ConsoleUi.TryParseColorMode(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseColorMode_UnknownValue_ReturnsFalse(string? value)
    {
        Assert.False(ConsoleUi.TryParseColorMode(value, out _));
    }

    [Fact]
    public void ShouldUseColor_Always_OverridesNoColorEnv()
    {
        using var env = new ColorEnvironmentScope();
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
        ConsoleUi.SetColorMode(ColorMode.Always);

        Assert.True(ConsoleUi.ShouldUseColor());
    }

    [Fact]
    public void ShouldUseColor_Never_OverridesCliColorForceEnv()
    {
        using var env = new ColorEnvironmentScope();
        Environment.SetEnvironmentVariable("CLICOLOR_FORCE", "1");
        ConsoleUi.SetColorMode(ColorMode.Never);

        Assert.False(ConsoleUi.ShouldUseColor());
    }

    [Fact]
    public void ColorizeKind_ColorModeAlways_EmitsAnsiEvenWhenRedirected()
    {
        using var env = new ColorEnvironmentScope();
        ConsoleUi.SetColorMode(ColorMode.Always);

        var output = ConsoleUi.ColorizeKind("class");

        Assert.Contains("\x1b[36m", output);
        Assert.Contains("\x1b[0m", output);
    }

    [Fact]
    public void ColorizeKind_JsonOutputScopeSuppressesAnsiEvenWhenForced()
    {
        using var env = new ColorEnvironmentScope();
        ConsoleUi.SetColorMode(ColorMode.Always);

        using (ConsoleUi.SuppressAnsiForJsonOutput(enabled: true))
        {
            var output = ConsoleUi.ColorizeKind("class");

            Assert.Equal("class", output);
            Assert.DoesNotContain('\x1b', output);
        }

        Assert.Contains("\x1b[36m", ConsoleUi.ColorizeKind("class"));
    }

    [Fact]
    public void ColorizeKind_ColorModeNever_OmitsAnsi()
    {
        using var env = new ColorEnvironmentScope();
        ConsoleUi.SetColorMode(ColorMode.Never);

        var output = ConsoleUi.ColorizeKind("class");

        Assert.DoesNotContain("\x1b[", output);
        Assert.Equal("class", output);
    }

    [Fact]
    public void PrintUsage_DocumentsColorFlag()
    {
        var output = CaptureFullUsageOutput(showBanner: false);
        Assert.Contains("--color <when>", output);
        Assert.Contains("`auto`", output);
        Assert.Contains("`always`", output);
        Assert.Contains("`never`", output);
        Assert.Contains("NO_COLOR", output);
        Assert.Contains("CLICOLOR_FORCE", output);
    }

    [Fact]
    public void PrintUsage_DocumentsPaletteFlag()
    {
        var output = CaptureFullUsageOutput(showBanner: false);
        Assert.Contains("--palette <name>", output);
        Assert.Contains("`basic`", output);
        Assert.Contains("`256`", output);
        Assert.Contains("`truecolor`", output);
        Assert.Contains("COLORTERM", output);
        Assert.Contains("CDIDX_COLOR_PALETTE", output);
    }

    [Theory]
    [InlineData("basic", ColorPalette.Basic)]
    [InlineData("BASIC", ColorPalette.Basic)]
    [InlineData("8", ColorPalette.Basic)]
    [InlineData("16", ColorPalette.Basic)]
    [InlineData("ansi", ColorPalette.Basic)]
    [InlineData("256", ColorPalette.Color256)]
    [InlineData("color256", ColorPalette.Color256)]
    [InlineData("8bit", ColorPalette.Color256)]
    [InlineData("truecolor", ColorPalette.Truecolor)]
    [InlineData("24bit", ColorPalette.Truecolor)]
    [InlineData("rgb", ColorPalette.Truecolor)]
    [InlineData(" Truecolor ", ColorPalette.Truecolor)]
    public void TryParseColorPalette_KnownValue_ReturnsTrue(string value, ColorPalette expected)
    {
        Assert.True(ConsoleUi.TryParseColorPalette(value, out var palette));
        Assert.Equal(expected, palette);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("none")]
    [InlineData("fancy")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseColorPalette_UnknownValue_ReturnsFalse(string? value)
    {
        Assert.False(ConsoleUi.TryParseColorPalette(value, out _));
    }

    [Fact]
    public void ColorizeKind_BasicPalette_DoesNotEmitDimEscape()
    {
        // The dim escape `\x1b[90m` (bright black) is unreadable on many
        // minimal SSH / CI terminals; the basic palette must avoid it (#1569).
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        ConsoleUi.SetColorMode(ColorMode.Always);
        ConsoleUi.SetColorPalette(ColorPalette.Basic);

        foreach (var kind in new[] { "namespace", "import", "class", "function" })
        {
            var output = ConsoleUi.ColorizeKind(kind);
            Assert.DoesNotContain("\x1b[90m", output);
        }
    }

    [Fact]
    public void ColorizeKind_Color256Palette_EmitsExtendedSgrCodes()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        ConsoleUi.SetColorMode(ColorMode.Always);
        ConsoleUi.SetColorPalette(ColorPalette.Color256);

        var output = ConsoleUi.ColorizeKind("class");
        Assert.StartsWith("\x1b[38;5;", output);
        Assert.EndsWith("\x1b[0m", output);
    }

    [Fact]
    public void ColorizeKind_TruecolorPalette_EmitsRgbSgrCodes()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        ConsoleUi.SetColorMode(ColorMode.Always);
        ConsoleUi.SetColorPalette(ColorPalette.Truecolor);

        var output = ConsoleUi.ColorizeKind("class");
        Assert.StartsWith("\x1b[38;2;", output);
        Assert.EndsWith("\x1b[0m", output);
    }

    [Fact]
    public void ResolveColorPalette_ExplicitOverrideWins()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", "256");
        ConsoleUi.SetColorPalette(ColorPalette.Basic);

        Assert.Equal(ColorPalette.Basic, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_EnvVarWinsOverDetection()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", "basic");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Basic, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_ColorTermTruecolor_DetectedAsTruecolor()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
        Environment.SetEnvironmentVariable("TERM", "xterm");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Truecolor, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_ColorTerm24bit_DetectedAsTruecolor()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", "24bit");
        Environment.SetEnvironmentVariable("TERM", "xterm");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Truecolor, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_Term256color_DetectedAsColor256()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", null);
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Color256, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ResolveColorPalette_MinimalTerm_FallsBackToBasic()
    {
        using var env = new ColorEnvironmentScope();
        using var pal = new ColorPaletteScope();
        Environment.SetEnvironmentVariable("COLORTERM", null);
        Environment.SetEnvironmentVariable("TERM", "xterm");
        ConsoleUi.SetColorPalette(null);

        Assert.Equal(ColorPalette.Basic, ConsoleUi.ResolveColorPalette());
    }

    [Fact]
    public void ColorizeKind_NoColorRequested_SuppressesAllPalettes()
    {
        // NO_COLOR must consistently suppress ANSI escapes across every palette
        // so users opting out via the env standard get clean output regardless
        // of `--palette` (#1569).
        foreach (var palette in new[] { ColorPalette.Basic, ColorPalette.Color256, ColorPalette.Truecolor })
        {
            using var env = new ColorEnvironmentScope();
            using var pal = new ColorPaletteScope();
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            ConsoleUi.SetColorPalette(palette);

            var output = ConsoleUi.ColorizeKind("class");
            Assert.DoesNotContain('\x1b', output);
            Assert.Equal("class", output);
        }
    }

    private sealed class ColorPaletteScope : IDisposable
    {
        private readonly ColorPalette? _original;
        private readonly string? _originalEnv;
        private readonly string? _originalColorTerm;
        private readonly string? _originalTerm;

        public ColorPaletteScope()
        {
            _original = ConsoleUi.GetExplicitColorPalette();
            _originalEnv = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
            _originalColorTerm = Environment.GetEnvironmentVariable("COLORTERM");
            _originalTerm = Environment.GetEnvironmentVariable("TERM");
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", null);
            Environment.SetEnvironmentVariable("COLORTERM", null);
            Environment.SetEnvironmentVariable("TERM", null);
            ConsoleUi.SetColorPalette(null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", _originalEnv);
            Environment.SetEnvironmentVariable("COLORTERM", _originalColorTerm);
            Environment.SetEnvironmentVariable("TERM", _originalTerm);
            ConsoleUi.SetColorPalette(_original);
        }
    }

    private sealed class ProgressAnimationEnvironmentScope : IDisposable
    {
        private readonly string? _originalDisableProgress;
        private readonly string? _originalReducedMotion;

        public ProgressAnimationEnvironmentScope()
        {
            _originalDisableProgress = Environment.GetEnvironmentVariable(ConsoleUi.DisableProgressEnvironmentVariable);
            _originalReducedMotion = Environment.GetEnvironmentVariable(ConsoleUi.PrefersReducedMotionEnvironmentVariable);
            Environment.SetEnvironmentVariable(ConsoleUi.DisableProgressEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(ConsoleUi.PrefersReducedMotionEnvironmentVariable, null);
            ConsoleUi.SetProgressAnimationEnabled(null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ConsoleUi.DisableProgressEnvironmentVariable, _originalDisableProgress);
            Environment.SetEnvironmentVariable(ConsoleUi.PrefersReducedMotionEnvironmentVariable, _originalReducedMotion);
            ConsoleUi.SetProgressAnimationEnabled(null);
        }
    }

    private sealed class ColorEnvironmentScope : IDisposable
    {
        private readonly bool _lockTaken;
        private readonly string? _originalNoColor;
        private readonly string? _originalForce;
        private readonly string? _originalCliColor;
        private readonly string? _originalColorTerm;
        private readonly string? _originalTerm;
        private readonly string? _originalPaletteEnv;
        private readonly ColorMode _originalMode;
        private readonly ColorPalette? _originalPalette;

        public ColorEnvironmentScope()
        {
            Monitor.Enter(TestConsoleLock.Gate);
            _lockTaken = true;
            _originalNoColor = Environment.GetEnvironmentVariable("NO_COLOR");
            _originalForce = Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
            _originalCliColor = Environment.GetEnvironmentVariable("CLICOLOR");
            _originalColorTerm = Environment.GetEnvironmentVariable("COLORTERM");
            _originalTerm = Environment.GetEnvironmentVariable("TERM");
            _originalPaletteEnv = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
            _originalMode = ConsoleUi.GetColorMode();
            _originalPalette = ConsoleUi.GetExplicitColorPalette();
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.SetEnvironmentVariable("CLICOLOR_FORCE", null);
            Environment.SetEnvironmentVariable("CLICOLOR", null);
            Environment.SetEnvironmentVariable("COLORTERM", null);
            Environment.SetEnvironmentVariable("TERM", null);
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", null);
            ConsoleUi.SetColorPalette(null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NO_COLOR", _originalNoColor);
            Environment.SetEnvironmentVariable("CLICOLOR_FORCE", _originalForce);
            Environment.SetEnvironmentVariable("CLICOLOR", _originalCliColor);
            Environment.SetEnvironmentVariable("COLORTERM", _originalColorTerm);
            Environment.SetEnvironmentVariable("TERM", _originalTerm);
            Environment.SetEnvironmentVariable("CDIDX_COLOR_PALETTE", _originalPaletteEnv);
            ConsoleUi.SetColorMode(_originalMode);
            ConsoleUi.SetColorPalette(_originalPalette);
            if (_lockTaken)
                Monitor.Exit(TestConsoleLock.Gate);
        }
    }

    private sealed class FlushCountingTextWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }

    private sealed class SlowChunkingTextWriter : TextWriter
    {
        private readonly StringBuilder builder = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            builder.Append(value);
            Thread.Sleep(1);
        }

        public override string ToString() => builder.ToString();
    }

    private static bool CallsTaskWait(MethodInfo method)
    {
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il == null)
            return false;

        var module = method.Module;
        for (var i = 0; i < il.Length;)
        {
            var opCode = ReadOpCode(il, ref i);
            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt) && i + 4 <= il.Length)
            {
                var token = BitConverter.ToInt32(il, i);
                i += 4;
                if (module.ResolveMember(token) is MethodInfo called
                    && called.Name == nameof(Task.Wait)
                    && called.DeclaringType == typeof(Task))
                {
                    return true;
                }

                continue;
            }

            i += OperandByteCount(opCode, il, i);
        }

        return false;
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        if (first != 0xfe)
            return SingleByteOpCodes[(short)first];

        var second = il[offset++];
        return MultiByteOpCodes[(short)second];
    }

    private static int OperandByteCount(OpCode opCode, byte[] il, int operandOffset)
        => opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineI
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandOffset)),
            _ => throw new NotSupportedException($"Unsupported IL operand type: {opCode.OperandType}"),
        };

    private static string CaptureFullUsageOutput(bool showBanner = true)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        ConsoleUi.PrintUsageFull(showBanner);
        return capture.Out!.ToString()!;
    }

    private static string CaptureBriefUsageOutput(bool showBanner = true)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        ConsoleUi.PrintUsageBrief(showBanner);
        return capture.Out!.ToString()!;
    }

    private static string ExtractBetween(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        start += startMarker.Length;
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Missing end marker: {endMarker}");
        return text[start..end];
    }
}
