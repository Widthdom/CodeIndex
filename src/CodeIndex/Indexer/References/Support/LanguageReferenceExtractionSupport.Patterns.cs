using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    // THREAD-SAFETY: This support surface only owns immutable post-construction Regex fields.
    // Any state accumulated while extracting references must stay in caller-provided collections
    // or local variables so concurrent ReferenceExtractor calls cannot share mutable state.
    private static readonly string[] RazorControlDirectives =
    {
        "@if",
        "@foreach",
        "@for",
        "@while",
        "@switch",
        "@using",
        "@lock",
        "@try",
        "@catch",
        "@finally",
        "@do"
    };
    private static readonly string[] RazorCodeDirectives = ["@code", "@functions"];
    private static readonly string[] RazorBareControlContinuationKeywords = ["else", "catch", "finally"];
    private static readonly string[] CTypeSpecifierKeywords = ["struct", "enum", "union"];
    private static readonly string[] PascalDeclarationKeywords =
    [
        "var", "const", "type", "property", "procedure", "function", "constructor", "destructor",
    ];
    private static readonly string[] CppAccessPrefixes = ["public ", "private ", "protected ", "virtual "];

    private static readonly Regex CppIncludeRegex = new(
        @"^(?:\s*#\s*(?:include(?:_next)?|import)\s*(?:<(?<name>[^>\r\n]+)>|""(?<name>[^""\r\n]+)""|(?<name>[^\s]+))|\s*(?:export\s+)?import\s+(?:<(?<name>[^>\r\n]+)>|""(?<name>[^""\r\n]+)""|(?<name>:?[A-Za-z_]\w*(?:[.:][A-Za-z_]\w*)*))\s*;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppBaseListRegex = new(
        @"^\s*(?:export\s+)?(?:(?:template|requires)\b[^{;]*\s+)*(?:class|struct)\s+[A-Za-z_]\w*(?:\s*final)?\s*:\s*(?<bases>[^{;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppNewTypeRegex = new(
        @"\bnew\s+(?<type>(?:[A-Za-z_]\w*\s*::\s*)*[A-Za-z_]\w*(?:\s*<[^;{}]+>)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppNamedCastTypeRegex = new(
        @"\b(?:static_cast|dynamic_cast|reinterpret_cast|const_cast)\s*<(?<type>[^;{}<>]+(?:<[^;{}<>]+>)?[^;{}<>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppCStyleCastTypeRegex = new(
        @"(?<![\w])\(\s*(?<type>(?:(?:const|volatile|typename|class|struct|enum)\s+)*(?:[A-Z_]\w*|[A-Za-z_]\w*\s*::\s*[A-Za-z_]\w*)(?:\s*<[^;{}()]+>)?(?:\s*[*&])*)\s*\)\s*(?:[A-Za-z_]\w*|\*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefCastTypeRegex = new(
        @"(?<![\w])\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t(?:\s*\*)*)\s*\)\s*(?:[A-Za-z_]\w*|\*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefSizeofTypeRegex = new(
        @"\bsizeof\s*\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t(?:\s*\*(?:\s*(?:const|volatile|restrict|_Atomic))?)*)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedSizeofTypeRegex = new(
        @"\bsizeof\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\s*\*(?:\s*(?:const|volatile|restrict|_Atomic))?)*\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefAlignofTypeRegex = new(
        @"\b(?:_Alignof|alignof|__alignof__|__alignof)\s*\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t(?:\s*\*)*)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedAlignofTypeRegex = new(
        @"\b(?:_Alignof|alignof|__alignof__|__alignof)\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefDeclarationTypeRegex = new(
        @"(?<![\w])(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*\s*(?=[=,;\[])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedDeclarationTypeRegex = new(
        @"(?<![\w])(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*\s*(?=[=,;\[])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefFunctionReturnTypeRegex = new(
        @"^\s*(?:(?:static|extern|inline|const|volatile|restrict|_Atomic)\s+)*(?<type>[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedFunctionReturnTypeRegex = new(
        @"^\s*(?:(?:static|extern|inline|const|volatile|restrict|_Atomic)\s+)*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefParameterTypeRegex = new(
        @"(?:\(|,)\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedParameterTypeRegex = new(
        @"(?:\(|,)\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefCompoundLiteralTypeRegex = new(
        @"\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedCompoundLiteralTypeRegex = new(
        @"\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefTypeofTypeRegex = new(
        @"\b(?:typeof|__typeof__|__typeof)\s*\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedTypeofTypeRegex = new(
        @"\b(?:typeof|__typeof__|__typeof)\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefTypeofUnqualTypeRegex = new(
        @"\b(?:typeof_unqual|__typeof_unqual__|__typeof_unqual)\s*\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedTypeofUnqualTypeRegex = new(
        @"\b(?:typeof_unqual|__typeof_unqual__|__typeof_unqual)\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefBuiltinTypesCompatibleFirstTypeRegex = new(
        @"\b__builtin_types_compatible_p\s*\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*,",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefBuiltinTypesCompatibleSecondTypeRegex = new(
        @"\b__builtin_types_compatible_p\s*\([^,;{}]+,\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedBuiltinTypesCompatibleFirstTypeRegex = new(
        @"\b__builtin_types_compatible_p\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?,",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedBuiltinTypesCompatibleSecondTypeRegex = new(
        @"\b__builtin_types_compatible_p\s*\([^,;{}]+,\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefGenericAssociationTypeRegex = new(
        @"(?:_Generic\s*\([^,;{}]*,|,)\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*(?:\s*(?:const|volatile|restrict|_Atomic))?)*\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedGenericAssociationTypeRegex = new(
        @"(?:_Generic\s*\([^,;{}]*,|,)\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\s*\*(?:\s*(?:const|volatile|restrict|_Atomic))?)*\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefAtomicTypeRegex = new(
        @"\b_Atomic\s*\(\s*(?<type>(?:(?:const|volatile|restrict)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedAtomicTypeRegex = new(
        @"\b_Atomic\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefAlignasTypeRegex = new(
        @"\b(?:_Alignas|alignas)\s*\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedAlignasTypeRegex = new(
        @"\b(?:_Alignas|alignas)\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefFunctionPointerAliasTypeRegex = new(
        @"\btypedef\s+(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\(\s*\*\s*[A-Za-z_]\w*\s*\)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedFunctionPointerAliasTypeRegex = new(
        @"\btypedef\s+(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\(\s*\*\s*[A-Za-z_]\w*\s*\)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefFunctionPointerDeclarationTypeRegex = new(
        @"(?<![\w])(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\(\s*\*\s*[A-Za-z_]\w*\s*\)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedFunctionPointerDeclarationTypeRegex = new(
        @"(?<![\w])(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\(\s*\*\s*[A-Za-z_]\w*\s*\)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefPointerArrayDeclarationTypeRegex = new(
        @"(?<![\w])(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\(\s*\*\s*[A-Za-z_]\w*\s*\)\s*\[",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedPointerArrayDeclarationTypeRegex = new(
        @"(?<![\w])(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\(\s*\*\s*[A-Za-z_]\w*\s*\)\s*\[",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefOffsetofTypeRegex = new(
        @"\b(?:offsetof|__builtin_offsetof)\s*\(\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*,",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedOffsetofTypeRegex = new(
        @"\b(?:offsetof|__builtin_offsetof)\s*\(\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?,",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTypedefVaArgTypeRegex = new(
        @"\b(?:va_arg|__builtin_va_arg)\s*\(\s*[^,;{}]+,\s*(?<type>(?:(?:const|volatile|restrict|_Atomic)\s+)*[A-Za-z_]\w*_t\b)(?:\s*\*)*\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CTaggedVaArgTypeRegex = new(
        @"\b(?:va_arg|__builtin_va_arg)\s*\(\s*[^,;{}]+,\s*(?<type>(?:struct|enum|union)\s+[A-Za-z_]\w*)\s*(?:\*+\s*)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] CVaArgFunctionNames =
    {
        "va_arg",
        "__builtin_va_arg",
    };
    private static readonly Regex CppTypeOperandOperatorRegex = new(
        @"\b(?:sizeof|alignof)\s*\(\s*(?<type>(?:(?:const|volatile|typename|class|struct|enum)\s+)*(?:[A-Z_]\w*|[A-Za-z_]\w*\s*::\s*[A-Za-z_]\w*)(?:\s*<[^;{}]+>)?(?:\s*[*&])*)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppTypeIdRegex = new(
        @"\btypeid\s*\(\s*(?<type>(?:(?:const|volatile|typename|class|struct|enum)\s+)*(?:[A-Z_]\w*|[A-Za-z_]\w*\s*::\s*[A-Za-z_]\w*)(?:\s*<[^;{}]+>)?(?:\s*[*&])*)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppDecltypeBraceConstructionRegex = new(
        @"\bdecltype\s*\(\s*(?<type>(?:[A-Za-z_]\w*\s*::\s*)*[A-Z_]\w*(?:\s*<[^;{}()]+>)?)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppFactoryTemplateArgumentRegex = new(
        @"\b(?:std\s*::\s*)?(?:make_unique|make_shared|make_optional)\s*<(?<type>[^;{}<>]+(?:<[^;{}<>]+>)?[^;{}<>]*)>\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppTypeTraitTemplateArgumentRegex = new(
        @"\b(?:std\s*::\s*)?(?:is_same|is_base_of|is_convertible|is_constructible|is_assignable|is_invocable)(?:_v)?\s*<(?<type>[^;{}<>]+(?:<[^;{}<>]+>)?[^;{}<>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppBraceConstructionRegex = new(
        @"(?:=\s*|return\s+|co_return\s+|throw\s+)(?<type>(?:[A-Za-z_]\w*\s*::\s*)*[A-Z_]\w*(?:\s*<[^;{}]+>)?)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppQualifiedTemplateBraceConstructionRegex = new(
        @"(?:=\s*|return\s+|co_return\s+|throw\s+)(?:[A-Za-z_]\w*\s*::\s*)+[A-Za-z_]\w*\s*<(?<args>[^;{}]+)>\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppUsingAliasTargetRegex = new(
        @"\b(?:template\s*<[^>]*>\s*)?using\s+[A-Za-z_]\w*\s*=\s*(?<type>[^;]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppTypedefAliasTargetRegex = new(
        @"\btypedef\s+(?![^;]*\()(?<type>.+?)\s+[A-Za-z_]\w*\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppExplicitTemplateInstantiationRegex = new(
        @"\b(?:extern\s+)?template\s+(?:class|struct)\s+(?<type>[^;]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppTemplateIdDeclarationRegex = new(
        @"(?<!template\s)(?<!class\s)(?<!struct\s)(?<type>(?:[A-Za-z_]\w*\s*::\s*)*[A-Z_]\w*)\s*<(?<args>[^;{}]+)>\s*(?:[*&]\s*)?[A-Za-z_]\w*\s*(?:[=;{,)]|\[[^\]]*\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppTemplateParameterDefaultTypeRegex = new(
        @"\b(?:typename|class)\s+[A-Za-z_]\w*\s*=\s*(?<type>(?:[A-Za-z_]\w*\s*::\s*)*[A-Za-z_]\w*(?:\s*<[^,>]+>)?(?:\s*[*&])?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppQualifiedMemberReceiverRegex = new(
        @"(?<![\w:])(?<type>(?:(?:[A-Za-z_]\w*)\s*::\s*)*[A-Z_]\w*)\s*::\s*[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppPointerToMemberTypeRegex = new(
        @"(?<![\w:])(?<type>(?:(?:[A-Za-z_]\w*)\s*::\s*)*[A-Z_]\w*)\s*::\s*\*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppTrailingReturnTypeRegex = new(
        @"\)\s*->\s*(?<type>(?:(?:const|volatile|typename|class|struct|enum)\s+)*(?:[A-Z_]\w*|[A-Za-z_]\w*\s*::\s*[A-Za-z_]\w*)(?:\s*<[^;{}]+>)?(?:\s*[*&])*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppRequiresConceptTypeRegex = new(
        @"\brequires\s+(?<type>(?:(?:[A-Za-z_]\w*)\s*::\s*)*[A-Z_]\w*)\s*<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppParenthesizedRequiresConceptTypeRegex = new(
        @"\brequires\s*\(\s*(?<type>(?:(?:[A-Za-z_]\w*)\s*::\s*)*[A-Z_]\w*)\s*<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppQualifiedRequiresConceptConstraintRegex = new(
        @"\brequires\s*\(?\s*(?<concept>(?:(?:[A-Za-z_]\w*)\s*::\s*)+[A-Za-z_]\w*)\s*<(?<args>[^;{}<>]+(?:<[^;{}<>]+>)?[^;{}<>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppConceptExpressionTypeRegex = new(
        @"(?:=|&&|\|\|)\s*(?<type>(?:(?:[A-Za-z_]\w*)\s*::\s*)*[A-Z_]\w*)\s*<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppCompoundRequirementConceptRegex = new(
        @"->\s*(?<concept>(?:(?:[A-Za-z_]\w*)\s*::\s*)*[A-Za-z_]\w*)\s*<(?<args>[^;{}<>]+(?:<[^;{}<>]+>)?[^;{}<>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppFriendTypeRegex = new(
        @"\bfriend\s+(?:class|struct|union|typename|enum(?:\s+class)?)\s+(?<type>(?:[A-Za-z_]\w*\s*::\s*)*[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppDynamicExceptionSpecRegex = new(
        @"\bthrow\s*\(\s*(?<type>(?:(?:[A-Za-z_]\w*\s*::\s*)*[A-Z_]\w*(?:\s*[*&])?(?:\s*,\s*)?)+)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CppDeclarationTypeRegex = new(
        @"(?<![\w:])(?<type>(?:(?:const|volatile|static|inline|constexpr|typename|class|struct|enum)\s+)*(?:[A-Z_]\w*|[A-Za-z_]\w*\s*::\s*[A-Za-z_]\w*)(?:\s*<[^;{}]+>)?(?:\s*[*&])*)\s+(?<name>[A-Za-z_]\w*)\s*(?=[,;)=])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DartCtorRegex = new(
        @"\b(?:new|const)\s+(?<name>[A-Z]\w*(?:\.[A-Za-z_]\w*)?)\s*(?:<[^>]+>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DartVariableTypeRegex = new(
        @"^\s*(?:(?:final|late|const)\s+)*(?<type>[A-Z]\w*(?:\s*<[^;=]+>)?)\s+[A-Za-z_]\w*\s*(?:=|;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DartFunctionSignatureRegex = new(
        @"^\s*(?:(?:external|static|abstract)\s+)*(?<return>[A-Z]\w*(?:\s*<[^;{}()]+>)?)\s+[A-Za-z_]\w*\s*\((?<params>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DartParameterTypeRegex = new(
        @"(?:^|,)\s*(?:(?:required|covariant|final)\s+)*(?<type>[A-Z]\w*(?:\s*<[^,)=]+>)?)\s+[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string VbIdentifierPattern = @"(?:\[[^\]\r\n]+\]|[A-Za-z_]\w*)";
    private const string VbQualifiedIdentifierPattern = @"(?:Global\.)?(?:" + VbIdentifierPattern + @")(?:\.(?:" + VbIdentifierPattern + @"))*";
    private static readonly Regex VbTypeKeywordRegex = new(
        @"\b(?:As\s+(?:New\s+)?|New\s+|Inherits\s+|Implements\s+)(?<type>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbGenericArgumentListRegex = new(
        @"\(\s*Of\s+(?<list>[^)\r\n]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbGenericDeclarationOwnerRegex = new(
        @"\b(?:Class|Structure|Interface|Delegate|Sub|Function)\s+" + VbIdentifierPattern + @"\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbGenericConstraintRegex = new(
        @"^\s*(?<param>" + VbIdentifierPattern + @")\s+As\s+(?<constraint>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbNewTypeRegex = new(
        @"\bNew\s+(?<type>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbImplementsListRegex = new(
        @"\bImplements\s+(?<list>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbImportsListRegex = new(
        @"^\s*Imports\s+(?<list>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbCastTypeRegex = new(
        @"\b(?:DirectCast|TryCast|CType)\s*\([^,\r\n]+,\s*(?<type>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbGetTypeRegex = new(
        @"\bGetType\s*\(\s*(?<type>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbTypeOfRegex = new(
        @"\bTypeOf\b.+?\bIs(?:Not)?\s+(?<type>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbNameOfRegex = new(
        @"\bNameOf\s*\(\s*(?<name>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbGetXmlNamespaceRegex = new(
        @"\bGetXmlNamespace\s*\(\s*(?<name>[A-Za-z_]\w*)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbAddressOfRegex = new(
        @"\bAddressOf\s+(?<name>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbHandlesTargetRegex = new(
        @"(?:\bHandles|,)\s+(?<name>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbAddHandlerRegex = new(
        @"\bAddHandler\s+(?<name>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbRemoveHandlerRegex = new(
        @"\bRemoveHandler\s+(?<name>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbRaiseEventRegex = new(
        @"\bRaiseEvent\s+(?<name>" + VbQualifiedIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbCallRegex = new(
        @"(?<![\w\]])(?<name>" + VbQualifiedIdentifierPattern + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbBareCallRegex = new(
        @"^\s*(?:Call\s+)?(?<name>" + VbQualifiedIdentifierPattern + @")(?<tail>\s*(?:$|.*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbBareMemberCallRegex = new(
        @"^\s*\.\s*(?<name>" + VbIdentifierPattern + @")(?<tail>\s*(?:$|.*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VbCallByNameRegex = new(
        @"\bCallByName\s*\([^,\r\n]+,\s*""(?<name>[^""\r\n]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FortranUseRegex = new(
        @"^\s*use(?:\s*,\s*(?:intrinsic|non_intrinsic))?(?:\s*::\s*|\s+)(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranUseOnlyRegex = new(
        @"^\s*use(?:\s*,\s*(?:intrinsic|non_intrinsic))?(?:\s*::\s*|\s+)[A-Za-z_]\w*\s*,\s*only\s*:\s*(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranUseRenameListRegex = new(
        @"^\s*use(?:\s*,\s*(?:intrinsic|non_intrinsic))?(?:\s*::\s*|\s+)[A-Za-z_]\w*\s*,\s*(?!only\s*:)(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranUseAliasRegex = new(
        @"(?:^|,)\s*(?<alias>[A-Za-z_]\w*)\s*=>\s*[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranUseAliasTargetRegex = new(
        @"(?:^|,)\s*[A-Za-z_]\w*\s*=>\s*(?<target>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranImportRegex = new(
        @"^\s*import(?:\s*,\s*only)?(?:\s*::\s*|\s*:\s*|\s+)(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranIncludeRegex = new(
        @"^\s*include\s*['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranBlankCommonMemberListRegex = new(
        @"^\s*common\s+(?<list>[^/].*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranParenthesizedNameListRegex = new(
        @"\((?<list>[^()]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranDataLineRegex = new(
        @"^\s*data\s+(?<tail>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranDataObjectGroupRegex = new(
        @"(?:^|,)\s*(?<list>[^/]+?)\s*/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranSaveRegex = new(
        @"^\s*save(?:\s*::|\s+)(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranSlashGroupNameRegex = new(
        @"/\s*(?<name>[A-Za-z_]\w*)\s*/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranSlashGroupMemberListRegex = new(
        @"/\s*[A-Za-z_]\w*\s*/(?<list>[^/]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranSubmoduleParentRegex = new(
        @"^\s*submodule\s*\(\s*(?<parent>[A-Za-z_]\w*)(?:\s*:\s*(?<ancestor>[A-Za-z_]\w*))?\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranExternalRegex = new(
        @"^\s*external(?:\s*::)?\s*(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranIntrinsicProcedureRegex = new(
        @"^\s*intrinsic(?:\s*::)?\s*(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranAccessListRegex = new(
        @"^\s*(?:public|private)(?:\s*::\s*|\s+)(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranFinalizerRegex = new(
        @"^\s*final(?:\s*::\s*|\s+)(?<list>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranSimpleListNameRegex = new(
        @"(?:^|,)\s*(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranTypeRegex = new(
        @"\b(?:type|class)\s*\(\s*(?<type>[A-Za-z_]\w*)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranTypeGuardRegex = new(
        @"\b(?:type|class)\s+is\s*\(\s*(?<type>[A-Za-z_]\w*)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranExtendsRegex = new(
        @"\bextends\s*\(\s*(?<type>[A-Za-z_]\w*)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranProcedureTypeRegex = new(
        @"\bprocedure\s*\(\s*(?<type>[A-Za-z_]\w*)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranAllocateTypeSpecRegex = new(
        @"\ballocate\s*\(\s*(?<type>[A-Za-z_]\w*)\s*::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranAllocateListRegex = new(
        @"^\s*allocate\s*\((?<list>.*)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranAllocateSourceKeywordRegex = new(
        @"\b(?:source|mold)\s*=\s*(?<name>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranAllocationStatusKeywordRegex = new(
        @"\b(?:stat|errmsg)\s*=\s*(?<name>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranDeallocateListRegex = new(
        @"^\s*deallocate\s*\((?<list>.*)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranIntrinsicKeywordKindRegex = new(
        @"\b(?:integer|real|complex|logical|character)\s*\([^)\r\n]*\bkind\s*=\s*(?<type>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranIntrinsicPositionalKindRegex = new(
        @"\b(?:integer|real|complex|logical)\s*\(\s*(?<type>[A-Za-z_]\w*)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranBindingTargetListRegex = new(
        @"=>.*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranBindingTargetRegex = new(
        @"(?:=>|,)\s*(?:(?:[A-Za-z_]\w*)\s*=>\s*)?(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranPointerAssignmentRegex = new(
        @"^\s*(?:[A-Za-z_]\w*(?:\s*%\s*[A-Za-z_]\w*)*)\s*=>\s*(?<name>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranAssociateLineRegex = new(
        @"^\s*associate\s*\((?<list>.+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranAssociateTargetRegex = new(
        @"(?:^|,)\s*[A-Za-z_]\w*\s*=>\s*(?<name>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FortranCallRegex = new(
        @"^\s*call\s+(?:(?:[A-Za-z_]\w*)\s*%\s*)*(?<name>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PascalUsesRegex = new(
        @"^\s*uses\s+(?<list>.+?)(?:;|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalTypeAfterColonRegex = new(
        @":\s*(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PascalClassBaseRegex = new(
        @"=\s*(?:class|interface|object)\s*\((?<bases>[^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalBareCallRegex = new(
        @"^\s*(?<name>[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ObjCMessageRegex = new(
        @"\[\s*(?<receiver>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjCInterfaceBaseRegex = new(
        @"^\s*@(?:interface|implementation)\s+[A-Za-z_]\w+(?:\s*\([^)]+\))?\s*:\s*(?<type>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjCProtocolListRegex = new(
        @"<(?<list>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjCDeclTypeRegex = new(
        @"(?<type>[A-Z]\w*)\s*\*+\s*[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjCSelectorRegex = new(
        @"@selector\s*\(\s*(?<name>[A-Za-z_]\w*:?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HaskellSignatureRegex = new(
        @"^\s*[a-z_]\w*\s*::\s*(?<types>.+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HaskellSpaceCallRegex = new(
        @"^\s*(?<name>[a-z_]\w*)\s+(?=(?:[A-Za-z_(]))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HaskellDefinitionRegex = new(
        @"^\s*(?<name>[a-z_]\w*)\b.*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ElixirImportRegex = new(
        @"^\s*(?:alias|import|require|use)\s+(?<name>[A-Z]\w*(?:\.[A-Z]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ElixirBehaviourRegex = new(
        @"^\s*@(?:behaviour|impl)\s+(?<name>[A-Z]\w*(?:\.[A-Z]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ElixirParenlessCallRegex = new(
        @"(?<![\w])(?<name>[a-z_]\w*[?!]?)\s+(?=(?:[A-Za-z_:@\[""']))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SmalltalkClassDeclarationRegex = new(
        @"^\s*(?:(?:[A-Za-z_]\w*)\s+subclass:|Class\s+named:|Object\s+subclass:)\s*#",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SmalltalkMessageSendRegex = new(
        @"(?<![#\w])(?<receiver>[A-Za-z_]\w*)\s+(?<selector>[a-z]\w*:?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SmalltalkMethodDefinitionRegex = new(
        @">>\s*(?<name>[A-Za-z_]\w*:?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RazorComponentTagRegex = new(
        @"<(?<name>[A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_]\w*)*)(?=[\s>/])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RazorDirectiveTypeRegex = new(
        @"^\s*@(?:inherits|implements|model)\s+(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RazorAttributeTypeRegex = new(
        @"^\s*@attribute\s+\[\s*(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RazorInjectRegex = new(
        @"^\s*@inject\s+(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s+[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RazorEventHandlerRegex = new(
        @"@on[A-Za-z_]\w*\s*=\s*""@?(?<name>[A-Za-z_]\w*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

}
