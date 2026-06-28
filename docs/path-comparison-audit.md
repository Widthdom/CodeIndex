# Path Comparison And Normalization Audit

> **[日本語版はこちら / Japanese version](#パス比較と正規化の監査)**

This note records the issue #4071 audit classification for high-count
`OrdinalIgnoreCase` and `Path.GetFullPath` hits. It distinguishes path equality
from intentionally case-insensitive protocol, option, schema, and language
domains.

## English

### Domain Classification

| Domain | Representative files | Policy |
|---|---|---|
| Indexed path equality and path sets | `DbReader.GraphQueries`, `RepoMapBuilder` | Use the persisted `workspace_path_case_sensitive` / `path_case_sensitive` stamp. Case-sensitive indexes keep `Foo.cs` and `foo.cs` distinct; legacy or case-insensitive indexes preserve the historical case-insensitive behavior. |
| Workspace and cleanup boundaries | `PathCasing`, `FileSystemBoundary`, `DbPathResolver`, `DataDirectorySecurity`, `McpPathBoundary`, `LspServer` | Normalize with `Path.GetFullPath`, then compare through `PathCasing` / `FileSystemBoundary` so containment follows the probed filesystem case policy. Cleanup targets also reject symlink, reparse-point, and device targets where deletion could cross a boundary. |
| URI and SQLite sidecar paths | `FileUriPolicy`, `PathUriNormalizer`, `DbPathResolver`, `ExportImportCommandRunner`, `SqliteConnectionPolicy` | Reject encoded URI path-boundary characters before normalization. SQLite DB sidecar overlap checks use both the live filesystem comparison and the stored DB path-case stamp when available. |
| Extension and filename conventions | `FileIndexer`, `FileIndexer.FileNameLanguages`, `FileIndexer.DefaultExclusions`, `SymbolExtractor.TypeScriptPathAliases` | Keep case-insensitive matching because these are language, extension, generated-file, and cross-platform convention domains, not file identity comparisons. |
| Language and schema identifiers | `ReferenceExtractor`, `LanguageReferenceExtractionSupport`, `SqlReferenceExtractor`, `DbSearchReader`, `DbSymbolReader`, `DbSchemaCache`, `RepoMapBuilder` name hints | Keep case-insensitive comparison where the language, SQL schema, SQLite schema, search-token, or entrypoint-name contract is case-insensitive. |
| CLI, MCP, labels, headers, and recipes | `QueryCommandRunner`, `ProgramRunner`, `McpToolHandlers`, `HttpMcpTransport`, `IssueDuplicatePreflight`, `GitHubIssueReporter`, `SearchAuditRecipes` | Keep case-insensitive matching for protocol tokens, option values, JSON formats, labels, HTTP headers, recipe names, and GitHub metadata. |
| Plugin, hook, and configured extractor paths | `ExtractorPluginRegistry`, `PostExtractionHooks`, `LanguageMapOverrides` | Normalize discovered paths, apply explicit trust-boundary checks, and reject unsafe symlink/reparse overrides where applicable. Registry-local duplicate suppression is not a workspace path-equality decision; new user-visible path identity checks should use `PathCasing` / `FileSystemBoundary`. |

### Changes From This Audit

- `map` entrypoint fallback de-duplication now uses the indexed workspace path
  case policy instead of always using `OrdinalIgnoreCase`.
- `impact` definition-file ambiguity now uses the same indexed workspace path
  case policy instead of collapsing case-variant paths unconditionally.
- Regression tests cover both case-sensitive and case-insensitive indexed
  workspace behavior for these path sets.

---

<a id="パス比較と正規化の監査"></a>
# パス比較と正規化の監査

このメモは issue #4071 の監査分類を記録します。高頻度の
`OrdinalIgnoreCase` と `Path.GetFullPath` の検出結果について、パス同一性の判定と、
プロトコル、オプション、スキーマ、言語など意図的に case-insensitive な領域を分けます。

## 日本語

### 領域分類

| 領域 | 代表ファイル | 方針 |
|---|---|---|
| indexed path の同一性と path set | `DbReader.GraphQueries`, `RepoMapBuilder` | 永続化された `workspace_path_case_sensitive` / `path_case_sensitive` stamp を使う。case-sensitive な index では `Foo.cs` と `foo.cs` を別 path として扱い、legacy または case-insensitive な index では従来どおり case-insensitive に扱う。 |
| workspace と cleanup boundary | `PathCasing`, `FileSystemBoundary`, `DbPathResolver`, `DataDirectorySecurity`, `McpPathBoundary`, `LspServer` | `Path.GetFullPath` で正規化したあと、`PathCasing` / `FileSystemBoundary` 経由で比較し、containment を実 FS の大小区別ポリシーに合わせる。削除系 target は boundary を越え得る symlink、reparse point、device も拒否する。 |
| URI と SQLite sidecar path | `FileUriPolicy`, `PathUriNormalizer`, `DbPathResolver`, `ExportImportCommandRunner`, `SqliteConnectionPolicy` | 正規化前に encoded URI path boundary 文字を拒否する。SQLite DB sidecar の重なり判定は、利用可能なら live filesystem comparison と保存済み DB path-case stamp の両方を使う。 |
| 拡張子とファイル名の慣習 | `FileIndexer`, `FileIndexer.FileNameLanguages`, `FileIndexer.DefaultExclusions`, `SymbolExtractor.TypeScriptPathAliases` | ここは file identity ではなく、言語、拡張子、generated file、cross-platform convention の領域なので case-insensitive のままにする。 |
| 言語・schema identifier | `ReferenceExtractor`, `LanguageReferenceExtractionSupport`, `SqlReferenceExtractor`, `DbSearchReader`, `DbSymbolReader`, `DbSchemaCache`, `RepoMapBuilder` name hints | 言語、SQL schema、SQLite schema、search token、entrypoint name の契約が case-insensitive な箇所は case-insensitive のままにする。 |
| CLI、MCP、label、header、recipe | `QueryCommandRunner`, `ProgramRunner`, `McpToolHandlers`, `HttpMcpTransport`, `IssueDuplicatePreflight`, `GitHubIssueReporter`, `SearchAuditRecipes` | protocol token、option value、JSON format、label、HTTP header、recipe name、GitHub metadata は case-insensitive matching を維持する。 |
| plugin、hook、configured extractor path | `ExtractorPluginRegistry`, `PostExtractionHooks`, `LanguageMapOverrides` | discovery path を正規化し、明示的な trust-boundary check を適用する。該当する override では unsafe な symlink / reparse point を拒否する。registry 内部の duplicate suppression は workspace path equality ではないため、新しいユーザー可視の path identity 判定では `PathCasing` / `FileSystemBoundary` を使う。 |

### この監査での変更

- `map` の entrypoint fallback 重複排除は、常に `OrdinalIgnoreCase` を使うのではなく、indexed workspace の path case policy を使うようになった。
- `impact` の definition file 曖昧性判定も同じ indexed workspace path case policy を使い、case variant path を無条件に畳み込まないようになった。
- これらの path set について、case-sensitive / case-insensitive 両方の indexed workspace 挙動を regression test で固定した。
