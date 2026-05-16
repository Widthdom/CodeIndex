# AI Agent, IDE, Editor, CI, and MCP Integration Policy

CodeIndex is designed to be called by humans and AI tools.

This policy explains which integrations are allowed without a separate written
agreement. It does not replace the license text in `LICENSE` or
`LICENSES/FSL-1.1-ALv2.txt`.

## Allowed Integrations

You may integrate with CodeIndex without a separate agreement when the integration
helps users use official CodeIndex releases for their own development work.

Allowed integrations include:

- AI coding agents invoking `cdidx` through CLI commands;
- AI coding agents invoking `cdidx mcp`;
- IDE/editor extensions that discover or call a user-installed `cdidx`;
- IDE/editor extensions that provide configuration UI for official CodeIndex;
- shell scripts, task runners, and CI jobs that run `cdidx`;
- MCP client configuration files;
- devcontainer, Dockerfile, and setup examples that install official CodeIndex
  releases;
- package-manager formulas, recipes, manifests, or mirrors that redistribute
  unmodified official CodeIndex releases with notices preserved;
- internal company wrappers that help employees use CodeIndex on authorized
  repositories;
- documentation showing how to integrate CodeIndex with AI assistants,
  terminals, editors, and CI systems.

## Allowed Data and Outputs

When you use CodeIndex on codebases you own, control, or are authorized to
access:

- your source code remains yours;
- your index files remain yours;
- search results, snippets, structured JSON, and MCP responses generated from
  your own codebases remain yours;
- CodeIndex does not claim ownership over your repositories or generated
  development context.

## API Surface and Library Use

CodeIndex ships as a **CLI and MCP server only**. It does not publish a library
or SDK API for embedders.

- The stable, supported surfaces are the `cdidx` CLI (including its `--json`
  output) and the `cdidx mcp` JSON-RPC interface. Versioning guarantees only
  apply to those surfaces.
- The `cdidx` NuGet package is published with `PackAsTool=true` and is intended
  to be installed as a .NET global tool, not added as an assembly reference.
- Types that happen to be `public` on the `cdidx` assembly (for example DTOs in
  `CodeIndex.Database` / `CodeIndex.Models`, or readers such as
  `CodeIndex.Database.DbReader`) exist to satisfy CLI and MCP composition. They
  are **implementation details**, not a public library contract, and may
  change, move, or become `internal` in any release without a deprecation
  cycle.
- Projects that need a programmatic interface should depend on the CLI's
  `--json` output or on the MCP server, both of which are covered by the
  changelog and the documented status contract.

If a future need justifies a real library API, it will be carved out as a
separate package with its own explicit interface and versioning contract.
Until then, treat embedding the `cdidx` assembly as unsupported.

## Boundary

The integration is allowed when it helps users operate official CodeIndex
releases or permissively licensed integration materials.

The integration may require a separate written agreement when it makes CodeIndex,
a modified CodeIndex engine, or a derivative work of CodeIndex available to
third parties as a commercial product or service that substitutes for CodeIndex
or offers substantially similar indexing/search/retrieval functionality.

## Examples

Allowed:

- "My agent calls `cdidx search` before editing files."
- "My agent calls `cdidx status --check --json` before searching so it can skip unnecessary reindexing when the DB already matches the workspace."
- "My VS Code extension lets users configure the path to their local `cdidx`."
- "My CI job runs `cdidx .` and `cdidx search` against our private repo."
- "My devcontainer installs the official `cdidx` release."
- "My internal platform lets employees search company repositories using
  CodeIndex."

Requires separate agreement:

- "I sell a hosted code search service powered by CodeIndex."
- "I publish a renamed CodeIndex fork as my own MCP code search product."
- "I embed a modified CodeIndex engine as the core retrieval feature of my
  commercial AI coding SaaS."
- "I offer a commercial IDE product where the CodeIndex-derived engine is a
  primary code-indexing/search feature offered to third parties."

## Trademarks

You may make truthful compatibility statements such as:

- "compatible with CodeIndex"
- "uses cdidx if installed"
- "configuration for CodeIndex"

You may not imply that your integration is official, endorsed, approved, or
maintained by Widthdom unless you have written permission. See `TRADEMARKS.md`.
