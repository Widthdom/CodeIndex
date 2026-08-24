using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed record FullScanTargetPreparation(
        FileIndexer.IndexingFileTargetCollection FileTargets,
        List<CSharpStaticInterfacePrepass.FileTarget> CSharpPrepassTargets);

    private static FullScanTargetPreparation PrepareFullScanTargets(
        FileIndexer indexer,
        FileIndexer.IndexingFileTargetCollection indexingTargets,
        bool symbolsOnly,
        int csharpPrepassCapacity)
    {
        var fileTargets = indexingTargets;
        var csharpPrepassTargets = new List<CSharpStaticInterfacePrepass.FileTarget>(
            symbolsOnly ? 0 : csharpPrepassCapacity);

        if (symbolsOnly)
            return new FullScanTargetPreparation(fileTargets, csharpPrepassTargets);

        foreach (var target in fileTargets)
        {
            if (target.ReusableLanguage != "csharp")
                continue;

            csharpPrepassTargets.Add(new CSharpStaticInterfacePrepass.FileTarget(
                target.FilePath,
                target.RelativePath,
                target.DisplayRelativePath,
                target.IndexPath,
                target.ReusableLanguage,
                target.GeneratedExtractionSuppressed,
                indexer.ResolvesSymlinkTargets));
        }

        return new FullScanTargetPreparation(fileTargets, csharpPrepassTargets);
    }
}
