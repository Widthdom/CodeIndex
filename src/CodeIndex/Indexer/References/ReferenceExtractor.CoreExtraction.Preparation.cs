using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreExtractionPreparation(
        ReferenceExtractionContext Request,
        ReferenceLinePreparation Lines,
        bool IsJsxFile,
        bool IsRazorFile,
        bool XamlReferenceEnabled);

    private readonly record struct CoreExtractionPreparationOutcome(
        bool IsComplete,
        List<ReferenceRecord>? References,
        CoreExtractionPreparation? Preparation)
    {
        internal static CoreExtractionPreparationOutcome Complete(
            List<ReferenceRecord> references)
            => new(true, references, null);

        internal static CoreExtractionPreparationOutcome Continue(
            CoreExtractionPreparation preparation)
            => new(false, null, preparation);
    }

    private static CoreExtractionPreparationOutcome PrepareCoreExtraction(
        ReferenceExtractionContext request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var language = request.Language;
        var isJsxFile = IsJsxFilePath(request.Path);
        var isRazorFile = IsRazorFilePath(request.Path)
            || request.RequestedLanguage is "razor" or "blazor" or "cshtml";

        if (language == "ambiguous_m")
            return CoreExtractionPreparationOutcome.Complete(
                ExtractAmbiguousMReferences(request));
        if (language is "clojure" or "erlang" or "ocaml" or "raku")
            return CoreExtractionPreparationOutcome.Complete(
                ExtractFunctionalLanguageReferences(request));

        if (TryExtractStructuralMetadataReferences(
                request.FileId,
                language,
                request.Content,
                request.Symbols,
                request.Path,
                request.ContentIsNormalized,
                request.HasOversizeLine,
                request.ConflictMarkerLine,
                request.MaxReferenceCount,
                request.CancellationToken,
                out var structuralMetadataReferences))
        {
            return CoreExtractionPreparationOutcome.Complete(
                structuralMetadataReferences);
        }

        if (!TryPrepareReferenceLines(
                language,
                request.Content,
                isRazorFile,
                request.ContentIsNormalized,
                request.HasOversizeLine,
                request.ConflictMarkerLine,
                out var preparedInput))
        {
            return CoreExtractionPreparationOutcome.Complete([]);
        }
        request.CancellationToken.ThrowIfCancellationRequested();

        var xamlReferenceEnabled = language == "xml"
            && XamlReferenceExtractor.IsXaml(preparedInput.Lines);
        if (language == "xml" && !xamlReferenceEnabled)
            return CoreExtractionPreparationOutcome.Complete([]);

        return CoreExtractionPreparationOutcome.Continue(
            new CoreExtractionPreparation(
                request,
                preparedInput,
                isJsxFile,
                isRazorFile,
                xamlReferenceEnabled));
    }
}
