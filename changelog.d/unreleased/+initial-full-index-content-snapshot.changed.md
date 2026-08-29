---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.RawBytes.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.UnknownLanguage.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
---

## English

- **Stable cold-index file reads no longer probe past their initial snapshot** — language-common content loads, negative raw-token scans, the C# static-interface prepass, and unknown-language coverage stop at the initial handle length on their first attempt and validate the final handle metadata plus actual bytes read instead of issuing a redundant EOF or `ReadByte` growth probe. Mutated files still reopen once for a bounded EOF retry, over-limit growth still fails on the current handle, and positive raw-token matches remain conservative.

## 日本語

- **cold index時のstableなfile readがinitial snapshotを越えてprobeしなくなりました** — 言語共通content load、negative raw-token scan、C# static-interface prepass、unknown-language coverageは、初回attemptをinitial handle lengthで停止し、冗長なEOFまたは`ReadByte` growth probeの代わりにfinal handle metadataと実読byte数を検証します。mutation時は従来どおり1回だけ再openしてbounded EOF retryを行い、上限超過growthは現在のhandleで失敗し、positive raw-token matchは保守的な判定を維持します。
