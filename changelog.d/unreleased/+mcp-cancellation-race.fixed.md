---
category: fixed
---

## English

- Fixed an MCP stdio race where a cancellation notification could arrive after a request frame was read but before that request registered as active, causing the cancellation to be dropped.

## 日本語

- MCP stdio で、request frame を読んだ後かつ active request 登録前に cancellation notification が届くと cancellation が落ちる競合を修正しました。
