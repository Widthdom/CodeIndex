using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private List<ReferenceRecord> ProjectTypeScriptAugmentationReferences(
        string? projectRoot,
        TypeScriptAugmentationScopePlan scopePlan,
        IReadOnlyList<TypeScriptInterfaceDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        var moduleFileIds = FindTypeScriptModuleFileIds(
            projectRoot,
            declarations,
            scopePlan.IncludeIndexedInterfaceMarkers,
            cancellationToken);
        var groupIndexes = new Dictionary<(string Name, string ScopeKey), int>(declarations.Count);
        var groups = new List<(int FirstDeclarationIndex, List<int>? DeclarationIndexes)>(declarations.Count);
        for (var declarationIndex = 0; declarationIndex < declarations.Count; declarationIndex++)
        {
            if ((declarationIndex & 1_023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var declaration = declarations[declarationIndex];
            var key = (
                declaration.Name,
                BuildTypeScriptScopeKey(
                    declaration.FileId,
                    declaration.Path,
                    declaration.Signature,
                    declaration.ContainerName,
                    moduleFileIds));
            if (!groupIndexes.TryGetValue(key, out var groupIndex))
            {
                groupIndexes.Add(key, groups.Count);
                groups.Add((declarationIndex, null));
                continue;
            }

            var group = groups[groupIndex];
            if (group.DeclarationIndexes == null)
                group.DeclarationIndexes = new List<int>(2) { group.FirstDeclarationIndex };
            group.DeclarationIndexes.Add(declarationIndex);
            groups[groupIndex] = group;
        }

        return ProjectMergedTypeScriptAugmentationGroups(
            scopePlan,
            declarations,
            groups,
            cancellationToken);
    }

    private static List<ReferenceRecord> ProjectMergedTypeScriptAugmentationGroups(
        TypeScriptAugmentationScopePlan scopePlan,
        IReadOnlyList<TypeScriptInterfaceDeclaration> declarations,
        IReadOnlyList<(int FirstDeclarationIndex, List<int>? DeclarationIndexes)> groups,
        CancellationToken cancellationToken)
    {
        var references = new List<ReferenceRecord>();
        var mergedGroupCount = 0;
        var materializedDeclarationIndexCount = 0;
        var mergedDeclarationCount = 0;
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            if ((groupIndex & 1_023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var group = groups[groupIndex];
            if (group.DeclarationIndexes == null)
                continue;

            mergedGroupCount++;
            materializedDeclarationIndexCount += group.DeclarationIndexes.Count;
            foreach (var declarationIndex in group.DeclarationIndexes)
            {
                if ((mergedDeclarationCount++ & 1_023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var declaration = declarations[declarationIndex];
                references.Add(new ReferenceRecord
                {
                    FileId = declaration.FileId,
                    SymbolName = declaration.Name,
                    ReferenceKind = "augmentation",
                    Line = declaration.Line,
                    Column = declaration.Column,
                    Context = declaration.Signature,
                    ContainerKind = declaration.Kind == "interface" ? "interface" : "type",
                    ContainerName = declaration.Name,
                });
            }
        }
        TypeScriptAugmentationGroupingForTesting?.Invoke(new TypeScriptAugmentationGroupingStats(
            declarations.Count,
            groups.Count,
            mergedGroupCount,
            materializedDeclarationIndexCount,
            scopePlan.ScopedNames?.Length));
        return references;
    }

    private void ApplyTypeScriptAugmentationReferences(
        List<ReferenceRecord> references,
        int deletedReferenceCount,
        HashSet<long> affectedFileIds,
        bool finalizeDeferredReferenceGraph,
        ReferenceSecondaryIndexBulkLoadGuard? referenceSecondaryIndexBulkLoad,
        CancellationToken cancellationToken)
    {
        InsertReferencesInAtomicFileScope(
            references,
            refreshMutualRecursionFlags: true,
            cancellationToken,
            referenceSecondaryIndexBulkLoad);
        if (references.Count == 0
            && (deletedReferenceCount > 0 || finalizeDeferredReferenceGraph))
        {
            // The insert helper intentionally no-ops for an empty batch. Augmentation
            // rebuilds finalize only when they deleted synthetic edges or explicitly
            // inherited a coalesced graph pass. Marker-only validation stays O(1) here.
            // 空batchはedge削除または先行pass統合時だけgraphを確定し、marker検証だけなら省く。
            cancellationToken.ThrowIfCancellationRequested();
            RefreshMutualRecursionFlags(
                cancellationToken,
                referenceSecondaryIndexBulkLoad: referenceSecondaryIndexBulkLoad);
        }
        for (var referenceIndex = 0; referenceIndex < references.Count; referenceIndex++)
        {
            if ((referenceIndex & 1_023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            affectedFileIds.Remove(references[referenceIndex].FileId);
        }
        RefreshHotspotReferenceCounts(affectedFileIds, cancellationToken);
    }
}
