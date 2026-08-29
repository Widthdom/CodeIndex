---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Cold graph finalization reuses scope candidates** — Language-independent reference ranks 1–4 now materialize their shared candidate relation once and select each reference's minimum rank from it, avoiding three repeated reference/name/language scans while retaining all best-rank ties.

## 日本語

- **初回 graph 確定で scope candidate を再利用** — 言語共通の reference rank 1〜4 は共有 candidate relation を1回だけ materialize し、そこから reference ごとの最小rankを選ぶようになりました。最良rankの全同順位を維持しながら、reference / name / language の重複走査を3回削減します。
