using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static void ApplyXmlSettingsAuditClassifications(DbReader reader, SearchAuditRecipeQuery query, List<SearchDisplayRow> rows)
    {
        if (!query.Classifiers.Any(classifier => classifier.Name == "xml_settings_evidence")) return;
        var session = reader.CreateXmlSettingsAuditSession();
        foreach (var row in rows)
        {
            row.Compact.AuditClassifications ??= [];
            row.Compact.AuditClassifications.Add(ClassifyXmlSettingsAuditResult(session, row.Compact));
        }
    }

    internal static void ApplyXmlSettingsAuditClassifications(DbReader reader, SearchAuditRecipeQuery query, List<CompactSearchResult> rows)
    {
        if (!query.Classifiers.Any(classifier => classifier.Name == "xml_settings_evidence")) return;
        var session = reader.CreateXmlSettingsAuditSession();
        foreach (var row in rows)
        {
            row.AuditClassifications ??= [];
            row.AuditClassifications.Add(ClassifyXmlSettingsAuditResult(session, row));
        }
    }

    private static SearchAuditClassificationJsonResult ClassifyXmlSettingsAuditResult(DbReader.XmlSettingsAuditSession session, CompactSearchResult row)
    {
        var evidence = session.Classify(row.Path, row.Lang,
            row.MatchFacets.Select(facet => facet.Line).Concat(row.MatchLines).Distinct().Take(17).ToArray());
        return new SearchAuditClassificationJsonResult(
                "xml_settings_evidence", evidence.State,
                "Source-backed XML configuration observation; not a vulnerability verdict.",
                "Review input trust and runtime use. Unknown, ambiguous or changed settings remain reviewable. Baseline reviews do not prove safety.",
                [$"reason:{evidence.Reason}", "match_confidence:textual_match_only", "vulnerability_confidence:not_established"])
        { XmlSettings = evidence };
    }
}
