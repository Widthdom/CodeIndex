---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.Fts.cs
  - src/CodeIndex/Database/DbWriter.FilePurge.cs
  - src/CodeIndex/Database/DbWriter.CSharpContracts.cs
  - src/CodeIndex/Database/DbWriter.FileReuse.cs
  - src/CodeIndex/Database/FtsBulkLoadTriggerGuard.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/JsonOutputContracts.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Mcp/McpServer.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Interrupted bulk FTS setup and cleanup can recover without a process restart** — If trigger suspension fails after the owner marker is written, including after a partial trigger drop, or if restoration or rebuild fails while a bulk guard is unwinding, the writer now best-effort downgrades the marker to owner-independent recovery state and rethrows the original error. A later request in the same process can restore triggers, rebuild searchable FTS state, and clear the marker.
- **Large incremental indexes now avoid recurring full-index FTS optimize** — Full-scan and MCP indexing switch to trigger-free bulk FTS rebuild/optimize only when at least three-fifths of the indexed byte set is dirty. Rewritten files use the larger of their persisted and current sizes, and the total restores shrink excess while also accounting for planned stale deletions, so large rewrites, deletes, and renames choose the policy from a consistent pre-update footprint. The purge runs inside the selected FTS guard. Planned stale IDs are excluded from C# static-interface and reusable-stat snapshots, preventing both obsolete implicit references and a plan-to-scan reappearing file from being skipped after its old row is purged. Snapshot path filtering is allocated only when non-purged indexed rows unused by the current targets outnumber those targets. MCP also invalidates C# contract reuse when a purge removes static-interface contracts, so a clean retry repairs implementers left unprocessed by a partial scan. Smaller updates and scoped CLI updates replace the recurring full optimize with an incremental segment merge whose minimum work target is 1,000 pages. SQLite processes complete segments, so the actual page count may exceed that target. A dedicated merge cadence leaves the since-optimize counter intact for `optimize --dry-run` recommendations, while transactional purge cancellation and bulk-guard abandonment preserve FTS consistency. Fresh and explicit rebuild behavior remains unchanged, and scoped JSON adds `fts_merge_ran` without removing the legacy optimize signal.

## 日本語

- **bulk FTS setup / cleanup の中断後も process restart なしで recovery できるようになりました** — owner marker 書込後の trigger suspend（partial trigger drop 後を含む）、または bulk guard unwind 中の trigger 復元・rebuild が失敗した場合、marker を owner 非依存の recovery state へ best-effort で降格し、元の error を再送出します。同一 process の後続 request が trigger を復元し、検索可能な FTS state を rebuild して marker を消去できます。
- **巨大な incremental index で反復的な full-index FTS optimize を避けるようになりました** — full-scan と MCP indexing は indexed byte 集合の5分の3以上が dirty の場合だけ trigger-free bulk FTS rebuild / optimize に切り替えます。rewrite file は永続化済み size と current size の大きい方を使い、total には shrink 差分と stale 削除予定 byte も含めるため、大規模 rewrite・delete・rename を一貫した更新前 footprint で判定します。purge 自体も選択した FTS guard 内で行います。stale plan の ID は C# static-interface と reusable-stat の snapshot から除外し、obsolete な implicit reference に加え、plan 後から scan までに再出現した file が旧 row の purge 後に skip されることも防ぎます。snapshot の path filter は、current target に使われない非 purge indexed row が current target より多い場合だけ確保します。MCP は static-interface contract を削除する purge で C# contract reuse も invalid にするため、partial scan で未処理になった implementer は次の clean retry で修復されます。小規模更新と scoped CLI update は定期的な full optimize を最小 work target 1,000 page の incremental segment merge に置き換えます。SQLite は完全な segment 単位で処理するため、実際の page 数は target を超える場合があります。専用 merge cadence により since-optimize counter は `cdidx optimize --dry-run` recommendation 用に維持され、transactional purge cancellation と bulk guard の abandon 処理が FTS 整合性を保ちます。fresh と明示的 rebuild の挙動は維持し、scoped JSON には従来の optimize signal を残したまま `fts_merge_ran` を追加します。
