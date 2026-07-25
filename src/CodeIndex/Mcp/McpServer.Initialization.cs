using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Mcp;

public partial class McpServer : IDisposable
{

    /// <summary>
    /// Handle the initialize handshake.
    /// initializeハンドシェイクを処理。
    /// </summary>
    private JsonNode HandleInitialize(
        JsonNode? id,
        JsonNode? _params,
        DeferredInitializeCommits? deferredInitializeCommits)
    {
        var negotiated = NegotiateProtocolVersion(_params, out var requestedVersion);
        if (negotiated == null)
        {
            // No overlap between the client's requested version and this server's supported
            // set. Issue #1554: respond with structured `-32602` (invalid params) carrying the
            // requested + supported versions in `error.data` so clients can branch on it
            // instead of guessing why the handshake silently failed. Reject before committing
            // any client/session snapshot so a failed re-initialize cannot corrupt the active
            // session (#4536, #4540).
            // クライアント要求バージョンとサーバー対応集合に重なりがない場合。Issue #1554:
            // クライアントが分岐判定できるよう、`error.data` に要求バージョンと対応バージョン
            // を入れた -32602 (invalid params) を返す。client/session snapshot の commit 前に
            // 拒否し、失敗した re-initialize で有効 session を壊さない (#4536, #4540)。
            DeferFrameLog(BuildUnsupportedProtocolLog(requestedVersion));
            return CreateUnsupportedProtocolError(id, requestedVersion);
        }

        // Parse caller-controlled identity, capability, and root metadata into a detached
        // draft. None of it becomes observable session state until protocol negotiation and
        // complete success-response serialization have both succeeded (#4540).
        // caller が制御する identity / capability / root metadata は切り離した draft へ解析する。
        // protocol 交渉と success response の serialization が完了するまで公開しない (#4540)。
        var initializeState = BuildInitializeState(_params);
        var result = new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false
                },
                ["resources"] = new JsonObject
                {
                    ["subscribe"] = false,
                    ["listChanged"] = false
                },
                ["prompts"] = new JsonObject
                {
                    ["listChanged"] = false
                },
                ["logging"] = new JsonObject(),
                ["roots"] = new JsonObject
                {
                    ["listChanged"] = true
                },
                ["sampling"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "cdidx",
                ["version"] = _version
            },
            // Server instructions — tool-selection guidance for AI clients
            // サーバー指示 — AIクライアント向けツール選択ガイダンス
            ["instructions"] = BuildInstructions()
        };
        var response = CreateSuccessResponse(true, id, result);
        if (deferredInitializeCommits is null)
            CommitInitializeState(initializeState);
        else
            deferredInitializeCommits.Register(response, initializeState);
        return response;
    }

    /// <summary>
    /// Build a detached snapshot of caller-controlled initialize metadata. The caller must
    /// commit this snapshot only after protocol negotiation and success-response serialization succeed.
    /// caller が制御する initialize metadata の切り離した snapshot を構築する。呼び出し元は
    /// protocol 交渉と success response の serialization 成功後に限って commit すること。
    /// </summary>
    private PendingInitializeState BuildInitializeState(JsonNode? initializeParams)
    {
        BoundedMcpText? clientNameDisplay = null;
        BoundedMcpText? clientVersionDisplay = null;
        JsonNode? clientCapabilities = null;
        int? clientCapabilitiesSerializedBytes = null;
        string? clientCapabilitiesTruncationReason = null;
        var clientSupportsRoots = false;
        var clientSupportsSampling = false;
        var clientRoots = new List<string>();
        var clientRootDiagnostics = new List<string>();
        var clientRootsTruncated = false;
        var markClientRootsStale = false;

        if (initializeParams is JsonObject obj)
        {
            markClientRootsStale = true;
            if (obj["clientInfo"] is JsonObject info)
            {
                clientNameDisplay = TryReadBoundedClientInfoMember(info, "name");
                clientVersionDisplay = TryReadBoundedClientInfoMember(info, "version");
            }

            if (!obj.TryGetPropertyValue("capabilities", out var capabilities))
                obj.TryGetPropertyValue("clientCapabilities", out capabilities);
            if (capabilities is not null)
            {
                if (capabilities is JsonObject capabilitiesObject)
                {
                    clientSupportsRoots = capabilitiesObject.TryGetPropertyValue("roots", out var rootsCapability)
                        && rootsCapability is not null;
                    clientSupportsSampling = capabilitiesObject.TryGetPropertyValue("sampling", out var samplingCapability)
                        && samplingCapability is not null;
                }

                if (!TryMeasureJsonUtf8BytesWithinLimit(capabilities, _jsonOptions, MaxClientCapabilitiesJsonBytes, out var serializedBytes))
                {
                    clientCapabilitiesSerializedBytes = serializedBytes;
                    clientCapabilities = new JsonObject();
                    clientCapabilitiesTruncationReason = "byte_limit";
                }
                else
                {
                    clientCapabilitiesSerializedBytes = serializedBytes;
                    if (!IsJsonNodeDepthWithinLimit(capabilities, MaxClientCapabilitiesDepth))
                    {
                        clientCapabilities = new JsonObject();
                        clientCapabilitiesTruncationReason = "depth_limit";
                    }
                    else
                    {
                        clientCapabilities = McpJsonNode.Clone(capabilities);
                    }
                }
            }

            void AddRoot(string uri)
            {
                clientRoots.Add(uri);
                if (clientRootDiagnostics.Count >= MaxClientRootCount)
                {
                    clientRootsTruncated = true;
                    return;
                }

                var display = McpBoundedText.ForDisplay(uri, MaxClientRootUriChars);
                clientRootDiagnostics.Add(display.Text);
                clientRootsTruncated |= display.Truncated;
            }

            if (TryReadStringValue(obj["rootUri"]) is { Length: > 0 } rootUri)
                AddRoot(rootUri);

            if (obj["roots"] is JsonArray roots)
            {
                foreach (var root in roots)
                {
                    var uri = TryReadStringValue(root?["uri"]) ?? TryReadStringValue(root);
                    if (!string.IsNullOrWhiteSpace(uri))
                        AddRoot(uri);
                }
            }
        }

        return new PendingInitializeState(
            ResolveCallerIdentity(initializeParams),
            markClientRootsStale,
            clientNameDisplay,
            clientVersionDisplay,
            clientCapabilities,
            clientCapabilitiesSerializedBytes,
            clientCapabilitiesTruncationReason,
            clientSupportsRoots,
            clientSupportsSampling,
            clientRoots.ToArray(),
            clientRootDiagnostics.ToArray(),
            clientRootsTruncated);
    }

    private void CommitInitializeState(PendingInitializeState state)
    {
        lock (_initializeStateGate)
        {
            var committed = BuildCommittedInitializeState(
                PublishedInitializeState,
                state,
                logCallerSwap: true);

            // One release publication makes lifecycle and all negotiated metadata visible
            // together; no reader can observe initialized=true with a partial state (#4540).
            // lifecycle と交渉済み metadata を 1 回の release publication で同時に公開し、
            // initialized=true と部分的な state の組み合わせを reader に見せない (#4540)。
            Volatile.Write(ref _initializeState, committed);
        }
    }

    private InitializeSessionState BuildCommittedInitializeState(
        InitializeSessionState previous,
        PendingInitializeState state,
        bool logCallerSwap)
    {
        var caller = previous.Caller;

        // Caller stickiness: allow upgrading from the default "unknown" bucket to a named
        // identity, but reject successful re-initialize attempts that swap named identities.
        // caller の sticky 制御: "unknown" から名前付き ID への昇格だけを許可し、成功した
        // re-initialize による名前付き ID 同士のスワップは拒否する。
        if (caller == "unknown")
        {
            caller = state.ResolvedCaller;
        }
        else if (state.ResolvedCaller != caller && state.ResolvedCaller != "unknown" && logCallerSwap)
        {
            DeferFrameLog(BuildCallerSwapRejectionLog(caller, state.ResolvedCaller));
        }

        return new InitializeSessionState(
            true,
            caller,
            state.ClientNameDisplay,
            state.ClientVersionDisplay,
            state.ClientCapabilities,
            state.ClientCapabilitiesSerializedBytes,
            state.ClientCapabilitiesTruncationReason,
            state.ClientSupportsRoots,
            state.ClientSupportsSampling,
            state.ClientRoots.ToArray(),
            state.ClientRootDiagnostics.ToArray(),
            state.ClientRootsTruncated,
            state.MarkClientRootsStale || previous.ClientRootsStale);
    }

    private sealed record InitializeSessionState(
        bool Initialized,
        string Caller,
        BoundedMcpText? ClientNameDisplay,
        BoundedMcpText? ClientVersionDisplay,
        JsonNode? ClientCapabilities,
        int? ClientCapabilitiesSerializedBytes,
        string? ClientCapabilitiesTruncationReason,
        bool ClientSupportsRoots,
        bool ClientSupportsSampling,
        string[] ClientRoots,
        string[] ClientRootDiagnostics,
        bool ClientRootsTruncated,
        bool ClientRootsStale)
    {
        internal static InitializeSessionState Empty { get; } = new(
            false,
            "unknown",
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            true);

        internal string? ClientName => ClientNameDisplay?.Text;
        internal string? ClientVersion => ClientVersionDisplay?.Text;
        internal int ClientRootCount => ClientRoots.Length;
    }

    private sealed record PendingInitializeState(
        string ResolvedCaller,
        bool MarkClientRootsStale,
        BoundedMcpText? ClientNameDisplay,
        BoundedMcpText? ClientVersionDisplay,
        JsonNode? ClientCapabilities,
        int? ClientCapabilitiesSerializedBytes,
        string? ClientCapabilitiesTruncationReason,
        bool ClientSupportsRoots,
        bool ClientSupportsSampling,
        string[] ClientRoots,
        string[] ClientRootDiagnostics,
        bool ClientRootsTruncated);

    private sealed class FrameInitializeState
    {
        private readonly object _gate = new();
        private InitializeSessionState _current;
        private int _acceptedRootsChange;

        internal FrameInitializeState(
            InitializeSessionState current,
            bool isProvisionalGeneration)
        {
            _current = current;
            IsProvisionalGeneration = isProvisionalGeneration;
        }

        internal bool IsProvisionalGeneration { get; }
        internal InitializeSessionState Current => Volatile.Read(ref _current);

        internal void MarkRootsChangeAccepted()
            => Volatile.Write(ref _acceptedRootsChange, 1);

        internal bool TryConsumeAcceptedRootsChange()
            => Interlocked.Exchange(ref _acceptedRootsChange, 0) != 0;

        internal bool TryAdvanceToPublishedGeneration(
            InitializeSessionState expectedState,
            InitializeSessionState publishedState)
        {
            if (IsProvisionalGeneration)
                return false;

            lock (_gate)
            {
                if (!ReferenceEquals(Current, expectedState))
                    return false;

                Volatile.Write(ref _current, publishedState);
                return true;
            }
        }

        internal bool TryRefreshClientRoots(
            InitializeSessionState expectedState,
            ClientRootSnapshot refreshedRoots)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(Current, expectedState))
                    return false;

                Volatile.Write(
                    ref _current,
                    expectedState with
                    {
                        ClientRoots = refreshedRoots.Roots.ToArray(),
                        ClientRootDiagnostics = refreshedRoots.Diagnostics.ToArray(),
                        ClientRootsTruncated = refreshedRoots.Truncated,
                        ClientRootsStale = false,
                    });
                return true;
            }
        }
    }

    /// <summary>
    /// Tracks initialize drafts for one wire frame until the exact success response that owns
    /// each draft has been serialized. The collection is frame-local but synchronized because
    /// isolated request dispatch can finish on a worker after its caller has timed out.
    /// initialize draft を wire frame 単位で追跡し、対応する success response の serialization
    /// 成功後にだけ commit する。timeout 後も worker が完了し得るため collection は同期する。
    /// </summary>
    private sealed class DeferredInitializeCommits
    {
        private readonly object _gate = new();
        private readonly List<Entry> _entries = [];

        internal void Register(JsonNode response, PendingInitializeState state)
        {
            lock (_gate)
                _entries.Add(new Entry(response, state));
        }

        internal bool TryGetRegisteredState(JsonNode response, out PendingInitializeState state)
        {
            lock (_gate)
            {
                foreach (var entry in _entries)
                {
                    if (!ReferenceEquals(entry.Response, response))
                        continue;

                    state = entry.State;
                    return true;
                }
            }

            state = null!;
            return false;
        }

        internal PendingInitializeState[] GetIncludedStates(JsonNode serializedResponse)
        {
            lock (_gate)
            {
                return _entries
                    .Where(entry => IsIncludedResponse(serializedResponse, entry.Response))
                    .Select(entry => entry.State)
                    .ToArray();
            }
        }

        private static bool IsIncludedResponse(JsonNode serializedResponse, JsonNode candidate)
        {
            if (ReferenceEquals(serializedResponse, candidate))
                return true;

            if (serializedResponse is not JsonArray batchResponse)
                return false;

            foreach (var item in batchResponse)
            {
                if (ReferenceEquals(item, candidate))
                    return true;
            }

            return false;
        }

        private sealed record Entry(JsonNode Response, PendingInitializeState State);
    }

    private static bool IsJsonNodeDepthWithinLimit(JsonNode node, int maxDepth)
        => IsJsonNodeDepthWithinLimit(node, depth: 0, maxDepth);

    private static bool IsJsonNodeDepthWithinLimit(JsonNode? node, int depth, int maxDepth)
    {
        if (node is null)
            return true;
        if (depth > maxDepth)
            return false;

        if (node is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                if (!IsJsonNodeDepthWithinLimit(kvp.Value, depth + 1, maxDepth))
                    return false;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (!IsJsonNodeDepthWithinLimit(item, depth + 1, maxDepth))
                    return false;
            }
        }

        return true;
    }

    private static ClientRootSnapshot BuildClientRootSnapshot(IEnumerable<string> roots)
    {
        var capturedRoots = new List<string>();
        var diagnostics = new List<string>();
        var truncated = false;
        foreach (var uri in roots)
        {
            capturedRoots.Add(uri);
            if (diagnostics.Count >= MaxClientRootCount)
            {
                truncated = true;
                continue;
            }

            var display = McpBoundedText.ForDisplay(uri, MaxClientRootUriChars);
            diagnostics.Add(display.Text);
            truncated |= display.Truncated;
        }

        return new ClientRootSnapshot(capturedRoots.ToArray(), diagnostics.ToArray(), truncated);
    }

    private void MarkClientRootsStale()
    {
        lock (_initializeStateGate)
        {
            var current = PublishedInitializeState;
            // Always replace the reference, even when already stale, so a notification that
            // races an in-flight roots/list refresh invalidates that refresh's expected state.
            // 既に stale でも必ず reference を置き換え、進行中の roots/list refresh と競合した
            // notification がその refresh の expected state を無効化できるようにする。
            Volatile.Write(ref _initializeState, current with { ClientRootsStale = true });
        }
    }

    private sealed record ClientRootSnapshot(string[] Roots, string[] Diagnostics, bool Truncated);

    internal JsonNode? ClientCapabilitiesForTests
    {
        get
        {
            var state = CurrentInitializeState;
            return McpJsonNode.Clone(state.ClientCapabilities);
        }
    }

    internal string[] ClientRootsForTests
    {
        get
        {
            var state = CurrentInitializeState;
            return state.ClientRoots.ToArray();
        }
    }

    internal bool ClientSupportsRootsForTests => CurrentInitializeState.ClientSupportsRoots;

    internal bool ClientSupportsSamplingForTests => CurrentInitializeState.ClientSupportsSampling;

    internal bool ClientRootsStaleForTests
    {
        get => CurrentInitializeState.ClientRootsStale;
        set
        {
            lock (_initializeStateGate)
            {
                var current = PublishedInitializeState;
                Volatile.Write(ref _initializeState, current with { ClientRootsStale = value });
            }
        }
    }

    internal string McpLogLevelForTests => _mcpLogLevel;

    internal Func<string, JsonObject?, JsonNode?>? ClientRequestHandlerForTests { get; set; }

    private static string? TryReadStringMember(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node))
            return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            return s.Trim();
        return null;
    }

    private static BoundedMcpText? TryReadBoundedClientInfoMember(JsonObject obj, string key)
    {
        var value = TryReadStringMember(obj, key);
        return value is null ? null : BoundClientInfoForDisplay(value);
    }

}
