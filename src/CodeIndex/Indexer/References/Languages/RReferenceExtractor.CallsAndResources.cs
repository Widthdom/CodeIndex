using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class RReferenceExtractor
{
    public static void EmitBacktickCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        if (preparedLine.IndexOf('`') < 0 || preparedLine.IndexOf('(') < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(BacktickCallRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var nameGroup = match.Groups["name"];
            var name = nameGroup.Value;
            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "call",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitInfixOperatorCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        if (preparedLine.IndexOf('%') < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     InfixOperatorCallRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var nameGroup = match.Groups["name"];
            var name = nameGroup.Value;
            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "call",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitSourceFileReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("source", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        if (!SourceFileReferenceStartRegex.IsMatch(preparedLine))
            return;

        if (!ContainsRQuotedArgument(originalLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        var match = SourceFileReferenceRegex.Match(line);
        if (!match.Success)
            return;

        var path = match.Groups["path"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            path.Value,
            path.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    public static void EmitLoadAllReferences(
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (originalLine.IndexOf("load_all", StringComparison.Ordinal) < 0
            || originalLine.IndexOf('(') < 0)
        {
            return;
        }

        if (!ContainsRQuotedArgument(originalLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        var match = LoadAllReferenceRegex.Match(line);
        if (!match.Success)
            return;

        var path = match.Groups["path"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            path.Value,
            path.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    public static void EmitDataCallReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("data", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        if (!DataCallStartRegex.IsMatch(preparedLine))
            return;

        if (!ContainsRQuotedArgument(originalLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in Regex.EnumerateMatches(DataCallDatasetRegex, line))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var name = match.Groups["name"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name.Value,
                name.Index,
                "reference",
                context,
                lineNumber,
                container);
        }

        var packageMatch = DataCallPackageRegex.Match(line);
        if (!packageMatch.Success)
            return;

        var package = packageMatch.Groups["name"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            package.Value,
            package.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    public static void EmitSystemFileReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("system.file", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        if (!SystemFileCallStartRegex.IsMatch(preparedLine))
            return;

        if (!ContainsRQuotedArgument(originalLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in Regex.EnumerateMatches(
                     SystemFilePathPartRegex,
                     line))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var name = match.Groups["name"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name.Value,
                name.Index,
                "reference",
                context,
                lineNumber,
                container);
        }

        var packageMatch = DataCallPackageRegex.Match(line);
        if (!packageMatch.Success)
            return;

        var package = packageMatch.Groups["name"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            package.Value,
            package.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    public static void EmitVignetteReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("vignette", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        EmitDocumentationTopicReferences(
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container,
            VignetteCallStartRegex);
    }

    public static void EmitHelpExampleReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if ((preparedLine.IndexOf("help", StringComparison.Ordinal) < 0
                && preparedLine.IndexOf("example", StringComparison.Ordinal) < 0)
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        EmitDocumentationTopicReferences(
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container,
            HelpExampleCallStartRegex);
    }

    private static void EmitDocumentationTopicReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Regex startRegex)
    {
        if (!startRegex.IsMatch(preparedLine))
            return;

        if (!ContainsRQuotedArgument(originalLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in Regex.EnumerateMatches(DocumentationTopicRegex, line))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var name = match.Groups["name"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name.Value,
                name.Index,
                "reference",
                context,
                lineNumber,
                container);
        }

        var packageMatch = DataCallPackageRegex.Match(line);
        if (!packageMatch.Success)
            return;

        var package = packageMatch.Groups["name"];
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            package.Value,
            package.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    public static void EmitInstallPackagesReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("install.packages", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        EmitPackageNameArgumentReferences(
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container,
            InstallPackagesCallStartRegex);
    }

    public static void EmitNamespacePackageInstallReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("install", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        EmitPackageNameArgumentReferences(
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container,
            NamespacePackageInstallCallStartRegex);
    }

    public static void EmitGitHubPackageInstallReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("install_github", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        EmitPackageNameArgumentReferences(
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container,
            GitHubPackageInstallCallStartRegex);
    }

    private static void EmitPackageNameArgumentReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Regex startRegex)
    {
        if (!startRegex.IsMatch(preparedLine))
            return;

        if (!ContainsRQuotedArgument(originalLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in Regex.EnumerateMatches(
                     InstallPackagesNameRegex,
                     line))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var name = match.Groups["name"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name.Value,
                name.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static bool ContainsRQuotedArgument(string line)
        => line.IndexOf('"') >= 0 || line.IndexOf('\'') >= 0;

}
