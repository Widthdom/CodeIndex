---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/HdlReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/ShaderReferenceExtractor.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Removed per-identifier closure allocations from hardware-language scopes** —
  Verilog, SystemVerilog, and VHDL shadow checks plus CUDA, GLSL, HLSL, Metal,
  and WGSL binding/resource checks now use direct indexed membership loops.

## 日本語

- **hardware-language scope の identifier ごとの closure allocation を解消しました** —
  Verilog、SystemVerilog、VHDL の shadow 判定と、CUDA、GLSL、HLSL、Metal、WGSL
  の binding / resource 判定は direct indexed membership loop を使います。
