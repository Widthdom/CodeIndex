---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.Fts.cs
---

## English

- **Large FTS rebuilds avoid repeated automatic segment merges before their final optimize** — The standard and trigram indexes now disable routine automerge only around each transactional rebuild, restore their prior settings before commit, and retain crisis merging as a safety valve. Cancellation and failure roll back the configuration together with the rebuild.

## 日本語

- **大規模 FTS rebuild では最終 optimize 前の自動 segment merge を繰り返さないようになりました** — standard / trigram index は各 transaction 内の rebuild 中だけ通常 automerge を無効化し、commit 前に以前の設定へ戻します。crisis merge は安全弁として維持し、cancellation / failure 時は設定と rebuild を一緒に rollback します。
