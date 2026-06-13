#!/usr/bin/env python3
"""Summarize a cdidx --metrics JSONL file.

Reports count, p50, p95, p99, and max elapsed_ms per (source, tool) pair so you
can spot latency regressions or throughput drops from a captured log without
re-running queries. Pass the JSONL path as the only argument; reads from stdin
when no argument is provided.

cdidx の --metrics JSONL ファイルを集計するサンプルスクリプト。
(source, tool) 別に count / p50 / p95 / p99 / max の elapsed_ms を表示する。
"""

from __future__ import annotations

import argparse
import json
import math
import random
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import BinaryIO, Iterable, Iterator


DEFAULT_MAX_LINES = 1_000_000
DEFAULT_MAX_RECORDS = 1_000_000
DEFAULT_MAX_LINE_BYTES = 1_048_576
DEFAULT_MAX_VALUES_PER_BUCKET = 10_000


def _percentile(sorted_values: list[float], pct: float) -> float:
    if not sorted_values:
        return math.nan
    if len(sorted_values) == 1:
        return sorted_values[0]
    k = (len(sorted_values) - 1) * (pct / 100.0)
    lower = math.floor(k)
    upper = math.ceil(k)
    if lower == upper:
        return sorted_values[int(k)]
    return sorted_values[lower] + (sorted_values[upper] - sorted_values[lower]) * (k - lower)


def _positive_int(value: str) -> int:
    try:
        parsed = int(value, 10)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer") from exc
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return parsed


def _drain_long_line(stream: BinaryIO, max_line_bytes: int) -> None:
    while True:
        chunk = stream.readline(max_line_bytes)
        if not chunk or chunk.endswith(b"\n"):
            return


def _iter_records(
    stream: BinaryIO,
    *,
    max_lines: int,
    max_records: int,
    max_line_bytes: int,
) -> Iterator[dict]:
    line_number = 0
    records = 0
    while line_number < max_lines:
        raw = stream.readline(max_line_bytes + 1)
        if not raw:
            return
        line_number += 1

        if len(raw) > max_line_bytes:
            if not raw.endswith(b"\n"):
                _drain_long_line(stream, max_line_bytes)
            print(f"warn: skipping line {line_number}: exceeds --max-line-bytes", file=sys.stderr)
            continue

        try:
            line = raw.decode("utf-8").strip()
        except UnicodeDecodeError:
            print(f"warn: skipping line {line_number}: invalid UTF-8", file=sys.stderr)
            continue
        if not line:
            continue
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            print(f"warn: skipping line {line_number}: invalid JSON", file=sys.stderr)
            continue
        records += 1
        if records > max_records:
            print(f"warn: stopped after --max-records={max_records}", file=sys.stderr)
            return
        yield record

    print(f"warn: stopped after --max-lines={max_lines}", file=sys.stderr)


@dataclass
class Bucket:
    max_samples: int
    rng: random.Random
    count: int = 0
    max_value: float = -math.inf
    samples: list[float] = field(default_factory=list)

    def add(self, value: float) -> None:
        self.count += 1
        self.max_value = max(self.max_value, value)
        if len(self.samples) < self.max_samples:
            self.samples.append(value)
            return

        replacement = self.rng.randrange(self.count)
        if replacement < self.max_samples:
            self.samples[replacement] = value


def summarize(
    records: Iterable[dict],
    *,
    max_values_per_bucket: int = DEFAULT_MAX_VALUES_PER_BUCKET,
    seed: int = 0,
) -> list[dict]:
    buckets: dict[tuple[str, str], Bucket] = {}
    for rec in records:
        elapsed = rec.get("elapsed_ms")
        if not isinstance(elapsed, (int, float)):
            continue
        key = (rec.get("source") or "?", rec.get("tool") or "?")
        bucket = buckets.get(key)
        if bucket is None:
            bucket_seed = f"{seed}:{key[0]}:{key[1]}"
            bucket = Bucket(max_values_per_bucket, random.Random(bucket_seed))
            buckets[key] = bucket
        bucket.add(float(elapsed))

    rows: list[dict] = []
    for (source, tool), bucket in sorted(buckets.items()):
        values = sorted(bucket.samples)
        rows.append({
            "source": source,
            "tool": tool,
            "count": bucket.count,
            "sample_count": len(values),
            "sampled": bucket.count > len(values),
            "p50_ms": round(_percentile(values, 50), 3),
            "p95_ms": round(_percentile(values, 95), 3),
            "p99_ms": round(_percentile(values, 99), 3),
            "max_ms": round(bucket.max_value, 3),
        })
    return rows


def _print_table(rows: list[dict]) -> None:
    if not rows:
        print("(no records)")
        return
    headers = ["source", "tool", "count", "sample_count", "sampled", "p50_ms", "p95_ms", "p99_ms", "max_ms"]
    widths = [max(len(h), *(len(str(r[h])) for r in rows)) for h in headers]
    fmt = "  ".join(f"{{:<{w}}}" for w in widths)
    print(fmt.format(*headers))
    print(fmt.format(*("-" * w for w in widths)))
    for r in rows:
        print(fmt.format(*(r[h] for h in headers)))


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize cdidx --metrics JSONL output")
    parser.add_argument("path", nargs="?", help="Path to JSONL file (defaults to stdin)")
    parser.add_argument("--json", action="store_true", help="Emit summary rows as JSON")
    parser.add_argument("--max-lines", type=_positive_int, default=DEFAULT_MAX_LINES, help="Maximum physical JSONL lines to read")
    parser.add_argument("--max-records", type=_positive_int, default=DEFAULT_MAX_RECORDS, help="Maximum decoded JSON records to process")
    parser.add_argument("--max-line-bytes", type=_positive_int, default=DEFAULT_MAX_LINE_BYTES, help="Maximum bytes accepted for one JSONL line")
    parser.add_argument("--max-values-per-bucket", type=_positive_int, default=DEFAULT_MAX_VALUES_PER_BUCKET, help="Maximum elapsed_ms values retained per (source, tool) bucket")
    parser.add_argument("--sample-seed", type=int, default=0, help="Deterministic seed for bounded percentile sampling")
    args = parser.parse_args()

    def records_from(stream: BinaryIO) -> Iterator[dict]:
        return _iter_records(
            stream,
            max_lines=args.max_lines,
            max_records=args.max_records,
            max_line_bytes=args.max_line_bytes,
        )

    if args.path and args.path != "-":
        with Path(args.path).open("rb") as fh:
            rows = summarize(
                records_from(fh),
                max_values_per_bucket=args.max_values_per_bucket,
                seed=args.sample_seed,
            )
    else:
        rows = summarize(
            records_from(sys.stdin.buffer),
            max_values_per_bucket=args.max_values_per_bucket,
            seed=args.sample_seed,
        )

    if args.json:
        json.dump(rows, sys.stdout, indent=2)
        sys.stdout.write("\n")
    else:
        _print_table(rows)
    return 0


if __name__ == "__main__":
    sys.exit(main())
