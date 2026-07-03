---
category: internal
affected: Indexer
---

## English

- Deferred Solution and app manifest symbol list allocation until project or manifest symbols are actually emitted, avoiding empty lists for metadata files with no indexed entries.

## 日本語

- Solution / app manifest の symbol list を project や manifest symbol が実際に出るまで遅延確保し、indexed entry のない metadata file で空 List を作らないようにしました。
