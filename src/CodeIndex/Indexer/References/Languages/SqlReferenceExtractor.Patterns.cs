using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class SqlReferenceExtractor
{
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
    private static readonly Regex MergeUpdateSetActionRegex = new(
        @"(?<![\w$])WHEN\b[\s\S]*?\bTHEN\s+UPDATE\s+SET\s+(?<body>[\s\S]*?)(?=(?<![\w$])WHEN\b|;|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MergeInsertActionRegex = new(
        @"(?<![\w$])WHEN\b[\s\S]*?\bTHEN\s+INSERT\s*(?<columns>\((?:[^()]|\([^()]*\))*\))?(?:\s+VALUES\s*(?<values>\((?:[^()]|\([^()]*\))*\)))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MergeOnClauseRegex = new(
        @"(?<![\w$])MERGE\b[\s\S]*?\bON\s+(?<body>[\s\S]*?)(?=(?<![\w$])WHEN\b|;|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QualifiedColumnReferenceRegex = new(
        $@"(?<![\w$])(?:{QuotedIdentifierPattern}|{BareIdentifierPattern})\s*\.\s*(?<name>{QuotedIdentifierPattern}|{BareIdentifierPattern})",
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
    private static readonly Regex GeneratedColumnMarkerRegex = new(
        @"\b(?:GENERATED\s+(?:ALWAYS\s+)?AS|NEXT\s+VALUE\s+FOR)\b|(?<![\w$])AS\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GeneratedColumnExpressionStartRegex = new(
        @"\b(?:GENERATED\s+(?:ALWAYS\s+)?AS|AS)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DefaultNextValueForExpressionRegex = new(
        $@"\bDEFAULT\s+NEXT\s+VALUE\s+FOR\s+(?<name>{QualifiedIdentifierNoCapturePattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlExpressionIdentifierRegex = new(
        $@"(?<name>{QualifiedIdentifierNoCapturePattern})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingTempIdentifierRegex = new(
        $@"^(?:(?:ONLY)\b\s+)?(?<item>(?:{TempIdentifierPattern}|{QualifiedIdentifierNoCapturePattern}))(?:\s+(?:AS\s+)?(?:{QuotedIdentifierPattern}|{BareIdentifierPattern}))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MergeTargetHintContinuationPrefixRegex = new(
        $@"(?<![\w$])MERGE\b(?:\s+{TopTargetModifierPattern})?(?:\s+INTO)?\s+{QualifiedIdentifierNoCapturePattern}\s+WITH\s*\((?:[^()]|\([^()]*\))*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WindowClauseColumnRegex = new(
        $@"(?<![\w$]){QualifiedIdentifierPattern}(?:\s+(?:ASC|DESC)\b)?(?:\s+NULLS\s+(?:FIRST|LAST)\b)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WindowFrameKeywordRegex = new(
        @"(?<![\w$])(?<name>ROWS|RANGE|GROUPS|BETWEEN|UNBOUNDED|PRECEDING|FOLLOWING|CURRENT|ROW|EXCLUDE|TIES|OTHERS|NO)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

}
