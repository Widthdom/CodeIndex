---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/DockerfileReferenceExtractor.cs
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
---

## English

- **Dockerfile reference extraction was split out of the large shared extractor** - named stage dependency detection for `FROM ... AS` and `COPY --from=` now lives in a dedicated helper while preserving indexed reference behavior.

## 日本語

- **Dockerfile reference 抽出を大きな共通 extractor から分離しました** - `FROM ... AS` と `COPY --from=` の名前付き stage 依存検出を専用 helper に移し、indexed reference の挙動は維持しました。
