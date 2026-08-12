---
category: fixed
affected:
  - src/CodeIndex/Cli/QueryCommandRunner.Batch.cs
  - src/CodeIndex/Cli/QueryCommandRunner.BatchParallelExecution.cs
---

## English

- **Parallel batch output limits no longer discard accepted, undispatched input records** — when `batch --parallel` reaches `--max-output-chars`, accepted nonblank input records that were not dispatched are returned to the shared bounded input pump in source order. The next batch invocation reparses those records with fresh line numbers and counters, ahead of input the pump already buffered, while commands that started remain owned by the first invocation.

## 日本語

- **parallel batch の出力上限で、受理済みかつ未 dispatch の input record が失われなくなりました** — `batch --parallel` が `--max-output-chars` に達した場合、受理済みでも未 dispatch の nonblank input record を source 順のまま共有の bounded input pump に戻します。次の batch invocation は、すでに pump が buffer した入力より先にそれらの record を再 parse し、新しい line number と counter を割り当てます。開始済みの command は最初の invocation の所有のままです。
