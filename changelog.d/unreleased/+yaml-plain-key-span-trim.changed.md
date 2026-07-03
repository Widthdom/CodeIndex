---
title: Trim YAML plain keys from capture spans
category: changed
---

## English

- Reduced transient string allocation while indexing YAML plain keys by trimming the regex capture span before materializing the key.

## 日本語

- YAML の plain key をインデックス化するとき、キーを文字列化する前に正規表現 capture の span を trim することで短命な文字列割り当てを削減しました。
