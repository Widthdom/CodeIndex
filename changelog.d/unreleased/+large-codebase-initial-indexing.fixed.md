---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - tests/CodeIndex.Tests/FreshReferenceResolutionTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerReferenceIndexBulkLoadTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Fresh large-codebase indexes finalize mutual-recursion edges without duplicate reverse lookups** — reference graph finalization now materializes each candidate edge's desired recursion flag once, avoiding a costly second set of random B-tree probes when only a small number of flags change.
- **Fresh and rebuilt indexes skip unused incremental graph bookkeeping** — once a full reference-graph refresh is known, symbol and reference batches no longer populate dirty-scope tables that the full plan never reads, removing repeated set construction across all indexed languages.
- **Initial C# workspace prepasses reuse the loaded extractor configuration** — CLI and MCP indexing no longer rediscover default plugins under a shared lock for every static/enum/const candidate after the workspace pattern snapshot has already been loaded.
- **First-time full indexes resolve only references that have candidates** — empty-database CLI scans persist canonical unresolved defaults during bulk insertion, aggregate the candidate table once, and update candidate-bearing references by primary key instead of probing every reference row. Rebuilds, updates, retained graphs, and MCP indexing keep their existing recovery contracts.

## 日本語

- **巨大コードベースの新規インデックスで相互再帰 edge の reverse lookup を重複実行しないようにしました** — reference graph の確定時に各候補 edge の望ましい recursion flag を一度だけ materialize し、変更対象の flag が少数の場合に発生していた高コストな2回目のランダム B-tree probe を避けます。
- **新規作成および rebuild 時に未使用の差分 graph bookkeeping を省くようにしました** — reference graph の full refresh が確定した後は、その plan が参照しない dirty scope table を symbol / reference batch ごとに投入せず、全インデックス対象言語にまたがる反復的な set 構築を取り除きます。
- **初回 C# workspace prepass で読込済み extractor config を再利用するようにしました** — workspace pattern snapshot の読込後に、static / enum / const の候補ごとに共有lock下でdefault pluginを再探索しないよう、CLIとMCP indexingを既読込経路へ接続します。
- **初回full indexでcandidateを持つreferenceだけを解決するようにしました** — 空databaseからのCLI scanではbulk insert中にcanonicalな未解決値を保存し、candidate tableを1回集約して、全reference rowをprobeせずcandidateを持つrowだけをprimary keyで更新します。rebuild、update、retained graph、MCP indexingの既存recovery契約は変更しません。
