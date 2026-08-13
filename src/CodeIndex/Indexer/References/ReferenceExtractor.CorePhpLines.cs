namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitPhpLineReferences(in CoreReferenceLineContext line)
    {
        if (line.Language != "php")
            return;

        EmitPhpTypeLineReferences(in line);
        EmitPhpImportLineReferences(in line);
        EmitPhpMemberLineReferences(in line);
    }

    private static void EmitPhpTypeLineReferences(
        in CoreReferenceLineContext line)
    {
        PhpReferenceExtractor.EmitStaticAccessReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitInstanceofReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitCatchTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitReturnTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitParameterTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitPropertyTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitInheritanceTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
    }

    private static void EmitPhpImportLineReferences(
        in CoreReferenceLineContext line)
    {
        PhpReferenceExtractor.EmitUseTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitUseFunctionReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
        PhpReferenceExtractor.EmitUseConstReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
    }

    private static void EmitPhpMemberLineReferences(
        in CoreReferenceLineContext line)
    {
        PhpReferenceExtractor.EmitObjectMemberAccessReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container);
    }
}
