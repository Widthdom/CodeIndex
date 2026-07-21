---
category: changed
affected:
  - src/CodeIndex/BoundedLineReader.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Parse.cs
  - src/CodeIndex/Cli/ProgramRunner.cs
  - src/CodeIndex/Cli/ConsoleUi.cs
  - src/CodeIndex/Cli/CliFlagSchema.cs
  - src/CodeIndex/Cli/EnvironmentVariableInventory.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.Files.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractionWorker.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerUpdateTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/BoundedLineReaderTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - USER_GUIDE.md
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Large-index diagnostics now identify expensive phases** — `index --json --memory-trace` reports C# prepass, extraction, reference-graph, text-index, finalization, and full-scan commit boundaries, while partial updates report the corresponding shared phases.
- **Automatic full-scan parallelism avoids high-core contention** — the default worker count now follows the CPU count up to eight instead of sixteen, while `--parallelism` and `CDIDX_INDEX_PARALLELISM` still accept explicit values through sixteen.
- **Reference resolution consolidates repeated graph lookups** — candidate counts, unique target families, target IDs, and stable keys are now derived by one aggregate per reference instead of repeated correlated subqueries across every supported language.
- **Unique symbol families are shared across graph fallbacks** — non-C#, C#, and C# attribute resolution now reuse one connection-local language/name aggregation instead of grouping the complete symbol table three times per graph refresh.
- **Source files cross the symbol-worker boundary once** — requests are serialized directly to UTF-8 bytes instead of materializing a UTF-16 JSON string and encoding it again for every file, with Unicode coverage across C#, Java, TypeScript, Python, Go, and Rust.
- **Symbol-worker responses stream as UTF-8** — extracted symbol batches are serialized directly to the worker stdout stream, removing the matching UTF-16 JSON allocation and re-encoding pass on the return path.
- **Worker responses stay as bytes in the parent** — a bounded, buffered UTF-8 frame reader now feeds response bytes directly to JSON deserialization while preserving frame limits, CRLF handling, cancellation, and persistent-worker remainder buffering.
- **Graph-neutral updates skip repository-wide recursion work** — incremental full scans, scoped updates, and MCP indexing now distinguish cleanup that actually changes symbol/reference identity rows, so text-only source edits avoid a whole-graph pass while symbol and reference mutations still refresh once per batch.

## 日本語

- **大規模 index の高コスト phase を診断しやすくしました** — `index --json --memory-trace` は C# prepass、extraction、reference graph、text index、finalize、full-scan commit の各境界を返し、partial update でも対応する共通 phase を返します。
- **高コア環境で automatic full-scan の競合を抑えました** — 既定 worker 数は CPU 数に追従しつつ上限を16から8へ下げました。`--parallelism` と `CDIDX_INDEX_PARALLELISM` の明示値は引き続き16まで指定できます。
- **reference resolution の重複 graph lookup を集約しました** — 全対応言語で candidate count、unique target family、target ID、stable key を reference ごとに1回の aggregate から導出し、相関 subquery の繰り返しをなくしました。
- **unique symbol family を graph fallback 間で共有しました** — non-C#、C#、C# attribute resolution は connection-local な language/name 集約を再利用し、graph refresh ごとに symbol table 全体を3回 group化しなくなりました。
- **source file の symbol-worker 境界通過を1回にしました** — file ごとに UTF-16 JSON string を作って再 encode せず、request を直接 UTF-8 byte へ serialize します。C#、Java、TypeScript、Python、Go、Rust を横断する Unicode coverage も追加しました。
- **symbol-worker response を UTF-8 stream にしました** — 抽出済み symbol batch を worker stdout stream へ直接 serialize し、戻り経路に残っていた UTF-16 JSON allocation と再 encoding pass もなくしました。
- **親 process 内でも worker response を byte のまま扱います** — bounded・buffered UTF-8 frame reader から response byte を JSON deserialize へ直接渡し、frame limit、CRLF、cancellation、persistent worker の remainder buffering を維持します。
- **graph-neutral な更新では repository 全体の再帰処理を省略します** — incremental full scan、scoped update、MCP indexing は symbol/reference identity 行を実際に変える cleanup を区別し、text-only な source 編集では全 graph pass を避けつつ、symbol/reference 変更時は batch ごとに1回だけ refresh します。
