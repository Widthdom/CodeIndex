using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static List<ReferenceRecord> ExtractCore(
        ReferenceExtractionContext request)
    {
        var preparationOutcome = PrepareCoreExtraction(request);
        if (preparationOutcome.IsComplete)
            return preparationOutcome.References!;

        var preparation = preparationOutcome.Preparation!.Value;
        var preSolidityLookups = BuildCorePreSolidityLookups(preparation);
        if (request.Language == "solidity")
            return ExtractCoreSolidityReferences(preparation, preSolidityLookups);

        var loop = CreateCoreReferenceLoopContext(preparation, preSolidityLookups);
        EmitCoreExtractionPrelude(loop);
        var pendingCSharpMultiLineTypePattern = EmitCoreReferenceLines(loop);
        return FinalizeCoreExtraction(loop, pendingCSharpMultiLineTypePattern);
    }
}
