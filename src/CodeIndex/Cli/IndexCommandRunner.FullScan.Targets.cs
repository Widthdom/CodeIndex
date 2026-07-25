using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed record FullScanTargetPreparation(
        FullScanFileTarget[] FileTargets,
        List<CSharpStaticInterfacePrepass.FileTarget> CSharpPrepassTargets);

    private static FullScanTargetPreparation PrepareFullScanTargets(
        FileIndexer indexer,
        string projectRoot,
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string> fileLanguages,
        bool symbolsOnly,
        int csharpPrepassCapacity)
    {
        var fileTargets = new FullScanFileTarget[files.Count];
        var csharpPrepassTargets = new List<CSharpStaticInterfacePrepass.FileTarget>(
            symbolsOnly ? 0 : csharpPrepassCapacity);
        var hasGeneratedCodeExtractionSuppressionPatterns =
            indexer.HasGeneratedCodeExtractionSuppressionPatterns;

        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var filePath = files[fileIndex];
            var language = FileIndexer.GetReusableDetectedLanguage(filePath, fileLanguages);
            var target = FullScanFileTarget.Create(projectRoot, filePath, language);
            fileTargets[fileIndex] = hasGeneratedCodeExtractionSuppressionPatterns
                ? target with
                {
                    GeneratedExtractionSuppressed =
                        indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath)
                }
                : target;
            if (symbolsOnly || language != "csharp")
                continue;

            var indexedTarget = fileTargets[fileIndex];
            csharpPrepassTargets.Add(new CSharpStaticInterfacePrepass.FileTarget(
                indexedTarget.FilePath,
                indexedTarget.RelativePath,
                indexedTarget.DisplayRelativePath,
                indexedTarget.IndexPath,
                indexedTarget.Language,
                indexedTarget.GeneratedExtractionSuppressed));
        }

        return new FullScanTargetPreparation(fileTargets, csharpPrepassTargets);
    }
}
