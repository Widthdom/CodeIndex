---
category: internal
affected: Indexer
---

## English

- Deferred MSBuild symbol and XML traversal stack allocation until symbols or nesting state are actually needed, reducing empty state for lightweight project files.

## 日本語

- MSBuild の symbol list と XML traversal stack を symbol やネスト状態が実際に必要になるまで遅延確保し、軽量な project file の空状態を減らしました。
