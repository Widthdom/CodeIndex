namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool EmitCoreConsumedLanguageTypeReferences(
        in CoreTypeReferenceContext type,
        ref bool xamlInXmlComment)
    {
        ref readonly var line = ref type.Line;
        switch (line.Language)
        {
            case "css":
                CssReferenceExtractor.EmitCss(
                    line.PreparedLine,
                    line.OriginalLine,
                    line.Context,
                    line.LineNumber,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.DefinitionNames,
                    line.Container);
                return false;
            case "sass":
                CssReferenceExtractor.EmitSass(
                    line.PreparedLine,
                    type.OriginalLineForLanguage,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.Container);
                return true;
            case "stylus":
                CssReferenceExtractor.EmitStylus(
                    line.PreparedLine,
                    type.OriginalLineForLanguage,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    type.AllDefinitionNames,
                    type.StylusVariableDefinitionNames,
                    line.Container);
                return true;
            case "xml":
                if (type.XamlReferenceEnabled)
                {
                    var xamlLine = XamlReferenceExtractor.StripXmlComments(
                        line.OriginalLine,
                        ref xamlInXmlComment);
                    XamlReferenceExtractor.Emit(
                        xamlLine,
                        line.Context,
                        line.LineNumber,
                        line.References,
                        line.Seen,
                        line.FileId,
                        line.Container,
                        type.XamlBindingPropertyElementState!,
                        type.XamlBindingMarkupExtensionState!);
                }

                return true;
            default:
                return false;
        }
    }
}
