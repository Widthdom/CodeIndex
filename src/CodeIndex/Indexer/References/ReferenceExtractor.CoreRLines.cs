namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitRDocumentationReferences(
        in CoreDocumentationLineContext line)
    {
        var context = line.OriginalLine;
        if (string.IsNullOrWhiteSpace(context))
            return;

        RReferenceExtractor.EmitRoxygenImportFromReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, container: null);
        RReferenceExtractor.EmitRoxygenImportReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, container: null);
        RReferenceExtractor.EmitRoxygenMethodReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, container: null);
    }

    private static void EmitRLineReferences(in CoreReferenceLineContext line)
    {
        EmitRNamespaceAndDispatchReferences(in line);
        EmitRWorkspaceAndDataReferences(in line);
        EmitRHelpAndPackageReferences(in line);
        EmitRMemberReferences(in line);
    }

    private static void EmitRNamespaceAndDispatchReferences(
        in CoreReferenceLineContext line)
    {
        RReferenceExtractor.EmitNamespaceReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.DefinitionNames);
        RReferenceExtractor.EmitNamespaceDirectiveReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitS4DispatchReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitBacktickCallReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.DefinitionNames);
        RReferenceExtractor.EmitInfixOperatorCallReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.DefinitionNames);
    }

    private static void EmitRWorkspaceAndDataReferences(
        in CoreReferenceLineContext line)
    {
        RReferenceExtractor.EmitSourceFileReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitLoadAllReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        RReferenceExtractor.EmitDataCallReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitSystemFileReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitVignetteReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
    }

    private static void EmitRHelpAndPackageReferences(
        in CoreReferenceLineContext line)
    {
        RReferenceExtractor.EmitHelpExampleReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitInstallPackagesReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitNamespacePackageInstallReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        RReferenceExtractor.EmitGitHubPackageInstallReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
    }

    private static void EmitRMemberReferences(in CoreReferenceLineContext line)
    {
        RReferenceExtractor.EmitDollarMemberReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.DefinitionNames);
        RReferenceExtractor.EmitBracketMemberReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container,
            line.DefinitionNames);
        RReferenceExtractor.EmitSlotMemberReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.DefinitionNames);
    }
}
