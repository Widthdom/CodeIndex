---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Reduced `cdidx .` time after a branch or HEAD change.** Default incremental full scans no longer pre-extract every file solely because the stored full-scan HEAD differs from the current worktree HEAD. Unchanged files can now be reused before symbol/reference extraction, while the existing HEAD-change warning, stale-file purge, readiness stamping, and current-HEAD metadata update remain intact.

## 日本語

- **branch / HEAD 変更後の `cdidx .` 時間を短縮しました。** 既定の incremental full scan は、保存済み full-scan HEAD と現在の worktree HEAD が違うという理由だけで全ファイルを先行抽出しなくなりました。既存の HEAD 変更警告、stale file purge、readiness stamp、現在 HEAD metadata 更新は維持しつつ、未変更ファイルを symbol / reference 抽出前に再利用できます。
