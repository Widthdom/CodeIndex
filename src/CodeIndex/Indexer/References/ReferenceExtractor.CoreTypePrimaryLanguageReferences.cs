namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitCorePrimaryLanguageTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        switch (line.Language)
        {
            case "java":
                EmitCoreJavaTypeReferences(in type);
                break;
            case "typescript":
                EmitCoreTypeScriptTypeReferences(in type);
                break;
            case "kotlin":
                EmitCoreKotlinTypeReferences(in type);
                break;
            case "swift":
                EmitCoreSwiftTypeReferences(in type);
                break;
            case "rust":
                EmitCoreRustTypeReferences(in type);
                break;
        }
    }

    private static void EmitCoreJavaTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        JavaReferenceExtractor.EmitModuleDirectiveReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall);

        JavaReferenceExtractor.EmitTypePositionReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            line.Container);
    }

    private static void EmitCoreTypeScriptTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        TypeScriptReferenceExtractor.EmitTypePositionReferences(
            line.PreparedLines,
            line.Lines,
            line.LineIndex,
            line.PreparedLine,
            line.Lines[line.LineIndex],
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            type.TypeScriptNamespaceAliases);

        TypeScriptReferenceExtractor.EmitDeclarationTypeReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall);

        TypeScriptReferenceExtractor.EmitAliasTargetReferences(
            line.PreparedLine,
            type.TypeScriptTypeAliases!,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall);
    }

    private static void EmitCoreKotlinTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        KotlinReferenceExtractor.EmitTypePositionReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall);
    }

    private static void EmitCoreSwiftTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        SwiftReferenceExtractor.EmitTypePositionReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            type.ResolveSwiftPropertyContainerForCall);
        SwiftReferenceExtractor.EmitAliasTargetReferences(
            line.PreparedLine,
            type.SwiftTypeAliases!,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall);
    }

    private static void EmitCoreRustTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        var enumCandidates = type.Lookups.GetRustEnumCandidates();
        var enumContainer = enumCandidates != null
            ? FindInnermostContainer(enumCandidates, line.LineNumber)
            : null;
        var typePositionLine = RustReferenceExtractor.MaskAttributeBodies(line.PreparedLine);
        RustReferenceExtractor.EmitTypePositionReferences(
            typePositionLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            line.Container,
            enumContainer);
    }
}
