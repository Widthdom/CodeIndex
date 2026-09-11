using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void XmlSettingsAudit_InlineFactoriesUnknownsAndOutputSelection_Issue5327()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("xml_settings_5327");
        var sources = new Dictionary<string, string>
        {
            ["Factory.cs"] = """
                using System.Xml;
                class Policy
                {
                    const long DocumentLimit = 4L * 1024 * 1024;
                    const long EntityLimit = 16L * 1024;
                    internal static XmlReaderSettings BuildSettings(DtdProcessing mode)
                    {
                        return new XmlReaderSettings
                        {
                            DtdProcessing = mode,
                            XmlResolver = null,
                            MaxCharactersInDocument = DocumentLimit,
                            MaxCharactersFromEntities = EntityLimit,
                        };
                    }
                }
                """,
            ["SafeWrapper.cs"] = """
                using System.Xml;
                class SafeWrapper
                {
                    void Read(string input)
                    {
                        using var reader = XmlReader.Create(input, Policy.BuildSettings(DtdProcessing.Prohibit));
                    }
                }
                """,
            ["SafeInline.cs"] = XmlAuditCaller("SafeInline", """
                using var reader = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
                    MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256
                });
                """),
            ["Unsafe.cs"] = XmlAuditCaller("Unsafe", """
                using var reader = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Parse, XmlResolver = new XmlUrlResolver(),
                    MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256
                });
                """),
            ["IgnoreOnly.cs"] = XmlAuditCaller("IgnoreOnly", "using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });"),
            ["Mutated.cs"] = XmlAuditCaller("Mutated", """
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 };
                using var reader = XmlReader.Create(input, settings);
                settings.DtdProcessing = DtdProcessing.Parse;
                """),
            ["Alias.cs"] = XmlAuditCaller("Alias", """
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 };
                var alias = settings;
                using var reader = XmlReader.Create(input, alias);
                """),
            ["Reassigned.cs"] = XmlAuditCaller("Reassigned", """
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 };
                settings = GetSettings();
                using var reader = XmlReader.Create(input, settings);
                """),
            ["Lookalike.cs"] = XmlAuditCaller("Lookalike", "using var reader = XmlReader.Create(input, BuildSafeXmlReaderSettings(DtdProcessing.Ignore));"),
            ["Dynamic.cs"] = XmlAuditCaller("Dynamic", "using var reader = XmlReader.Create(input, Policy.BuildSettings(GetDtdProcessing()));"),
            ["SafeLocal.cs"] = XmlAuditCaller("SafeLocal", """
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 };
                using var reader = XmlReader.Create(input, settings);
                """),
            ["UsingAlias.cs"] = "using DtdProcessing = Other.Mode;\n" + XmlAuditCaller("UsingAlias", "using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 });"),
            ["OtherSettings.cs"] = XmlAuditCaller("OtherSettings", "var other = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse }; using var reader = XmlReader.Create(input, Policy.BuildSettings(DtdProcessing.Ignore));"),
            ["LegacyOverride.cs"] = XmlAuditCaller("LegacyOverride", "using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256, ProhibitDtd = false });"),
            ["NumericIdentifier.cs"] = XmlAuditCaller("NumericIdentifier", "const long _4096 = 0; using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = _4096, MaxCharactersFromEntities = 256 });"),
            ["EscapedMutation.cs"] = XmlAuditCaller("EscapedMutation", """
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 };
                s\u0065ttings.MaxCharactersInDocument = 0;
                using var reader = XmlReader.Create(input, settings);
                """),
            ["Shadow.cs"] = """
                using System.Xml;
                class Modes { public System.Xml.DtdProcessing Prohibit => System.Xml.DtdProcessing.Parse; }
                class Shadow
                {
                    static Modes DtdProcessing = new Modes();
                    void Read(string input)
                    {
                        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 });
                    }
                }
                """,
            ["OtherConstant.cs"] = """
                using System.Xml;
                class OtherConstant
                {
                    static long Limit = 0;
                    void Read(string input)
                    {
                        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = Limit, MaxCharactersFromEntities = 256 });
                    }
                }
                class UnrelatedConstant
                {
                    const long Limit = 4096;
                }
                """,
            ["Depth.cs"] = """
                using System.Xml;
                class Depth
                {
                    void Read(string input)
                    {
                        using var reader = XmlReader.Create(input, FirstSettings(DtdProcessing.Ignore));
                    }
                    XmlReaderSettings FirstSettings(DtdProcessing mode) => SecondSettings(mode);
                    XmlReaderSettings SecondSettings(DtdProcessing mode) => ThirdSettings(mode);
                    XmlReaderSettings ThirdSettings(DtdProcessing mode) => Policy.BuildSettings(mode);
                }
                """,
        };
        foreach (var (path, content) in sources) TestProjectHelper.WriteTextFile(project.Root, "src/" + path, content);
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);

        JsonElement Run(params string[] extra)
        {
            var result = CaptureConsole(() => QueryCommandRunner.RunAudit(
                ["xml-parser-security", "--db", dbPath, "--limit", "100", "--json", .. extra], _jsonOptions));
            Assert.True(result.Result == 0, result.Stdout + result.Stderr);
            using var json = ParseJsonOutput(result.Stdout);
            return json.RootElement.Clone();
        }
        static IEnumerable<JsonElement> Rows(JsonElement root) => root.GetProperty("queries").EnumerateArray()
            .SelectMany(query => query.GetProperty("results").EnumerateArray());
        static JsonElement Evidence(JsonElement row) => row.GetProperty("audit_classifications")[0].GetProperty("xml_settings");
        var raw = Run();
        var all = Rows(raw).ToArray();
        foreach (var name in new[] { "SafeInline", "SafeWrapper", "SafeLocal", "Unsafe", "IgnoreOnly", "Mutated", "Alias", "Reassigned", "Lookalike", "Dynamic", "UsingAlias", "OtherSettings", "LegacyOverride", "NumericIdentifier", "EscapedMutation", "Shadow", "OtherConstant" })
        {
            var rows = all.Where(row => row.GetProperty("path").GetString() == "src/" + name + ".cs").ToArray();
            Assert.NotEmpty(rows);
            var expected = name.StartsWith("Safe", StringComparison.Ordinal) ? "safe_under_observed_guards"
                : name == "Unsafe" ? "confirmed_unsafe_configuration" : "needs_review";
            Assert.All(rows, row => Assert.True(Evidence(row).GetProperty("state").GetString() == expected, row.GetRawText()));
        }
        var wrapper = Evidence(all.First(row => row.GetProperty("path").GetString() == "src/SafeWrapper.cs"));
        Assert.Equal(4194304, wrapper.GetProperty("max_characters_in_document").GetInt64());
        Assert.Contains(wrapper.GetProperty("sources").EnumerateArray(), source => source.GetProperty("path").GetString() == "src/Factory.cs");
        Assert.Equal("not_established", wrapper.GetProperty("vulnerability_confidence").GetString());
        Assert.Contains(all, row => row.GetProperty("path").GetString() == "src/Depth.cs"
            && Evidence(row).GetProperty("reason").GetString() == "depth_budget_exceeded");
        foreach (var (name, reason) in new[] { ("LegacyOverride", "initializer_side_effects_unknown"),
            ("NumericIdentifier", "guard_combination_unproven"), ("EscapedMutation", "alias_or_lexical_context_unsupported"),
            ("Shadow", "alias_or_lexical_context_unsupported"), ("OtherConstant", "guard_combination_unproven") })
            Assert.Contains(Rows(Run("--path", "src/" + name + ".cs", "--exclude-safe-xml")),
                row => Evidence(row).GetProperty("reason").GetString() == reason);

        foreach (var format in new[] { "json", "compact" })
        {
            var filtered = Run("--format", format, "--exclude-safe-xml");
            var filteredRows = Rows(filtered).ToArray();
            Assert.DoesNotContain(filteredRows, row => Evidence(row).GetProperty("state").GetString() == "safe_under_observed_guards");
            Assert.Contains(filteredRows, row => Evidence(row).GetProperty("state").GetString() == "confirmed_unsafe_configuration");
            foreach (var name in new[] { "LegacyOverride", "NumericIdentifier", "EscapedMutation", "Shadow", "OtherConstant" })
                Assert.Contains(filteredRows, row => row.GetProperty("path").GetString() == "src/" + name + ".cs");
            foreach (var query in filtered.GetProperty("queries").EnumerateArray())
            {
                Assert.Equal(query.GetProperty("source_total").GetInt32(), query.GetProperty("selected_total").GetInt32() + query.GetProperty("selector_omitted_count").GetInt32());
                Assert.Equal("exclude_safe_xml", query.GetProperty("selectors")[0].GetProperty("mode").GetString());
            }
        }
        var drafts = Run("--format", "issue-drafts");
        Assert.All(drafts.GetProperty("drafts").EnumerateArray(), draft => Assert.Equal("textual_match", draft.GetProperty("triage").GetProperty("confidence_scope").GetString()));
        Assert.Contains(drafts.GetProperty("drafts").EnumerateArray(), draft => draft.GetProperty("body").GetString()!.Contains("vulnerability confidence", StringComparison.Ordinal));
        Assert.All(drafts.GetProperty("drafts").EnumerateArray(), draft => Assert.All(draft.GetProperty("evidence").EnumerateArray(), row => Assert.True(row.TryGetProperty("audit_classifications", out _))));
        var filteredDrafts = Run("--format", "issue-drafts", "--exclude-safe-xml");
        Assert.All(filteredDrafts.GetProperty("drafts").EnumerateArray(), draft => Assert.All(draft.GetProperty("evidence").EnumerateArray(), row => Assert.NotEqual("safe_under_observed_guards", Evidence(row).GetProperty("state").GetString())));
        var narrow = Run("--path", "src/SafeInline.cs", "--exclude-safe-xml");
        Assert.Empty(Rows(narrow));
        Assert.True(narrow.GetProperty("queries").EnumerateArray().Sum(q => q.GetProperty("selector_omitted_count").GetInt32()) > 0);
        var emptyDrafts = Run("--path", "src/SafeInline.cs", "--exclude-safe-xml", "--format", "issue-drafts");
        Assert.Empty(emptyDrafts.GetProperty("drafts").EnumerateArray());
        Assert.True(emptyDrafts.GetProperty("selection_accounting").EnumerateArray().Sum(q => q.GetProperty("selector_omitted_count").GetInt32()) > 0);
        var limited = Run("--exclude-safe-xml", "--limit", "1");
        Assert.All(limited.GetProperty("queries").EnumerateArray(), q => Assert.True(q.GetProperty("count").GetInt32() <= 1));
        var human = CaptureConsole(() => QueryCommandRunner.RunAudit(["xml-parser-security", "--db", dbPath, "--exclude-safe-xml", "--format", "text"], _jsonOptions));
        Assert.Equal(0, human.Result);
        foreach (var unsupported in new[] { new[] { "--count" }, ["--json=ndjson"], ["--format", "sarif"], ["--summary-only"] })
        {
            var rejected = CaptureConsole(() => QueryCommandRunner.RunAudit(["xml-parser-security", "--db", dbPath, "--exclude-safe-xml", "--json", .. unsupported], _jsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, rejected.Result);
        }
        var allRejected = CaptureConsole(() => QueryCommandRunner.RunAudit(["--all", "--db", dbPath, "--exclude-safe-xml", "--json"], _jsonOptions));
        Assert.Equal(CommandExitCodes.UsageError, allRejected.Result);

        using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
        using (var reader = new DbReader(db.Connection))
        {
            var session = reader.CreateXmlSettingsAuditSession();
            XmlSettingsAuditEvidence? last = null;
            for (var i = 0; i < 129; i++) last = session.Classify("src/SafeWrapper.cs", "csharp", [6]);
            Assert.Equal("node_budget_exceeded", last!.Reason);
        }

        using (var server = new McpServer(dbPath, "test", dbPathExplicit: true))
        {
            var response = server.HandleMessage(JsonNode.Parse("""
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"recipe":"xml-parser-security","limit":100}}}
                """)!)!;
            var content = response["result"]!["structuredContent"]!;
            Assert.Contains("safe_under_observed_guards", content.ToJsonString());
            Assert.Contains("confirmed_unsafe_configuration", content.ToJsonString());
        }

        // A review annotation cannot override later source drift or absent graph metadata.
        TestProjectHelper.AppendTextFile(project.Root, "src/SafeWrapper.cs", "\n// manual baseline review: safe\n");
        Assert.All(Rows(Run("--path", "src/SafeWrapper.cs")), row => Assert.Equal("needs_review", Evidence(row).GetProperty("state").GetString()));
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            new DbWriter(db.Connection).SetMeta(DbContext.IndexCompletenessMetaKey, "incomplete");
        Assert.All(Rows(Run("--path", "src/SafeInline.cs")), row => Assert.Equal("needs_review", Evidence(row).GetProperty("state").GetString()));
    }

    private static string XmlAuditCaller(string name, string body)
        => "using System.Xml;\nclass " + name + "\n{\n void Read(string input)\n {\n" + body + "\n }\n}\n";

    [Fact]
    public void XmlSettingsAudit_SourceBudgetsMissingRangesAndAmbiguousFactories_Issue5327()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("xml_settings_bounds_5327");
        const string operation = "using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 });";
        TestProjectHelper.WriteTextFile(project.Root, "src/Bytes.cs", XmlAuditCaller("Bytes", operation) + "\n/*" + new string('x', 262144) + "*/");
        TestProjectHelper.WriteTextFile(project.Root, "src/Lines.cs", XmlAuditCaller("Lines", operation) + new string('\n', 4096));
        TestProjectHelper.WriteTextFile(project.Root, "src/Valid.cs", XmlAuditCaller("Valid", operation));
        TestProjectHelper.WriteTextFile(project.Root, "src/PartialShadow.cs", XmlAuditCaller("PartialShadow", operation).Replace("class PartialShadow", "partial class PartialShadow", StringComparison.Ordinal));
        TestProjectHelper.WriteTextFile(project.Root, "src/PartialMember.cs", "partial class PartialShadow { static dynamic DtdProcessing; }");
        TestProjectHelper.WriteTextFile(project.Root, "src/ParameterShadow.cs", XmlAuditCaller("ParameterShadow", operation).Replace("string input", "string input, dynamic @DtdProcessing", StringComparison.Ordinal));
        TestProjectHelper.WriteTextFile(project.Root, "src/LocalConstantShadow.cs", """
            using System.Xml;
            class LocalConstantShadow
            {
                const long Limit = 4096;
                void Read(string input)
                {
                    System.Int64 Limit = 0;
                    using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = Limit, MaxCharactersFromEntities = 256 });
                }
            }
            """);
        TestProjectHelper.WriteTextFile(project.Root, "src/Factories.cs", """
            using System.Xml;
            class A
            {
                internal static XmlReaderSettings SameXmlReaderSettingsFactory() => new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4096, MaxCharactersFromEntities = 256 };
            }
            class B
            {
                internal static XmlReaderSettings SameXmlReaderSettingsFactory() => new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = new XmlUrlResolver() };
            }
            class Caller
            {
                void Read(string input)
                {
                    using var reader = XmlReader.Create(input, A.SameXmlReaderSettingsFactory());
                }
            }
            """);
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var indexed = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", dbPath, "--json", "--quiet"], _jsonOptions));
        Assert.True(indexed.Result == 0, indexed.Stdout + indexed.Stderr);
        XmlSettingsAuditEvidence Classify(string path, int line = 6)
        {
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using var reader = new DbReader(db.Connection);
            return reader.CreateXmlSettingsAuditSession().Classify("src/" + path, "csharp", [line]);
        }
        Assert.Equal("safe_under_observed_guards", Classify("Valid.cs").State);
        Assert.Equal("needs_review", Classify("Bytes.cs").State);
        Assert.Equal("source_line_budget_exceeded", Classify("Lines.cs").Reason);
        Assert.Equal("factory_target_ambiguous", Classify("Factories.cs", 14).Reason);
        Assert.Equal("framework_value_binding_unresolved", Classify("PartialShadow.cs").Reason);
        Assert.Equal("alias_or_lexical_context_unsupported", Classify("ParameterShadow.cs").Reason);
        Assert.Equal("guard_combination_unproven", Classify("LocalConstantShadow.cs", 8).Reason);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            new DbWriter(db.Connection).SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("csharp"), "0");
        Assert.Equal("needs_review", Classify("Valid.cs").State);
    }
}
