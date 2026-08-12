using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static List<ReferenceRecord> ExtractCoreSolidityReferences(
        CoreExtractionPreparation preparation,
        CorePreSolidityLookups preSolidityLookups)
        => ExtractSolidityReferences(
            preparation.Request.FileId,
            preparation.Lines.Lines,
            preparation.Lines.PreparedLines,
            preSolidityLookups.Containers.Resolver);

    private static void EmitCoreExtractionPrelude(
        CoreReferenceLoopContext loop)
    {
        var request = loop.Request;
        if (request.Language == "csharp")
        {
            EmitCSharpAsyncIteratorReferences(
                request.FileId,
                loop.Preparation.Lines,
                loop.Preparation.StructuralLines,
                request.Symbols,
                loop.References,
                loop.Seen);
            EmitCSharpStaticInterfaceMemberImplementationReferences(
                request.FileId,
                loop.Preparation.Lines,
                loop.Preparation.StructuralLines,
                request.Symbols,
                request.WorkspaceSymbols ?? request.Symbols,
                request.CSharpStaticInterfaceMemberLookups,
                loop.References,
                loop.Seen);
        }
        else if (request.Language == "rust")
        {
            RustReferenceExtractor.EmitMultilineAttributeReferences(
                loop.Preparation.PreparedLines,
                loop.References,
                loop.Seen,
                request.FileId,
                (lineNumber, _) => FindInnermostContainer(
                    loop.ContainerCandidates,
                    lineNumber));
        }
    }
}
