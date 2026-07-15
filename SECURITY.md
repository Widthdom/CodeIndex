# Security Policy

## MCP Threat Model

`cdidx mcp` is intended for a local, trusted MCP client that the operator
chooses to run against the current workspace. The default transport is stdio
JSON-RPC: any process that can write frames to the server's stdin can request
any enabled MCP tool. Treat that stdin boundary as the authorization boundary
unless you explicitly add the optional token controls below.

The optional HTTP transport is also a local operator surface, not a public
multi-tenant service. It rejects wildcard listen hosts and requires a bearer
secret on every listener by default, including loopback. Set
`CDIDX_MCP_HTTP_TOKEN` (preferred) or the `CDIDX_MCP_AUTH_TOKEN` fallback, then
send `Authorization: Bearer <token>` on every HTTP request; HTTP clients do not
also send `params.auth.token`. `--allow-unauthenticated-http` is an explicit
unsafe exception for loopback only and is refused on non-loopback listeners.

Native clients may omit `Origin`. When it is present, the HTTP transport
requires one exact match for the configured listener origin and rejects
malformed, `null`, ambiguous, or cross-origin values before authentication.
CORS preflight is rejected without permissive response headers. POST accepts
only `application/json` with an omitted or UTF-8 charset and uses strict UTF-8
decoding, preventing browser-simple media types and malformed byte sequences
from reaching JSON-RPC dispatch.

Stdio requests can require a shared secret by setting `CDIDX_MCP_AUTH_TOKEN`.
When the variable is unset, stdio keeps the historical local-trusted-client
behavior and does not authenticate individual JSON-RPC frames. Tool allow/deny
environment variables can reduce the advertised/callable tool set, but they are
not a substitute for trusting the client or protecting the transport.
When the token is configured, it protects both responded requests and every
state-changing notification (cancellation, roots invalidation, shutdown, and
exit). A rejected notification remains response-free as required by JSON-RPC
and cannot mutate server state; only a bounded local diagnostic is emitted.

### Write-Shaped MCP Tools

Most MCP tools read the existing index, but the following tools intentionally
write local state or can create remote side effects:

- `index` updates the configured SQLite index and `.cdidx/` state for the
  selected project path.
- `backfill_fold` mutates the configured SQLite index to populate folded-name
  metadata.
- `suggest_improvement` writes `.cdidx/suggestions-*.json` locally and, when a
  GitHub repository plus `CDIDX_GITHUB_TOKEN` are configured, can submit the
  suggestion as a GitHub Issue.

Because the MCP server executes tool requests from the connected client, an
attacker who controls that client, can write to the stdio stream, obtains the
HTTP bearer token, or can otherwise man-in-the-middle the transport can trigger
these write-shaped tools, refresh or rewrite local index data, and submit
GitHub issues with the configured token's repository permissions. Run `cdidx
mcp` only in workspaces and client stacks you trust, keep tokens scoped to the
minimum required repositories, and disable write-shaped tools in environments
where the MCP client is not fully trusted.

### Network Boundary

The default stdio transport opens no inbound listener. The HTTP transport opens
only the listener requested by the operator. The MCP server does not need
outbound network access for normal local indexing and query operations; the
documented remote side effect is `suggest_improvement` using
`CDIDX_GITHUB_TOKEN` to create a GitHub Issue when configured.

## Reporting a Vulnerability

Please do not open a public GitHub issue for a suspected vulnerability.

Use GitHub's private vulnerability reporting flow from the repository Security
tab when it is available. If that flow is unavailable, contact the maintainer
through GitHub and ask for a private reporting channel before sharing exploit
details, proof-of-concept code, logs, or affected private paths.

Include enough information for maintainers to reproduce and assess the issue:

- affected `cdidx` version or commit;
- operating system and installation method;
- minimal reproduction steps;
- expected and actual behavior;
- impact and any known workarounds.

## Coordinated Disclosure

Maintainers will acknowledge valid private reports as soon as practical, triage
the impact, and coordinate a fix and release plan before public disclosure.
Please keep vulnerability details private until a fix or mitigation is
available, unless maintainers explicitly agree to an earlier disclosure.

## Supported Versions

Security fixes target the current released version and the `main` branch.
Older versions may receive fixes only when the maintainers decide the risk and
upgrade cost justify a backport.
