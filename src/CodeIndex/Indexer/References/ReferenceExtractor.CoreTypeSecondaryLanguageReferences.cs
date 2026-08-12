namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitCoreSecondaryLanguageTypeReferences(in CoreTypeReferenceContext type)
    {
        EmitCoreCAndGoTypeReferences(in type);
        EmitCoreAdditionalLanguageTypeReferences(in type);
    }

    private static void EmitCoreCAndGoTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        switch (line.Language)
        {
            case "c":
                CReferenceExtractor.EmitTypePositionReferences(
                    line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId,
                    line.Context, line.LineNumber, line.ResolveContainerForCall);
                break;
            case "cpp":
                CppReferenceExtractor.EmitTypePositionReferences(
                    line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId,
                    line.Context, line.LineNumber, line.ResolveContainerForCall);
                break;
            case "go":
                GoReferenceExtractor.EmitConcurrencyReferences(
                    line.PreparedLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall);
                GoReferenceExtractor.EmitTypePositionReferences(
                    line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId,
                    line.Context, line.LineNumber, line.ResolveContainerForCall,
                    type.GoImportBlockLines?[line.LineIndex] == true);
                break;
        }
    }

    private static void EmitCoreAdditionalLanguageTypeReferences(in CoreTypeReferenceContext type)
    {
        ref readonly var line = ref type.Line;
        switch (line.Language)
        {
            case "dart":
                DartReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall);
                break;
            case "vb":
                VisualBasicReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall);
                break;
            case "fortran":
                FortranReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, line.Container);
                break;
            case "pascal":
                PascalReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, line.Container);
                break;
            case "objc":
                ObjectiveCReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, line.Container);
                break;
            case "haskell":
                HaskellReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
                break;
            case "elixir":
                ElixirReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
                break;
            case "lua":
                LuaReferenceExtractor.EmitTypePositionReferences(type.LuaReferenceLines?[line.LineIndex] ?? line.OriginalLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
                break;
        }
    }
}
