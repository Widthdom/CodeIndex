using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class RReferenceExtractor
{
    // R namespace references like `pkg::fun` and `pkg:::fun` should be searchable as references
    // even when they are not invoked as calls.
    // R の namespace 参照 `pkg::fun` / `pkg:::fun` を参照として記録する。
    private static readonly Regex NamespaceReferenceRegex = new(
        @"(?<![\w.])(?<package>[\w.]+)(?<sep>:::?)(?:(?<backtickName>`[^`]+`)|(?<name>[\w.]+))",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceImportDirectiveRegex = new(
        @"^\s*import\s*\(\s*(?<package>[\w.]+)(?:\s*,|\s*\))",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceImportFromDirectiveRegex = new(
        @"^\s*import(?:Classes|Methods)?From\s*\(\s*(?<package>[\w.]+)\s*,(?<names>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceExportDirectiveRegex = new(
        @"^\s*export(?:Classes|Methods)?\s*\(\s*(?<names>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceS3MethodDirectiveRegex = new(
        @"^\s*S3method\s*\(\s*(?:`(?<genericBacktick>[^`]+)`|['""](?<genericQuoted>[^'""]+)['""]|(?<generic>[A-Za-z.][\w.]*))\s*,\s*(?:`(?<classBacktick>[^`]+)`|['""](?<classQuoted>[^'""]+)['""]|(?<class>[A-Za-z.][\w.]*))(?:\s*,\s*(?:`(?<methodBacktick>[^`]+)`|['""](?<methodQuoted>[^'""]+)['""]|(?<method>[A-Za-z.][\w.]*)))?\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceUseDynLibDirectiveRegex = new(
        @"^\s*useDynLib\s*\(\s*(?:`(?<packageBacktick>[^`]+)`|['""](?<packageQuoted>[^'""]+)['""]|(?<package>[\w.]+))",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceUseDynLibRoutineRegex = new(
        @"(?:^|,)\s*(?!(?:\.[A-Za-z.][\w.]*|[A-Za-z.][\w.]*\s*=))(?:`(?<backtickName>[^`]+)`|['""](?<quotedName>[^'""]+)['""]|(?<name>[A-Za-z.][\w.]*))",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceDirectiveStartRegex = new(
        @"^\s*(?:import\s*\(|import(?:Classes|Methods)?From\s*\(|export(?:Classes|Methods)?\s*\(|S3method\s*\(|useDynLib\s*\()",
        RegexOptions.Compiled);
    private static readonly Regex NamespaceDirectiveNameRegex = new(
        @"`(?<backtickName>[^`]+)`|(?<name>[A-Za-z.][\w.]*)",
        RegexOptions.Compiled);
    private static readonly Regex BacktickCallRegex = new(
        @"`(?<name>[^`]+)`\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex InfixOperatorCallRegex = new(
        @"(?<!`)(?<name>%[^%\s]+%)(?!`)",
        RegexOptions.Compiled);
    private static readonly Regex SourceFileReferenceRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?(?:source|sys\.source)\s*\(\s*(?:file\s*=\s*)?['""](?<path>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex SourceFileReferenceStartRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?(?:source|sys\.source)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex LoadAllReferenceRegex = new(
        @"^\s*(?:(?:devtools|pkgload)::)load_all\s*\(\s*(?:path\s*=\s*)?['""](?<path>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex DataCallStartRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?data\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex DataCallDatasetRegex = new(
        @"(?:\(|,)\s*(?:list\s*=\s*)?['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex DataCallPackageRegex = new(
        @"\bpackage\s*=\s*['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex SystemFileCallStartRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?system\.file\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex SystemFilePathPartRegex = new(
        @"(?:\(|,)\s*(?!(?:[A-Za-z.][\w.]*\s*=))['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex VignetteCallStartRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?vignette\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex HelpExampleCallStartRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?(?:help|example)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex DocumentationTopicRegex = new(
        @"(?:\(|,)\s*(?!(?:[A-Za-z.][\w.]*\s*=))['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex InstallPackagesCallStartRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?install\.packages\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex NamespacePackageInstallCallStartRegex = new(
        @"^\s*(?:(?:renv)::install|(?:pak)::pkg_install)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex GitHubPackageInstallCallStartRegex = new(
        @"^\s*(?:(?:remotes|devtools)::)install_github\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex InstallPackagesNameRegex = new(
        @"(?:\(|,)\s*(?!(?:[A-Za-z.][\w.]*\s*=))(?:c\s*\(\s*)?['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled);
    private static readonly Regex DollarMemberReferenceRegex = new(
        @"(?<![\w.])(?:(?:`(?<backtickReceiver>[^`]+)`)|(?<receiver>[A-Za-z.][\w.]*))\$(?:(?:`(?<backtickName>[^`]+)`)|(?<name>[A-Za-z.][\w.]*))",
        RegexOptions.Compiled);
    private static readonly Regex BracketMemberReferenceRegex = new(
        @"(?<![\w.])(?:(?:`(?<backtickReceiver>[^`]+)`)|(?<receiver>[A-Za-z.][\w.]*))\s*\[\[\s*(?<quote>['""])(?<name>[^'""]+)\k<quote>\s*\]\]",
        RegexOptions.Compiled);
    private static readonly Regex SlotMemberReferenceRegex = new(
        @"(?<![\w.])(?:(?:`(?<backtickReceiver>[^`]+)`)|(?<receiver>[A-Za-z.][\w.]*))@(?:(?:`(?<backtickName>[^`]+)`)|(?<name>[A-Za-z.][\w.]*))",
        RegexOptions.Compiled);
    private static readonly Regex RoxygenImportFromTagRegex = new(
        @"^\s*#'\s*@(?:importFrom|importClassesFrom|importMethodsFrom)\s+(?<package>[\w.]+)\s+(?<names>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex RoxygenImportTagRegex = new(
        @"^\s*#'\s*@import\s+(?<packages>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex RoxygenMethodTagRegex = new(
        @"^\s*#'\s*@method\s+(?:`(?<genericBacktick>[^`]+)`|['""](?<genericQuoted>[^'""]+)['""]|(?<generic>[^\s]+))\s+(?:`(?<classBacktick>[^`]+)`|['""](?<classQuoted>[^'""]+)['""]|(?<class>[^\s]+))",
        RegexOptions.Compiled);
    private static readonly Regex S4SetGenericCallRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?set(?:Group)?Generic\s*\(\s*(?:(?:f|generic|name)\s*=\s*)?(?:`(?<backtickName>[^`]+)`|['""](?<quotedName>[^'""]+)['""]|(?<name>[A-Za-z.][\w.]*))",
        RegexOptions.Compiled);
    private static readonly Regex S4SetClassCallRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?(?:setClass|setRefClass|setClassUnion|setOldClass)\s*\(\s*(?:(?:Class|classes|className|classname|name)\s*=\s*)?(?:`(?<backtickName>[^`]+)`|['""](?<quotedName>[^'""]+)['""]|(?<name>[A-Za-z.][\w.]*))",
        RegexOptions.Compiled);
    private static readonly Regex S4SetMethodCallRegex = new(
        @"^\s*(?:(?:[\w.]+)::)?setMethod\s*\(\s*(?:(?:f|generic|name)\s*=\s*)?(?:`(?<genericBacktick>[^`]+)`|['""](?<genericQuoted>[^'""]+)['""]|(?<generic>[A-Za-z.][\w.]*))\s*,(?<tail>.*)$",
        RegexOptions.Compiled);
    private static readonly Regex S4SignatureCallRegex = new(
        @"signature\s*\((?<body>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex S4SignatureClassRegex = new(
        @"(?:^|,)\s*(?:(?<parameter>[A-Za-z.][\w.]*)\s*=\s*)?(?:`(?<backtickName>[^`]+)`|['""](?<quotedName>[^'""]+)['""]|(?<name>[A-Za-z.][\w.]*))",
        RegexOptions.Compiled);

    public static void EmitNamespaceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        foreach (Match match in NamespaceReferenceRegex.Matches(preparedLine))
        {
            var package = match.Groups["package"].Value;
            var separator = match.Groups["sep"].Value;
            var backtickNameGroup = match.Groups["backtickName"];
            var nameGroup = backtickNameGroup.Success ? backtickNameGroup : match.Groups["name"];
            var name = backtickNameGroup.Success
                ? backtickNameGroup.Value[1..^1]
                : nameGroup.Value;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{package}{separator}{name}",
                match.Groups["package"].Index,
                "reference",
                context,
                lineNumber,
                container);

            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index + (backtickNameGroup.Success ? 1 : 0),
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitNamespaceDirectiveReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var directiveLine = NamespaceDirectiveStartRegex.IsMatch(preparedLine)
            ? StripRNamespaceDirectiveComment(originalLine)
            : preparedLine;

        var importFromMatch = NamespaceImportFromDirectiveRegex.Match(directiveLine);
        if (importFromMatch.Success)
        {
            var package = importFromMatch.Groups["package"].Value;
            var namesGroup = importFromMatch.Groups["names"];
            foreach (var (name, nameIndex) in EnumerateNamespaceDirectiveNames(namesGroup.Value, namesGroup.Index))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    $"{package}::{name}",
                    importFromMatch.Groups["package"].Index,
                    "reference",
                    context,
                    lineNumber,
                    container);
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    nameIndex,
                    "reference",
                    context,
                    lineNumber,
                    container);
            }

            return;
        }

        var importMatch = NamespaceImportDirectiveRegex.Match(directiveLine);
        if (importMatch.Success)
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                importMatch.Groups["package"].Value,
                importMatch.Groups["package"].Index,
                "reference",
                context,
                lineNumber,
                container);
            return;
        }

        var s3MethodMatch = NamespaceS3MethodDirectiveRegex.Match(directiveLine);
        if (s3MethodMatch.Success)
        {
            var generic = GetNamespaceDirectiveToken(
                s3MethodMatch,
                "genericBacktick",
                "genericQuoted",
                "generic");
            var @class = GetNamespaceDirectiveToken(
                s3MethodMatch,
                "classBacktick",
                "classQuoted",
                "class");
            var explicitMethod = GetNamespaceDirectiveToken(
                s3MethodMatch,
                "methodBacktick",
                "methodQuoted",
                "method");
            if (generic != null && @class != null)
            {
                var method = explicitMethod ?? ($"{generic.Value.Name}.{@class.Value.Name}", generic.Value.Index);
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    method.Name,
                    method.Index,
                    "reference",
                    context,
                    lineNumber,
                    container);
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    generic.Value.Name,
                    generic.Value.Index,
                    "reference",
                    context,
                    lineNumber,
                    container);
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    @class.Value.Name,
                    @class.Value.Index,
                    "reference",
                    context,
                    lineNumber,
                    container);
            }

            return;
        }

        var useDynLibMatch = NamespaceUseDynLibDirectiveRegex.Match(directiveLine);
        if (useDynLibMatch.Success)
        {
            var package = GetNamespaceDirectiveToken(
                useDynLibMatch,
                "packageBacktick",
                "packageQuoted",
                "package");
            if (package != null)
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    package.Value.Name,
                    package.Value.Index,
                    "reference",
                    context,
                    lineNumber,
                    container);
            }

            var routinesStart = useDynLibMatch.Index + useDynLibMatch.Length;
            var routines = directiveLine[routinesStart..];
            foreach (Match routineMatch in NamespaceUseDynLibRoutineRegex.Matches(routines))
            {
                var routine = GetNamespaceDirectiveToken(
                    routineMatch,
                    "backtickName",
                    "quotedName",
                    "name");
                if (routine == null)
                    continue;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    routine.Value.Name,
                    routinesStart + routine.Value.Index,
                    "reference",
                    context,
                    lineNumber,
                    container);
            }

            return;
        }

        var exportMatch = NamespaceExportDirectiveRegex.Match(directiveLine);
        if (!exportMatch.Success)
            return;

        var exportNamesGroup = exportMatch.Groups["names"];
        foreach (var (name, nameIndex) in EnumerateNamespaceDirectiveNames(exportNamesGroup.Value, exportNamesGroup.Index))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameIndex,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitRoxygenImportFromReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = RoxygenImportFromTagRegex.Match(originalLine);
        if (!match.Success)
            return;

        var package = match.Groups["package"];
        var namesGroup = match.Groups["names"];
        foreach (var (name, nameIndex) in EnumerateNamespaceDirectiveNames(namesGroup.Value, namesGroup.Index))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{package.Value}::{name}",
                package.Index,
                "reference",
                context,
                lineNumber,
                container);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameIndex,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitRoxygenImportReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = RoxygenImportTagRegex.Match(originalLine);
        if (!match.Success)
            return;

        var packagesGroup = match.Groups["packages"];
        foreach (var (package, packageIndex) in EnumerateNamespaceDirectiveNames(packagesGroup.Value, packagesGroup.Index))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                package,
                packageIndex,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitS4DispatchReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var line = StripRNamespaceDirectiveComment(originalLine);

        var genericMatch = S4SetGenericCallRegex.Match(line);
        if (genericMatch.Success)
        {
            AddS4Reference(genericMatch, "backtickName", "quotedName", "name", "reference");
            return;
        }

        var classMatch = S4SetClassCallRegex.Match(line);
        if (classMatch.Success)
        {
            AddS4Reference(classMatch, "backtickName", "quotedName", "name", "type_reference");
            return;
        }

        var methodMatch = S4SetMethodCallRegex.Match(line);
        if (!methodMatch.Success)
            return;

        var generic = GetNamespaceDirectiveToken(
            methodMatch,
            "genericBacktick",
            "genericQuoted",
            "generic");
        if (generic == null)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            generic.Value.Name,
            generic.Value.Index,
            "reference",
            context,
            lineNumber,
            container);

        var tailGroup = methodMatch.Groups["tail"];
        var signatureCall = S4SignatureCallRegex.Match(tailGroup.Value);
        if (!signatureCall.Success)
            return;

        var signatureBody = signatureCall.Groups["body"];
        foreach (Match signatureMatch in S4SignatureClassRegex.Matches(signatureBody.Value))
        {
            var classToken = GetNamespaceDirectiveToken(
                signatureMatch,
                "backtickName",
                "quotedName",
                "name");
            if (classToken == null)
                continue;

            var absoluteIndex = tailGroup.Index + signatureBody.Index + classToken.Value.Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                classToken.Value.Name,
                absoluteIndex,
                "type_reference",
                context,
                lineNumber,
                container);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{generic.Value.Name}.{classToken.Value.Name}",
                generic.Value.Index,
                "reference",
                context,
                lineNumber,
                container);
        }

        void AddS4Reference(Match match, string backtickGroup, string quotedGroup, string nameGroup, string kind)
        {
            var token = GetNamespaceDirectiveToken(match, backtickGroup, quotedGroup, nameGroup);
            if (token == null)
                return;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                token.Value.Name,
                token.Value.Index,
                kind,
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitRoxygenMethodReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = RoxygenMethodTagRegex.Match(originalLine);
        if (!match.Success)
            return;

        var generic = GetNamespaceDirectiveToken(
            match,
            "genericBacktick",
            "genericQuoted",
            "generic");
        var @class = GetNamespaceDirectiveToken(
            match,
            "classBacktick",
            "classQuoted",
            "class");
        if (generic == null || @class == null)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            $"{generic.Value.Name}.{@class.Value.Name}",
            generic.Value.Index,
            "reference",
            context,
            lineNumber,
            container);
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            generic.Value.Name,
            generic.Value.Index,
            "reference",
            context,
            lineNumber,
            container);
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            @class.Value.Name,
            @class.Value.Index,
            "reference",
            context,
            lineNumber,
            container);
    }

    public static void EmitBacktickCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        foreach (Match match in BacktickCallRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        foreach (Match match in InfixOperatorCallRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!SourceFileReferenceStartRegex.IsMatch(preparedLine))
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!DataCallStartRegex.IsMatch(preparedLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in DataCallDatasetRegex.Matches(line))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!SystemFileCallStartRegex.IsMatch(preparedLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in SystemFilePathPartRegex.Matches(line))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Regex startRegex)
    {
        if (!startRegex.IsMatch(preparedLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in DocumentationTopicRegex.Matches(line))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Regex startRegex)
    {
        if (!startRegex.IsMatch(preparedLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in InstallPackagesNameRegex.Matches(line))
        {
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

    public static void EmitDollarMemberReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        foreach (Match match in DollarMemberReferenceRegex.Matches(preparedLine))
        {
            var backtickReceiverGroup = match.Groups["backtickReceiver"];
            var receiverGroup = backtickReceiverGroup.Success ? backtickReceiverGroup : match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var backtickNameGroup = match.Groups["backtickName"];
            var nameGroup = backtickNameGroup.Success ? backtickNameGroup : match.Groups["name"];
            var name = nameGroup.Value;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{receiver}${name}",
                receiverGroup.Index,
                "reference",
                context,
                lineNumber,
                container);

            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitBracketMemberReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        if (!preparedLine.Contains("[[", StringComparison.Ordinal))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in BracketMemberReferenceRegex.Matches(line))
        {
            var backtickReceiverGroup = match.Groups["backtickReceiver"];
            var receiverGroup = backtickReceiverGroup.Success ? backtickReceiverGroup : match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var nameGroup = match.Groups["name"];
            var name = nameGroup.Value;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{receiver}${name}",
                receiverGroup.Index,
                "reference",
                context,
                lineNumber,
                container);

            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitSlotMemberReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        foreach (Match match in SlotMemberReferenceRegex.Matches(preparedLine))
        {
            var backtickReceiverGroup = match.Groups["backtickReceiver"];
            var receiverGroup = backtickReceiverGroup.Success ? backtickReceiverGroup : match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var backtickNameGroup = match.Groups["backtickName"];
            var nameGroup = backtickNameGroup.Success ? backtickNameGroup : match.Groups["name"];
            var name = nameGroup.Value;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{receiver}@{name}",
                receiverGroup.Index,
                "reference",
                context,
                lineNumber,
                container);

            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static IEnumerable<(string Name, int Index)> EnumerateNamespaceDirectiveNames(string value, int baseIndex)
    {
        foreach (Match match in NamespaceDirectiveNameRegex.Matches(value))
        {
            var backtickNameGroup = match.Groups["backtickName"];
            var nameGroup = backtickNameGroup.Success ? backtickNameGroup : match.Groups["name"];
            yield return (nameGroup.Value, baseIndex + nameGroup.Index + (backtickNameGroup.Success ? 1 : 0));
        }
    }

    private static (string Name, int Index)? GetNamespaceDirectiveToken(Match match, params string[] groupNames)
    {
        foreach (var groupName in groupNames)
        {
            var group = match.Groups[groupName];
            if (group.Success)
                return (group.Value, group.Index);
        }

        return null;
    }

    private static string StripRNamespaceDirectiveComment(string line)
    {
        var inBacktickIdentifier = false;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (inBacktickIdentifier)
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == '`')
                    inBacktickIdentifier = false;
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '`')
            {
                inBacktickIdentifier = true;
                continue;
            }

            if (ch == '#')
                return line[..i];
        }

        return line;
    }
}
