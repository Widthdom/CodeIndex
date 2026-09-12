using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void GetStatus_ValidatesCSharpExpansionMetadata_Issue5347()
    {
        const string field = "last_index_run.csharp_workspace_expansion";
        const string key = DbContext.LastIndexRunCSharpWorkspaceExpansionMetaKey;
        _writer.SetMeta(DbContext.LastIndexRunModeMetaKey, "update");
        var valid = new CSharpWorkspaceExpansion
        {
            Trigger = "member_reference_targets", Decision = "expanded", Reason = "baseline_unavailable",
            OriginalTargetCount = 1, ExpandedTargetCount = 3, FinalTargetCount = 3,
        };
        _writer.SetMeta(key, JsonSerializer.Serialize(valid, StatusMetadataJsonContext.Default.CSharpWorkspaceExpansion));
        Assert.Equal(3, _reader.GetStatus().LastIndexRun?.CSharpWorkspaceExpansion?.FinalTargetCount);
        foreach (var bad in new[]
        {
            new CSharpWorkspaceExpansion { Trigger = "bad\ntrigger" },
            new CSharpWorkspaceExpansion { Decision = new string('x', 500) },
            new CSharpWorkspaceExpansion { OriginalTargetCount = 2, ExpandedTargetCount = 1 },
            new CSharpWorkspaceExpansion { FinalTargetCount = 1 },
            new CSharpWorkspaceExpansion { WorkspacePrepassInputBytes = -1 },
        })
        {
            _writer.SetMeta(key, JsonSerializer.Serialize(bad, StatusMetadataJsonContext.Default.CSharpWorkspaceExpansion));
            var rejected = _reader.GetStatus();
            Assert.Null(rejected.LastIndexRun?.CSharpWorkspaceExpansion);
            AssertMetadataDiagnostic(rejected, field, DbReader.StatusMetadataSemanticValidationFailedReason);
        }
        _writer.SetMeta(key, "{");
        AssertMetadataDiagnostic(_reader.GetStatus(), field, DbReader.StatusMetadataInvalidJsonReason);
        _writer.SetMeta(key, new string(' ', StatusMetadataLimits.MaxRawUtf8Bytes + 1));
        AssertMetadataDiagnostic(_reader.GetStatus(), field, DbReader.StatusMetadataRawSizeExceededReason);
        _writer.SetMeta(key, null);
        Assert.Null(_reader.GetStatus().LastIndexRun?.CSharpWorkspaceExpansion);
    }
}
