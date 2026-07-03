---
category: internal
affected: Indexer
---

## English

- Deferred structured-data symbol state for JSON and YAML until symbols are actually needed, avoiding empty symbol lists and YAML path stacks on files with no extractable mappings.

## 日本語

- JSON / YAML の structured-data symbol state を実際に symbol が必要になるまで遅延し、抽出可能な mapping がないファイルで空の symbol list や YAML path stack を作らないようにしました。
