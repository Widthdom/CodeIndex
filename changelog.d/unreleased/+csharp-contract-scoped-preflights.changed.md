---
category: changed
affected:
  - src/CodeIndex/Cli/GitHelper.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.UpdateTargets.cs
  - src/CodeIndex/Database/DbContext.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.CSharpContracts.cs
  - src/CodeIndex/Database/DbWriter.FileCleanup.cs
  - src/CodeIndex/Database/DbWriter.FilePurge.cs
  - src/CodeIndex/Database/DbWriter.FileReuse.cs
  - src/CodeIndex/Database/DbWriter.Files.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Indexer/Extensibility/ExtractorPluginRegistry.Discovery.cs
  - src/CodeIndex/Indexer/Extensibility/ExtractorPluginRegistry.PatternConfig.cs
  - src/CodeIndex/Indexer/Extensibility/ExtractorPluginRegistry.cs
  - src/CodeIndex/Indexer/Hooks/PostExtractionHooks.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ContentLoadingContracts.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryEnumeration.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryTraversal.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Gitmodules.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.IgnoreRuleLoading.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.LanguageDetection.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanInputSnapshot.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Types.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/LanguageMapOverrides.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - src/CodeIndex/Mcp/McpToolHandlers.QueryTools.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.HookIsolationFixture/HookIsolationFixture.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerUpdateTests.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/GitHelperTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/PostExtractionHookTests.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **C# static-interface preflights now avoid repository-wide work while preserving incremental references** — scoped updates trust authoritative negative source evidence, use bounded path-first transition probes, group common checksum/stem cleanup keys, and reuse stat-matched persisted checksums instead of rescanning the full index or reopening every unchanged target. Git-derived update paths are planned directly from persisted C# rows, so commit/range updates do not stream every live target again merely to compute cleanup checksums; NUL-delimited Git name-status parsing preserves non-ASCII, tab, and newline paths without quoted-path ambiguity. Incomplete-workspace deferral resolves all candidate paths to persisted C# rows in cache-neutral batches of at most 500 instead of issuing one SQLite query per non-C# target. Persisted workspace reads now probe member-capable `symbols(file_id, kind)` rows first and fetch interface declarations only for exact retained container names, so negative and LIKE-decoy-only runs never materialize every plain interface. Full CLI and MCP scans skip source, persisted-symbol, and lookup materialization for an unchanged authoritative true or false workspace only when the prior index is explicitly complete and GraphReady and all filter/version/root/hotspot/stat/language-transition checks still match; legacy or partial metadata falls back safely. Full scans neither create nor consume HEAD-only directory checkpoints and delete legacy checkpoint files after their first immutable input barrier. Each scanned directory contributes one pre-listing baseline, while content/identity-bound ignore, language-map, pattern, submodule, and nested-repository inputs share the same bounded snapshot; pattern enumeration and safety failures make it incomplete instead of silently dropping rules. Full, expanded-update, and MCP consumers validate that snapshot immediately before domain/index-state mutation and again before readiness without retaining the former after-traversal stat map. First-barrier instability preserves prior rows, trust, evidence, purge, and FTS recovery state; C# file drift found while still read-only can promote one complete raw prepass and all-C# refresh, while later drift, fatal discovery gaps, reappeared cleanup paths, and binary/oversized skip-record races roll back or defer mutations and leave evidence unknown for a clean retry. Interrupted-FTS and rebuild repairs run after the first barrier when a scan snapshot is present, and otherwise at the mode-specific mutation boundary. Partial MCP scans derive an authoritative retained/purge plan from successfully listed subtrees and conservatively transition absent persisted C# rows only where discovery was complete. Cleanup candidate SQL stays C#-scoped and cache-neutral in batches of at most 500. Indexed ASCII `NOCASE` and managed case folding only prefilter alias candidates; deletion requires a non-exact spelling and a matching filesystem file identity inside the same case-fold bucket, preserving distinct Unicode pairs, mixed-policy paths, and cross-target hardlinks. Raw built-in source evidence is observed before hooks, filters, and row caps, and managed keyword-boundary validation rejects LIKE-only decoys.

## 日本語

- **C# static-interface preflight が incremental reference を維持しながら repository 全体の処理を避けるようになりました** — scoped update は authoritative な negative source evidence を利用し、bounded な path-first transition probe、共通 checksum/stem cleanup key のgroup化、stat一致の永続checksum再利用によって、full index scanや全未変更targetの再openを避けます。Git由来のupdate pathは永続C# rowから直接planするため、commit/range updateはcleanup checksumだけのために全live targetを再streamしません。NUL区切りのGit name-status解析により、non-ASCII・tab・改行を含むpathもquoted-pathの曖昧さなく保持します。incomplete workspaceの退避では、non-C# targetごとにSQLite queryを発行せず、全candidate pathをcache-neutralな最大500件batchで永続C# rowへ解決します。永続workspace readはmember候補の `symbols(file_id, kind)` を先にprobeし、厳密に保持したcontainer名のinterface宣言だけを取得するため、negativeまたはLIKE decoyだけのrunでは通常interface全体をmaterializeしません。full CLI/MCPは、以前のindexが明示的にcompleteかつGraphReadyで、filter/version/root/hotspot/stat/language-transition条件がすべて一致するauthoritativeなtrue/false workspaceだけを、source・永続symbol・lookup materializationが0のshortcutにします。legacy/partial metadataは保守的にfallbackします。full scanはHEAD-only directory checkpointを作成も参照もせず、最初のimmutable input barrier後に旧checkpoint fileを削除します。各走査directoryはlisting前baselineを1回だけ記録し、内容・identityに結び付いたignore/language-map/pattern/submodule設定とnested-repository入力を同じbounded snapshotへ集約します。pattern列挙・安全性検査の失敗はruleを暗黙に落とさずsnapshotをincompleteにします。full、expanded update、MCPは旧after-traversal stat mapを保持せず、このsnapshotをdomain/index-state mutation直前とreadiness直前の2回だけ検証します。first barrierの不安定性は以前のrow・trust・evidence・purge・FTS recovery状態を保持します。read-only段階のC# file driftはcomplete raw prepass 1回と全C# refreshへ昇格でき、その後のdrift、fatal discovery gap、cleanup path再出現、binary/oversized skip-record raceはmutationをrollbackまたは延期して、evidenceをunknownのままclean retryへ委ねます。中断FTSとrebuildのrepairはscan snapshotがある経路ではfirst barrier後、それ以外ではmode固有のmutation境界で実行します。partial MCP scanは正常にlistingできたsubtreeからauthoritativeなretained/purge planを作り、discoveryがcompleteな範囲だけで欠落した永続C# rowを保守的にtransitionします。cleanup候補SQLはC#限定・cache-neutralな最大500件batchを維持します。indexed ASCII `NOCASE` とmanaged case foldingはalias候補の絞り込みだけに使い、削除にはnon-exact spellingと同じcase-fold bucket内のfilesystem file identity一致を必須とするため、distinctなUnicode pair・mixed case-policy path・cross-target hardlinkを保持します。raw built-in source evidenceはhook・filter・row capより前に観測し、managed keyword境界判定でLIKEだけに一致するdecoyを拒否します。
