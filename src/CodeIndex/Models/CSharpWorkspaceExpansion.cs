using System.Text.Json.Serialization;

namespace CodeIndex.Models;

/// <summary>Fixed-size diagnostics for the C# scoped-update preflight.</summary>
public sealed class CSharpWorkspaceExpansion
{
    [JsonRequired]
    public string Trigger { get; set; } = "no_csharp_targets";
    [JsonRequired]
    public string Decision { get; set; } = "not_expanded";
    [JsonRequired]
    public string Reason { get; set; } = "no_csharp_targets";
    [JsonPropertyName("original_target_count")]
    [JsonRequired]
    public int OriginalTargetCount { get; set; }
    [JsonPropertyName("expanded_target_count")]
    [JsonRequired]
    public int ExpandedTargetCount { get; set; }
    [JsonPropertyName("final_target_count")]
    [JsonRequired]
    public int FinalTargetCount { get; set; }
    [JsonPropertyName("initial_prepass_ms")]
    [JsonRequired]
    public long InitialPrepassMs { get; set; }
    [JsonPropertyName("workspace_scan_ms")]
    [JsonRequired]
    public long WorkspaceScanMs { get; set; }
    [JsonPropertyName("workspace_prepass_ms")]
    [JsonRequired]
    public long WorkspacePrepassMs { get; set; }
    [JsonPropertyName("workspace_prepass_file_count")]
    [JsonRequired]
    public int WorkspacePrepassFileCount { get; set; }
    [JsonPropertyName("workspace_prepass_input_bytes")]
    [JsonRequired]
    public long WorkspacePrepassInputBytes { get; set; }

    internal bool IsValid()
        => Trigger is "no_csharp_targets" or "csharp_targets"
                or "persisted_static_interface_contracts" or "member_reference_targets"
                or "source_static_interface_contracts" or "incomplete_contract_evidence"
                or "configuration_changed" or "prior_partial_index"
            && Decision is "not_expanded" or "expanded" or "narrowed" or "deferred" or "full_scan"
            && Reason is "no_csharp_targets" or "no_workspace_contracts"
                or "baseline_unavailable" or "contract_inputs_changed" or "contract_inputs_unchanged"
                or "safety_checks_required" or "incomplete_workspace"
                or "configuration_changed" or "prior_partial_index"
            && OriginalTargetCount >= 0 && ExpandedTargetCount >= OriginalTargetCount
            && FinalTargetCount >= 0 && FinalTargetCount <= ExpandedTargetCount
            && InitialPrepassMs >= 0 && WorkspaceScanMs >= 0 && WorkspacePrepassMs >= 0
            && WorkspacePrepassFileCount >= 0 && WorkspacePrepassInputBytes >= 0;
}
