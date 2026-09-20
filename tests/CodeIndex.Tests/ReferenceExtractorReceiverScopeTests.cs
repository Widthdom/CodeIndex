using System.Collections;
using System.Reflection;
using System.Text;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class ReferenceExtractorReceiverScopeTests
{
    [Fact]
    public void EnumMemberCandidates_BuildReceiverScopesOnlyForEligibleReads()
    {
        var targets = new Dictionary<string, List<(string, string?, bool)>>(StringComparer.Ordinal)
        {
            ["Ready"] = [("Status", "Demo.Status", true)],
        };
        ReferenceExtractor.CSharpUsingAliasRecord[] aliases =
        [
            new("Alias", "Demo.Status", 1, 1, 100, true),
            new("Wrong", "Other.Mode", 1, 1, 100, true),
        ];
        var container = new SymbolRecord
        {
            FileId = 1,
            Kind = "function",
            Name = "Run",
            StartLine = 1,
            EndLine = 100,
            BodyStartLine = 1,
            BodyEndLine = 100,
        };
        var typeNames = new Dictionary<string, ReferenceExtractor.CSharpContainingTypeValueReceiverNames>();
        var unshadowedNames = new Dictionary<int, List<ReferenceExtractor.CSharpFunctionValueReceiverNameRecord>>();
        var shadowedNames = new Dictionary<int, List<ReferenceExtractor.CSharpFunctionValueReceiverNameRecord>>
        {
            [1] = [new("Status", 1, 0, 100, int.MaxValue), new("x", 1, 0, 100, int.MaxValue)],
        };
        (string Source, bool Shadowed, int LookupCalls, bool EmitsRead)[] cases =
        [
            ("value.Ready", false, 0, false),
            ("Wrong.Ready", false, 0, false),
            ("Status.Missing", false, 0, false),
            ("Status.Ready()", false, 0, false),
            ("Alias.Ready = value", false, 0, false),
            ("global::Other.Mode.Ready", false, 0, false),
            ("global::Demo.Status.Ready", true, 0, true),
            ("Status.Ready", false, 1, true),
            ("Alias.Ready", false, 1, true),
            ("Status.Ready == value", false, 1, true),
            ("Status.Ready += value", false, 1, true),
            ("Status.Ready", true, 1, false),
            ("ordinary words Status.Ready", false, 1, true),
            ("foo.", false, 0, false),
            ("global::", false, 0, false),
            ("Status.Ready.", false, 0, false),
            ("x::global::Status.Ready", false, 1, true),
            ("x::global::Status.Ready", true, 1, false),
        ];

        foreach (var scenario in cases)
        {
            var typeLookupCalls = 0;
            var functionLookupCalls = 0;
            var references = new List<ReferenceRecord>();
            ReferenceExtractor.EmitCSharpQualifiedEnumMemberReferences(
                scenario.Source, targets, null, aliases,
                () => { typeLookupCalls++; return typeNames; },
                requestedContainer =>
                {
                    Assert.Same(container, requestedContainer);
                    functionLookupCalls++;
                    return scenario.Shadowed ? shadowedNames : unshadowedNames;
                },
                references, new ReferenceDedupeSet(), 1, scenario.Source, 20, _ => container);

            Assert.Equal(scenario.LookupCalls, typeLookupCalls);
            Assert.Equal(scenario.LookupCalls, functionLookupCalls);
            if (!scenario.EmitsRead)
            {
                Assert.Empty(references);
                continue;
            }

            var reference = Assert.Single(references);
            Assert.Equal("Ready", reference.SymbolName);
            Assert.Equal("member_read", reference.ReferenceKind);
            Assert.Equal("Run", reference.ContainerName);
            Assert.Equal(20, reference.Line);
            Assert.Equal(scenario.Source.IndexOf("Ready", StringComparison.Ordinal) + 1, reference.Column);
            Assert.Equal(scenario.Source, reference.Context);
        }
    }

    [Fact]
    public void CaptureLocalTracking_SkipsArrowFreeBodyDespiteSiblingCapture()
    {
        string[] preparedLines =
        [
            "var ignored = 0;",
            "var maskedString =     ;",
            "}",
            "var kept = 1;",
            "var next = () => kept;",
            "var later = 2;",
            "}",
        ];
        var plain = new SymbolRecord
        {
            FileId = 1,
            Kind = "function",
            Name = "Run",
            StartLine = 1,
            EndLine = 3,
            BodyStartLine = 1,
            BodyEndLine = 3,
        };
        var capturing = new SymbolRecord
        {
            FileId = 1,
            Kind = "function",
            Name = "Run",
            StartLine = 4,
            EndLine = 7,
            BodyStartLine = 4,
            BodyEndLine = 7,
        };
        var stateType = typeof(ReferenceExtractor).GetNestedType("CSharpLocalCaptureState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
            args: [preparedLines, CancellationToken.None], culture: null)!;
        var track = typeof(ReferenceExtractor).GetMethod("TrackCSharpLocalDeclarations", BindingFlags.Static | BindingFlags.NonPublic)!;
        var names = (IDictionary)stateType.GetField("Names", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        object?[] arguments = [preparedLines[0], plain, state];
        for (var warmup = 0; warmup < 16; warmup++)
            track.Invoke(null, arguments);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 2_048; index++)
            track.Invoke(null, arguments);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(names);
        Assert.True(allocated < 262_144,
            $"Arrow-free local tracking allocated {allocated:N0} bytes; expected no per-line regex match allocations.");

        arguments[1] = capturing;
        for (var index = 3; index < preparedLines.Length; index++)
        {
            arguments[0] = preparedLines[index];
            track.Invoke(null, arguments);
        }
        var localNames = Assert.IsType<HashSet<string>>(Assert.Single(names.Values.Cast<object>()));
        Assert.Equal(["kept", "later"], localNames);

        arguments[0] = preparedLines[1];
        arguments[1] = plain;
        track.Invoke(null, arguments);
        Assert.Single(names);
        Assert.DoesNotContain("ignored", localNames);
        Assert.DoesNotContain("maskedString", localNames);
    }

    [Fact]
    public void QualifiedNameNormalization_PreservesSingleSegmentsAndMalformedSeparators()
    {
        var normalize = typeof(ReferenceExtractor).GetMethod("TryNormalizeCSharpQualifiedName", BindingFlags.Static | BindingFlags.NonPublic)!;
        (string Input, string? Expected)[] cases =
        [
            ("Status", "Status"), (" @Status ", "Status"), ("Café", "Café"),
            ("global::Status", "Status"), ("global::Demo.Status", "Demo.Status"),
            ("x::global::Status", "x.global.Status"),
            ("foo.", null), ("global::", null), ("Status Ready", null), ("A..B", null),
        ];
        foreach (var scenario in cases)
            Assert.Equal(scenario.Expected, normalize.Invoke(null, [scenario.Input]));
    }

    [Fact]
    public void ReceiverLookupCache_PreservesSameLineWinnersAndCachesEmptyScopes()
    {
        var lines = new ReadCountingLines(
        [
            "var first = 1;", "var last = 2;", "{}",
            "var retained = 3;", "{}", "var unrelated = 4;", "{}",
            "var testLocal = 5;", "var fieldLocal = 6;",
        ]);
        SymbolRecord[] symbols =
        [
            Callable("First", 1, 1), Callable("Last", 1, 2), Callable("EmptyLast", 1, 3),
            Callable("Retained", 4, 4), Callable("EmptyProperty", 4, 5, "property"),
            Callable("Unrelated", 6, 6), Callable("Empty", 7, 7),
            Callable("Test", 8, 8, "test.method"), Callable("Field", 9, 9, "field"),
            new() { Kind = "function", Name = "NoBody", StartLine = 10 },
        ];
        var cache = new ReferenceExtractor.CSharpValueReceiverLookupCache(symbols, lines, new HashSet<string>(), []);

        Assert.Contains("Field", cache.GetContainingTypeNames()["Demo.Host"].InstanceNames);
        cache.GetFunctionNames(null);
        cache.GetFunctionNames(Callable("Class", 1, 1, "class"));
        cache.GetFunctionNames(symbols[7]);
        Assert.Equal(0, lines.ReadCount);

        var names = cache.GetFunctionNames(symbols[0]);
        Assert.Equal("last", Assert.Single(names[1]).Name);
        Assert.All(lines.Reads.Take(3), count => Assert.True(count > 0));
        Assert.All(lines.Reads.Skip(3), count => Assert.Equal(0, count));
        var firstReadCounts = lines.Reads.ToArray();
        Assert.Same(names, cache.GetFunctionNames(symbols[1]));
        Assert.Equal(firstReadCounts, lines.Reads);

        cache.GetFunctionNames(symbols[6]);
        Assert.False(names.ContainsKey(7));
        var afterEmpty = lines.ReadCount;
        cache.GetFunctionNames(symbols[6]);
        Assert.Equal(afterEmpty, lines.ReadCount);
        var emptyReadCount = lines.Reads[6];

        Assert.Same(names, cache.GetAllFunctionNames());
        Assert.Equal([1, 4, 6], names.Keys.Order().ToArray());
        Assert.Equal("retained", Assert.Single(names[4]).Name);
        Assert.Equal("unrelated", Assert.Single(names[6]).Name);
        Assert.Equal(firstReadCounts.Take(3), lines.Reads.Take(3));
        Assert.Equal(emptyReadCount, lines.Reads[6]);
        Assert.Equal(0, lines.Reads[7]);
        Assert.Equal(0, lines.Reads[8]);
        var afterAll = lines.ReadCount;
        cache.GetAllFunctionNames();
        cache.GetFunctionNames(symbols[9]);
        Assert.Equal(afterAll, lines.ReadCount);

        static SymbolRecord Callable(string name, int startLine, int bodyLine, string kind = "function") => new()
        {
            FileId = 1,
            Kind = kind,
            Name = name,
            StartLine = startLine,
            EndLine = bodyLine,
            BodyStartLine = bodyLine,
            BodyEndLine = bodyLine,
            ContainerQualifiedName = "Demo.Host",
        };
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("razor")]
    [InlineData("blazor")]
    [InlineData("cshtml")]
    public void ReceiverLookupCache_SparseReadsMatchEagerReferencesAndFullMaterialization(string language)
    {
        var source = new StringBuilder("""
            namespace Demo;
            enum Status { Ready }
            class Holder { public int Ready; }
            class Uses
            {
                void Read()
                {
                    _ = Status.Ready;
                }
                void Shadow()
                {
                    Holder Status = new();
                    _ = Status.Ready;
                }
                int Value
                {
                    get { _ = Status.Ready; return 0; }
                }
                void WithLocal()
                {
                    void Local() { }
                    Local();
                }
            """);
        source.AppendLine();
        for (var index = 0; index < 64; index++)
        {
            source.Append("    void Unrelated").Append(index).AppendLine("()");
            source.AppendLine("    {");
            source.Append("        var ordinary").Append(index).AppendLine(" = 1;");
            source.AppendLine("    }");
        }
        source.AppendLine("}");
        var content = source.ToString();
        var symbols = SymbolExtractor.Extract(1, language, content);
        var rawLines = content.Split('\n');
        var sparseLines = new ReadCountingLines(rawLines);
        var eagerLines = new ReadCountingLines(rawLines);
        var knownTypes = new HashSet<string>(["Status", "Demo.Status", "Holder", "Demo.Holder", "Uses", "Demo.Uses"], StringComparer.Ordinal);
        var sparse = new ReferenceExtractor.CSharpValueReceiverLookupCache(symbols, sparseLines, knownTypes, []);
        var eager = new ReferenceExtractor.CSharpValueReceiverLookupCache(symbols, eagerLines, knownTypes, []);
        var eagerNames = eager.GetAllFunctionNames();
        var targets = ReferenceExtractor.BuildCSharpQualifiedPatternLookups(symbols).EnumMemberLookup;
        var expected = ReadEnums(eager);
        var actual = ReadEnums(sparse);

        Assert.Equal(["Read", "Value"], actual.Select(reference => reference.ContainerName));
        AssertReferenceFieldsEqual(expected, actual);
        Assert.True(sparseLines.ReadCount * 4 < eagerLines.ReadCount,
            $"Sparse receivers read {sparseLines.ReadCount} structural lines versus {eagerLines.ReadCount} eagerly.");
        var unrelated = symbols.Single(symbol => symbol.Name == "Unrelated63");
        Assert.Equal(0, sparseLines.Reads[unrelated.BodyStartLine!.Value - 1]);

        // Local-function finalization requests every scope after earlier enum reads.
        // enum の限定解析後に local function 解決が全 scope を要求しても結果を維持する。
        var completed = sparse.GetAllFunctionNames();
        Assert.Equal(eagerNames.Keys.Order(), completed.Keys.Order());
        foreach (var (startLine, names) in eagerNames)
            Assert.Equal(names, completed[startLine]);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);
        Assert.Contains(references, reference => reference.SymbolName == "Local"
            && reference.ReferenceKind == "call"
            && reference.TargetQualifier?.StartsWith("\u001fcsharp_local:", StringComparison.Ordinal) == true);
        var canonicalSymbols = SymbolExtractor.Extract(1, "csharp", content);
        var canonicalReferences = ReferenceExtractor.Extract(1, "csharp", content, canonicalSymbols);
        AssertReferenceFieldsEqual(canonicalReferences, references);

        List<ReferenceRecord> ReadEnums(ReferenceExtractor.CSharpValueReceiverLookupCache cache)
        {
            var result = new List<ReferenceRecord>();
            var seen = new ReferenceDedupeSet();
            for (var index = 0; index < rawLines.Length; index++)
            {
                var lineNumber = index + 1;
                var container = symbols.FirstOrDefault(symbol => symbol.Kind is "function" or "property"
                    && symbol.BodyStartLine <= lineNumber && symbol.BodyEndLine >= lineNumber);
                ReferenceExtractor.EmitCSharpQualifiedEnumMemberReferences(
                    rawLines[index], targets, null, [], cache.GetContainingTypeNames, cache.GetFunctionNames,
                    result, seen, 1, rawLines[index].Trim(), lineNumber, _ => container);
            }
            return result;
        }
    }

    private static void AssertReferenceFieldsEqual(IReadOnlyList<ReferenceRecord> expected, IReadOnlyList<ReferenceRecord> actual)
    {
        var fields = typeof(ReferenceRecord).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Equal(16, fields.Length);
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            foreach (var field in fields)
                Assert.Equal(field.GetValue(expected[index]), field.GetValue(actual[index]));
        }
    }

    private sealed class ReadCountingLines(IReadOnlyList<string> lines) : IReadOnlyList<string>
    {
        internal int[] Reads { get; } = new int[lines.Count];
        internal int ReadCount { get; private set; }
        public int Count => lines.Count;
        public string this[int index]
        {
            get
            {
                ReadCount++;
                Reads[index]++;
                return lines[index];
            }
        }
        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
