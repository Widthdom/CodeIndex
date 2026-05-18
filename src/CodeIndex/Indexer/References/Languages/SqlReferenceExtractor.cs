using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class SqlReferenceExtractor
{
    internal readonly record struct DefinitionLeafSpan(string LeafName, int StartIndex, int EndIndexExclusive);

    private readonly record struct CteBodySpan(int StartIndex, int EndIndexExclusive);

    internal readonly record struct IdentifierScanState(
        bool InBlockComment,
        string? DollarQuoteDelimiter,
        bool InSingleQuotedString);

    internal sealed class State
    {
        public HashSet<string> EstablishedTempObjectNames { get; } = new(StringComparer.Ordinal);
        public string StatementPrefix { get; set; } = string.Empty;
        public IdentifierScanState IdentifierScanState { get; set; }
    }

    private const string ProcCallIdentifierPattern = @"(?:\[(?:[^\]\r\n]|\]\])+\]|`[^`\r\n]+`|""(?:""""|[^""\r\n])+""|##?\w+|[_\p{L}][\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Pc}$]*(?:;\d+)?)";
    private const string ProcCallQualifierPattern = @"(?:(?:" + ProcCallIdentifierPattern + @")?\s*\.\s*)*";
    private const string DoubleQuotedIdentifierPattern = "\"(?:\"\"|[^\"\\r\\n])+\"";
    private const string QuotedIdentifierPattern =
        @"(?:\[[^\[\]\r\n]+\]|`[^`\r\n]+`|" + DoubleQuotedIdentifierPattern + @")";
    private const string BareIdentifierPattern = @"(?:##?\w+|[_\p{L}][\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Pc}$]*)";
    private const string TempIdentifierPattern =
        @"(?:\[(?:##?\w+)\]|`(?:##?\w+)`|" + "\"(?:##?\\w+)\"" + @"|##?\w+)";
    private const string QualifiedIdentifierNoCapturePattern =
        @"(?:(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")\s*\.\s*)*(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")";
    private const string QualifiedIdentifierPattern =
        @"(?:(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")\s*\.\s*)*(?<name>" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")";
    private const string QualifiedIdentifierWithQualifierPattern =
        @"(?:(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")\s*\.\s*)+(?<name>" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")";
    private const string SourceAliasTailPattern =
        @"(?:\s+(?:AS\s+)?(?!JOIN\b|ON\b|USING\b|WHERE\b|GROUP\b|HAVING\b|ORDER\b|LIMIT\b|OFFSET\b|FETCH\b|UNION\b|EXCEPT\b|INTERSECT\b|RETURNING\b|FOR\b|WINDOW\b)(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + "))?";
    private const string SourceTableHintTailPattern =
        @"(?:\s+WITH\s*\((?:[^()]|\([^()]*\))*\))?";
    private const string ParenthesizedSourcePattern =
        @"\((?:[^()]|\((?<paren>)|\)(?<-paren>))*(?(paren)(?!))\)";
    private const string DerivedTableColumnAliasListPattern =
        @"(?:\s*\(\s*(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")(?:\s*,\s*(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @"))*\s*\))?";
    private const string SourceListItemPattern =
        @"(?:(?:ONLY|LATERAL)\b\s+)*(?:" +
        QualifiedIdentifierPattern + SourceTableHintTailPattern + SourceAliasTailPattern +
        @"|" + ParenthesizedSourcePattern + SourceAliasTailPattern + DerivedTableColumnAliasListPattern +
        @")";
    private const string TopTargetModifierPattern =
        @"TOP\s*\([^)\r\n]*\)(?:\s+PERCENT)?(?:\s+WITH\s+TIES)?";
    private const string MergeTargetHintPattern =
        @"WITH\s*\((?:[^()]|\([^()]*\))*\)";
    private const string DropStatisticsItemPattern =
        @"(?:(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")\s*\.\s*)*(?<name>" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")\s*\.\s*(?:" + QuotedIdentifierPattern + "|" + BareIdentifierPattern + @")";
    private static readonly Regex CteDefinitionRegex = new(
        $@"(?<![\w$])(?:(?:WITH)\b\s+(?:RECURSIVE\b\s+)?)?(?<name>{QuotedIdentifierPattern}|{BareIdentifierPattern})(?:\s*\([^)]*\))?\s+AS\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProcCallRegex = new(
        @"(?<![\w$])(?:EXEC|EXECUTE|CALL)\b\s+(?:@\w+\s*=\s*)?" + ProcCallQualifierPattern + @"(?<name>" + ProcCallIdentifierPattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SystemVariableReferenceRegex = new(
        @"(?<!@)(?<name>@@[_\p{L}][\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Pc}$]*(?:\s*\.\s*[_\p{L}][\p{L}\p{Mn}\p{Mc}\p{Nd}\p{Pc}$]*)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FromSourceListRegex = new(
        $@"(?<![\w$])FROM\b\s+{SourceListItemPattern}(?:\s*,\s*{SourceListItemPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SourceReferenceRegex = new(
        $@"(?<![\w$])(?:JOIN|(?:CROSS|OUTER)\s+APPLY)\b\s+{SourceListItemPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MergeUsingSourceRegex = new(
        $@"(?<![\w$])MERGE\b(?:\s+{TopTargetModifierPattern})?(?:\s+INTO)?\s+{QualifiedIdentifierNoCapturePattern}(?:\s+{MergeTargetHintPattern})?(?:\s+(?:AS\s+)?(?!USING\b|WITH\b)(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?\s+USING\b\s+(?:(?:ONLY|LATERAL)\b\s+)*{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MergeUsingPrefixRegex = new(
        $@"(?<![\w$])MERGE\b(?:\s+{TopTargetModifierPattern})?(?:\s+INTO)?\s+{QualifiedIdentifierNoCapturePattern}(?:\s+{MergeTargetHintPattern})?(?:\s+(?:AS\s+)?(?!USING\b|WITH\b)(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeleteUsingSourceRegex = new(
        $@"(?<![\w$])DELETE\b(?:\s+{TopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?\s+{QualifiedIdentifierNoCapturePattern}(?:\s+(?:AS\s+)?(?!USING\b|WHERE\b|RETURNING\b)(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?\s+USING\b\s+(?:(?:ONLY|LATERAL)\b\s+)?{QualifiedIdentifierPattern}(?:\s+(?:AS\s+)?(?!WHERE\b|RETURNING\b|ON\b|USING\b)(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?(?:\s*,\s*(?:(?:ONLY|LATERAL)\b\s+)?{QualifiedIdentifierPattern}(?:\s+(?:AS\s+)?(?!WHERE\b|RETURNING\b|ON\b|USING\b)(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?)*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeleteTargetWithoutFromRegex = new(
        $@"(?<![\w$])DELETE\b(?:\s+{TopTargetModifierPattern})?\s+(?!FROM\b){QualifiedIdentifierWithQualifierPattern}(?=\s*(?:;|$)|\s+(?:FROM|WHERE|OUTPUT|OPTION|RETURNING)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OutputIntoTargetRegex = new(
        $@"(?<![\w$])OUTPUT\b[\s\S]*?\bINTO\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeleteUsingPrefixRegex = new(
        $@"(?<![\w$])DELETE\b(?:\s+{TopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?\s+{QualifiedIdentifierNoCapturePattern}(?:\s+(?:AS\s+)?(?!USING\b|WHERE\b|RETURNING\b)(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeleteUsingListContinuationPrefixRegex = new(
        @"(?<![\w$])DELETE\b[\s\S]*\bUSING\b[\s\S]*,\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FromListContinuationPrefixRegex = new(
        @"(?<![\w$])FROM\b[\s\S]*,\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TargetReferencePrefixRegex = new(
        $@"(?<![\w$])(?:INSERT(?:\s+{TopTargetModifierPattern})?(?:\s+INTO)?|BULK\s+INSERT|UPDATE\b(?:\s+(?:{TopTargetModifierPattern}|ONLY\b))*|DELETE\b(?:\s+{TopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?|TRUNCATE\s+TABLE(?:\s+ONLY\b)?|CREATE(?:\s+(?:TEMP|TEMPORARY))?\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?|ALTER\s+TABLE|DROP\s+TABLE(?:\s+IF\s+EXISTS)?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TargetReferenceRegex = new(
        $@"(?<![\w$])(?:INSERT(?:\s+{TopTargetModifierPattern})?\s+(?:INTO\s+|(?!OR\b|IGNORE\b|OVERWRITE\b|LOW_PRIORITY\b|DELAYED\b|HIGH_PRIORITY\b)){QualifiedIdentifierPattern}|BULK\s+INSERT\s+{QualifiedIdentifierPattern}|UPDATE\b(?:\s+{TopTargetModifierPattern})\s+{QualifiedIdentifierPattern}|UPDATE\b(?:\s+ONLY\b)*\s+{QualifiedIdentifierPattern}|MERGE\b(?:\s+{TopTargetModifierPattern})?(?:\s+INTO)?\s+{QualifiedIdentifierPattern}|DELETE\b(?:\s+{TopTargetModifierPattern})?\s+FROM(?:\s+ONLY\b)?\s+{QualifiedIdentifierPattern}|ALTER\s+TABLE\s+{QualifiedIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TruncateTargetRegex = new(
        $@"(?<![\w$])TRUNCATE\s+TABLE\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropTableTargetRegex = new(
        $@"(?<![\w$])DROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TopCallSuppressionRegex = new(
        @"(?<![\w$])(?:SELECT|INSERT|UPDATE|MERGE|DELETE)\b\s+(?<name>TOP)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AccessMethodCallSuppressionRegex = new(
        $@"(?<![\w$])CREATE\b(?:\s+UNIQUE\b)?\s+INDEX\b[\s\S]*?\bUSING\b\s+(?<name>{QuotedIdentifierPattern}|{BareIdentifierPattern})(?=\s*\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateIndexOnTargetRegex = new(
        $@"(?<![\w$])CREATE\b(?:\s+UNIQUE\b)?(?:\s+(?:NONCLUSTERED\s+COLUMNSTORE|CLUSTERED|NONCLUSTERED|COLUMNSTORE|XML|SPATIAL))?\s+INDEX\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateSpecialXmlIndexOnTargetRegex = new(
        $@"(?<![\w$])CREATE\s+(?:PRIMARY\s+XML|SELECTIVE\s+XML)\s+INDEX\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateClusteredColumnstoreIndexOnTargetRegex = new(
        $@"(?<![\w$])CREATE\b(?:\s+UNIQUE\b)?\s+CLUSTERED\s+COLUMNSTORE\s+INDEX\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateHashIndexOnTargetRegex = new(
        $@"(?<![\w$])CREATE\b(?:\s+UNIQUE\b)?\s+NONCLUSTERED\s+HASH\s+INDEX\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateFullTextIndexOnTargetRegex = new(
        $@"(?<![\w$])CREATE\s+FULLTEXT\s+INDEX\b\s+ON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterIndexOnTargetRegex = new(
        $@"(?<![\w$])ALTER\s+INDEX\b\s+(?:ALL|{QualifiedIdentifierNoCapturePattern})\s+ON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterFullTextIndexOnTargetRegex = new(
        $@"(?<![\w$])ALTER\s+FULLTEXT\s+INDEX\s+ON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropIndexOnTargetRegex = new(
        $@"(?<![\w$])DROP\s+INDEX\b\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierNoCapturePattern}\s+ON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropIndexLegacyTargetRegex = new(
        $@"(?<![\w$])DROP\s+INDEX\s+(?:IF\s+EXISTS\s+)?{DropStatisticsItemPattern}(?:\s*,\s*{DropStatisticsItemPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropFullTextIndexOnTargetRegex = new(
        $@"(?<![\w$])DROP\s+FULLTEXT\s+INDEX\s+ON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateTriggerOnTargetRegex = new(
        $@"(?<![\w$])CREATE\s+(?:OR\s+ALTER\s+)?TRIGGER\b\s+{QualifiedIdentifierNoCapturePattern}[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateSecurityPolicyPredicateTargetRegex = new(
        $@"(?<![\w$])CREATE\s+SECURITY\s+POLICY\b[\s\S]*?\b(?:FILTER|BLOCK)\s+PREDICATE\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}(?:[\s\S]*?\b(?:FILTER|BLOCK)\s+PREDICATE\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterSecurityPolicyPredicateTargetRegex = new(
        $@"(?<![\w$])ALTER\s+SECURITY\s+POLICY\b[\s\S]*?\b(?:FILTER|BLOCK)\s+PREDICATE\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}(?:[\s\S]*?\b(?:FILTER|BLOCK)\s+PREDICATE\b[\s\S]*?\bON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ToggleTriggerOnTargetRegex = new(
        $@"(?<![\w$])(?:ENABLE|DISABLE)\s+TRIGGER\b\s+(?:ALL|{QualifiedIdentifierNoCapturePattern})\s+ON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ForeignKeyReferencesTargetRegex = new(
        $@"(?<![\w$])REFERENCES\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateSynonymForTargetRegex = new(
        $@"(?<![\w$])CREATE\s+(?:OR\s+REPLACE\s+)?(?:PUBLIC\s+)?SYNONYM\b\s+{QualifiedIdentifierNoCapturePattern}\s+FOR\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropSynonymTargetRegex = new(
        $@"(?<![\w$])DROP\s+(?:PUBLIC\s+)?SYNONYM\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropViewTargetRegex = new(
        $@"(?<![\w$])DROP\s+(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropProcedureTargetRegex = new(
        $@"(?<![\w$])DROP\s+(?:PROCEDURE|PROC)\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropFunctionTargetRegex = new(
        $@"(?<![\w$])DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropTriggerTargetRegex = new(
        $@"(?<![\w$])DROP\s+TRIGGER\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropSequenceTargetRegex = new(
        $@"(?<![\w$])DROP\s+SEQUENCE\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropTypeTargetRegex = new(
        $@"(?<![\w$])DROP\s+TYPE\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropRuleTargetRegex = new(
        $@"(?<![\w$])DROP\s+RULE\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropDefaultTargetRegex = new(
        $@"(?<![\w$])DROP\s+DEFAULT\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropAggregateTargetRegex = new(
        $@"(?<![\w$])DROP\s+AGGREGATE\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropSecurityPolicyTargetRegex = new(
        $@"(?<![\w$])DROP\s+SECURITY\s+POLICY\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropFullTextCatalogTargetRegex = new(
        $@"(?<![\w$])DROP\s+FULLTEXT\s+CATALOG\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropPartitionSchemeTargetRegex = new(
        $@"(?<![\w$])DROP\s+PARTITION\s+SCHEME\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropPartitionFunctionTargetRegex = new(
        $@"(?<![\w$])DROP\s+PARTITION\s+FUNCTION\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropXmlSchemaCollectionTargetRegex = new(
        $@"(?<![\w$])DROP\s+XML\s+SCHEMA\s+COLLECTION\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropAssemblyTargetRegex = new(
        $@"(?<![\w$])DROP\s+ASSEMBLY\s+(?:IF\s+EXISTS\s+)?{QualifiedIdentifierPattern}(?:\s*,\s*{QualifiedIdentifierPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterViewTargetRegex = new(
        $@"(?<![\w$])ALTER\s+VIEW\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterProcedureTargetRegex = new(
        $@"(?<![\w$])ALTER\s+(?:PROCEDURE|PROC)\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterFunctionTargetRegex = new(
        $@"(?<![\w$])ALTER\s+FUNCTION\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterTriggerTargetRegex = new(
        $@"(?<![\w$])ALTER\s+TRIGGER\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterSequenceTargetRegex = new(
        $@"(?<![\w$])ALTER\s+SEQUENCE\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterSecurityPolicyTargetRegex = new(
        $@"(?<![\w$])ALTER\s+SECURITY\s+POLICY\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterFullTextCatalogTargetRegex = new(
        $@"(?<![\w$])ALTER\s+FULLTEXT\s+CATALOG\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterPartitionFunctionTargetRegex = new(
        $@"(?<![\w$])ALTER\s+PARTITION\s+FUNCTION\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterPartitionSchemeTargetRegex = new(
        $@"(?<![\w$])ALTER\s+PARTITION\s+SCHEME\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterXmlSchemaCollectionTargetRegex = new(
        $@"(?<![\w$])ALTER\s+XML\s+SCHEMA\s+COLLECTION\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterAssemblyTargetRegex = new(
        $@"(?<![\w$])ALTER\s+ASSEMBLY\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterSchemaTransferTargetRegex = new(
        $@"(?<![\w$])ALTER\s+SCHEMA\b\s+{QualifiedIdentifierNoCapturePattern}\s+TRANSFER\s+{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterTableSwitchTargetRegex = new(
        $@"(?<![\w$])ALTER\s+TABLE\s+{QualifiedIdentifierNoCapturePattern}\s+SWITCH\b[\s\S]*?\bTO\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterTableSystemVersioningHistoryTargetRegex = new(
        $@"(?<![\w$])ALTER\s+TABLE\s+{QualifiedIdentifierNoCapturePattern}[\s\S]*?\bSYSTEM_VERSIONING\s*=\s*ON\b[\s\S]*?\bHISTORY_TABLE\s*=\s*{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ObjectPermissionTargetRegex = new(
        $@"(?<![\w$])(?:GRANT|DENY|REVOKE)\b[\s\S]*?\bON\s+(?:OBJECT\s*::\s*)?(?![A-Z_]+\s*::){QualifiedIdentifierPattern}\s+(?:TO|FROM)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RevokePermissionStatementRegex = new(
        $@"(?<![\w$])REVOKE\b[\s\S]*?\bON\s+(?:OBJECT\s*::\s*)?(?![A-Z_]+\s*::){QualifiedIdentifierNoCapturePattern}\s+FROM\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterAuthorizationObjectTargetRegex = new(
        $@"(?<![\w$])ALTER\s+AUTHORIZATION\s+ON\s+OBJECT\s*::\s*{QualifiedIdentifierPattern}\s+TO\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AlterAuthorizationBareTargetRegex = new(
        $@"(?<![\w$])ALTER\s+AUTHORIZATION\s+ON\s+(?![A-Z_]+\s*::){QualifiedIdentifierPattern}\s+TO\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UpdateStatisticsTargetRegex = new(
        $@"(?<![\w$])UPDATE\s+STATISTICS\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateStatisticsOnTargetRegex = new(
        $@"(?<![\w$])CREATE\s+STATISTICS\b\s+{QualifiedIdentifierNoCapturePattern}\s+ON\s+(?:(?:ONLY)\b\s+)?{QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DropStatisticsTargetRegex = new(
        $@"(?<![\w$])DROP\s+STATISTICS\s+{DropStatisticsItemPattern}(?:\s*,\s*{DropStatisticsItemPattern})*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SelectIntoTargetStatementRegex = new(
        $@"(?<![\w$])SELECT\b.*?\bINTO\s+(?!OUTFILE\b|DUMPFILE\b){QualifiedIdentifierPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SelectIntoTargetPrefixRegex = new(
        @"(?<![\w$])SELECT\b.*?\bINTO\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateTempTableRegex = new(
        $@"(?<![\w$])CREATE(?:\s+(?:TEMP|TEMPORARY))?\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+(?<name>{TempIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UsingKeywordRegex = new(@"(?<![\w$])USING\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CreateTempRoutineRegex = new(
        $@"(?<![\w$])CREATE(?:\s+OR\s+(?:REPLACE|ALTER))?(?:\s+(?:TEMP|TEMPORARY))?\s+(?:PROC(?:EDURE)?|FUNCTION)\b(?:\s+IF\s+NOT\s+EXISTS)?\s+(?<name>{TempIdentifierPattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingTempIdentifierRegex = new(
        $@"^(?:(?:ONLY)\b\s+)?(?<item>(?:{TempIdentifierPattern}|{QualifiedIdentifierNoCapturePattern}))(?:\s+(?:AS\s+)?(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MergeTargetHintContinuationPrefixRegex = new(
        $@"(?<![\w$])MERGE\b(?:\s+{TopTargetModifierPattern})?(?:\s+INTO)?\s+{QualifiedIdentifierNoCapturePattern}\s+WITH\s*\((?:[^()]|\([^()]*\))*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static State CreateState() => new();

    public static void AddDefinitionNameAliases(HashSet<string> names, SymbolRecord symbol)
    {
        var leafName = SqlNameResolver.GetLeafName(symbol.Name);
        if (!string.IsNullOrWhiteSpace(leafName))
            names.Add(leafName);
    }

    public static Dictionary<int, List<DefinitionLeafSpan>> BuildDefinitionLeafSpansByLine(
        string[] lines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var spansByLine = new Dictionary<int, List<DefinitionLeafSpan>>();
        foreach (var symbol in symbols)
        {
            if (symbol.Line < 1 || symbol.Line > lines.Length)
                continue;
            if (!TryFindDefinitionLeafSpan(lines[symbol.Line - 1], symbol.Name, out var span))
                continue;

            if (!spansByLine.TryGetValue(symbol.Line, out var spans))
            {
                spans = [];
                spansByLine[symbol.Line] = spans;
            }

            spans.Add(span);
        }

        return spansByLine;
    }

    public static bool ShouldSuppressDefinitionCall(
        IReadOnlyList<DefinitionLeafSpan>? definitionLeafSpans,
        string resolvedName,
        int callIndex)
    {
        if (definitionLeafSpans == null)
            return false;

        foreach (var span in definitionLeafSpans)
        {
            if (callIndex >= span.StartIndex
                && callIndex < span.EndIndexExclusive
                && string.Equals(span.LeafName, resolvedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static HashSet<int> Emit(
        string structuralLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        State state,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        var suppressedCallIndices = new HashSet<int>();
        var lineFragment = PrepareLineForIdentifierScan(
            structuralLine,
            state.IdentifierScanState,
            state.StatementPrefix,
            out var lineEndedByLineComment,
            out var nextIdentifierScanState);
        state.IdentifierScanState = nextIdentifierScanState;
        if (string.IsNullOrWhiteSpace(lineFragment))
            return suppressedCallIndices;

        if (ShouldFlushTempObjectPrefixAtLineBoundary(state.StatementPrefix, lineFragment))
        {
            CollectTempObjectNamesFromStatement(state.StatementPrefix, state.EstablishedTempObjectNames);
            state.StatementPrefix = string.Empty;
        }

        var combinedLine = CombineStatementPrefix(state.StatementPrefix, lineFragment, out var lineOffset);
        int statementStart = 0;

        while (true)
        {
            int terminatorIndex = FindStatementTerminator(combinedLine, statementStart);
            int statementEnd = terminatorIndex >= 0 ? terminatorIndex + 1 : combinedLine.Length;
            var statement = combinedLine[statementStart..statementEnd];
            int statementLineOffset = Math.Max(0, lineOffset - statementStart);

            if (!string.IsNullOrWhiteSpace(statement))
            {
                EmitStatementReferences(
                    statement,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    state.EstablishedTempObjectNames,
                    suppressedCallIndices,
                    resolveContainerForCall,
                    shouldIgnoreName,
                    shouldSuppressDefinitionCall);
            }

            if (terminatorIndex < 0)
                break;

            CollectTempObjectNamesFromStatement(statement, state.EstablishedTempObjectNames);
            statementStart = terminatorIndex + 1;
            while (statementStart < combinedLine.Length && char.IsWhiteSpace(combinedLine[statementStart]))
                statementStart++;
        }

        state.StatementPrefix = AdvanceStatementPrefix(combinedLine, statementStart, lineEndedByLineComment);
        return suppressedCallIndices;
    }

    private static void EmitStatementReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        var cteBodySpans = FindCteBodySpans(statement);
        HashSet<int>? usingSourceIndices = null;
        foreach (Match match in MergeUsingSourceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;

            (usingSourceIndices ??= []).Add(nameGroup.Index);
        }

        foreach (Match match in DeleteUsingSourceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                if (capture.Index < statementLineOffset)
                    continue;

                (usingSourceIndices ??= []).Add(capture.Index);
            }
        }

        foreach (Match match in TopCallSuppressionRegex.Matches(statement))
        {
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;

            suppressedCallIndices.Add(nameGroup.Index + statementStart - lineOffset);
        }

        foreach (Match match in AccessMethodCallSuppressionRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            if (usingSourceIndices != null && usingSourceIndices.Contains(nameGroup.Index))
                continue;

            suppressedCallIndices.Add(nameGroup.Index + statementStart - lineOffset);
        }

        EmitProcedureCalls(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            resolveContainerForCall,
            shouldIgnoreName,
            shouldSuppressDefinitionCall);

        EmitSystemVariableReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall);

        EmitSourceCaptureReferences(
            FromSourceListRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName,
            cteBodySpans);

        EmitSourceCaptureReferences(
            SourceReferenceRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName,
            cteBodySpans);

        EmitMergeUsingReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitSourceCaptureReferences(
            DeleteUsingSourceRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            establishedTempObjectNames,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DeleteTargetWithoutFromRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            OutputIntoTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitSelectIntoTargetReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            CreateIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitMultiTargetReferences(
            CreateSpecialXmlIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitMultiTargetReferences(
            CreateClusteredColumnstoreIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitMultiTargetReferences(
            CreateHashIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitMultiTargetReferences(
            CreateFullTextIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitMultiTargetReferences(
            AlterIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterFullTextIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropIndexLegacyTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropFullTextIndexOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            CreateTriggerOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            CreateSecurityPolicyPredicateTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterSecurityPolicyPredicateTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            ToggleTriggerOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            ForeignKeyReferencesTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitMultiTargetReferences(
            CreateSynonymForTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropSynonymTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropViewTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropProcedureTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropFunctionTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropTriggerTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropSequenceTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropTypeTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropRuleTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropDefaultTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropAggregateTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropSecurityPolicyTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropFullTextCatalogTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropPartitionSchemeTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropPartitionFunctionTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropXmlSchemaCollectionTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropAssemblyTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterViewTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterProcedureTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterFunctionTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterTriggerTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterSequenceTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterSecurityPolicyTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterFullTextCatalogTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterPartitionFunctionTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterPartitionSchemeTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterXmlSchemaCollectionTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterAssemblyTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterSchemaTransferTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterTableSwitchTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterTableSystemVersioningHistoryTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            ObjectPermissionTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterAuthorizationObjectTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            AlterAuthorizationBareTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            UpdateStatisticsTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            CreateStatisticsOnTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName,
            suppressedCallIndices);

        EmitMultiTargetReferences(
            DropStatisticsTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitTargetReferences(
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            suppressedCallIndices,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            DropTableTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);

        EmitMultiTargetReferences(
            TruncateTargetRegex.Matches(statement),
            statement,
            statementStart,
            statementLineOffset,
            lineOffset,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            resolveContainerForCall,
            shouldIgnoreName);
    }

    private static void EmitProcedureCalls(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        Func<string, int, bool> shouldSuppressDefinitionCall)
    {
        foreach (Match match in ProcCallRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;

            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;
            if (shouldSuppressDefinitionCall(resolvedName, nameIndex))
                continue;
            if (!wasQuoted
                && resolvedName.StartsWith("#", StringComparison.Ordinal)
                && !establishedTempObjectNames.Contains(resolvedName))
                continue;

            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "call", context, lineNumber, container);
        }
    }

    private static void EmitSystemVariableReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match match in SystemVariableReferenceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;

            var resolvedName = SqlSymbolNameNormalizer.Normalize(nameGroup.Value);
            var nameColumn = nameGroup.Index + statementStart - lineOffset;
            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "system_variable", context, lineNumber, container);
        }
    }

    private static void EmitSourceCaptureReferences(
        MatchCollection matches,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        IReadOnlyList<CteBodySpan>? cteBodySpans = null)
    {
        if (RevokePermissionStatementRegex.IsMatch(statement))
            return;

        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            foreach (Capture capture in match.Groups["name"].Captures)
            {
                EmitSourceReference(
                    capture.Value,
                    capture.Index,
                    statement,
                    statementStart,
                    statementLineOffset,
                    lineOffset,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    establishedTempObjectNames,
                    suppressedCallIndices,
                    resolveContainerForCall,
                    shouldIgnoreName,
                    GetSourceReferenceKind(capture.Index, cteBodySpans));
            }
        }
    }

    private static void EmitMergeUsingReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in MergeUsingSourceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            EmitSourceReference(
                nameGroup.Value,
                nameGroup.Index,
                statement,
                statementStart,
                statementLineOffset,
                lineOffset,
                context,
                lineNumber,
                references,
                seen,
                fileId,
                establishedTempObjectNames,
                suppressedCallIndices,
                resolveContainerForCall,
                shouldIgnoreName);
        }
    }

    private static void EmitSourceReference(
        string rawName,
        int rawIndex,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> establishedTempObjectNames,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        string referenceKind = "reference")
    {
        if (rawIndex < statementLineOffset)
            return;

        var followedByOpenParen = IsFollowedByOpenParen(statement, rawIndex + rawName.Length);
        NormalizeIdentifier(rawName, rawIndex, out var resolvedName, out var nameIndex, out var wasQuoted);
        int nameColumn = nameIndex + statementStart - lineOffset;
        if (!wasQuoted && shouldIgnoreName(resolvedName))
            return;
        if (followedByOpenParen)
        {
            var container = resolveContainerForCall(rawIndex);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "call", context, lineNumber, container);
            if (!wasQuoted)
                suppressedCallIndices.Add(GetCallLikeSuppressionIndex(statement, rawIndex) + statementStart - lineOffset);
            return;
        }
        if (resolvedName.StartsWith("#", StringComparison.Ordinal)
            && !establishedTempObjectNames.Contains(resolvedName))
            return;

        var referenceContainer = resolveContainerForCall(rawIndex);
        ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, referenceKind, context, lineNumber, referenceContainer);
    }

    private static string GetSourceReferenceKind(int index, IReadOnlyList<CteBodySpan>? cteBodySpans)
    {
        if (cteBodySpans == null)
            return "reference";

        foreach (var span in cteBodySpans)
        {
            if (index >= span.StartIndex && index < span.EndIndexExclusive)
                return "cte_body_reference";
        }

        return "reference";
    }

    private static List<CteBodySpan>? FindCteBodySpans(string statement)
    {
        if (statement.IndexOf("WITH", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        List<CteBodySpan>? spans = null;
        foreach (Match match in CteDefinitionRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            var openParenIndex = match.Index + match.Length - 1;
            var closeParenIndex = FindMatchingParen(statement, openParenIndex);
            (spans ??= []).Add(new CteBodySpan(openParenIndex + 1, closeParenIndex < 0 ? statement.Length : closeParenIndex));
        }

        return spans;
    }

    private static int FindMatchingParen(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
                continue;
            }

            if (text[i] != ')')
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static void EmitSelectIntoTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int>? suppressedCallIndices = null)
    {
        foreach (Match match in SelectIntoTargetStatementRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;
            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;

            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
        }
    }

    private static void EmitTargetReferences(
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<int> suppressedCallIndices,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName)
    {
        foreach (Match match in TargetReferenceRegex.Matches(statement))
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Index < statementLineOffset)
                continue;
            NormalizeIdentifier(nameGroup.Value, nameGroup.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
            int nameColumn = nameIndex + statementStart - lineOffset;
            if (!wasQuoted && shouldIgnoreName(resolvedName))
                continue;
            if (!wasQuoted
                && string.Equals(resolvedName, "SET", StringComparison.OrdinalIgnoreCase)
                && match.Value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!wasQuoted
                && string.Equals(resolvedName, "STATISTICS", StringComparison.OrdinalIgnoreCase)
                && match.Value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                continue;

            var container = resolveContainerForCall(nameGroup.Index);
            ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
            if (IsFollowedByOpenParen(statement, nameGroup.Index + nameGroup.Length))
                AddCallLikeSuppressionIndices(suppressedCallIndices, statement, nameGroup.Index, statementStart, lineOffset);
        }
    }

    private static void EmitMultiTargetReferences(
        MatchCollection matches,
        string statement,
        int statementStart,
        int statementLineOffset,
        int lineOffset,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Func<string, bool> shouldIgnoreName,
        HashSet<int>? suppressedCallIndices = null)
    {
        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                if (capture.Index < statementLineOffset)
                    continue;
                NormalizeIdentifier(capture.Value, capture.Index, out var resolvedName, out var nameIndex, out var wasQuoted);
                int nameColumn = nameIndex + statementStart - lineOffset;
                if (!wasQuoted && shouldIgnoreName(resolvedName))
                    continue;

                var container = resolveContainerForCall(capture.Index);
                ReferenceExtractor.AddReference(references, seen, fileId, resolvedName, nameColumn, "reference", context, lineNumber, container);
                if (suppressedCallIndices != null && IsFollowedByOpenParen(statement, capture.Index + capture.Length))
                    AddCallLikeSuppressionIndices(suppressedCallIndices, statement, capture.Index, statementStart, lineOffset);
            }
        }
    }

    private static void NormalizeIdentifier(
        string rawName,
        int rawIndex,
        out string resolvedName,
        out int resolvedIndex,
        out bool wasQuoted)
    {
        if (rawName.Length >= 2
            && ((rawName[0] == '[' && rawName[^1] == ']')
                || (rawName[0] == '`' && rawName[^1] == '`')
                || (rawName[0] == '"' && rawName[^1] == '"')))
        {
            resolvedName = rawName.Substring(1, rawName.Length - 2);
            if (rawName[0] == '"')
                resolvedName = resolvedName.Replace("\"\"", "\"", StringComparison.Ordinal);
            else if (rawName[0] == '[')
                resolvedName = resolvedName.Replace("]]", "]", StringComparison.Ordinal);
            resolvedIndex = rawIndex + 1;
            wasQuoted = true;
            return;
        }

        resolvedName = rawName;
        resolvedIndex = rawIndex;
        wasQuoted = false;
    }

    private static bool IsFollowedByOpenParen(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '(';
    }

    private static int GetCallLikeSuppressionIndex(string line, int index)
    {
        while (index < line.Length && line[index] == '#')
            index++;

        return index;
    }

    private static void AddCallLikeSuppressionIndices(
        HashSet<int> suppressedCallIndices,
        string line,
        int leafIndex,
        int statementStart,
        int lineOffset)
    {
        var leafSuppressionIndex = GetCallLikeSuppressionIndex(line, leafIndex) + statementStart - lineOffset;
        suppressedCallIndices.Add(leafSuppressionIndex);

        var qualifiedStart = FindQualifiedIdentifierStart(line, leafIndex);
        if (qualifiedStart == leafIndex)
            return;

        suppressedCallIndices.Add(GetCallLikeSuppressionIndex(line, qualifiedStart) + statementStart - lineOffset);
    }

    private static int FindQualifiedIdentifierStart(string line, int leafIndex)
    {
        var start = leafIndex;
        while (start > 0)
        {
            var scan = start - 1;
            while (scan >= 0 && char.IsWhiteSpace(line[scan]))
                scan--;
            if (scan < 0 || line[scan] != '.')
                break;

            scan--;
            while (scan >= 0 && char.IsWhiteSpace(line[scan]))
                scan--;
            if (scan < 0)
                break;

            start = ScanIdentifierSegmentStart(line, scan);
        }

        return start;
    }

    private static int ScanIdentifierSegmentStart(string line, int index)
    {
        if (line[index] == ']')
        {
            index--;
            while (index >= 0)
            {
                if (line[index] == '[')
                    return index;
                index--;
            }

            return 0;
        }

        if (line[index] is '"' or '`')
        {
            var quote = line[index--];
            while (index >= 0)
            {
                if (line[index] == quote)
                    return index;
                index--;
            }

            return 0;
        }

        while (index >= 0 && IsIdentifierContinuationForReverseScan(line[index]))
            index--;

        return index + 1;
    }

    private static bool IsIdentifierContinuationForReverseScan(char ch)
        => ch is '_' or '$' or '#'
           || char.IsLetterOrDigit(ch)
           || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark
               or System.Globalization.UnicodeCategory.SpacingCombiningMark
               or System.Globalization.UnicodeCategory.ConnectorPunctuation;

    private static string CombineStatementPrefix(string prefix, string line, out int lineOffset)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            lineOffset = 0;
            return line;
        }

        lineOffset = prefix.Length + 1;
        return prefix + "\n" + line;
    }

    private static string AdvanceStatementPrefix(
        string combined,
        int statementStart,
        bool lineEndedByLineComment)
    {
        var remaining = statementStart == 0 ? combined : combined[statementStart..];
        if (!lineEndedByLineComment)
            return remaining;

        return CanStatementRequireLineCommentCarry(remaining) ? remaining : string.Empty;
    }

    private static bool ShouldFlushTempObjectPrefixAtLineBoundary(
        string prefix,
        string nextLine)
    {
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(nextLine))
            return false;
        if (!CanStatementEstablishTempObject(prefix))
            return false;

        return StartsTopLevelStatement(nextLine);
    }

    private static bool CanStatementEstablishTempObject(string statement)
    {
        if (statement.IndexOf('#') < 0)
            return false;

        return TargetReferenceRegex.IsMatch(statement)
            || TruncateTargetRegex.IsMatch(statement)
            || SelectIntoTargetStatementRegex.IsMatch(statement)
            || CreateTempTableRegex.IsMatch(statement)
            || CreateTempRoutineRegex.IsMatch(statement);
    }

    private static bool CanStatementRequireLineCommentCarry(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        return CanStatementEstablishTempObject(statement)
            || TargetReferencePrefixRegex.IsMatch(statement)
            || FromListContinuationPrefixRegex.IsMatch(statement)
            || SelectIntoTargetPrefixRegex.IsMatch(statement)
            || DeleteUsingPrefixRegex.IsMatch(statement)
            || DeleteUsingListContinuationPrefixRegex.IsMatch(statement)
            || MergeUsingPrefixRegex.IsMatch(statement)
            || MergeTargetHintContinuationPrefixRegex.IsMatch(statement);
    }

    private static bool StartsTopLevelStatement(string line)
    {
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || !char.IsLetter(line[index]))
            return false;

        int start = index;
        while (index < line.Length && char.IsLetter(line[index]))
            index++;

        var keyword = line[start..index].ToUpperInvariant();
        if (keyword == "WITH")
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            return index >= line.Length || line[index] != '(';
        }

        return keyword switch
        {
            "SELECT" => true,
            "INSERT" => true,
            "UPDATE" => true,
            "DELETE" => true,
            "MERGE" => true,
            "CREATE" => true,
            "ALTER" => true,
            "DROP" => true,
            "TRUNCATE" => true,
            "SET" => true,
            "DECLARE" => true,
            "IF" => true,
            "WHILE" => true,
            "DO" => true,
            "BEGIN" => true,
            "EXEC" => true,
            "EXECUTE" => true,
            "CALL" => true,
            _ => false,
        };
    }

    private static int FindStatementTerminator(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ';')
                return i;
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
                continue;
            }
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(text, i + 1);
                if (closing < 0)
                    return -1;
                i = closing;
            }
        }

        return -1;
    }

    private static int FindClosingDoubleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static int FindClosingSingleQuote(string text, int startIndex)
    {
        for (int i = startIndex; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                continue;
            }
            if (text[i] != '\'')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool IsInsideDoubleQuotedRegion(string text, int index)
    {
        if (index <= 0)
            return false;

        bool inside = false;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] != '"')
                continue;
            if (inside && i + 1 < index && text[i + 1] == '"')
            {
                i++;
                continue;
            }

            inside = !inside;
        }

        return inside;
    }

    private static bool TryReadDollarQuoteDelimiter(
        string line,
        int index,
        out string delimiter)
    {
        delimiter = string.Empty;
        if (index < 0 || index >= line.Length || line[index] != '$')
            return false;
        if (index > 0 && (char.IsLetterOrDigit(line[index - 1]) || line[index - 1] == '_'))
            return false;
        if (index + 1 >= line.Length)
            return false;
        if (line[index + 1] == '$')
        {
            delimiter = "$$";
            return true;
        }
        if (!(char.IsLetter(line[index + 1]) || line[index + 1] == '_'))
            return false;

        int probe = index + 2;
        while (probe < line.Length && (char.IsLetterOrDigit(line[probe]) || line[probe] == '_'))
            probe++;
        if (probe >= line.Length || line[probe] != '$')
            return false;

        delimiter = line[index..(probe + 1)];
        return true;
    }

    private static int SkipWhitespaceAhead(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static void CollectTempObjectNamesFromStatement(
        string statement,
        HashSet<string> names)
    {
        CollectTempObjectNamesFromMatches(TargetReferenceRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(TruncateTargetRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(SelectIntoTargetStatementRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(CreateTempTableRegex.Matches(statement), statement, names);
        CollectTempObjectNamesFromMatches(CreateTempRoutineRegex.Matches(statement), statement, names);
    }

    private static void CollectTempObjectNamesFromMatches(MatchCollection matches, string statement, HashSet<string> names)
    {
        foreach (Match match in matches)
        {
            if (IsInsideDoubleQuotedRegion(statement, match.Index))
                continue;
            var nameGroup = match.Groups["name"];
            if (nameGroup.Captures.Count == 0)
                continue;

            foreach (Capture capture in nameGroup.Captures)
            {
                NormalizeIdentifier(capture.Value, capture.Index, out var resolvedName, out _, out _);
                if (resolvedName.StartsWith("#", StringComparison.Ordinal))
                    names.Add(resolvedName);
            }
        }
    }

    private static bool TryFindDefinitionLeafSpan(string line, string qualifiedName, out DefinitionLeafSpan span)
    {
        span = default;
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(qualifiedName))
            return false;

        var leafName = SqlNameResolver.GetLeafName(qualifiedName);
        if (string.IsNullOrWhiteSpace(leafName))
            return false;

        var rawSegments = SplitQualifiedNameSourceSegments(qualifiedName);
        if (rawSegments.Count == 0)
            return false;

        var pattern = new StringBuilder();
        for (var i = 0; i < rawSegments.Count; i++)
        {
            if (i > 0)
                pattern.Append(@"\s*\.\s*");

            var escaped = Regex.Escape(rawSegments[i]);
            if (i == rawSegments.Count - 1)
                pattern.Append("(?<leaf>").Append(escaped).Append(')');
            else
                pattern.Append(escaped);
        }

        var match = Regex.Match(line, pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var leafGroup = match.Groups["leaf"];
        if (!leafGroup.Success)
            return false;

        span = new DefinitionLeafSpan(leafName, leafGroup.Index, leafGroup.Index + leafGroup.Length);
        return true;
    }

    private static List<string> SplitQualifiedNameSourceSegments(string qualifiedName)
    {
        var trimmed = qualifiedName.Trim();
        var segments = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (quote != '\0')
            {
                current.Append(ch);
                if (quote == '[')
                {
                    if (ch == ']')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == ']')
                        {
                            current.Append(trimmed[i + 1]);
                            i++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }

                    continue;
                }

                if (ch == quote)
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == quote)
                    {
                        current.Append(trimmed[i + 1]);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (ch is '[' or '"' or '`')
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (ch == '.')
            {
                AppendQualifiedNameSourceSegment(segments, current);
                continue;
            }

            current.Append(ch);
        }

        AppendQualifiedNameSourceSegment(segments, current);
        return segments;
    }

    private static void AppendQualifiedNameSourceSegment(List<string> segments, StringBuilder current)
    {
        var value = current.ToString().Trim();
        if (value.Length > 0)
            segments.Add(value);
        current.Clear();
    }

    private static string PrepareLineForIdentifierScan(
        string line,
        IdentifierScanState state,
        string? statementPrefix,
        out bool lineEndedByLineComment,
        out IdentifierScanState nextState)
    {
        lineEndedByLineComment = false;
        if (string.IsNullOrEmpty(line))
        {
            nextState = state;
            return line;
        }

        var sanitized = line.ToCharArray();
        bool inBlockComment = state.InBlockComment;
        string? dollarQuoteDelimiter = state.DollarQuoteDelimiter;
        bool inSingleQuotedString = state.InSingleQuotedString;

        void BlankRange(int start, int endExclusive)
        {
            start = Math.Max(0, start);
            endExclusive = Math.Min(sanitized.Length, endExclusive);
            for (int blankIndex = start; blankIndex < endExclusive; blankIndex++)
                sanitized[blankIndex] = ' ';
        }

        for (int i = 0; i < line.Length;)
        {
            if (inBlockComment)
            {
                int closing = line.IndexOf("*/", i, StringComparison.Ordinal);
                int end = closing >= 0 ? closing + 2 : line.Length;
                BlankRange(i, end);
                if (closing < 0)
                    break;
                i = end;
                inBlockComment = false;
                continue;
            }
            if (!string.IsNullOrEmpty(dollarQuoteDelimiter))
            {
                int closing = line.IndexOf(dollarQuoteDelimiter, i, StringComparison.Ordinal);
                if (closing < 0)
                {
                    BlankRange(i, line.Length);
                    break;
                }

                int nextContent = SkipWhitespaceAhead(line, closing + dollarQuoteDelimiter.Length);
                if (nextContent < line.Length
                    && line[nextContent] != ';'
                    && line[nextContent] != ','
                    && line[nextContent] != ')'
                    && line[nextContent] != ']')
                {
                    int nestedClosing = line.IndexOf(
                        dollarQuoteDelimiter,
                        closing + dollarQuoteDelimiter.Length,
                        StringComparison.Ordinal);
                    if (nestedClosing >= 0)
                    {
                        int end = nestedClosing + dollarQuoteDelimiter.Length;
                        BlankRange(i, end);
                        i = end;
                        continue;
                    }
                }

                int closingEnd = closing + dollarQuoteDelimiter.Length;
                BlankRange(i, closingEnd);
                i = closingEnd;
                dollarQuoteDelimiter = null;
                continue;
            }
            if (inSingleQuotedString)
            {
                int closing = FindClosingSingleQuote(line, i);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing >= 0)
                {
                    inSingleQuotedString = false;
                    continue;
                }

                break;
            }

            char c = line[i];
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(line, i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '`')
            {
                int closing = line.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '[')
            {
                int closing = line.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing + 1;
                continue;
            }
            if (c == '\'')
            {
                int closing = FindClosingSingleQuote(line, i + 1);
                int end = closing >= 0 ? closing + 1 : line.Length;
                BlankRange(i, end);
                i = end;
                if (closing < 0)
                    inSingleQuotedString = true;
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                BlankRange(i, i + 2);
                i += 2;
                inBlockComment = true;
                continue;
            }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                lineEndedByLineComment = true;
                BlankRange(i, line.Length);
                break;
            }
            if (c == '#')
            {
                if (ShouldTreatHashAsComment(line, i, statementPrefix))
                {
                    lineEndedByLineComment = true;
                    BlankRange(i, line.Length);
                    break;
                }
            }
            if (c == '$' && TryReadDollarQuoteDelimiter(line, i, out var delimiter))
            {
                BlankRange(i, i + delimiter.Length);
                i += delimiter.Length;
                dollarQuoteDelimiter = delimiter;
                continue;
            }

            i++;
        }

        nextState = new IdentifierScanState(inBlockComment, dollarQuoteDelimiter, inSingleQuotedString);
        return new string(sanitized);
    }

    private static bool ShouldTreatHashAsComment(string line, int hashIndex, string? statementPrefix)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        if (probe < 0 && !string.IsNullOrWhiteSpace(statementPrefix))
        {
            var combined = statementPrefix + "\n" + line;
            return ShouldTreatHashAsCommentCore(combined, statementPrefix.Length + 1 + hashIndex);
        }

        return ShouldTreatHashAsCommentCore(line, hashIndex);
    }

    private static bool ShouldTreatHashAsCommentCore(string line, int hashIndex)
    {
        if (hashIndex < 0 || hashIndex >= line.Length || line[hashIndex] != '#')
            return false;

        int next = hashIndex + 1;
        if (hashIndex > 0
            && line[hashIndex - 1] == '#'
            && next < line.Length
            && (char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return false;
        if (next + 1 < line.Length
            && line[next] == '#'
            && (char.IsLetterOrDigit(line[next + 1]) || line[next + 1] == '_'))
            return false;
        if (next >= line.Length || !(char.IsLetterOrDigit(line[next]) || line[next] == '_'))
            return true;

        int probe = hashIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(line[probe]))
            probe--;
        while (probe >= 0 && line[probe] == ',')
        {
            var priorListItem = line[..probe];
            int sourceStart = FindLastCommaOutsideQuotedIdentifiers(priorListItem);
            if (sourceStart >= 0)
                sourceStart++;
            else
            {
                var usingMatches = UsingKeywordRegex.Matches(priorListItem);
                if (usingMatches.Count > 0)
                    sourceStart = usingMatches[^1].Index + usingMatches[^1].Length;
                else
                {
                    sourceStart = priorListItem.LastIndexOf('#');
                    if (sourceStart < 0)
                        return true;
                }
            }
            while (sourceStart < priorListItem.Length && char.IsWhiteSpace(priorListItem[sourceStart]))
                sourceStart++;

            var listMatch = TrailingTempIdentifierRegex.Match(priorListItem[sourceStart..]);
            if (!listMatch.Success)
                return true;

            probe = sourceStart - 1;
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
        }
        if (probe < 0)
            return true;
        if (line[probe] == '.')
            return false;
        if (line[probe] == ')')
        {
            int depth = 1;
            probe--;
            while (probe >= 0 && depth > 0)
            {
                if (line[probe] == ')')
                    depth++;
                else if (line[probe] == '(')
                    depth--;
                probe--;
            }
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
            if (probe < 0)
                return true;

            int modifierEnd = probe;
            while (probe >= 0 && char.IsLetter(line[probe]))
                probe--;
            int modifierStart = probe + 1;
            if (modifierStart <= modifierEnd
                && string.Equals(line[modifierStart..(modifierEnd + 1)], "TOP", StringComparison.OrdinalIgnoreCase))
            {
                while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                    probe--;
                if (probe < 0)
                    return true;
            }
        }

        int tokenEnd = probe;
        while (probe >= 0 && char.IsLetter(line[probe]))
            probe--;
        int tokenStart = probe + 1;
        if (tokenStart > tokenEnd)
            return true;

        var token = line[tokenStart..(tokenEnd + 1)];
        return !string.Equals(token, "FROM", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "JOIN", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "MERGE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "USING", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "INTO", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "UPDATE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "TABLE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXEC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "EXECUTE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "CALL", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROCEDURE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "PROC", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "FUNCTION", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindLastCommaOutsideQuotedIdentifiers(string text)
    {
        int lastComma = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                int closing = FindClosingDoubleQuote(text, i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '`')
            {
                int closing = text.IndexOf('`', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == '[')
            {
                int closing = text.IndexOf(']', i + 1);
                if (closing < 0)
                    break;
                i = closing;
                continue;
            }
            if (c == ',')
                lastComma = i;
        }

        return lastComma;
    }
}
