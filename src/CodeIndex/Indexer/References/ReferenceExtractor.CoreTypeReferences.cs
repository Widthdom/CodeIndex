using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreTypeReferenceContext(
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
        XamlReferenceExtractor.BindingMarkupExtensionState? XamlBindingMarkupExtensionState)
    {
        public readonly CoreReferenceLineContext Line;

        public CoreTypeReferenceContext(
            in CoreReferenceLineContext line,
            CoreExtractionLookups lookups,
            IReadOnlyList<SymbolRecord> containerCandidates,
            IReadOnlyList<SymbolRecord> symbols,
            string[] structuralLines,
            IReadOnlyDictionary<
                string,
                List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>
                cSharpQualifiedConstantPatternMemberLookup,
            IReadOnlyDictionary<
                string,
                List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>
                cSharpQualifiedTypePatternLookup,
            IReadOnlyList<CSharpUsingAliasRecord> cSharpUsingAliases,
            IReadOnlyList<CSharpUsingStaticRecord> cSharpUsingStatics,
            Dictionary<string, HashSet<string>>? cSharpLocalNamesByFunction,
            CSharpWhereConstraintState? pendingCSharpWhereConstraint,
            HashSet<string>? kotlinConstructorTypeNames,
            IReadOnlyList<TypeScriptReferenceExtractor.NamespaceAliasBinding> typeScriptNamespaceAliases,
            IReadOnlyList<TypeScriptReferenceExtractor.TypeAliasBinding>? typeScriptTypeAliases,
            IReadOnlyList<SwiftReferenceExtractor.TypeAliasBinding>? swiftTypeAliases,
            Func<int, SymbolRecord?> resolveSwiftPropertyContainerForCall,
            bool[]? goImportBlockLines,
            string[]? luaReferenceLines,
            string originalLineForLanguage,
            IReadOnlySet<string>? allDefinitionNames,
            HashSet<string>? stylusVariableDefinitionNames,
            bool xamlReferenceEnabled,
            XamlReferenceExtractor.BindingPropertyElementState? xamlBindingPropertyElementState,
            XamlReferenceExtractor.BindingMarkupExtensionState? xamlBindingMarkupExtensionState)
            : this(
                lookups,
                containerCandidates,
                symbols,
                structuralLines,
                cSharpQualifiedConstantPatternMemberLookup,
                cSharpQualifiedTypePatternLookup,
                cSharpUsingAliases,
                cSharpUsingStatics,
                cSharpLocalNamesByFunction,
                pendingCSharpWhereConstraint,
                kotlinConstructorTypeNames,
                typeScriptNamespaceAliases,
                typeScriptTypeAliases,
                swiftTypeAliases,
                resolveSwiftPropertyContainerForCall,
                goImportBlockLines,
                luaReferenceLines,
                originalLineForLanguage,
                allDefinitionNames,
                stylusVariableDefinitionNames,
                xamlReferenceEnabled,
                xamlBindingPropertyElementState,
                xamlBindingMarkupExtensionState)
        {
            Line = line;
        }
    }

    private static bool EmitCoreTypeReferences(
        in CoreTypeReferenceContext type,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern,
        ref bool xamlInXmlComment)
    {
        EmitCoreTypePreludeReferences(in type, ref pendingCSharpMultiLineTypePattern);
        EmitCoreCSharpTypeReferences(in type, ref pendingCSharpMultiLineTypePattern);
        EmitCorePrimaryLanguageTypeReferences(in type);
        EmitCoreSecondaryLanguageTypeReferences(in type);
        return EmitCoreConsumedLanguageTypeReferences(in type, ref xamlInXmlComment);
    }
}
