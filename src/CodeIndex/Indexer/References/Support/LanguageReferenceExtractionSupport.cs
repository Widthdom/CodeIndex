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
    private static readonly Regex FortranCommonLineRegex = new(
        @"^\s*common\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranBlankCommonMemberListRegex = new(
        @"^\s*common\s+(?<list>[^/].*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranNamelistLineRegex = new(
        @"^\s*namelist\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FortranEquivalenceLineRegex = new(
        @"^\s*equivalence\b",
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
    private static readonly Regex FortranProcedureBindingLineRegex = new(
        @"^\s*(?:procedure|generic)\b",
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

    public static void EmitTypePositionReferences(
        string language,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container,
        bool isGoImportBlockLine = false)
    {
        switch (language)
        {
            case "c":
            case "cpp":
                EmitCppTypeReferences(language, preparedLine, originalLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "go":
                EmitGoTypeReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, isGoImportBlockLine);
                break;
            case "dart":
                EmitDartTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "vb":
                EmitVbTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "fortran":
                EmitFortranTypeReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
                break;
            case "pascal":
                EmitPascalTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
                break;
            case "objc":
                EmitObjCTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
                break;
            case "haskell":
                EmitHaskellTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
                break;
            case "elixir":
                EmitElixirTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
                break;
            case "lua":
                LuaReferenceExtractor.EmitTypePositionReferences(originalLine, references, seen, fileId, context, lineNumber, container);
                break;
        }
    }

    public static void EmitAdditionalCallReferences(
        string language,
        string preparedLine,
        string originalLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames)
    {
        switch (language)
        {
            case "fortran":
                EmitFortranCallReferences(preparedLine, addCallLikeReference);
                break;
            case "pascal":
                EmitPascalCallReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "objc":
                EmitObjCMessageReferences(preparedLine, addCallLikeReference, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "haskell":
                EmitHaskellSpaceCallReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "elixir":
                EmitElixirParenlessCallReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "lua":
                LuaReferenceExtractor.EmitAdditionalCallReferences(preparedLine, addCallLikeReference, references, seen, fileId, context, lineNumber, resolveContainerForColumn, definitionNames);
                break;
            case "smalltalk":
                EmitSmalltalkMessageReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "vb":
                EmitVisualBasicCallByNameReferences(originalLine, preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                EmitVisualBasicEscapedCallReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, definitionNames);
                EmitVisualBasicBareCallReferences(preparedLine, addCallLikeReference, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                EmitVisualBasicBareMemberCallReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
        }
    }

    public static void EmitRazorReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames,
        IReadOnlySet<string>? fileDefinitionNames,
        IReadOnlyList<string>? implementedTypeNames)
    {
        if (originalLine.IndexOf('<') >= 0)
        {
            foreach (Match match in RazorComponentTagRegex.Matches(originalLine))
            {
                var group = match.Groups["name"];
                var rawName = group.Value;
                var name = rawName;
                if (definitionNames?.Contains(name) == true)
                    continue;
                var nameIndex = group.Index;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    nameIndex,
                    "call",
                    context,
                    lineNumber,
                    resolveContainerForColumn(nameIndex));
            }
        }

        if (originalLine.IndexOf('@') >= 0)
        {
            if (originalLine.IndexOf("inherits", StringComparison.Ordinal) >= 0
                || originalLine.IndexOf("implements", StringComparison.Ordinal) >= 0
                || originalLine.IndexOf("model", StringComparison.Ordinal) >= 0)
            {
                foreach (var match in EnumerateMatches(RazorDirectiveTypeRegex, originalLine))
                    EmitRazorTypeReference(match);
            }

            if (originalLine.IndexOf("@attribute", StringComparison.Ordinal) >= 0 && originalLine.IndexOf('[') >= 0)
            {
                foreach (var match in EnumerateMatches(RazorAttributeTypeRegex, originalLine))
                    EmitRazorTypeReference(match);
            }

            if (originalLine.IndexOf("@inject", StringComparison.Ordinal) >= 0)
            {
                foreach (var match in EnumerateMatches(RazorInjectRegex, originalLine))
                    EmitRazorTypeReference(match);
            }
        }

        if (originalLine.IndexOf("@on", StringComparison.Ordinal) >= 0 && originalLine.IndexOf('=') >= 0)
        {
            foreach (Match match in RazorEventHandlerRegex.Matches(originalLine))
            {
                var name = match.Groups["name"].Value;
                var nameIndex = match.Groups["name"].Index;
                var container = resolveContainerForColumn(nameIndex);
                var kind = "razor_event_binding";

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    nameIndex,
                    kind,
                    context,
                    lineNumber,
                    container);

                if (fileDefinitionNames?.Contains(name) == true || implementedTypeNames is not { Count: > 0 })
                    continue;

                foreach (var implementedTypeName in implementedTypeNames)
                {
                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        name,
                        nameIndex,
                        "implicit_implementation",
                        context,
                        lineNumber,
                        new SymbolRecord
                        {
                            Kind = "interface",
                            Name = LastQualifiedSegment(implementedTypeName),
                            Line = lineNumber,
                            StartLine = lineNumber,
                            EndLine = lineNumber
                        });
                }
            }
        }

        void EmitRazorTypeReference(Match match)
        {
            var group = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                group.Value,
                group.Index,
                context,
                lineNumber,
                resolveContainerForColumn(group.Index),
                "csharp");
        }
    }

    public static IReadOnlyList<string> ExtractRazorImplementedTypeNames(IReadOnlyList<string> originalLines)
    {
        if (!MayContainRazorImplementsDirective(originalLines))
            return Array.Empty<string>();

        List<string>? result = null;
        foreach (var line in originalLines)
        {
            var match = RazorDirectiveTypeRegex.Match(line);
            if (!match.Success || !line.TrimStart().StartsWith("@implements", StringComparison.Ordinal))
                continue;

            result ??= new List<string>();
            result.Add(match.Groups["type"].Value);
        }

        return result ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private static bool MayContainRazorImplementsDirective(IReadOnlyList<string> originalLines)
    {
        foreach (var line in originalLines)
        {
            if (line.Contains("@implements", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static string[] MaskRazorCommentLines(IReadOnlyList<string> originalLines)
    {
        var result = new string[originalLines.Count];
        var inRazorComment = false;
        var inHtmlComment = false;
        var inCodeBlock = false;
        var codeDepth = 0;
        var inCSharpBlockComment = false;
        var razorControlDepth = 0;
        var inRazorControlBlockComment = false;
        var pendingRazorControlBlock = false;

        for (var lineIndex = 0; lineIndex < originalLines.Count; lineIndex++)
        {
            var line = originalLines[lineIndex];
            var chars = line.ToCharArray();
            var cursor = 0;
            while (cursor < line.Length)
            {
                if (inRazorComment)
                {
                    var close = line.IndexOf("*@", cursor, StringComparison.Ordinal);
                    var end = close < 0 ? line.Length : close + 2;
                    MaskRange(chars, cursor, end);
                    inRazorComment = close < 0;
                    cursor = end;
                    continue;
                }

                if (inHtmlComment)
                {
                    var close = line.IndexOf("-->", cursor, StringComparison.Ordinal);
                    var end = close < 0 ? line.Length : close + 3;
                    MaskRange(chars, cursor, end);
                    inHtmlComment = close < 0;
                    cursor = end;
                    continue;
                }

                if (line.AsSpan(cursor).StartsWith("@*", StringComparison.Ordinal))
                {
                    inRazorComment = true;
                    continue;
                }

                if (line.AsSpan(cursor).StartsWith("<!--", StringComparison.Ordinal))
                {
                    inHtmlComment = true;
                    continue;
                }

                cursor++;
            }

            var codeScanStart = 0;
            if (!inCodeBlock)
            {
                codeScanStart = IndexOfRazorCodeDirective(chars);
                if (codeScanStart >= 0)
                    inCodeBlock = true;
                else
                {
                    codeScanStart = IndexOfRazorExplicitCodeBlock(chars);
                    if (codeScanStart >= 0)
                        inCodeBlock = true;
                }
            }

            if (inCodeBlock)
                MaskCSharpStringsAndCommentsInRazorCode(chars, Math.Max(0, codeScanStart), ref codeDepth, ref inCodeBlock, ref inCSharpBlockComment);
            else
            {
                var controlStart = IndexOfRazorControlDirective(chars);
                if (controlStart >= 0)
                {
                    var delta = MaskRazorControlCodeLine(chars, controlStart, ref inRazorControlBlockComment);
                    razorControlDepth = Math.Max(0, razorControlDepth + delta);
                    pendingRazorControlBlock = delta <= 0;
                }
                else if (IndexOfRazorBareControlContinuation(chars) is var continuationStart && continuationStart >= 0)
                {
                    var delta = MaskRazorControlCodeLine(chars, continuationStart, ref inRazorControlBlockComment);
                    razorControlDepth = Math.Max(0, razorControlDepth + delta);
                    pendingRazorControlBlock = delta <= 0;
                }
                else if (pendingRazorControlBlock && IsRazorCodeLineInsideControl(chars))
                {
                    var delta = MaskRazorControlCodeLine(chars, FirstNonWhitespaceIndex(chars), ref inRazorControlBlockComment);
                    razorControlDepth = Math.Max(0, razorControlDepth + delta);
                    if (delta != 0)
                        pendingRazorControlBlock = false;
                }
                else if (razorControlDepth > 0 && IsRazorCodeLineInsideControl(chars))
                {
                    razorControlDepth = Math.Max(
                        0,
                        razorControlDepth + MaskRazorControlCodeLine(chars, FirstNonWhitespaceIndex(chars), ref inRazorControlBlockComment));
                }
            }

            result[lineIndex] = new string(chars);
        }

        return result;
    }

    private static int IndexOfRazorCodeDirective(char[] chars)
    {
        var line = new string(chars);
        foreach (var directive in RazorCodeDirectives)
        {
            var index = line.IndexOf(directive, StringComparison.Ordinal);
            if (index < 0)
                continue;
            var beforeOk = index == 0 || char.IsWhiteSpace(line[index - 1]);
            var afterIndex = index + directive.Length;
            var afterOk = afterIndex == line.Length || char.IsWhiteSpace(line[afterIndex]) || line[afterIndex] == '{';
            if (beforeOk && afterOk)
                return index;
        }

        return -1;
    }

    private static int IndexOfRazorBareControlContinuation(char[] chars)
    {
        var index = FirstNonWhitespaceIndex(chars);
        if (index < 0)
            return -1;

        var line = new string(chars);
        foreach (var keyword in RazorBareControlContinuationKeywords)
        {
            if (line.AsSpan(index).StartsWith(keyword, StringComparison.Ordinal)
                && (index + keyword.Length == line.Length || !IsSimpleIdentifierPart(line[index + keyword.Length])))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfRazorExplicitCodeBlock(char[] chars)
    {
        var line = new string(chars);
        var index = line.IndexOf("@{", StringComparison.Ordinal);
        return index >= 0 ? index : -1;
    }

    private static int IndexOfRazorControlDirective(char[] chars)
    {
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] != '@')
                continue;

            var beforeOk = index == 0 || char.IsWhiteSpace(chars[index - 1]) || chars[index - 1] == '}';
            if (!beforeOk)
                continue;

            foreach (var directive in RazorControlDirectives)
            {
                if (!CharsStartWith(chars, index, directive))
                    continue;

                var afterIndex = index + directive.Length;
                var afterOk = afterIndex == chars.Length || !IsSimpleIdentifierPart(chars[afterIndex]);
                if (beforeOk && afterOk)
                    return index;
            }
        }

        return -1;
    }

    private static bool CharsStartWith(char[] chars, int index, string value)
    {
        if (index + value.Length > chars.Length)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (chars[index + i] != value[i])
                return false;
        }

        return true;
    }

    private static bool IsRazorCodeLineInsideControl(char[] chars)
    {
        var index = FirstNonWhitespaceIndex(chars);
        if (index < 0)
            return false;

        return !LooksLikeRazorMarkupStart(new string(chars), index);
    }

    private static int FirstNonWhitespaceIndex(char[] chars)
    {
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsWhiteSpace(chars[i]))
                return i;
        }

        return -1;
    }

    private static bool LooksLikeRazorMarkupStart(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length)
            return false;
        if (line[index] == '<')
            return true;

        return line[index] == '@'
            && index + 1 < line.Length
            && line[index + 1] is ':' or '<';
    }

    private static int MaskRazorControlCodeLine(char[] chars, int start, ref bool inBlockComment)
    {
        var line = new string(chars);
        var firstOpenBrace = -1;
        var delta = CountCSharpBraceDelta(line, start, ref inBlockComment, ref firstOpenBrace);
        if (firstOpenBrace >= 0 && LooksLikeRazorMarkupStart(line, firstOpenBrace + 1))
        {
            MaskRange(chars, start, firstOpenBrace + 1);
            return delta;
        }

        MaskRange(chars, start, chars.Length);
        return delta;
    }

    private static int CountCSharpBraceDelta(string line, int start, ref bool inBlockComment, ref int firstOpenBrace)
    {
        var delta = 0;
        for (var cursor = Math.Max(0, start); cursor < line.Length; cursor++)
        {
            if (inBlockComment)
            {
                if (line[cursor] == '*' && cursor + 1 < line.Length && line[cursor + 1] == '/')
                {
                    inBlockComment = false;
                    cursor++;
                }

                continue;
            }

            if (line[cursor] == '/' && cursor + 1 < line.Length && line[cursor + 1] == '/')
                break;

            if (line[cursor] == '/' && cursor + 1 < line.Length && line[cursor + 1] == '*')
            {
                inBlockComment = true;
                cursor++;
                continue;
            }

            if (line[cursor] is '"' or '\'')
            {
                var quote = line[cursor++];
                while (cursor < line.Length)
                {
                    if (line[cursor] == '\\' && cursor + 1 < line.Length)
                    {
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == quote)
                        break;
                    cursor++;
                }

                continue;
            }

            if (line[cursor] == '{')
            {
                if (firstOpenBrace < 0)
                    firstOpenBrace = cursor;
                delta++;
            }
            else if (line[cursor] == '}')
            {
                delta--;
            }
        }

        return delta;
    }

    private static void MaskCSharpStringsAndCommentsInRazorCode(
        char[] chars,
        int start,
        ref int codeDepth,
        ref bool inCodeBlock,
        ref bool inBlockComment)
    {
        for (var cursor = start; cursor < chars.Length; cursor++)
        {
            if (inBlockComment)
            {
                if (chars[cursor] == '*' && cursor + 1 < chars.Length && chars[cursor + 1] == '/')
                {
                    chars[cursor++] = ' ';
                    chars[cursor] = ' ';
                    inBlockComment = false;
                    continue;
                }

                chars[cursor] = ' ';
                continue;
            }

            if (chars[cursor] == '/' && cursor + 1 < chars.Length && chars[cursor + 1] == '/')
            {
                MaskRange(chars, cursor, chars.Length);
                break;
            }

            if (chars[cursor] == '/' && cursor + 1 < chars.Length && chars[cursor + 1] == '*')
            {
                chars[cursor++] = ' ';
                chars[cursor] = ' ';
                inBlockComment = true;
                continue;
            }

            if (chars[cursor] is '"' or '\'')
            {
                var quote = chars[cursor];
                chars[cursor++] = ' ';
                while (cursor < chars.Length)
                {
                    if (chars[cursor] == '\\' && cursor + 1 < chars.Length)
                    {
                        chars[cursor++] = ' ';
                        chars[cursor] = ' ';
                        cursor++;
                        continue;
                    }

                    var closes = chars[cursor] == quote;
                    chars[cursor++] = ' ';
                    if (closes)
                        break;
                }
                cursor--;
                continue;
            }

            if (chars[cursor] == '{')
            {
                codeDepth++;
                chars[cursor] = ' ';
                continue;
            }

            if (chars[cursor] == '}' && codeDepth > 0)
            {
                codeDepth--;
                chars[cursor] = ' ';
                if (codeDepth == 0)
                    inCodeBlock = false;
                continue;
            }

            chars[cursor] = ' ';
        }
    }

    private static void EmitCppTypeReferences(
        string language,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var hasCppIncludeMarker = !string.IsNullOrWhiteSpace(preparedLine)
            && (originalLine.IndexOf('#') >= 0
                || originalLine.IndexOf("import", StringComparison.Ordinal) >= 0
                || originalLine.IndexOf("include", StringComparison.Ordinal) >= 0);
        var includeMatch = hasCppIncludeMarker
            ? CppIncludeRegex.Match(originalLine)
            : Match.Empty;
        if (includeMatch.Success)
        {
            var group = includeMatch.Groups["name"];
            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "type_reference", context, lineNumber, resolveContainerForColumn(group.Index));
        }

        var baseMatch = preparedLine.IndexOf(':') >= 0
            && (preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0)
            ? CppBaseListRegex.Match(preparedLine)
            : Match.Empty;
        if (baseMatch.Success)
        {
            var group = baseMatch.Groups["bases"];
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(group.Value))
            {
                var segment = group.Value.Substring(segmentStart, segmentLength);
                var expression = StripCppAccessPrefix(segment);
                if (expression.Length == 0)
                    continue;

                var absoluteStart = group.Index + segmentStart + segment.IndexOf(expression, StringComparison.Ordinal);
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, expression, absoluteStart, context, lineNumber, resolveContainerForColumn(absoluteStart), language);
            }
        }

        if (preparedLine.IndexOf("new", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in CppNewTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (preparedLine.IndexOf("_cast", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in CppNamedCastTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (preparedLine.IndexOf('(') >= 0)
        {
            foreach (Match match in CppCStyleCastTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (language == "c")
        {
            var hasCParen = preparedLine.IndexOf('(') >= 0;
            var hasCTypedefTypeMarker = preparedLine.IndexOf("_t", StringComparison.Ordinal) >= 0;
            var hasCTaggedTypeMarker = preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("union", StringComparison.Ordinal) >= 0;
            if (hasCParen && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefCastTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCSizeofMarker = hasCParen
                && preparedLine.IndexOf("sizeof", StringComparison.Ordinal) >= 0;
            if (hasCSizeofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefSizeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCSizeofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedSizeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCAlignofMarker = hasCParen
                && (preparedLine.IndexOf("alignof", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("_Alignof", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("__alignof", StringComparison.Ordinal) >= 0);
            if (hasCAlignofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefAlignofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCAlignofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedAlignofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCDeclarationTerminator = preparedLine.IndexOf('=') >= 0
                || preparedLine.IndexOf(',') >= 0
                || preparedLine.IndexOf(';') >= 0
                || preparedLine.IndexOf('[') >= 0;
            if (hasCDeclarationTerminator && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCDeclarationTerminator && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCParen && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefFunctionReturnTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCParen && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedFunctionReturnTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCParameterDelimiter = hasCParen || preparedLine.IndexOf(',') >= 0;
            if (hasCParameterDelimiter && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefParameterTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCParameterDelimiter && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedParameterTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCCompoundLiteralMarkers = hasCParen && preparedLine.IndexOf('{') >= 0;
            if (hasCCompoundLiteralMarkers && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefCompoundLiteralTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCCompoundLiteralMarkers && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedCompoundLiteralTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCTypeofMarker = hasCParen
                && preparedLine.IndexOf("typeof", StringComparison.Ordinal) >= 0;
            if (hasCTypeofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefTypeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCTypeofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedTypeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCTypeofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefTypeofUnqualTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCTypeofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedTypeofUnqualTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCBuiltinTypesCompatibleMarker = hasCParen
                && preparedLine.IndexOf("__builtin_types_compatible_p", StringComparison.Ordinal) >= 0;
            if (hasCBuiltinTypesCompatibleMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefBuiltinTypesCompatibleFirstTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCBuiltinTypesCompatibleMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefBuiltinTypesCompatibleSecondTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCBuiltinTypesCompatibleMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedBuiltinTypesCompatibleFirstTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCBuiltinTypesCompatibleMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedBuiltinTypesCompatibleSecondTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCGenericAssociationMarker = preparedLine.IndexOf(':') >= 0
                && (preparedLine.IndexOf("_Generic", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf(',') >= 0);
            if (hasCGenericAssociationMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefGenericAssociationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCGenericAssociationMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedGenericAssociationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCAtomicMarker = hasCParen
                && preparedLine.IndexOf("_Atomic", StringComparison.Ordinal) >= 0;
            if (hasCAtomicMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefAtomicTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCAtomicMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedAtomicTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCAlignasMarker = hasCParen
                && (preparedLine.IndexOf("alignas", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("_Alignas", StringComparison.Ordinal) >= 0);
            if (hasCAlignasMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefAlignasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCAlignasMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedAlignasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCFunctionPointerMarker = hasCParen
                && preparedLine.IndexOf('*') >= 0;
            var hasCFunctionPointerAliasMarker = hasCFunctionPointerMarker
                && preparedLine.IndexOf("typedef", StringComparison.Ordinal) >= 0;
            if (hasCFunctionPointerAliasMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefFunctionPointerAliasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCFunctionPointerAliasMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedFunctionPointerAliasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCFunctionPointerMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefFunctionPointerDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCFunctionPointerMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedFunctionPointerDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCPointerArrayMarker = hasCFunctionPointerMarker
                && preparedLine.IndexOf('[') >= 0;
            if (hasCPointerArrayMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefPointerArrayDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCPointerArrayMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedPointerArrayDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCOffsetofMarker = hasCParen
                && preparedLine.IndexOf(',') >= 0
                && (preparedLine.IndexOf("offsetof", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("__builtin_offsetof", StringComparison.Ordinal) >= 0);
            if (hasCOffsetofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefOffsetofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCOffsetofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedOffsetofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCVaArgMarker = hasCParen
                && preparedLine.IndexOf(',') >= 0
                && (preparedLine.IndexOf("va_arg", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("__builtin_va_arg", StringComparison.Ordinal) >= 0);
            if (hasCVaArgMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefVaArgTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCVaArgMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedVaArgTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCVaArgMarker)
                EmitCVaArgTypeOperandReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, language);
        }

        var hasCppParen = preparedLine.IndexOf('(') >= 0;
        var hasCppTypeOperandOperatorMarker = hasCppParen
            && (preparedLine.IndexOf("sizeof", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("alignof", StringComparison.Ordinal) >= 0);
        if (hasCppTypeOperandOperatorMarker)
        {
            foreach (Match match in CppTypeOperandOperatorRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTypeIdMarker = hasCppParen
            && preparedLine.IndexOf("typeid", StringComparison.Ordinal) >= 0;
        if (hasCppTypeIdMarker)
        {
            foreach (Match match in CppTypeIdRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppDecltypeBraceMarker = hasCppParen
            && preparedLine.IndexOf('{') >= 0
            && preparedLine.IndexOf("decltype", StringComparison.Ordinal) >= 0;
        if (hasCppDecltypeBraceMarker)
        {
            foreach (Match match in CppDecltypeBraceConstructionRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTemplateOpen = preparedLine.IndexOf('<') >= 0;
        var hasCppFactoryTemplateMarker = hasCppParen
            && hasCppTemplateOpen
            && preparedLine.IndexOf("make_", StringComparison.Ordinal) >= 0;
        if (hasCppFactoryTemplateMarker)
        {
            foreach (Match match in CppFactoryTemplateArgumentRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTypeTraitTemplateMarker = hasCppTemplateOpen
            && preparedLine.IndexOf("is_", StringComparison.Ordinal) >= 0;
        if (hasCppTypeTraitTemplateMarker)
        {
            foreach (Match match in CppTypeTraitTemplateArgumentRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppBrace = preparedLine.IndexOf('{') >= 0;
        var hasCppBraceConstructionMarker = hasCppBrace
            && (preparedLine.IndexOf("return", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("throw", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf('=') >= 0);
        if (hasCppBraceConstructionMarker)
        {
            foreach (Match match in CppBraceConstructionRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppScopeSeparator = preparedLine.IndexOf("::", StringComparison.Ordinal) >= 0;
        var hasCppQualifiedTemplateBraceMarker = hasCppBraceConstructionMarker
            && hasCppTemplateOpen
            && hasCppScopeSeparator;
        if (hasCppQualifiedTemplateBraceMarker)
        {
            foreach (Match match in CppQualifiedTemplateBraceConstructionRegex.Matches(preparedLine))
            {
                var group = match.Groups["args"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppUsingAliasMarker = preparedLine.IndexOf("using", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf('=') >= 0
            && preparedLine.IndexOf(';') >= 0;
        if (hasCppUsingAliasMarker)
        {
            foreach (Match match in CppUsingAliasTargetRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTypedefAliasMarker = !hasCppParen
            && preparedLine.IndexOf("typedef", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf(';') >= 0;
        if (hasCppTypedefAliasMarker)
        {
            foreach (Match match in CppTypedefAliasTargetRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppExplicitTemplateInstantiationMarker = preparedLine.IndexOf("template", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf(';') >= 0
            && (preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0);
        if (hasCppExplicitTemplateInstantiationMarker)
        {
            foreach (Match match in CppExplicitTemplateInstantiationRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTemplateClose = preparedLine.IndexOf('>') >= 0;
        var hasCppTemplateIdDeclarationMarker = hasCppTemplateOpen
            && hasCppTemplateClose
            && (preparedLine.IndexOf('=') >= 0
                || preparedLine.IndexOf(';') >= 0
                || preparedLine.IndexOf('{') >= 0
                || preparedLine.IndexOf(',') >= 0
                || preparedLine.IndexOf(')') >= 0
                || preparedLine.IndexOf('[') >= 0);
        if (hasCppTemplateIdDeclarationMarker)
        {
            foreach (Match match in CppTemplateIdDeclarationRegex.Matches(preparedLine))
            {
                if (IsCppTemplateDeclarationOrSpecializationLine(preparedLine, match.Index))
                    continue;

                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, match.Groups["args"].Value, match.Groups["args"].Index, context, lineNumber, resolveContainerForColumn(match.Groups["args"].Index), language);
            }
        }

        var hasCppTemplateParameterDefaultMarker = preparedLine.IndexOf('=') >= 0
            && (preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0);
        if (hasCppTemplateParameterDefaultMarker)
        {
            foreach (Match match in CppTemplateParameterDefaultTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (hasCppScopeSeparator)
        {
            foreach (Match match in CppQualifiedMemberReceiverRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppPointerToMemberMarker = hasCppScopeSeparator
            && preparedLine.IndexOf('*') >= 0;
        if (hasCppPointerToMemberMarker)
        {
            foreach (Match match in CppPointerToMemberTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTrailingReturnMarker = preparedLine.IndexOf(')') >= 0
            && preparedLine.IndexOf("->", StringComparison.Ordinal) >= 0;
        if (hasCppTrailingReturnMarker)
        {
            foreach (Match match in CppTrailingReturnTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (preparedLine.Contains("requires", StringComparison.Ordinal) || preparedLine.Contains("concept", StringComparison.Ordinal))
        {
            var hasCppRequiresConceptTypeMarker = hasCppTemplateOpen
                && preparedLine.IndexOf("requires", StringComparison.Ordinal) >= 0;
            if (hasCppRequiresConceptTypeMarker)
            {
                foreach (Match match in CppRequiresConceptTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCppRequiresConceptTypeMarker && hasCppParen)
            {
                foreach (Match match in CppParenthesizedRequiresConceptTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCppQualifiedRequiresConceptMarker = hasCppRequiresConceptTypeMarker
                && hasCppScopeSeparator;
            if (hasCppQualifiedRequiresConceptMarker)
            {
                foreach (Match match in CppQualifiedRequiresConceptConstraintRegex.Matches(preparedLine))
                {
                    var conceptGroup = match.Groups["concept"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, conceptGroup.Value, conceptGroup.Index, context, lineNumber, resolveContainerForColumn(conceptGroup.Index), language);

                    var argsGroup = match.Groups["args"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, argsGroup.Value, argsGroup.Index, context, lineNumber, resolveContainerForColumn(argsGroup.Index), language);
                }
            }

            var hasCppConceptExpressionMarker = hasCppTemplateOpen
                && (preparedLine.IndexOf('=') >= 0
                    || preparedLine.IndexOf("&&", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("||", StringComparison.Ordinal) >= 0);
            if (hasCppConceptExpressionMarker)
            {
                foreach (Match match in CppConceptExpressionTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }
        }

        var hasCppCompoundRequirementConceptMarker = hasCppTemplateOpen
            && hasCppTemplateClose
            && preparedLine.IndexOf("->", StringComparison.Ordinal) >= 0;
        if (hasCppCompoundRequirementConceptMarker)
        {
            foreach (Match match in CppCompoundRequirementConceptRegex.Matches(preparedLine))
            {
                var conceptGroup = match.Groups["concept"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, conceptGroup.Value, conceptGroup.Index, context, lineNumber, resolveContainerForColumn(conceptGroup.Index), language);

                var argsGroup = match.Groups["args"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, argsGroup.Value, argsGroup.Index, context, lineNumber, resolveContainerForColumn(argsGroup.Index), language);
            }
        }

        var hasCppFriendTypeMarker = preparedLine.IndexOf("friend", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf(';') >= 0
            && (preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("union", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0);
        if (hasCppFriendTypeMarker)
        {
            foreach (Match match in CppFriendTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppDynamicExceptionSpecMarker = hasCppParen
            && preparedLine.IndexOf("throw", StringComparison.Ordinal) >= 0;
        if (hasCppDynamicExceptionSpecMarker)
        {
            foreach (Match match in CppDynamicExceptionSpecRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppDeclarationTerminator = preparedLine.IndexOf(',') >= 0
            || preparedLine.IndexOf(';') >= 0
            || preparedLine.IndexOf(')') >= 0
            || preparedLine.IndexOf('=') >= 0;
        var hasCppDeclarationTypeMarker = hasCppDeclarationTerminator
            && (ContainsAsciiUppercase(preparedLine)
                || hasCppScopeSeparator
                || hasCppTemplateOpen
                || preparedLine.IndexOf("const", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("volatile", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("static", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("inline", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("constexpr", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0);
        if (hasCppDeclarationTypeMarker)
        {
            foreach (Match match in CppDeclarationTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var expression = StripCppAccessPrefix(group.Value);
                if (expression.Length == 0)
                    continue;

                var start = group.Index + group.Value.IndexOf(expression, StringComparison.Ordinal);
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, expression, start, context, lineNumber, resolveContainerForColumn(start), language);
            }
        }
    }

    private static void EmitCVaArgTypeOperandReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        string language)
    {
        foreach (var functionName in CVaArgFunctionNames)
        {
            var searchStart = 0;
            while (searchStart < line.Length)
            {
                var functionIndex = line.IndexOf(functionName, searchStart, StringComparison.Ordinal);
                if (functionIndex < 0)
                    break;

                searchStart = functionIndex + functionName.Length;
                if (!IsIdentifierAt(line, functionIndex, functionName))
                    continue;

                var open = SkipWhitespace(line, functionIndex + functionName.Length);
                if (open >= line.Length || line[open] != '(')
                    continue;

                var close = ReferenceExtractor.FindMatchingChar(line, open, '(', ')');
                if (close < 0)
                    continue;

                var argumentList = line.Substring(open + 1, close - open - 1);
                var arguments = SplitTopLevelCArgumentSpans(argumentList);
                if (arguments.Count < 2)
                    continue;

                var typeArgument = arguments[1];
                if (typeArgument.Length <= 0)
                    continue;

                var rawType = argumentList.Substring(typeArgument.Start, typeArgument.Length);
                var expression = rawType.Trim();
                if (expression.Length == 0 || !LooksLikeCVaArgTypeOperand(expression))
                    continue;

                var trimStart = rawType.IndexOf(expression, StringComparison.Ordinal);
                var absoluteStart = open + 1 + typeArgument.Start + Math.Max(0, trimStart);
                ReferenceExtractor.AddTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    expression,
                    absoluteStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart),
                    language);
            }
        }
    }

    private static List<(int Start, int Length)> SplitTopLevelCArgumentSpans(string text)
    {
        var spans = new List<(int Start, int Length)>();
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ',' when parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    private static bool LooksLikeCVaArgTypeOperand(string expression)
    {
        var cursor = SkipLeadingCTypeQualifiers(expression, 0);
        if (cursor >= expression.Length)
            return false;

        foreach (var keyword in CTypeSpecifierKeywords)
        {
            if (StartsWithKeyword(expression, cursor, keyword))
            {
                cursor = SkipWhitespace(expression, cursor + keyword.Length);
                return cursor < expression.Length && IsIdentifierStart(expression[cursor]);
            }
        }

        if (!IsIdentifierStart(expression[cursor]))
            return false;

        var nameStart = cursor;
        cursor++;
        while (cursor < expression.Length && IsSimpleIdentifierPart(expression[cursor]))
            cursor++;

        return expression.AsSpan(nameStart, cursor - nameStart).EndsWith("_t", StringComparison.Ordinal);
    }

    private static int SkipLeadingCTypeQualifiers(string expression, int cursor)
    {
        while (cursor < expression.Length)
        {
            cursor = SkipWhitespace(expression, cursor);
            var next = cursor;
            if (StartsWithKeyword(expression, cursor, "const"))
                next += "const".Length;
            else if (StartsWithKeyword(expression, cursor, "volatile"))
                next += "volatile".Length;
            else if (StartsWithKeyword(expression, cursor, "restrict"))
                next += "restrict".Length;
            else if (StartsWithKeyword(expression, cursor, "_Atomic"))
                next += "_Atomic".Length;
            else
                return cursor;

            cursor = next;
        }

        return cursor;
    }

    private static bool StartsWithKeyword(string line, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > line.Length)
            return false;
        if (!line.AsSpan(index, keyword.Length).SequenceEqual(keyword))
            return false;

        var beforeOk = index == 0 || !IsSimpleIdentifierPart(line[index - 1]);
        var after = index + keyword.Length;
        var afterOk = after >= line.Length || !IsSimpleIdentifierPart(line[after]);
        return beforeOk && afterOk;
    }

    private static void EmitDartTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        TypedLanguageReferenceExtractor.EmitKeywordFollowingTypeReferences(
            preparedLine,
            ["extends", "with", "implements", "on", "as", "is"],
            "dart",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(preparedLine, 0, preparedLine.Length, "dart", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitColonVariableTypeReferences(preparedLine, ["final", "var", "late", "const"], "dart", references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        var hasDartDeclarationTerminator = preparedLine.IndexOf('=') >= 0
            || preparedLine.IndexOf(';') >= 0;
        var hasDartUppercaseTypeMarker = ContainsAsciiUppercase(preparedLine);
        if (hasDartDeclarationTerminator && hasDartUppercaseTypeMarker)
        {
            foreach (Match match in DartVariableTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "dart");
            }
        }

        var hasDartParen = preparedLine.IndexOf('(') >= 0;
        var signatureMatch = hasDartParen && hasDartUppercaseTypeMarker
            ? DartFunctionSignatureRegex.Match(preparedLine)
            : Match.Empty;
        if (signatureMatch.Success)
        {
            var returnGroup = signatureMatch.Groups["return"];
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, returnGroup.Value, returnGroup.Index, context, lineNumber, resolveContainerForColumn(returnGroup.Index), "dart");

            var parametersGroup = signatureMatch.Groups["params"];
            foreach (Match parameterMatch in DartParameterTypeRegex.Matches(parametersGroup.Value))
            {
                var typeGroup = parameterMatch.Groups["type"];
                var absoluteIndex = parametersGroup.Index + typeGroup.Index;
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, typeGroup.Value, absoluteIndex, context, lineNumber, resolveContainerForColumn(absoluteIndex), "dart");
            }
        }

        var hasDartCtorMarker = hasDartParen
            && hasDartUppercaseTypeMarker
            && (preparedLine.IndexOf("new", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("const", StringComparison.Ordinal) >= 0);
        if (hasDartCtorMarker)
        {
            foreach (Match match in DartCtorRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "instantiate", context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }
    }

    private static void EmitVbTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var hasVbTypeKeywordMarker = preparedLine.IndexOf("As", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("New", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("Inherits", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("Implements", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbTypeKeywordMarker)
        {
            foreach (Match match in VbTypeKeywordRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbGenericArgumentListMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("Of", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbGenericArgumentListMarker)
        {
            foreach (Match match in VbGenericArgumentListRegex.Matches(preparedLine))
            {
                if (VbGenericDeclarationOwnerRegex.IsMatch(preparedLine[..match.Index]))
                {
                    var constraintGroup = match.Groups["list"];
                    EmitVbGenericConstraintReferences(constraintGroup.Value, constraintGroup.Index, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                    continue;
                }

                var group = match.Groups["list"];
                EmitCommaSeparatedNames(group.Value, group.Index, "vb", references, seen, fileId, context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }

        var hasVbNewMarker = preparedLine.IndexOf("New", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbNewMarker)
        {
            foreach (Match match in VbNewTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                if (string.Equals(name, "With", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "instantiate", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbImplementsMarker = preparedLine.IndexOf("Implements", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbImplementsMarker)
        {
            foreach (Match match in VbImplementsListRegex.Matches(preparedLine))
            {
                var group = match.Groups["list"];
                EmitCommaSeparatedNames(group.Value, group.Index, "vb", references, seen, fileId, context, lineNumber, resolveContainerForColumn(group.Index));
                if (IsVisualBasicMemberImplementsClause(preparedLine, match.Index))
                    EmitVisualBasicImplementsOwnerReferences(group.Value, group.Index, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            }
        }

        var hasVbImportsMarker = preparedLine.IndexOf("Imports", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbImportsMarker)
        {
            var importsMatch = VbImportsListRegex.Match(preparedLine);
            if (importsMatch.Success)
            {
                var group = importsMatch.Groups["list"];
                EmitCommaSeparatedNames(group.Value, group.Index, "vb", references, seen, fileId, context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }

        var hasVbCastTypeMarker = preparedLine.Contains('(')
            && preparedLine.Contains(',')
            && (preparedLine.IndexOf("Cast", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("CType", StringComparison.OrdinalIgnoreCase) >= 0);
        if (hasVbCastTypeMarker)
        {
            foreach (Match match in VbCastTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbGetTypeMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("GetType", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbGetTypeMarker)
        {
            foreach (Match match in VbGetTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbTypeOfMarker = preparedLine.IndexOf("TypeOf", StringComparison.OrdinalIgnoreCase) >= 0
            && preparedLine.IndexOf("Is", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbTypeOfMarker)
        {
            foreach (Match match in VbTypeOfRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbNameOfMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("NameOf", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbNameOfMarker)
        {
            foreach (Match match in VbNameOfRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var rawNameIndex = group.Index + Math.Max(0, nameOffset);
                var nameIndex = rawName.StartsWith('[') ? rawNameIndex + 1 : rawNameIndex;
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "type_reference", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbGetXmlNamespaceMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("GetXmlNamespace", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbGetXmlNamespaceMarker)
        {
            foreach (Match match in VbGetXmlNamespaceRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "type_reference", context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }

        var hasVbAddressOfMarker = preparedLine.IndexOf("AddressOf", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbAddressOfMarker)
        {
            foreach (Match match in VbAddressOfRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var handlesIndex = preparedLine.IndexOf("Handles", StringComparison.OrdinalIgnoreCase);
        if (handlesIndex >= 0)
        {
            var handlesText = preparedLine[handlesIndex..];
            foreach (Match match in VbHandlesTargetRegex.Matches(handlesText))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = handlesIndex + group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "subscribe", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbAddHandlerMarker = preparedLine.IndexOf("AddHandler", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbAddHandlerMarker)
        {
            foreach (Match match in VbAddHandlerRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "subscribe", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbRemoveHandlerMarker = preparedLine.IndexOf("RemoveHandler", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbRemoveHandlerMarker)
        {
            foreach (Match match in VbRemoveHandlerRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "unsubscribe", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbRaiseEventMarker = preparedLine.IndexOf("RaiseEvent", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbRaiseEventMarker)
        {
            foreach (Match match in VbRaiseEventRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }
    }

    private static void EmitVisualBasicEscapedCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames)
    {
        if (!preparedLine.Contains('(') || !preparedLine.Contains('['))
            return;

        foreach (Match match in VbCallRegex.Matches(preparedLine))
        {
            var group = match.Groups["name"];
            if (!group.Value.Contains('['))
                continue;

            var rawName = LastQualifiedSegment(group.Value);
            var name = NormalizeVbIdentifierSegment(rawName);
            if (ShouldSkipVisualBasicEscapedCall(preparedLine, group.Index, name, definitionNames))
                continue;

            var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
            var rawNameIndex = group.Index + Math.Max(0, nameOffset);
            var nameIndex = rawName.StartsWith('[') ? rawNameIndex + 1 : rawNameIndex;
            ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
        }
    }

    private static void EmitVisualBasicBareCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var firstNonWhitespace = FirstNonWhitespaceIndex(preparedLine);
        if (firstNonWhitespace < 0 || !CanStartVisualBasicIdentifierPattern(preparedLine[firstNonWhitespace]))
            return;

        var match = VbBareCallRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var group = match.Groups["name"];
        var tail = match.Groups["tail"].Value.TrimStart();
        if (ShouldSkipVisualBasicBareCall(group.Value, tail))
            return;

        var rawName = LastQualifiedSegment(group.Value);
        var name = NormalizeVbIdentifierSegment(rawName);
        var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
        var rawNameIndex = group.Index + Math.Max(0, nameOffset);
        var nameIndex = rawName.StartsWith('[') ? rawNameIndex + 1 : rawNameIndex;
        if (rawName.StartsWith('['))
            ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
        else
            addCallLikeReference(name, nameIndex);
    }

    private static void EmitVisualBasicCallByNameReferences(
        string originalLine,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!originalLine.Contains('(')
            || !originalLine.Contains(',')
            || !originalLine.Contains('"')
            || originalLine.IndexOf("CallByName", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        foreach (Match match in VbCallByNameRegex.Matches(originalLine))
        {
            if (match.Index >= preparedLine.Length || char.IsWhiteSpace(preparedLine[match.Index]))
                continue;

            var group = match.Groups["name"];
            var name = group.Value.Trim();
            if (!IsSimpleVisualBasicIdentifier(name))
                continue;

            ReferenceExtractor.AddReference(references, seen, fileId, name, group.Index, "call", context, lineNumber, resolveContainerForColumn(group.Index));
        }
    }

    private static bool IsSimpleVisualBasicIdentifier(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (!IsSimpleIdentifierPart(value[i]))
                return false;
        }

        return true;
    }

    private static int FirstNonWhitespaceIndex(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
                return i;
        }

        return -1;
    }

    private static bool StartsWithKeywordIgnoringLeadingWhitespace(string value, string keyword)
    {
        var start = FirstNonWhitespaceIndex(value);
        if (start < 0 || value.Length - start < keyword.Length)
            return false;

        if (!value.AsSpan(start, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var boundary = start + keyword.Length;
        return boundary >= value.Length || !IsSimpleIdentifierPart(value[boundary]);
    }

    private static bool CanStartVisualBasicIdentifierPattern(char value) =>
        value == '['
        || CanStartAsciiIdentifierPattern(value);

    private static bool CanStartFortranIdentifierPattern(char value) =>
        CanStartAsciiIdentifierPattern(value);

    private static bool CanStartAsciiIdentifierPattern(char value) =>
        value == '_'
        || value is >= 'A' and <= 'Z'
        || value is >= 'a' and <= 'z';

    private static bool ShouldSkipVisualBasicBareCall(string rawName, string tail)
    {
        if (tail.StartsWith('(') || tail.StartsWith('=') || tail.StartsWith(':'))
            return true;
        if (tail.StartsWith("As ", StringComparison.OrdinalIgnoreCase))
            return true;

        var firstSegment = rawName;
        var dotIndex = rawName.IndexOf('.');
        if (dotIndex >= 0)
            firstSegment = rawName[..dotIndex];
        firstSegment = NormalizeVbIdentifierSegment(firstSegment);

        return IsVisualBasicBareCallStatementHead(firstSegment);
    }

    private static bool IsVisualBasicBareCallStatementHead(string name) =>
        name.Equals("Public", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Private", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Protected", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Friend", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Shared", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Overrides", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Overridable", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NotOverridable", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MustOverride", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Overloads", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Shadows", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Async", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Iterator", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Partial", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Declare", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Dim", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ReDim", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Const", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Let", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Loop", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ElseIf", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Finally", StringComparison.OrdinalIgnoreCase);

    private static void EmitVisualBasicBareMemberCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var firstNonWhitespace = FirstNonWhitespaceIndex(preparedLine);
        if (firstNonWhitespace < 0 || preparedLine[firstNonWhitespace] != '.')
            return;

        var match = VbBareMemberCallRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var group = match.Groups["name"];
        var tail = match.Groups["tail"].Value.TrimStart();
        if (tail.StartsWith('(') || tail.StartsWith('=') || tail.StartsWith(':') || tail.StartsWith("As ", StringComparison.OrdinalIgnoreCase))
            return;

        var rawName = group.Value;
        var name = NormalizeVbIdentifierSegment(rawName);
        var nameIndex = rawName.StartsWith('[') ? group.Index + 1 : group.Index;
        ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
    }

    private static bool IsVisualBasicMemberImplementsClause(string line, int implementsIndex)
    {
        var head = line[..implementsIndex];
        return head.Contains(')')
            || head.Contains(" Property ", StringComparison.OrdinalIgnoreCase)
            || head.TrimStart().StartsWith("Property ", StringComparison.OrdinalIgnoreCase)
            || head.Contains(" Event ", StringComparison.OrdinalIgnoreCase)
            || head.TrimStart().StartsWith("Event ", StringComparison.OrdinalIgnoreCase);
    }

    private static void EmitVisualBasicImplementsOwnerReferences(
        string list,
        int listStart,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
                continue;

            var dotIndex = LastVisualBasicQualifierDot(trimmed);
            if (dotIndex <= 0)
                continue;

            var owner = trimmed[..dotIndex].Trim();
            if (owner.Length == 0)
                continue;

            var ownerOffset = segment.IndexOf(owner, StringComparison.Ordinal);
            var ownerStart = listStart + segmentStart + Math.Max(0, ownerOffset);
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, owner, ownerStart, context, lineNumber, resolveContainerForColumn(ownerStart), "vb");
        }
    }

    private static int LastVisualBasicQualifierDot(string value)
    {
        var inEscapedIdentifier = false;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] == ']')
            {
                inEscapedIdentifier = true;
                continue;
            }

            if (value[i] == '[')
            {
                inEscapedIdentifier = false;
                continue;
            }

            if (value[i] == '.' && !inEscapedIdentifier)
                return i;
        }

        return -1;
    }

    private static bool ShouldSkipVisualBasicEscapedCall(
        string line,
        int nameIndex,
        string name,
        IReadOnlySet<string>? definitionNames)
    {
        var previous = GetPreviousSimpleWord(line, nameIndex);
        if (previous.Length == 0)
            return false;

        if (string.Equals(previous, "New", StringComparison.OrdinalIgnoreCase)
            || string.Equals(previous, "RaiseEvent", StringComparison.OrdinalIgnoreCase))
            return true;

        if (definitionNames?.Contains(name) != true)
            return false;

        return previous.Equals("Sub", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Function", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Property", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Event", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Delegate", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Class", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Structure", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Interface", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Enum", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPreviousSimpleWord(string line, int index)
    {
        var cursor = index - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;
        if (cursor < 0)
            return string.Empty;

        var end = cursor + 1;
        while (cursor >= 0 && IsSimpleIdentifierPart(line[cursor]))
            cursor--;

        return line[(cursor + 1)..end];
    }

    private static void EmitFortranTypeReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        var hasFortranUseMarker = StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "use");
        if (hasFortranUseMarker)
        {
            foreach (Match match in FortranUseRegex.Matches(preparedLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "type_reference", context, lineNumber, container);

            var useOnlyMatch = FortranUseOnlyRegex.Match(preparedLine);
            if (useOnlyMatch.Success)
            {
                EmitCommaSeparatedNames(useOnlyMatch.Groups["list"].Value, useOnlyMatch.Groups["list"].Index, "fortran", references, seen, fileId, context, lineNumber, container);

                var list = useOnlyMatch.Groups["list"];
                foreach (Match match in FortranUseAliasRegex.Matches(list.Value))
                {
                    var group = match.Groups["alias"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }

                foreach (Match match in FortranUseAliasTargetRegex.Matches(list.Value))
                {
                    var group = match.Groups["target"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }
            }

            var useRenameMatch = FortranUseRenameListRegex.Match(preparedLine);
            if (useRenameMatch.Success)
            {
                var list = useRenameMatch.Groups["list"];
                foreach (Match match in FortranUseAliasRegex.Matches(list.Value))
                {
                    var group = match.Groups["alias"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }

                foreach (Match match in FortranUseAliasTargetRegex.Matches(list.Value))
                {
                    var group = match.Groups["target"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "import"))
        {
            var importMatch = FortranImportRegex.Match(preparedLine);
            if (importMatch.Success)
                EmitCommaSeparatedNames(importMatch.Groups["list"].Value, importMatch.Groups["list"].Index, "fortran", references, seen, fileId, context, lineNumber, container);
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(originalLine, "include")
            && (originalLine.IndexOf('\'') >= 0 || originalLine.IndexOf('"') >= 0))
        {
            foreach (Match match in FortranIncludeRegex.Matches(originalLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        var isFortranCommonLine = preparedLine.IndexOf("common", StringComparison.OrdinalIgnoreCase) >= 0
            && FortranCommonLineRegex.IsMatch(preparedLine);
        var isFortranNamelistLine = preparedLine.IndexOf("namelist", StringComparison.OrdinalIgnoreCase) >= 0
            && FortranNamelistLineRegex.IsMatch(preparedLine);
        if (isFortranCommonLine || isFortranNamelistLine)
        {
            if (preparedLine.IndexOf('/') >= 0)
            {
                foreach (Match match in FortranSlashGroupNameRegex.Matches(preparedLine))
                    ReferenceExtractor.AddReference(references, seen, fileId, match, "reference", context, lineNumber, container);

                foreach (Match memberListMatch in FortranSlashGroupMemberListRegex.Matches(preparedLine))
                {
                    var list = memberListMatch.Groups["list"];
                    foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                    {
                        var group = match.Groups["name"];
                        ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                    }
                }
            }
        }

        if (isFortranCommonLine && preparedLine.IndexOf('/') < 0)
        {
            var blankCommonMemberListMatch = FortranBlankCommonMemberListRegex.Match(preparedLine);
            if (blankCommonMemberListMatch.Success)
            {
                var list = blankCommonMemberListMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (preparedLine.IndexOf("equivalence", StringComparison.OrdinalIgnoreCase) >= 0
            && preparedLine.IndexOf('(') >= 0
            && FortranEquivalenceLineRegex.IsMatch(preparedLine))
        {
            foreach (Match listMatch in FortranParenthesizedNameListRegex.Matches(preparedLine))
            {
                var list = listMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (preparedLine.IndexOf("data", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var dataLineMatch = FortranDataLineRegex.Match(preparedLine);
            if (dataLineMatch.Success)
            {
                var tail = dataLineMatch.Groups["tail"];
                if (tail.Value.IndexOf('/') >= 0)
                {
                    foreach (Match groupMatch in FortranDataObjectGroupRegex.Matches(tail.Value))
                    {
                        var list = groupMatch.Groups["list"];
                        foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, tail.Index + list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }
                }
            }
        }

        if (preparedLine.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var saveMatch = FortranSaveRegex.Match(preparedLine);
            if (saveMatch.Success)
            {
                var list = saveMatch.Groups["list"];
                if (list.Value.IndexOf('/') >= 0)
                {
                    foreach (Match match in FortranSlashGroupNameRegex.Matches(list.Value))
                        ReferenceExtractor.AddReference(references, seen, fileId, match.Groups["name"].Value, list.Index + match.Groups["name"].Index, "reference", context, lineNumber, container);
                }

                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (preparedLine.IndexOf("submodule", StringComparison.OrdinalIgnoreCase) >= 0
            && preparedLine.IndexOf('(') >= 0)
        {
            var submoduleMatch = FortranSubmoduleParentRegex.Match(preparedLine);
            if (submoduleMatch.Success)
            {
                var parent = submoduleMatch.Groups["parent"];
                ReferenceExtractor.AddReference(references, seen, fileId, parent.Value, parent.Index, "type_reference", context, lineNumber, container);

                var ancestor = submoduleMatch.Groups["ancestor"];
                if (ancestor.Success)
                    ReferenceExtractor.AddReference(references, seen, fileId, ancestor.Value, ancestor.Index, "type_reference", context, lineNumber, container);
            }
        }

        if (preparedLine.IndexOf("external", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var externalMatch = FortranExternalRegex.Match(preparedLine);
            if (externalMatch.Success)
            {
                var list = externalMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (preparedLine.IndexOf("intrinsic", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var intrinsicProcedureMatch = FortranIntrinsicProcedureRegex.Match(preparedLine);
            if (intrinsicProcedureMatch.Success)
            {
                var list = intrinsicProcedureMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (preparedLine.IndexOf("public", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("private", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var accessListMatch = FortranAccessListRegex.Match(preparedLine);
            if (accessListMatch.Success)
            {
                var list = accessListMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (preparedLine.IndexOf("final", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var finalizerMatch = FortranFinalizerRegex.Match(preparedLine);
            if (finalizerMatch.Success)
            {
                var list = finalizerMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if ((preparedLine.IndexOf("procedure", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("generic", StringComparison.OrdinalIgnoreCase) >= 0)
            && FortranProcedureBindingLineRegex.IsMatch(preparedLine))
        {
            if (preparedLine.IndexOf("=>", StringComparison.Ordinal) >= 0)
            {
                var targetListMatch = FortranBindingTargetListRegex.Match(preparedLine);
                if (targetListMatch.Success)
                {
                    foreach (Match match in FortranBindingTargetRegex.Matches(targetListMatch.Value))
                    {
                        var group = match.Groups["name"];
                        ReferenceExtractor.AddReference(references, seen, fileId, group.Value, targetListMatch.Index + group.Index, "reference", context, lineNumber, container);
                    }
                }
            }
        }

        if (preparedLine.IndexOf("=>", StringComparison.Ordinal) >= 0)
        {
            var firstNonWhitespace = FirstNonWhitespaceIndex(preparedLine);
            if (firstNonWhitespace >= 0 && CanStartFortranIdentifierPattern(preparedLine[firstNonWhitespace]))
            {
                var pointerAssignmentMatch = FortranPointerAssignmentRegex.Match(preparedLine);
                if (pointerAssignmentMatch.Success)
                {
                    var group = pointerAssignmentMatch.Groups["name"];
                    if (!group.Value.Equals("null", StringComparison.OrdinalIgnoreCase))
                        ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (preparedLine.IndexOf("associate", StringComparison.OrdinalIgnoreCase) >= 0
            && preparedLine.IndexOf("=>", StringComparison.Ordinal) >= 0)
        {
            var associateMatch = FortranAssociateLineRegex.Match(preparedLine);
            if (associateMatch.Success)
            {
                var list = associateMatch.Groups["list"];
                foreach (Match match in FortranAssociateTargetRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        var hasFortranParen = preparedLine.IndexOf('(') >= 0;
        var hasFortranTypeOrClassMarker = preparedLine.IndexOf("type", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("class", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasFortranParen && hasFortranTypeOrClassMarker)
        {
            foreach (Match match in FortranTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }

            if (preparedLine.IndexOf("is", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foreach (Match match in FortranTypeGuardRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
                }
            }
        }

        if (hasFortranParen && preparedLine.IndexOf("extends", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in FortranExtendsRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        if (hasFortranParen && preparedLine.IndexOf("procedure", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in FortranProcedureTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        var hasFortranAllocateMarker = hasFortranParen
            && preparedLine.IndexOf("allocate", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasFortranAllocateMarker && preparedLine.IndexOf("::", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in FortranAllocateTypeSpecRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        if (hasFortranAllocateMarker)
        {
            var allocateListMatch = FortranAllocateListRegex.Match(preparedLine);
            if (allocateListMatch.Success)
            {
                var list = allocateListMatch.Groups["list"];
                var objectList = list.Value;
                var objectListStart = list.Index;
                var typeSpecEnd = objectList.IndexOf("::", StringComparison.Ordinal);
                if (typeSpecEnd >= 0)
                {
                    objectListStart += typeSpecEnd + 2;
                    objectList = objectList[(typeSpecEnd + 2)..];
                }

                foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(objectList))
                {
                    var segment = objectList.Substring(segmentStart, segmentLength);
                    if (segment.Contains('=', StringComparison.Ordinal))
                        continue;

                    var leading = 0;
                    while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
                        leading++;
                    if (leading >= segment.Length || !IsIdentifierStart(segment[leading]))
                        continue;

                    var end = leading + 1;
                    while (end < segment.Length && IsSimpleIdentifierPart(segment[end]))
                        end++;

                    ReferenceExtractor.AddReference(references, seen, fileId, segment[leading..end], objectListStart + segmentStart + leading, "reference", context, lineNumber, container);
                }

                if (list.Value.IndexOf('=') >= 0)
                {
                    if (list.Value.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0
                        || list.Value.IndexOf("mold", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Match match in FortranAllocateSourceKeywordRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }

                    if (list.Value.IndexOf("stat", StringComparison.OrdinalIgnoreCase) >= 0
                        || list.Value.IndexOf("errmsg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Match match in FortranAllocationStatusKeywordRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }
                }
            }
        }

        if (hasFortranParen && preparedLine.IndexOf("deallocate", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var deallocateListMatch = FortranDeallocateListRegex.Match(preparedLine);
            if (deallocateListMatch.Success)
            {
                var list = deallocateListMatch.Groups["list"];
                foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list.Value))
                {
                    var segment = list.Value.Substring(segmentStart, segmentLength);
                    if (segment.Contains('=', StringComparison.Ordinal))
                        continue;

                    var leading = 0;
                    while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
                        leading++;
                    if (leading >= segment.Length || !IsIdentifierStart(segment[leading]))
                        continue;

                    var end = leading + 1;
                    while (end < segment.Length && IsSimpleIdentifierPart(segment[end]))
                        end++;

                    ReferenceExtractor.AddReference(references, seen, fileId, segment[leading..end], list.Index + segmentStart + leading, "reference", context, lineNumber, container);
                }

                if (list.Value.IndexOf('=') >= 0)
                {
                    if (list.Value.IndexOf("stat", StringComparison.OrdinalIgnoreCase) >= 0
                        || list.Value.IndexOf("errmsg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Match match in FortranAllocationStatusKeywordRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }
                }
            }
        }

        var hasFortranIntrinsicKindMarker = hasFortranParen
            && (preparedLine.IndexOf("integer", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("real", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("complex", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("logical", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("character", StringComparison.OrdinalIgnoreCase) >= 0);
        if (hasFortranIntrinsicKindMarker && preparedLine.IndexOf("kind", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in FortranIntrinsicKeywordKindRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        if (hasFortranIntrinsicKindMarker)
        {
            foreach (Match match in FortranIntrinsicPositionalKindRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }
    }

    private static void EmitPascalTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("uses", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var usesMatch = PascalUsesRegex.Match(preparedLine);
            if (usesMatch.Success)
                EmitCommaSeparatedNames(usesMatch.Groups["list"].Value, usesMatch.Groups["list"].Index, "pascal", references, seen, fileId, context, lineNumber, container);
        }

        var hasPascalBaseMarker = preparedLine.IndexOf('=') >= 0
            && preparedLine.IndexOf('(') >= 0
            && (preparedLine.IndexOf("class", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("interface", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("object", StringComparison.OrdinalIgnoreCase) >= 0);
        if (hasPascalBaseMarker)
        {
            foreach (Match match in PascalClassBaseRegex.Matches(preparedLine))
                EmitCommaSeparatedNames(match.Groups["bases"].Value, match.Groups["bases"].Index, "pascal", references, seen, fileId, context, lineNumber, resolveContainerForColumn(match.Groups["bases"].Index));
        }

        if (preparedLine.IndexOf(':') < 0)
            return;

        foreach (Match match in PascalTypeAfterColonRegex.Matches(preparedLine))
        {
            if (!IsPascalColonTypeReferenceContext(preparedLine, lineNumber, container))
                continue;

            var group = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "pascal");
        }
    }

    private static bool IsPascalColonTypeReferenceContext(string preparedLine, int lineNumber, SymbolRecord? container)
    {
        var trimmed = preparedLine.TrimStart();
        if (container?.Kind != "function"
            || !container.BodyStartLine.HasValue
            || lineNumber < container.BodyStartLine.Value)
        {
            return true;
        }

        return StartsWithPascalDeclarationKeyword(trimmed);
    }

    private static bool StartsWithPascalDeclarationKeyword(string trimmedLine)
    {
        foreach (var keyword in PascalDeclarationKeywords)
        {
            if (trimmedLine.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && (trimmedLine.Length == keyword.Length || !IsSimpleIdentifierPart(trimmedLine[keyword.Length])))
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitObjCTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('@') >= 0 && preparedLine.IndexOf(':') >= 0)
        {
            foreach (Match match in ObjCInterfaceBaseRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "type_reference", context, lineNumber, container);
            }
        }

        if (preparedLine.IndexOf('<') >= 0 && preparedLine.IndexOf('>') >= 0)
        {
            foreach (Match match in ObjCProtocolListRegex.Matches(preparedLine))
                EmitCommaSeparatedNames(match.Groups["list"].Value, match.Groups["list"].Index, "objc", references, seen, fileId, context, lineNumber, container);
        }

        if (preparedLine.IndexOf('*') < 0)
            return;

        foreach (Match match in ObjCDeclTypeRegex.Matches(preparedLine))
        {
            var group = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "objc");
        }
    }

    private static void EmitHaskellTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = HaskellSignatureRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var group = match.Groups["types"];
        ReferenceExtractor.AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            group.Value,
            group.Index,
            context,
            lineNumber,
            container,
            "haskell",
            BuildHaskellIgnoredTypeVariables(group.Value));
    }

    private static IReadOnlySet<string>? BuildHaskellIgnoredTypeVariables(string expression)
    {
        HashSet<string>? ignored = null;
        for (var cursor = 0; cursor < expression.Length; cursor++)
        {
            if (!IsSimpleIdentifierPart(expression[cursor]))
                continue;

            var start = cursor;
            while (cursor < expression.Length && IsSimpleIdentifierPart(expression[cursor]))
                cursor++;

            if (char.IsLower(expression[start]))
            {
                ignored ??= new HashSet<string>(StringComparer.Ordinal);
                ignored.Add(expression[start..cursor]);
            }

            cursor--;
        }

        return ignored;
    }

    private static void EmitElixirTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var hasImportMarker = preparedLine.IndexOf("alias", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("import", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("require", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("use", StringComparison.Ordinal) >= 0;
        if (hasImportMarker)
        {
            foreach (var match in EnumerateMatches(ElixirImportRegex, preparedLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "type_reference", context, lineNumber, container);
        }

        var hasBehaviourMarker = preparedLine.IndexOf('@') >= 0
            && (preparedLine.IndexOf("behaviour", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("impl", StringComparison.Ordinal) >= 0);
        if (hasBehaviourMarker)
        {
            foreach (var match in EnumerateMatches(ElixirBehaviourRegex, preparedLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "type_reference", context, lineNumber, container);
        }
    }

    private static bool IsIdentifierAt(string line, int index, string identifier)
    {
        if (index < 0 || index + identifier.Length > line.Length)
            return false;
        if (string.CompareOrdinal(line, index, identifier, 0, identifier.Length) != 0)
            return false;
        if (index > 0 && IsSimpleIdentifierPart(line[index - 1]))
            return false;

        var after = index + identifier.Length;
        return after >= line.Length || !IsSimpleIdentifierPart(line[after]);
    }

    private static bool IsSimpleIdentifierPart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static void EmitFortranCallReferences(string preparedLine, Action<string, int> addCallLikeReference)
    {
        if (!StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "call"))
            return;

        foreach (Match match in FortranCallRegex.Matches(preparedLine))
            addCallLikeReference(match.Groups["name"].Value, match.Groups["name"].Index);
    }

    private static void EmitPascalCallReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        if (preparedLine.IndexOf(';') < 0)
            return;

        var match = PascalBareCallRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value;
        if (definitionNames?.Contains(name) == true)
            return;

        addCallLikeReference(name, match.Groups["name"].Index);
    }

    private static void EmitObjCMessageReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf('[') >= 0)
        {
            foreach (Match match in ObjCMessageRegex.Matches(preparedLine))
            {
                var receiver = match.Groups["receiver"];
                var selector = match.Groups["name"];
                if (char.IsUpper(receiver.Value[0]) && selector.Value is "alloc" or "new")
                {
                    ReferenceExtractor.AddReference(references, seen, fileId, receiver.Value, receiver.Index, "instantiate", context, lineNumber, resolveContainerForColumn(receiver.Index));
                }

                addCallLikeReference(selector.Value, selector.Index);
            }
        }

        if (preparedLine.IndexOf("@selector", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in ObjCSelectorRegex.Matches(preparedLine))
                addCallLikeReference(match.Groups["name"].Value.TrimEnd(':'), match.Groups["name"].Index);
        }
    }

    private static void EmitHaskellSpaceCallReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        string? definitionName = null;
        var scanStart = 0;
        var scanText = preparedLine;
        if (preparedLine.IndexOf('=') >= 0)
        {
            var definitionMatch = HaskellDefinitionRegex.Match(preparedLine);
            if (definitionMatch.Success)
            {
                definitionName = definitionMatch.Groups["name"].Value;
                var equalsIndex = preparedLine.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    scanStart = equalsIndex + 1;
                    scanText = preparedLine[scanStart..];
                }
            }
        }

        foreach (Match match in HaskellSpaceCallRegex.Matches(scanText))
        {
            var name = match.Groups["name"].Value;
            if (definitionNames?.Contains(name) == true || string.Equals(name, definitionName, StringComparison.Ordinal))
                continue;
            addCallLikeReference(name, scanStart + match.Groups["name"].Index);
        }
    }

    private static void EmitElixirParenlessCallReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        if (!ContainsWhitespace(preparedLine))
            return;

        foreach (Match match in ElixirParenlessCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (definitionNames?.Contains(name) == true)
                continue;
            addCallLikeReference(name, match.Groups["name"].Index);
        }
    }

    private static void EmitSmalltalkMessageReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        if (!ContainsWhitespace(preparedLine))
            return;

        var definitionMatch = SmalltalkMethodDefinitionRegex.Match(preparedLine);
        if (definitionMatch.Success || SmalltalkClassDeclarationRegex.IsMatch(preparedLine))
            return;

        var consumedUntil = 0;
        foreach (Match match in SmalltalkMessageSendRegex.Matches(preparedLine))
        {
            if (match.Index < consumedUntil)
                continue;

            var selectorGroup = match.Groups["selector"];
            var name = ReadSmalltalkSelector(preparedLine, selectorGroup.Index, out var selectorEndIndex);
            consumedUntil = Math.Max(consumedUntil, selectorEndIndex);
            if (definitionNames?.Contains(name) == true)
                continue;
            addCallLikeReference(name, selectorGroup.Index);
        }
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
                return true;
        }

        return false;
    }

    private static string ReadSmalltalkSelector(string line, int selectorIndex, out int endIndex)
    {
        if (!TryReadSmalltalkSelectorPart(line, selectorIndex, out var firstPart, out var cursor))
        {
            endIndex = selectorIndex;
            return string.Empty;
        }

        if (!firstPart.EndsWith(':'))
        {
            endIndex = cursor;
            return firstPart;
        }

        var selector = firstPart;
        while (true)
        {
            var argumentStart = SkipWhitespace(line, cursor);
            if (argumentStart >= line.Length || !IsIdentifierStart(line[argumentStart]))
                break;

            var argumentEnd = argumentStart + 1;
            while (argumentEnd < line.Length && IsSimpleIdentifierPart(line[argumentEnd]))
                argumentEnd++;

            var nextSelectorStart = SkipWhitespace(line, argumentEnd);
            if (!TryReadSmalltalkSelectorPart(line, nextSelectorStart, out var nextPart, out var nextEnd)
                || !nextPart.EndsWith(':'))
            {
                break;
            }

            selector += nextPart;
            cursor = nextEnd;
        }

        endIndex = cursor;
        return selector;
    }

    private static bool TryReadSmalltalkSelectorPart(string line, int start, out string part, out int end)
    {
        part = string.Empty;
        end = start;
        if (start >= line.Length || !IsIdentifierStart(line[start]))
            return false;

        end = start + 1;
        while (end < line.Length && IsSimpleIdentifierPart(line[end]))
            end++;
        if (end < line.Length && line[end] == ':')
            end++;

        part = line[start..end];
        return true;
    }

    private static void EmitCommaSeparatedNames(
        string list,
        int listStart,
        string language,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var leading = ReferenceExtractor.CountLeadingWhitespace(list, segmentStart, segmentLength);
            var trimmedLength = segmentLength - leading;
            while (trimmedLength > 0 && char.IsWhiteSpace(list[segmentStart + leading + trimmedLength - 1]))
                trimmedLength--;
            if (trimmedLength == 0)
                continue;
            var expressionStart = segmentStart + leading;
            var raw = list.Substring(expressionStart, trimmedLength);
            if (language == "vb")
            {
                var equalsIndex = list.IndexOf('=', segmentStart, segmentLength);
                if (equalsIndex >= 0)
                {
                    var rhsStart = equalsIndex + 1;
                    var rhsLength = segmentStart + segmentLength - rhsStart;
                    var rhsLeading = ReferenceExtractor.CountLeadingWhitespace(list, rhsStart, rhsLength);
                    expressionStart = rhsStart + rhsLeading;
                    var rhsTrimmedLength = rhsLength - rhsLeading;
                    while (rhsTrimmedLength > 0 && char.IsWhiteSpace(list[expressionStart + rhsTrimmedLength - 1]))
                        rhsTrimmedLength--;
                    if (rhsTrimmedLength == 0)
                        continue;

                    raw = list.Substring(expressionStart, rhsTrimmedLength);
                }
            }

            var name = GetLastWhitespaceSeparatedToken(raw);
            var offset = list.IndexOf(name, expressionStart, StringComparison.Ordinal);
            if (offset < 0)
                offset = expressionStart;
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, name, listStart + offset, context, lineNumber, container, language);
        }
    }

    private static string GetLastWhitespaceSeparatedToken(string value)
    {
        var end = value.Length;
        while (end > 0 && (value[end - 1] == ' ' || value[end - 1] == '\t'))
            end--;
        var start = end;
        while (start > 0 && value[start - 1] != ' ' && value[start - 1] != '\t')
            start--;

        return start == 0 && end == value.Length ? value : value[start..end];
    }

    private static void EmitVbGenericConstraintReferences(
        string list,
        int listStart,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var ignoredSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "As", "Class", "New", "Structure",
        };

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var match = VbGenericConstraintRegex.Match(segment);
            if (match.Success)
            {
                ignoredSegments.Add(match.Groups["param"].Value);
                ignoredSegments.Add(NormalizeVbIdentifierSegment(match.Groups["param"].Value));
            }
        }

        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var match = VbGenericConstraintRegex.Match(segment);
            if (!match.Success)
                continue;

            var constraintGroup = match.Groups["constraint"];
            // The generic-list regex is shallow; skip nested constraints rather than emit type parameters as concrete types.
            if (constraintGroup.Value.Contains("(Of", StringComparison.OrdinalIgnoreCase))
                continue;

            var absoluteConstraintStart = listStart + segmentStart + constraintGroup.Index;
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                constraintGroup.Value,
                absoluteConstraintStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteConstraintStart),
                "vb",
                ignoredSegments);
        }
    }

    private static string StripCppAccessPrefix(string value)
    {
        var text = value.Trim();
        bool removed;
        do
        {
            removed = false;
            foreach (var prefix in CppAccessPrefixes)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    text = text[prefix.Length..].TrimStart();
                    removed = true;
                }
            }
        } while (removed);

        return text;
    }

    private static string LastCppQualifiedSegment(string value)
    {
        var text = value.Trim();
        var genericIndex = text.IndexOf('<');
        if (genericIndex >= 0)
            text = text[..genericIndex].TrimEnd();
        var separator = text.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? text[(separator + 2)..].Trim() : text;
    }

    private static bool ContainsAsciiUppercase(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'A' and <= 'Z')
                return true;
        }

        return false;
    }

    private static bool IsCppTemplateDeclarationOrSpecializationLine(string line, int matchIndex)
    {
        var prefix = line[..Math.Clamp(matchIndex, 0, line.Length)].TrimStart();
        return prefix.StartsWith("template", StringComparison.Ordinal)
            || prefix.StartsWith("export template", StringComparison.Ordinal);
    }

    private static string LastQualifiedSegment(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot >= 0 && dot + 1 < value.Length ? value[(dot + 1)..] : value;
    }

    private static string NormalizeVbIdentifierSegment(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            return trimmed[1..^1];

        return trimmed;
    }

    private static string LastPathSegment(string value)
    {
        var slash = value.LastIndexOf('/');
        return slash >= 0 && slash + 1 < value.Length ? value[(slash + 1)..] : value;
    }

    private static int LastWhitespaceSeparatedTokenStart(string value)
    {
        var end = value.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(value[end]))
            end--;
        if (end < 0)
            return -1;

        var start = end;
        while (start >= 0 && !char.IsWhiteSpace(value[start]))
            start--;
        return start + 1;
    }

    private static IEnumerable<Match> EnumerateMatches(Regex regex, string input)
    {
        foreach (Match match in BoundedRegex.EnumerateMatches(regex, input))
            yield return match;
    }

    private static void MaskRange(char[] chars, int start, int end)
    {
        for (var i = start; i < end && i < chars.Length; i++)
            chars[i] = ' ';
    }

    private static int SkipWhitespace(string line, int start)
    {
        while (start < line.Length && char.IsWhiteSpace(line[start]))
            start++;
        return start;
    }

    private static bool IsIdentifierStart(char ch) =>
        ch == '_' || char.IsLetter(ch);
}
