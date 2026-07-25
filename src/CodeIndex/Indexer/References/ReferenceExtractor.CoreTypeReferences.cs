using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreTypeReferenceContext(
        CoreReferenceLineContext Line,
        CoreExtractionLookups Lookups,
        IReadOnlyList<SymbolRecord> ContainerCandidates,
        IReadOnlyList<SymbolRecord> Symbols,
        string[] StructuralLines,
        IReadOnlyDictionary<
            string,
            List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>
            CSharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<
            string,
            List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>
            CSharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> CSharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> CSharpUsingStatics,
        Dictionary<string, HashSet<string>>? CSharpLocalNamesByFunction,
        CSharpWhereConstraintState? PendingCSharpWhereConstraint,
        HashSet<string>? KotlinConstructorTypeNames,
        IReadOnlyList<TypeScriptReferenceExtractor.NamespaceAliasBinding> TypeScriptNamespaceAliases,
        IReadOnlyList<TypeScriptReferenceExtractor.TypeAliasBinding>? TypeScriptTypeAliases,
        IReadOnlyList<SwiftReferenceExtractor.TypeAliasBinding>? SwiftTypeAliases,
        Func<int, SymbolRecord?> ResolveSwiftPropertyContainerForCall,
        bool[]? GoImportBlockLines,
        string[]? LuaReferenceLines,
        string OriginalLineForLanguage,
        IReadOnlySet<string>? AllDefinitionNames,
        HashSet<string>? StylusVariableDefinitionNames,
        bool XamlReferenceEnabled,
        XamlReferenceExtractor.BindingPropertyElementState? XamlBindingPropertyElementState,
        XamlReferenceExtractor.BindingMarkupExtensionState? XamlBindingMarkupExtensionState);

    private static bool EmitCoreTypeReferences(
        CoreTypeReferenceContext type,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern,
        ref bool xamlInXmlComment)
    {
        var line = type.Line;
        if (line.Language == "csharp")
        {
            CSharpReferenceExtractor.AdvanceMultiLineTypePatternState(
                line.PreparedLine,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                type.CSharpQualifiedConstantPatternMemberLookup,
                type.CSharpUsingAliases,
                type.CSharpUsingStatics,
                type.Lookups.HasActiveSameFileCSharpTypeCandidate,
                line.References,
                line.Seen,
                line.FileId,
                ref pendingCSharpMultiLineTypePattern);
        }

        // Event subscription/unsubscription (C#) / イベント購読・解除 (C#)
        if (line.Language is "csharp")
        {
            foreach (Match match in EventSubscriptionRegex.Matches(line.PreparedLine))
            {
                var eventContainer = line.ResolveContainerForCall(match.Groups["name"].Index);
                AddReference(line.References, line.Seen, line.FileId, match, "subscribe", line.Context, line.LineNumber, eventContainer);
            }
        }

        // Constructor chain-call rewrites: C# `: this(...)` / `: base(...)`, Java `this(...)` / `super(...)`,
        // and Kotlin `constructor(...) : this(...)` / `: super(...)`.
        // コンストラクタ連鎖呼び出しの書き換え
        if (line.Language is "csharp")
        {
            CSharpReferenceExtractor.EmitCtorChainReferences(
                line.PreparedLine, type.Lookups.GetEnclosingTypeCandidates, type.ContainerCandidates,
                type.StructuralLines, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
        }
        else if (line.Language is "java")
        {
            JavaReferenceExtractor.EmitCtorChainReferences(
                line.PreparedLine, type.Lookups.GetEnclosingTypeCandidates, type.Symbols, type.StructuralLines,
                line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
        }
        else if (line.Language is "kotlin")
        {
            KotlinReferenceExtractor.EmitCtorDelegationReferences(
                line.PreparedLine, type.Lookups.GetEnclosingTypeCandidates, type.Symbols, type.StructuralLines,
                line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
        }

        // Compile-time type/member line.References that CallRegex cannot see because the
        // argument has no trailing `(` of its own. See issue #253.
        // 末尾の `(` を持たず CallRegex では取れないコンパイル時の型/メンバ参照。issue #253 参照。
        if (line.Language is "csharp")
        {
            var csharpGenericParameterNames = CollectCSharpGenericParameterNamesForDeclaration(line.PreparedLine);
            foreach (Match match in CSharpTypeKeywordIntroRegex.Matches(line.PreparedLine))
            {
                int parenIndex = match.Index + match.Length - 1; // position of '(' / '(' の位置
                ExtractCSharpTypeKeywordSegments(
                    line.References, line.Seen, line.FileId, line.PreparedLine, parenIndex + 1,
                    line.Context, line.LineNumber, line.Container, line.Language, csharpGenericParameterNames);
            }
            ExtractCSharpReflectionNameLiteralReferences(
                line.References, line.Seen, line.FileId, line.PreparedLine, line.OriginalLine, line.Context, line.LineNumber, line.Container);
        }
        else if (line.Language is "java")
        {
            JavaReferenceExtractor.EmitDotClassTypeLiteralReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.Container);
        }
        else if (line.Language is "kotlin")
        {
            KotlinReferenceExtractor.EmitClassLiteralReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.Container);
            KotlinReferenceExtractor.EmitBacktickConstructorReferences(
                line.PreparedLine,
                type.KotlinConstructorTypeNames!,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }

        // Type-position line.References without an introducing keyword-call: base lists,
        // declaration types, generic constraints, throws clauses, type tests, and
        // XML-doc crefs. These are dependency edges for `line.References` / `impact`, but
        // not invocation edges for default `callers` / `callees`. See issue #256.
        // キーワード呼び出しの外にある型位置参照（継承リスト、宣言型、generic 制約、
        // throws、型テスト、XML doc cref）。`line.References` / `impact` では依存として扱うが、
        // 既定の `callers` / `callees` では呼び出しエッジではない。issue #256 参照。
        if (line.Language is "csharp" or "java" or "kotlin")
        {
            EmitCatchTypeReferences(
                line.Language,
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }

        if (line.Language == "csharp")
        {
            EmitCSharpLambdaCaptureReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.Container,
                type.CSharpLocalNamesByFunction);

            CSharpReferenceExtractor.EmitTypePositionReferences(
                line.PreparedLine,
                line.OriginalLine,
                type.CSharpQualifiedConstantPatternMemberLookup,
                type.CSharpQualifiedTypePatternLookup,
                type.CSharpUsingAliases,
                type.CSharpUsingStatics,
                type.Lookups.HasActiveSameFileCSharpTypeCandidate,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                line.Container,
                type.PendingCSharpWhereConstraint!,
                ref pendingCSharpMultiLineTypePattern);

            if (CSharpReferenceExtractor.HasTrailingIsAsTypePatternIntro(line.PreparedLine, line.OriginalLine))
            {
                CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
            }

            if (CSharpReferenceExtractor.HasTrailingCaseTypePatternIntro(line.PreparedLine, line.OriginalLine))
            {
                CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
            }

            TrackCSharpLocalDeclarations(line.PreparedLine, line.Container, type.CSharpLocalNamesByFunction);
        }
        else if (line.Language == "java")
        {
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
        else if (line.Language == "typescript")
        {
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
        else if (line.Language == "kotlin")
        {
            KotlinReferenceExtractor.EmitTypePositionReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }
        else if (line.Language == "swift")
        {
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
        else if (line.Language == "rust")
        {
            var rustEnumCandidatesForLine = type.Lookups.GetRustEnumCandidates();
            var rustEnumContainer = rustEnumCandidatesForLine != null
                ? FindInnermostContainer(rustEnumCandidatesForLine, line.LineNumber)
                : null;
            var rustTypePositionLine = RustReferenceExtractor.MaskAttributeBodies(line.PreparedLine);
            RustReferenceExtractor.EmitTypePositionReferences(
                rustTypePositionLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                line.Container,
                rustEnumContainer);
        }
        else if (line.Language == "c")
            CReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall);
        else if (line.Language == "cpp")
            CppReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall);
        else if (line.Language == "go")
        {
            GoReferenceExtractor.EmitConcurrencyReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
            GoReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, type.GoImportBlockLines?[line.LineIndex] == true);
        }
        else if (line.Language == "dart")
            DartReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall);
        else if (line.Language == "vb")
            VisualBasicReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall);
        else if (line.Language == "fortran")
            FortranReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.OriginalLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, line.Container);
        else if (line.Language == "pascal")
            PascalReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, line.Container);
        else if (line.Language == "objc")
            ObjectiveCReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, line.Container);
        else if (line.Language == "haskell")
            HaskellReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
        else if (line.Language == "elixir")
            ElixirReferenceExtractor.EmitTypePositionReferences(line.PreparedLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
        else if (line.Language == "lua")
            LuaReferenceExtractor.EmitTypePositionReferences(type.LuaReferenceLines?[line.LineIndex] ?? line.OriginalLine, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
        else if (line.Language == "css")
        {
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
        }
        else if (line.Language == "sass")
        {
            CssReferenceExtractor.EmitSass(line.PreparedLine, type.OriginalLineForLanguage, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.Container);
            return true;
        }
        else if (line.Language == "stylus")
        {
            CssReferenceExtractor.EmitStylus(line.PreparedLine, type.OriginalLineForLanguage, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, type.AllDefinitionNames, type.StylusVariableDefinitionNames, line.Container);
            return true;
        }
        else if (line.Language == "xml" && type.XamlReferenceEnabled)
        {
            var xamlLine = XamlReferenceExtractor.StripXmlComments(line.OriginalLine, ref xamlInXmlComment);
            XamlReferenceExtractor.Emit(xamlLine, line.Context, line.LineNumber, line.References, line.Seen, line.FileId, line.Container, type.XamlBindingPropertyElementState!, type.XamlBindingMarkupExtensionState!);
            return true;
        }
        else if (line.Language == "xml")
        {
            return true;
        }
        return false;
    }
}
