using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    // Never interpret legacy, stale, NULL, or unknown identity evidence as unresolved.
    internal string DependencyResolutionStateSql(string referenceAlias = "r")
        => HasCurrentReferenceIdentityContractForRead() && _referenceColumns.Contains("resolution_state")
            ? $"CASE WHEN {referenceAlias}.resolution_state IN ('resolved', 'resolved_group', 'ambiguous', 'unresolved') THEN {referenceAlias}.resolution_state ELSE 'unavailable' END"
            : "'unavailable'";

    private DependencySqlFragment BuildDependencyEvidenceFilter(
        DependencyEvidenceFilter? filter,
        string parameterPrefix)
    {
        if (filter == null || !filter.IsActive)
            return DependencySqlFragment.Empty;
        var builder = new DependencySqlFragmentBuilder();
        AppendValues(filter.Resolutions, DependencyResolutionStateSql(), "Resolution");
        if (filter.Kinds.Count > 0)
        {
            var predicates = new List<string>();
            for (var i = 0; i < filter.Kinds.Count; i++)
            {
                var name = SqliteDynamicSql.BuildParameterName(parameterPrefix + "Kind", i);
                // Canonical subscribe expands raw event kinds; explicit raw kinds stay exact.
                var expression = filter.Kinds[i] == "subscribe"
                    ? GetLogicalReferenceKindSql("r.reference_kind")
                    : "r.reference_kind";
                predicates.Add($"{expression} = {name}");
                builder.AddText(name, filter.Kinds[i]);
            }
            builder.Append(" AND (" + string.Join(" OR ", predicates) + ")");
        }
        return builder.Build();

        void AppendValues(IReadOnlyList<string> values, string expression, string suffix)
        {
            if (values.Count == 0)
                return;
            var names = new List<string>();
            for (var i = 0; i < values.Count; i++)
            {
                var name = SqliteDynamicSql.BuildParameterName(parameterPrefix + suffix, i);
                names.Add(name);
                builder.AddText(name, values[i]);
            }
            builder.Append($" AND {expression} IN ({string.Join(",", names)})");
        }
    }

    internal void AppendDependencyEvidenceFilter(SqliteCommand command, DependencyEvidenceFilter filter)
    {
        var fragment = BuildDependencyEvidenceFilter(filter, "crossEvidence");
        command.CommandText += fragment.Sql;
        BindDependencyQueryParameters(command, fragment.Parameters);
    }
}
