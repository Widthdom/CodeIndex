using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private readonly record struct PatternSymbolRange(
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine);

    private readonly record struct PatternSymbolEmissionContext(
        long FileId,
        string Language,
        SymbolPattern Pattern,
        string[] Lines,
        int LineIndex,
        int LineOffset,
        int AbsoluteStartColumn,
        string SourceLine,
        string PatternMatchLine,
        Match Match,
        string Name,
        string Kind,
        string Signature,
        string? RawReturnType,
        string? PythonSubKind,
        string? PythonModulePrefix,
        List<string>? RubyAttrNames,
        PatternSymbolRange Range,
        PatternSignatureBounds SignatureBounds,
        List<SymbolRecord> Symbols,
        SymbolExtractionState ExtractionState,
        HashSet<SymbolLineIdentity>? CssSeenSymbols,
        HashSet<string>? DockerfileStageNames);

    private static string EmitPatternSymbols(PatternSymbolEmissionContext context)
    {
        var kind = context.Kind;
        if (context.Language == "cpp"
            && IsCppTemplateSpecializationSymbol(
                kind,
                context.Name,
                context.Signature,
                context.Lines,
                context.LineIndex))
        {
            kind = "specialization";
        }

        if (ShouldSuppressJavaStatementSymbol(context))
            return kind;

        if (context.Language == "csharp"
            && context.Pattern.Kind == "function"
            && IsCSharpTestMethod(context.Lines, context.LineIndex))
        {
            kind = "test.method";
        }

        var pythonImportEntries = context.Language == "python" && context.Pattern.Kind == "import"
            ? TryExpandPythonImportSymbols(
                context.Lines,
                context.LineIndex,
                context.AbsoluteStartColumn,
                context.PythonModulePrefix)
            : null;
        var csharpDeclaratorEntries = context.Language == "csharp"
            && context.Pattern.Kind == "property"
            && context.Pattern.BodyStyle == BodyStyle.None
            ? TryExpandCSharpFieldDeclaratorList(
                context.PatternMatchLine,
                context.AbsoluteStartColumn,
                context.Match,
                context.Pattern.ReturnTypeGroup,
                context.Name)
            : null;
        var swiftEnumCaseEntries = context.Language == "swift"
            && context.Pattern.Kind == "property"
            && context.Pattern.BodyStyle == BodyStyle.None
            ? TryExpandSwiftEnumCaseDeclaratorList(
                context.PatternMatchLine,
                context.AbsoluteStartColumn,
                context.Match)
            : null;
        var fortranEnumeratorEntries = context.Language == "fortran"
            && context.Pattern.Kind == "property"
            && context.Pattern.BodyStyle == BodyStyle.None
            ? TryExpandFortranEnumeratorDeclaratorList(context.PatternMatchLine, context.Match)
            : null;
        var fortranParameterEntries = context.Language == "fortran"
            && context.Pattern.Kind == "property"
            && context.Pattern.BodyStyle == BodyStyle.None
            ? TryExpandFortranParameterDeclaratorList(context.PatternMatchLine, context.Match)
            : null;
        var fortranProcedureNames = ExpandFortranProcedureNames(context);

        if (pythonImportEntries != null)
        {
            foreach (var entry in pythonImportEntries)
                AddEmittedPatternSymbol(context, kind, entry.Name, entry.StartColumn, context.RawReturnType);
        }
        else if (csharpDeclaratorEntries != null)
        {
            foreach (var entry in csharpDeclaratorEntries)
            {
                AddEmittedPatternSymbol(
                    context,
                    kind,
                    entry.Name,
                    ResolveDefaultPatternStartColumn(context),
                    entry.ReturnType);
            }
        }
        else if (swiftEnumCaseEntries != null)
        {
            foreach (var entry in swiftEnumCaseEntries)
                AddEmittedPatternSymbol(context, kind, entry.Name, entry.StartColumn, entry.ReturnType);
        }
        else if (fortranEnumeratorEntries != null)
        {
            foreach (var entry in fortranEnumeratorEntries)
                AddEmittedPatternSymbol(context, kind, entry.Name, entry.StartColumn, context.RawReturnType);
        }
        else if (fortranParameterEntries != null)
        {
            foreach (var entry in fortranParameterEntries)
                AddEmittedPatternSymbol(context, kind, entry.Name, entry.StartColumn, context.RawReturnType);
        }
        else if (fortranProcedureNames != null)
        {
            foreach (var procedureName in fortranProcedureNames)
            {
                AddEmittedPatternSymbol(
                    context,
                    kind,
                    procedureName,
                    ResolveDefaultPatternStartColumn(context),
                    context.RawReturnType);
            }
        }
        else if (context.RubyAttrNames != null)
        {
            AddRubyAttributeSymbols(context, kind);
        }
        else
        {
            AddDefaultPatternSymbol(context, kind);
        }

        return kind;
    }

    private static bool ShouldSuppressJavaStatementSymbol(PatternSymbolEmissionContext context)
    {
        if (context.Language != "java" || context.Pattern.Kind != "function")
            return false;

        var trimmedSignature = context.Signature.TrimStart();
        return context.Name == "switch"
            || trimmedSignature.StartsWith("return ", StringComparison.Ordinal)
            || trimmedSignature.StartsWith("switch ", StringComparison.Ordinal)
            || trimmedSignature.StartsWith("case ", StringComparison.Ordinal);
    }

    private static List<string>? ExpandFortranProcedureNames(PatternSymbolEmissionContext context)
    {
        if (context.Language != "fortran"
            || context.Pattern.Kind != "function"
            || !context.Name.Contains(',')
            || !context.Signature.Contains("procedure", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        List<string>? procedureNames = null;
        var names = context.Name.AsSpan();
        var nameStart = 0;
        while (nameStart <= names.Length)
        {
            var separator = names[nameStart..].IndexOf(',');
            var nameEnd = separator >= 0 ? nameStart + separator : names.Length;
            var candidate = names[nameStart..nameEnd].Trim();
            if (candidate.Length > 0)
                (procedureNames ??= []).Add(candidate.ToString());

            if (separator < 0)
                break;
            nameStart = nameEnd + 1;
        }

        return procedureNames;
    }

    private static void AddRubyAttributeSymbols(
        PatternSymbolEmissionContext context,
        string kind)
    {
        var rubyAttrSearchStart = context.AbsoluteStartColumn;
        foreach (var rubyAttrName in context.RubyAttrNames!)
        {
            var rubyAttrStartColumn = rubyAttrSearchStart;
            if (!string.Equals(rubyAttrName, context.Name, StringComparison.Ordinal))
            {
                var foundRubyAttrStart = context.PatternMatchLine.IndexOf(
                    rubyAttrName,
                    rubyAttrSearchStart,
                    StringComparison.Ordinal);
                if (foundRubyAttrStart >= 0)
                    rubyAttrStartColumn = foundRubyAttrStart;
            }

            AddEmittedPatternSymbol(
                context,
                kind,
                rubyAttrName,
                rubyAttrStartColumn,
                context.RawReturnType);
            rubyAttrSearchStart = rubyAttrStartColumn + Math.Max(1, rubyAttrName.Length);
        }
    }

    private static void AddDefaultPatternSymbol(
        PatternSymbolEmissionContext context,
        string kind)
    {
        var csharpExplicitInterfaceIdentityNameFolded = context.Language == "csharp"
            ? CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                context.Name,
                context.Match)
            : null;
        var csharpMetadataTarget = TryClassifyCSharpExtractorMetadataTarget(
            context.Language,
            context.Pattern.Kind,
            context.Signature);
        AddEmittedPatternSymbol(
            context,
            kind,
            context.Name,
            ResolveLanguagePatternStartColumn(context),
            context.RawReturnType,
            context.Language == "cpp" && kind == "specialization" ? context.Name : null,
            context.PythonSubKind
                ?? ResolveLanguageSubKind(
                    context.Language,
                    kind,
                    context.Signature,
                    context.PatternMatchLine),
            csharpMetadataTarget,
            csharpExplicitInterfaceIdentityNameFolded);

        if (context.DockerfileStageNames != null && kind == "stage")
            context.DockerfileStageNames.Add(context.Name);

        if (context.Language == "objc"
            && context.Pattern.Kind == "class"
            && TryGetObjCCategoryDisplayName(
                context.PatternMatchLine[context.AbsoluteStartColumn..],
                context.Name,
                out var categoryDisplayName))
        {
            AddEmittedPatternSymbol(
                context,
                "class",
                categoryDisplayName,
                ResolveDefaultPatternStartColumn(context),
                context.RawReturnType);
        }
    }

    private static int ResolveLanguagePatternStartColumn(PatternSymbolEmissionContext context)
    {
        if (context.Language is "ada" or "cython" or "d" or "julia" or "matlab" or "nim")
            return context.LineOffset + context.Match.Groups["name"].Index;

        if (context.Language == "rust" && context.Pattern.Kind == "function")
            return context.Match.Groups["name"].Index;

        return ResolveDefaultPatternStartColumn(context);
    }

    private static int ResolveDefaultPatternStartColumn(PatternSymbolEmissionContext context) =>
        context.SignatureBounds.CSharpSingleLineCollapsedMatch
            ? context.SignatureBounds.CSharpSignatureRawStartColumn
            : context.AbsoluteStartColumn;

    private static void AddEmittedPatternSymbol(
        PatternSymbolEmissionContext context,
        string kind,
        string name,
        int startColumn,
        string? returnType,
        string? familyKey = null,
        string? subKind = null,
        bool? isMetadataTarget = null,
        string? identityNameFolded = null)
    {
        var startLine = context.LineIndex + 1;
        AddSymbolRecord(
            context.Symbols,
            context.ExtractionState,
            context.CssSeenSymbols,
            startLine,
            new SymbolRecord
            {
                FileId = context.FileId,
                Kind = kind,
                Name = name,
                IdentityNameFolded = identityNameFolded,
                Line = startLine,
                StartLine = startLine,
                StartColumn = startColumn,
                EndLine = Math.Max(startLine, context.Range.EndLine),
                BodyStartLine = context.Range.BodyStartLine,
                BodyEndLine = context.Range.BodyEndLine,
                Signature = context.Signature,
                FamilyKey = familyKey,
                SubKind = subKind,
                Visibility = TryGetGroup(context.Match, context.Pattern.VisibilityGroup),
                ReturnType = NormalizeMetadata(returnType),
                IsMetadataTarget = isMetadataTarget,
                MetadataTargetSource = isMetadataTarget == true
                    ? SymbolRecord.MetadataTargetSourceExtractor
                    : null,
            },
            context.SourceLine);
    }
}
