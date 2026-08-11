---
category: fixed
affected:
  - src/CodeIndex/Cli/ProgramRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - src/CodeIndex/Database/ReferenceSecondaryIndexBulkLoadGuard.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Indexer/CSharpPrepassSymbolArtifactCache.cs
  - src/CodeIndex/Indexer/Extensibility/ExtractorPluginRegistry.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Cpp.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Css.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractCore.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractionPhases.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Patterns.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractionWorker.cs
  - src/CodeIndex/WorkerProtocolJsonValidator.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
  - tests/CodeIndex.Tests/ExtractorPluginRegistryTests.cs
  - tests/CodeIndex.Tests/CSharpPrepassSymbolArtifactCacheTests.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/FreshReferenceResolutionTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerReferenceIndexBulkLoadTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/ReferenceSecondaryIndexBulkLoadGuardTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorRequiredLiteralGateTests.cs
  - tests/CodeIndex.Tests/SymbolExtractionWorkerUtf8ProtocolTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Fresh large-codebase indexes finalize mutual-recursion edges without duplicate reverse lookups** — reference graph finalization now materializes each candidate edge's desired recursion flag once, avoiding a costly second set of random B-tree probes when only a small number of flags change.
- **Fresh and rebuilt indexes skip unused incremental graph bookkeeping** — once a full reference-graph refresh is known, symbol and reference batches no longer populate dirty-scope tables that the full plan never reads, removing repeated set construction across all indexed languages.
- **Initial C# workspace prepasses reuse the loaded extractor configuration** — CLI and MCP indexing no longer rediscover default plugins under a shared lock for every static/enum/const candidate after the workspace pattern snapshot has already been loaded.
- **First-time full indexes resolve only references that have candidates** — empty-database CLI scans persist canonical unresolved defaults during bulk insertion, aggregate the candidate table once, and update candidate-bearing references by primary key instead of probing every reference row. Rebuilds, updates, retained graphs, and MCP indexing keep their existing recovery contracts.
- **Fresh graph planning uses post-load cardinalities** — truly empty CLI and MCP bulk loads now analyze the populated file, symbol, and reference tables immediately before candidate resolution, allowing SQLite to plan the expensive first graph build from current statistics. Rebuilds, updates, existing databases, and symbols-only runs keep their prior lifecycle; cancellation still aborts, while a statistics-only SQLite failure rolls back its savepoint and continues best-effort.
- **Persistent symbol workers reuse bounded pattern-directory snapshots across languages** — each project-root reload discovers user and root configs once, while nested ancestor directories, including missing and rejected results, are observed once per worker command. The cache follows live filesystem casing and falls back to uncached discovery when full so configs are never skipped; direct registry callers keep dynamic discovery.
- **First-time C# extraction reuses checksum-verified prepass symbols** — empty-database CLI and MCP full indexes can consume bounded, take-once built-in symbols already extracted for the static-interface workspace. After materializing the immutable lookup snapshots, the prepass transfers ownership of admitted per-file symbol lists and releases the redundant workspace fallback objects instead of cloning the full symbol graph. The main pass still rereads and validates every file and falls back on checksum drift, incomplete prepasses, regex timeouts, or cache limits; rebuilds, updates, and symbols-only runs remain unchanged.
- **Initial indexes skip regex patterns whose mandatory literals are absent** — built-in case-sensitive symbol patterns now opt into an audited, Ordinal two-or-more-character literal gate both at file selection and immediately before each regex call against its exact transformed input. Pattern order and output stay unchanged across C#/Fortran merges, Java/Kotlin annotation stripping, C# wrapped-modifier and incomplete-attribute recovery, C++ same-line members, and CSS reconstructed selector segments; a bare C# static-constructor gate miss still reaches the synthesized `static ...` retry. IgnoreCase, custom/plugin, one-character, and no-common-literal patterns remain ungated.
- **Symbol workers consume the existing UTF-8 request frames without decoding them twice** — the parent keeps its single `SerializeToUtf8Bytes` write, while the all-language child path now performs bounded newline framing, validation, and deserialization directly from raw standard-input bytes. CRLF/final-EOF framing, protocol and JSON bounds, cancellation, Unicode behavior, and sanitized invalid-UTF-8/JSON errors remain unchanged; the decoded `TextReader` path stays available for diagnostics.

## 日本語

- **巨大コードベースの新規インデックスで相互再帰 edge の reverse lookup を重複実行しないようにしました** — reference graph の確定時に各候補 edge の望ましい recursion flag を一度だけ materialize し、変更対象の flag が少数の場合に発生していた高コストな2回目のランダム B-tree probe を避けます。
- **新規作成および rebuild 時に未使用の差分 graph bookkeeping を省くようにしました** — reference graph の full refresh が確定した後は、その plan が参照しない dirty scope table を symbol / reference batch ごとに投入せず、全インデックス対象言語にまたがる反復的な set 構築を取り除きます。
- **初回 C# workspace prepass で読込済み extractor config を再利用するようにしました** — workspace pattern snapshot の読込後に、static / enum / const の候補ごとに共有lock下でdefault pluginを再探索しないよう、CLIとMCP indexingを既読込経路へ接続します。
- **初回full indexでcandidateを持つreferenceだけを解決するようにしました** — 空databaseからのCLI scanではbulk insert中にcanonicalな未解決値を保存し、candidate tableを1回集約して、全reference rowをprobeせずcandidateを持つrowだけをprimary keyで更新します。rebuild、update、retained graph、MCP indexingの既存recovery契約は変更しません。
- **新規graph planningでbulk load後のcardinalityを使うようにしました** — 真に空のdatabaseから始まるCLI / MCP bulk loadでは、candidate解決の直前に投入済みのfile、symbol、reference tableを解析し、SQLiteが初回の高コストなgraph構築を最新統計から計画できるようにします。rebuild、update、既存database、symbols-onlyのlifecycleは従来どおりです。cancellationは引き続き中断し、統計更新だけのSQLite failureはsavepointを戻してbest-effortで継続します。
- **persistent symbol worker が全言語で上限付き pattern-directory snapshot を再利用するようにしました** — project-root reload ごとに user / root config を1回だけ探索し、missing や reject を含む nested ancestor directory は worker command ごとに初回結果を再利用します。cache は実 filesystem の case policy に従い、飽和時は uncached discovery に fallback して config を skip しません。registry の direct caller は従来どおり動的に探索します。
- **初回 C# extraction で checksum 検証済み prepass symbol を再利用するようにしました** — 空 database からの CLI / MCP full index は、static-interface workspace 用に抽出済みの built-in symbol を上限付き・take-once で利用できます。immutable な lookup snapshot を materialize した後、prepass は admit した file ごとの symbol list の所有権を移し、symbol graph 全体を clone せず重複する workspace fallback object を解放します。main pass は各 file を引き続き再読込・検証し、checksum drift、不完全な prepass、regex timeout、cache 上限では通常 extraction へ fallback します。rebuild、update、symbols-only は従来どおりです。
- **初回 index で必須 literal がない正規表現 pattern を skip するようにしました** — built-in の case-sensitive symbol pattern は、file 選択時と各 regex call の直前に、実際の変換済み input に対して監査済みの2文字以上の literal を Ordinal で判定します。C# / Fortran の結合、Java / Kotlin annotation 除去、C# wrapped-modifier / 不完全 attribute recovery、C++ same-line member、CSS の再構成済み selector segment でも pattern 順と出力を変えず、bare C# static constructor の初回 gate miss 後も合成した `static ...` を再試行します。IgnoreCase、custom/plugin、1文字、共通 literal を持たない pattern は gate 対象外です。
- **symbol worker が既存の UTF-8 request frame を二重 decode せず処理するようにしました** — parent 側の `SerializeToUtf8Bytes` による1回の書き込みは変えず、全言語共通の child 経路で標準入力の raw byte から上限付き newline framing、validation、deserialize を直接行います。CRLF / final EOF の framing、protocol / JSON 上限、cancellation、Unicode の挙動、不正 UTF-8 / JSON の sanitization 済み error は従来どおりで、decoded `TextReader` 経路も診断用に維持します。
