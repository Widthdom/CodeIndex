# Git Range And Sparse Worktree Boundaries

## English

`cdidx index <projectPath> --changed-between <old-ref> <new-ref>` and
`--commits <commit-ref>` use Git name-status output to select partial-update
targets. Rename and copy records include both the old path and the new path so
stale DB rows can be purged, and delete records remain delete candidates when
the path is truly absent from the current checkout.

Sparse checkouts, partial clones, and manual
`git update-index --skip-worktree <path>` mark paths as intentionally absent.
When a Git-range target is missing from disk but still has the Git
skip-worktree bit, partial update and `--dry-run` leave the existing DB row
intact instead of treating the path as a real delete. The same path is reported
by `cdidx status --check --json` under `workspace_check.outside_sparse_cone_files`.

Use a full checkout, unset skip-worktree, or run a full rebuild when a path
outside the sparse cone should be indexed again or deliberately purged.

## 日本語

`cdidx index <projectPath> --changed-between <old-ref> <new-ref>` と
`--commits <commit-ref>` は、Git の name-status 出力から partial update の対象
path を選びます。rename / copy は旧 path と新 path の両方を対象に含めるため、
stale な DB row を purge できます。delete record は、現在の checkout で本当に
欠落している場合に delete 候補のまま扱われます。

sparse checkout、partial clone、手動の
`git update-index --skip-worktree <path>` は、path が意図的に worktree から
外れていることを Git index に記録します。Git-range の対象 path が disk 上に無く、
かつ Git の skip-worktree bit を持つ場合、partial update と `--dry-run` はその
path を実削除として扱わず、既存 DB row を保持します。同じ path は
`cdidx status --check --json` の `workspace_check.outside_sparse_cone_files` に
表示されます。

sparse cone の外にある path を再び index する、または意図的に purge する場合は、
full checkout に戻す、skip-worktree を解除する、または full rebuild を実行してください。
