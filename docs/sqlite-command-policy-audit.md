# SQLite Command Policy Audit

Issue #4070 audited `CommandText` and `PRAGMA` construction in production code. The current dogfood commands are:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search --recipe dogfood-risk-patterns/raw-sql-command-text --path src/ --exclude-tests --count --group-by file --limit 80
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search --recipe dogfood-risk-patterns/pragma-command --path src/ --exclude-tests --count --group-by file --limit 80
```

| Category | Files / examples | Policy |
|---|---|---|
| Constant SQL with parameterized values | `DbReader*`, `DbWriter`, `DiffCommandRunner`, `ExportImportCommandRunner`, `DbPathResolver`, `IndexCommandRunner*`, `PreparedCommandCache`, `SqliteConnectionPolicy`, `McpServer` | Keep values in `SqliteCommandPolicy.Add*` helpers or explicit typed parameters. |
| Dynamic query fragments with fixed internal clauses | `DbSymbolReader`, `DbReader.GraphQueries`, `DbReader.CSharpResolution`, `DbReader.References`, `DbSearchReader`, `RepoMapBuilder`, `QueryCommandRunner` | Only compose repository-owned SQL fragments such as filters, sort expressions, and CTEs; bind user values separately. |
| Dynamic identifiers and schema discovery | `DbContext`, `DbSchemaCache`, `ReportCommandRunner` | Route table/index/column names through `SqliteIdentifier.Quote` or `SqliteCommandPolicy` helpers such as `TableInfoPragmaSql`. |
| PRAGMA reads and writes | `DbContext`, `DbWriter`, `DbCommandRunner`, `DbReader.FilesStatus`, `DbSchemaCache`, `DiffCommandRunner`, `ReportCommandRunner` | Prefer constant names or specific helper methods. Runtime PRAGMA values are constrained through `DbPragmaPolicy`. |
| Migration, DDL, and diagnostics | `DbContext`, `DbWriter`, `DbDebug`, `DbCommandRunner`, `ReportCommandRunner` | Migration SQL stays repository-owned and stable; diagnostics should remain bounded and avoid raw user data. |

Any new raw SQL construction should fit one of these categories. If it needs a new PRAGMA or identifier path, add a narrow helper instead of interpolating the final command inline.
