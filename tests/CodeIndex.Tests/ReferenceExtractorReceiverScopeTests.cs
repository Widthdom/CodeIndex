using System.Collections;
using System.Reflection;
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
                () => { functionLookupCalls++; return scenario.Shadowed ? shadowedNames : unshadowedNames; },
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
}
