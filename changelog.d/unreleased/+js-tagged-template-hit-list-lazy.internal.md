---
category: internal
affected: Indexer
---

## English

- Reduced small allocations while grouping JavaScript and TypeScript tagged-template reference hits by keeping single-hit lines as compact read-only lists and only promoting duplicate lines to mutable buckets.

## 日本語

- JavaScript / TypeScript のタグ付きテンプレート参照ヒットを行単位にまとめる際、単発行は小さな読み取り専用リストのまま保持し、重複行だけ可変 bucket に昇格することで小さな割り当てを減らしました。
