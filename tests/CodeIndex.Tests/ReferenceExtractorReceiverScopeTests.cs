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
            [1] = [new("Status", 1, 0, 100, int.MaxValue)],
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
}
