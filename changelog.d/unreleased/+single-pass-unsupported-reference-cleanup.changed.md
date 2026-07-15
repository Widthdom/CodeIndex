---
category: changed
---

## English

- Make update-mode cleanup of references from unsupported languages a single transactional delete, avoiding a redundant full count scan while keeping graph readiness demotion atomic.

## 日本語

- 更新モードにおける未対応言語の参照削除を単一トランザクションのDELETEへまとめ、グラフreadinessの降格をatomicに保ちながら、重複していた全件COUNT走査を省きました。
