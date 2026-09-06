using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void SearchMatchClassifier_ShellSubstitutionQuotesEscapesAndBounds_Issue5275()
    {
        (string Text, string Origin)[] cases =
        [
            ("needle --help", "code"),
            ("value=$(needle --help)", "code"),
            ("value=\"$(needle --help)\"", "code"),
            ("value=\"$(ENV=\"$timeout\" needle --help)\"", "code"),
            ("value=\"$(echo \"$(needle --help)\")\"", "code"),
            ("value=\"$((1 + $(needle)))\"", "code"),
            ("value=\"$( (echo ')'); needle --help)\"", "code"),
            ("value=`needle --help`", "code"),
            ("value=\"`needle --help`\"", "code"),
            ("value=\"$(echo \"`needle`\")\"", "code"),
            ("value=\"`echo $(needle)`\"", "code"),
            ("value=\"`echo \"\\`needle\\`\"`\"", "code"),
            ("value=\"`echo \"\\$(needle)\"`\"", "code"),
            ("value=\"`echo '\\`needle\\`'`\"", "string_literal"),
            ("value=\"`echo \"\\\\$(needle)\"`\"", "string_literal"),
            ("value=\"`echo \"\\`echo ok\\` needle\"`\"", "string_literal"),
            ("value=\"$(case x in x) needle;; esac)\"", "code"),
            ("value=\"$(case x in (x) needle;; esac)\"", "code"),
            ("value=\"$(case 'x' in x) echo ok;; *) needle;; esac)\"", "code"),
            ("value=\"$(case x in x) case y in y) needle;; esac;; esac)\"", "code"),
            ("value=\"$(case x in x) echo ok;; esac) needle\"", "string_literal"),
            ("value=\"$(echo case) needle\"", "string_literal"),
            ("value=\"$(echo then case x in x) needle\"", "string_literal"),
            ("value=\"$({ case x in x) needle;; esac; })\"", "code"),
            ("value=\"$(case x in x) echo esac; needle;; esac)\"", "code"),
            ("value=\"\\\\$(needle)\"", "code"),
            ("value='a\\'; needle", "code"),
            ("value=\\$'a\\'; needle", "code"),
            ("value=\"$(echo x\\ #; needle)\"", "code"),
            ("value=\"$(echo ''#; needle)\"", "code"),
            ("value=\"$(echo '#'); needle\"", "string_literal"),
            ("value=\"before $(echo ok) needle\"", "string_literal"),
            ("value=\"needle $(echo ok)\"", "string_literal"),
            ("value=\"$(echo 'needle')\"", "string_literal"),
            ("value=\"$(echo \"needle\")\"", "string_literal"),
            ("value='$(needle)'", "string_literal"),
            ("value='`needle`'", "string_literal"),
            ("value=\"\\$(needle)\"", "string_literal"),
            ("value=\"\\`needle\\`\"", "string_literal"),
            ("value=$'escaped\\\' $(needle)'", "string_literal"),
            ("echo 'Usage: $(needle --help)'", "help_text"),
            ("echo \"Usage: \\$(needle --help)\"", "help_text"),
            ("echo \"Usage: \\`needle --help\\`\"", "help_text"),
            ("# value=\"$(needle --help)\"", "comment"),
            ("echo ok; # $(needle --help)", "comment"),
            ("value=\"$(echo ok; # needle)\"", "comment"),
            (string.Concat(Enumerable.Repeat("$(", 63)) + "needle", "code"),
            (string.Concat(Enumerable.Repeat("$(", 64)) + "needle", "unknown"),
            (string.Concat(Enumerable.Repeat("case x in x) ", 64)) + "needle", "code"),
            (string.Concat(Enumerable.Repeat("case x in x) ", 65)) + "needle", "unknown"),
            (string.Concat(Enumerable.Range(0, 5).Select(level => new string('\\', (1 << level) - 1) + "`")) +
                new string(' ', 60000) + "needle" +
                string.Concat(Enumerable.Range(0, 5).Reverse().Select(level => new string('\\', (1 << level) - 1) + "`")), "unknown"),
            (new string(' ', 65535) + "needle", "code"),
            (new string(' ', 65536) + "needle", "unknown"),
        ];
        foreach (var lang in new[] { "shell", "bash", "zsh" })
            foreach (var (text, expected) in cases)
            {
                var column = text.IndexOf("needle", StringComparison.Ordinal) + 1;
                var facet = SearchMatchClassifier.Classify("src/a.sh", lang, 7, text, column, 6);
                Assert.True(expected == facet.Origin, $"{lang}: {text[..Math.Min(text.Length, 180)]}: {facet.Origin}");
                Assert.Equal(7, facet.Line);
                Assert.Equal(column, facet.Column);
                Assert.Equal(6, facet.Length);
            }

        foreach (var lang in new[] { "csharp", "javascript" })
        {
            const string text = "var value = \"$(needle --help)\";";
            Assert.Equal("help_text", SearchMatchClassifier.Classify(
                "src/a", lang, 1, text, text.IndexOf("needle", StringComparison.Ordinal) + 1, 6).Origin);
        }
    }

    [Fact]
    public void RunSearch_ShellSubstitutionsPreserveOriginsFiltersAndCoordinates_Issue5275()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_shell_origin_5275");
        try
        {
            const string needle = "run_curl_with_optional_loopback_bypass";
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            (string Path, string Text, string Origin)[] fixtures =
            [
                ("src/direct.sh", "run_curl_with_optional_loopback_bypass --help", "code"),
                ("src/installer.sh", "http_code=\"$(CURL_ATTEMPT_RETRY_COUNT=0 CURL_ATTEMPT_MAX_TIME_SECONDS=\"$remaining_seconds\" run_curl_with_optional_loopback_bypass \"$url\" -sSL --max-filesize \"$max_bytes\" -o \"$body_fifo\" -w '%{http_code}' \"$url\" 2>\"$curl_stderr\")\" || curl_status=$?", "code"),
                ("src/nested.sh", "value=\"$(echo \"$(run_curl_with_optional_loopback_bypass --help)\")\"", "code"),
                ("src/backtick.sh", "value=\"`run_curl_with_optional_loopback_bypass --help`\"", "code"),
                ("src/backtick-nested.sh", "value=\"`echo \"\\`run_curl_with_optional_loopback_bypass --help\\`\"`\"", "code"),
                ("src/backtick-dollar.sh", "value=\"`echo \"\\$(run_curl_with_optional_loopback_bypass --help)\"`\"", "code"),
                ("src/case.sh", "value=\"$(case x in x) run_curl_with_optional_loopback_bypass --help;; esac)\"", "code"),
                ("src/case-literal.sh", "value=\"$(case x in x) echo ok;; esac) run_curl_with_optional_loopback_bypass\"", "string_literal"),
                ("src/backtick-literal.sh", "value=\"`echo '\\`run_curl_with_optional_loopback_bypass --help\\`'`\"", "help_text"),
                ("src/string.sh", "value='$(run_curl_with_optional_loopback_bypass)'", "string_literal"),
                ("src/help.sh", "echo 'Usage: $(run_curl_with_optional_loopback_bypass --help)'", "help_text"),
                ("src/escaped.sh", "echo \"Usage: \\$(run_curl_with_optional_loopback_bypass --help)\"", "help_text"),
                ("src/comment.sh", "# $(run_curl_with_optional_loopback_bypass --help)", "comment"),
            ];
            foreach (var fixture in fixtures)
                TestProjectHelper.InsertIndexedFile(dbPath, fixture.Path, "shell", "\n" + fixture.Text + "\n");

            var recipePath = Path.Combine(projectRoot, "recipes.json");
            File.WriteAllText(recipePath, """
                {"recipes":[{"name":"shell-substitutions","description":"Shell origin regression.",
                  "queries":[{"name":"helper","query":"run_curl_with_optional_loopback_bypass",
                    "description":"Find the helper.","recommended_labels":["audit"],
                    "false_positive_guidance":"Review literal examples."}]}]}
                """);
            using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
            env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);

            foreach (var mode in new[] { "ordinary", "named", "recipe" })
                foreach (var filter in new[] { "all", "code", "exclude-strings" })
                {
                    string[] queryArgs = mode switch
                    {
                        "named" => ["--named-query", "helper=" + needle],
                        "recipe" => ["--recipe", "shell-substitutions"],
                        _ => [needle],
                    };
                    string[] filterArgs = filter switch
                    {
                        "code" => ["--origin", "code"],
                        "exclude-strings" => ["--exclude-strings"],
                        _ => [],
                    };
                    var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                        [.. queryArgs, "--db", dbPath, "--exact-substring", "--limit", "20",
                        "--snippet-lines", "1", mode == "ordinary" ? "--json=array" : "--json", .. filterArgs],
                        _jsonOptions));
                    Assert.Equal(CommandExitCodes.Success, exitCode);
                    Assert.Empty(stderr);
                    using var document = ParseJsonOutput(stdout);
                    var rows = (mode == "ordinary" ? document.RootElement :
                        Assert.Single(document.RootElement.GetProperty("queries").EnumerateArray()).GetProperty("results"))
                        .EnumerateArray().ToArray();
                    var expected = fixtures.Where(f => filter == "all" ||
                        (filter == "code" ? f.Origin == "code" : !SearchMatchClassifier.IsStringLikeOrigin(f.Origin))).ToArray();
                    Assert.Equal(expected.Select(f => f.Path).Order(), rows.Select(r => r.GetProperty("path").GetString()).Order());
                    foreach (var fixture in expected)
                    {
                        var row = Assert.Single(rows, r => r.GetProperty("path").GetString() == fixture.Path);
                        var facet = Assert.Single(row.GetProperty("match_facets").EnumerateArray());
                        Assert.Equal(fixture.Origin, facet.GetProperty("origin").GetString());
                        Assert.Equal(2, facet.GetProperty("line").GetInt32());
                        Assert.Equal(fixture.Text.IndexOf(needle, StringComparison.Ordinal) + 1, facet.GetProperty("column").GetInt32());
                        Assert.Equal(needle.Length, facet.GetProperty("length").GetInt32());
                        Assert.Equal(fixture.Text, row.GetProperty("snippet").GetString());
                        var highlight = Assert.Single(row.GetProperty("highlights").EnumerateArray());
                        Assert.Equal(fixture.Origin, Assert.Single(highlight.GetProperty("match_origins").EnumerateArray()).GetString());
                        var occurrence = Assert.Single(highlight.GetProperty("term_occurrences").EnumerateArray());
                        Assert.Equal(facet.GetProperty("column").GetInt32(), occurrence.GetProperty("column").GetInt32());
                        Assert.Equal(2, occurrence.GetProperty("line").GetInt32());
                        Assert.Equal(needle.Length, occurrence.GetProperty("length").GetInt32());
                    }
                }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
