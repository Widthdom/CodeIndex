---
category: fixed
---

## English

- Make CLI and MCP indexing cancellation interrupt long-running SQLite work during mutual-recursion refresh and C# contract preflight, so large interrupted runs stop promptly without continuing into stale-file purge or readiness stamping.

## 日本語

- CLIとMCPのインデックス作成で、相互再帰更新およびC#契約preflight中の長時間SQLite処理をキャンセル時に中断し、大規模処理の中断後に古いファイルのpurgeやreadiness stampへ進まないようにしました。
