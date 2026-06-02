using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

/// <summary>
/// Reads and writes improvement suggestions to .cdidx/suggestions-*.json.
/// Provides deduplication via SHA256 hash of (category + language + externally visible title and description).
/// All read-modify-write operations are serialized with a file lock to prevent
/// concurrent writers from silently overwriting each other's changes.
/// 改善提案を .cdidx/suggestions-*.json に読み書きする。
/// (category + language + 外部表示用 title と description) のSHA256ハッシュで重複排除する。
/// 全ての read-modify-write 操作はファイルロックでシリアライズされ、
/// 並行書き込み者が互いの変更をサイレントに上書きすることを防ぐ。
/// </summary>
public class SuggestionStore
{
    private readonly string _filePath;
    private readonly string _lockPath;
    private readonly TimeProvider _timeProvider;
    private readonly string _archivePath;
    private static readonly TimeSpan s_inFlightSubmitRetryDelay = TimeSpan.FromMinutes(1);
    internal const FileShare StreamingReadFileShare = FileShare.ReadWrite | FileShare.Delete;
    internal const string DedupThresholdEnvironmentVariable = "CDIDX_SUGGESTION_DEDUP_THRESHOLD";
    internal const string MaxAgeDaysEnvironmentVariable = "CDIDX_SUGGESTION_MAX_AGE_DAYS";
    internal const string MaxCountEnvironmentVariable = "CDIDX_SUGGESTION_MAX_COUNT";
    internal const double DefaultDedupThreshold = 0.85;
    internal const int DefaultMaxAgeDays = 365;
    internal const int DefaultMaxCount = 5000;
    internal const int MaxSuggestionStoreBytes = 8 * 1024 * 1024;
    internal const int MaximumMaxAgeDays = 3650;
    internal const int MaximumMaxCount = 100_000;
    private const int FuzzyDedupRecentLimit = 100;
    private const string RedactedAwsAccessKey = "[REDACTED:aws_access_key]";
    private const string RedactedBearerToken = "[REDACTED:bearer_token]";
    private const string RedactedCredential = "[REDACTED:credential]";
    private const string RedactedHighEntropyToken = "[REDACTED:high_entropy_token]";
    private static readonly Regex s_awsAccessKeyRegex = new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_bearerTokenRegex = new(@"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_namedSecretRegex = new(@"(?i)\b(password|secret)=([^&\s]{1,200})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_highEntropyTokenRegex = new(@"\b(?=[A-Za-z0-9._~+/=-]{32,}\b)(?=.*[A-Z])(?=.*[a-z])(?=.*\d)[A-Za-z0-9._~+/=-]+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> s_dedupStopWords = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "are",
        "be",
        "for",
        "in",
        "is",
        "missing",
        "no",
        "not",
        "of",
        "the",
        "to",
        "with",
    };

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    static SuggestionStore()
    {
        var enumConverter = new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower);
        s_jsonOptions.Converters.Add(enumConverter);
        s_readOptions.Converters.Add(enumConverter);
    }

    /// <summary>
    /// Create a new SuggestionStore for the given directory and database name.
    /// When dbName is provided, the store is scoped to that database identity
    /// (e.g. "suggestions-codeindex.json") so that multiple databases in the
    /// same directory do not share a dedup namespace.
    /// 指定したディレクトリとデータベース名用の SuggestionStore を作成する。
    /// dbName が指定された場合、ストアはそのデータベース固有のスコープになり
    /// （例: "suggestions-codeindex.json"）、同一ディレクトリ内の複数DBが
    /// 重複排除の名前空間を共有しないようにする。
    /// </summary>
    /// <param name="cdidxDir">Path to the directory / ディレクトリのパス</param>
    /// <param name="dbName">
    /// Database filename without extension (optional, defaults to "codeindex").
    /// 拡張子なしのデータベースファイル名（任意、デフォルトは "codeindex"）。
    /// </param>
    public SuggestionStore(string cdidxDir, string? dbName = null)
        : this(cdidxDir, dbName, TimeProvider.System)
    {
    }

    internal SuggestionStore(string cdidxDir, string? dbName, TimeProvider timeProvider)
    {
        // Derive a safe store filename from the DB identity.
        // DB固有の安全なストアファイル名を導出する。
        var safeName = string.IsNullOrWhiteSpace(dbName) ? "codeindex" : dbName;
        _filePath = Path.Combine(cdidxDir, $"suggestions-{safeName}.json");
        _lockPath = Path.Combine(cdidxDir, $"suggestions-{safeName}.lock");
        _timeProvider = timeProvider;
        _archivePath = Path.Combine(cdidxDir, $"suggestions-{safeName}.archive.jsonl");
    }

    /// <summary>
    /// Compute the dedup hash for a suggestion.
    /// The hash is derived from category, language (lowered), GitHub-visible title, and
    /// GitHub-visible description after outbound code scrubbing, trimming, and lowercasing.
    /// 提案の重複排除用ハッシュを計算する。
    /// category、language（小文字化）、GitHub 表示用 title、GitHub 表示用にコード除去された
    /// description（trim + 小文字化）から導出する。
    /// </summary>
    public static string ComputeHash(string category, string? language, string description)
    {
        var externallyVisibleDescription = GitHubIssueReporter.ScrubInlineCode(description);
        var externallyVisibleTitle = GitHubIssueReporter.BuildIssueTitle(category, description);
        var normalized = $"{category}|{(language ?? "").ToLowerInvariant()}|{externallyVisibleTitle.ToLowerInvariant()}|{externallyVisibleDescription.Trim().ToLowerInvariant()}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Try to add a suggestion. Returns false if a suggestion with the same hash already exists.
    /// The entire read-modify-write is protected by a file lock.
    /// Throws IOException if the file cannot be read due to a transient I/O error.
    /// 提案の追加を試みる。同一ハッシュの提案が既にあれば false を返す。
    /// read-modify-write 全体がファイルロックで保護される。
    /// 一時的な I/O エラーでファイルが読めない場合は IOException をスローする。
    /// </summary>
    public bool TryAdd(SuggestionRecord record)
    {
        record = RedactRecordForPersistence(record);
        return WithFileLock(() =>
        {
            var existing = ReadUnlocked();
            var prunedBeforeDuplicateCheck = PruneUnlocked(existing);
            if (FindDuplicate(existing, record, ResolveDedupThreshold()).Record != null)
            {
                if (prunedBeforeDuplicateCheck)
                    SaveUnlocked(existing);
                return false;
            }

            StampCreatedAt(record);
            existing.Add(record);
            PruneUnlocked(existing);
            SaveUnlocked(existing);
            return true;
        });
    }

    /// <summary>
    /// Result of TryAddAndSubmit: whether the record was new or duplicate,
    /// whether it was already submitted, its lifecycle status, and the upstream URL if known.
    /// TryAddAndSubmit の結果: 新規か重複か、既に送信済みか、lifecycle 状態、判明している upstream URL。
    /// </summary>
    public record AddAndSubmitResult(
        bool IsNew,
        bool AlreadySubmitted,
        SuggestionStatus Status,
        string? UpstreamUrl,
        string? SubmissionError = null,
        string? DuplicateOfHash = null,
        double? DuplicateScore = null);

    /// <summary>
    /// Result of a GitHub submission attempt.
    /// GitHub 送信試行の結果。
    /// </summary>
    public record SubmitAttemptResult(string? IssueUrl, string? Error, DateTime? NextRetryAt = null)
    {
        public static SubmitAttemptResult Success(string issueUrl) => new(issueUrl, null, null);

        public static SubmitAttemptResult Failure(string error) => new(null, error, null);

        public static SubmitAttemptResult RetryAfter(string error, DateTime nextRetryAt) => new(null, error, nextRetryAt);

        public static SubmitAttemptResult Skipped() => new(null, null, null);
    }

    /// <summary>
    /// Add a suggestion under the file lock, then attempt GitHub submission outside the lock.
    /// The store reserves the attempt before releasing the lock so concurrent callers do not
    /// also submit the same unsubmitted duplicate while the remote API is slow.
    /// 提案をファイルロック内で追加し、その後 GitHub 送信はロック外で試行する。
    /// remote API が遅い間に並行呼び出しが同じ未送信重複を送信しないよう、
    /// ロック解放前に送信試行を予約する。
    /// </summary>
    /// <param name="record">The suggestion to add / 追加する提案</param>
    /// <param name="submitToGitHub">
    /// Optional callback to submit to GitHub. Called outside the lock only when submission
    /// is needed (new record or unsubmitted duplicate). Returns the issue URL on success.
    /// GitHub 送信用のオプションコールバック。送信が必要な場合（新規レコードまたは
    /// 未送信の重複）にのみロック外で呼ばれる。成功時は Issue URL を返す。
    /// </param>
    public AddAndSubmitResult TryAddAndSubmit(SuggestionRecord record, Func<SuggestionRecord, SubmitAttemptResult>? submitToGitHub)
    {
        return TryAddAndSubmitAsync(
            record,
            submitToGitHub == null
                ? null
                : r => Task.FromResult(submitToGitHub(r))).GetAwaiter().GetResult();
    }

    public async Task<AddAndSubmitResult> TryAddAndSubmitAsync(
        SuggestionRecord record,
        Func<SuggestionRecord, Task<SubmitAttemptResult>>? submitToGitHub)
    {
        record = RedactRecordForPersistence(record);
        var reservation = WithFileLock(() =>
        {
            var existing = ReadUnlocked();
            var prunedBeforeDuplicateCheck = PruneUnlocked(existing);
            var duplicate = FindDuplicate(existing, record, ResolveDedupThreshold());
            var found = duplicate.Record;

            bool isNew = found == null;
            bool alreadySubmitted = found != null && HasUpstreamSubmission(found);

            if (isNew)
            {
                StampCreatedAt(record);
                existing.Add(record);
                PruneUnlocked(existing);
                SaveUnlocked(existing);
                found = record;
            }

            var current = found!;
            if (!alreadySubmitted && submitToGitHub != null && ShouldAttemptSubmit(current))
            {
                var attemptedAt = GetUtcNow();
                StampSubmitAttempt(current, attemptedAt, null, attemptedAt.Add(s_inFlightSubmitRetryDelay));
                SaveUnlocked(existing);
                return new SubmitReservation(
                    isNew,
                    alreadySubmitted,
                    current.Hash,
                    current.Status,
                    current.UpstreamUrl,
                    CloneForSubmit(current),
                    attemptedAt,
                    isNew ? null : current.Hash,
                    isNew ? null : duplicate.Score);
            }

            if (prunedBeforeDuplicateCheck)
                SaveUnlocked(existing);

            return new SubmitReservation(
                isNew,
                alreadySubmitted,
                current.Hash,
                current.Status,
                current.UpstreamUrl,
                null,
                null,
                isNew ? null : current.Hash,
                isNew ? null : duplicate.Score);
        });

        if (reservation.RecordToSubmit == null || submitToGitHub == null || reservation.AttemptedAt == null)
        {
            return new AddAndSubmitResult(
                reservation.IsNew,
                reservation.AlreadySubmitted,
                reservation.Status,
                reservation.UpstreamUrl,
                null,
                reservation.DuplicateOfHash,
                reservation.DuplicateScore);
        }

        SubmitAttemptResult submitResult;
        try
        {
            submitResult = await submitToGitHub(reservation.RecordToSubmit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Best-effort — GitHub submission failure does not fail the local operation.
            // ベストエフォート — GitHub 送信失敗はローカル操作を失敗させない。
            submitResult = SubmitAttemptResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }

        return WithFileLock(() =>
        {
            var existing = ReadUnlocked();
            var found = existing.FirstOrDefault(s => s.Hash == reservation.Hash);
            if (found == null)
            {
                return new AddAndSubmitResult(
                    reservation.IsNew,
                    reservation.AlreadySubmitted,
                    reservation.Status,
                    reservation.UpstreamUrl,
                    null,
                    reservation.DuplicateOfHash,
                    reservation.DuplicateScore);
            }

            var issueUrl = submitResult.IssueUrl;
            StampSubmitResult(found, submitResult);
            if (issueUrl != null)
                MarkSubmitted(found, issueUrl, reservation.AttemptedAt.Value);

            SaveUnlocked(existing);
            return new AddAndSubmitResult(
                reservation.IsNew,
                reservation.AlreadySubmitted,
                found.Status,
                issueUrl ?? found.UpstreamUrl,
                submitResult.Error,
                reservation.DuplicateOfHash,
                reservation.DuplicateScore);
        });
    }

    /// <summary>
    /// Load all suggestions from disk. Returns an empty list if the file
    /// does not exist or is empty. Corrupt files are preserved as .bak.
    /// Throws IOException on transient file access errors (fail-closed).
    /// ディスクから全提案を読み込む。ファイルが存在しないか空の場合は空リストを返す。
    /// 破損ファイルは .bak として保存される。
    /// 一時的なファイルアクセスエラーでは IOException をスローする（fail-closed）。
    /// </summary>
    public List<SuggestionRecord> LoadAll()
    {
        return ReadUnlocked();
    }

    /// <summary>
    /// Load suggestions matching the lifecycle status without materializing
    /// the whole store before filtering.
    /// ライフサイクル状態に一致する提案を、フィルタ前にストア全体を実体化せず読み込む。
    /// </summary>
    public List<SuggestionRecord> LoadByStatus(SuggestionStatus status)
    {
        return ReadFilteredUnlocked(s => s.Status == status);
    }

    /// <summary>
    /// Load suggestions created at or after the given timestamp without materializing
    /// the whole store before filtering.
    /// 指定時刻以降に作成された提案を、フィルタ前にストア全体を実体化せず読み込む。
    /// </summary>
    public List<SuggestionRecord> LoadSince(DateTimeOffset since)
    {
        var threshold = since.ToUniversalTime();
        return ReadFilteredUnlocked(s => ToUtcOffset(s.CreatedAt) >= threshold);
    }

    /// <summary>
    /// Load suggestions for a category without materializing the whole store before filtering.
    /// カテゴリに一致する提案を、フィルタ前にストア全体を実体化せず読み込む。
    /// </summary>
    public List<SuggestionRecord> LoadByCategory(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        var normalized = category.Trim();
        return ReadFilteredUnlocked(s => string.Equals(s.Category, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Load suggestions for a language without materializing the whole store before filtering.
    /// 言語に一致する提案を、フィルタ前にストア全体を実体化せず読み込む。
    /// </summary>
    public List<SuggestionRecord> LoadByLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var normalized = language.Trim();
        return ReadFilteredUnlocked(s => string.Equals(s.Language, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Load a page of suggestions in stored order without materializing entries outside the page.
    /// 保存順の提案ページを、ページ外のエントリを実体化せず読み込む。
    /// </summary>
    public List<SuggestionRecord> Load(int skip, int take)
    {
        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), "skip must be non-negative.");
        if (take < 0)
            throw new ArgumentOutOfRangeException(nameof(take), "take must be non-negative.");
        if (take == 0)
            return new List<SuggestionRecord>();

        return ReadFilteredUnlocked(_ => true, skip, take);
    }

    /// <summary>
    /// Mark a suggestion as submitted to GitHub by updating its URL and flag.
    /// The entire read-modify-write is protected by a file lock.
    /// NOTE: Prefer TryAddAndSubmit for new submissions — this method exists
    /// for backward compatibility only.
    /// 提案を GitHub 送信済みとしてマークし、URL とフラグを更新する。
    /// read-modify-write 全体がファイルロックで保護される。
    /// 注: 新規送信には TryAddAndSubmit を推奨 — このメソッドは後方互換のみ。
    /// </summary>
    public void MarkSubmitted(string hash, string issueUrl)
    {
        WithFileLock(() =>
        {
            var all = ReadUnlocked();
            var record = all.FirstOrDefault(s => s.Hash == hash);
            if (record == null)
                return;

            MarkSubmitted(record, issueUrl, GetUtcNow());
            SaveUnlocked(all);
        });
    }

    // --- Internal implementation / 内部実装 ---

    /// <summary>
    /// Read suggestions without acquiring the lock (caller must hold it).
    /// ロックを取得せずに提案を読み込む（呼び出し元がロックを保持していること）。
    /// </summary>
    private List<SuggestionRecord> ReadUnlocked()
    {
        var ioPath = LongPath.EnsureWindowsPrefix(_filePath);
        if (!File.Exists(ioPath))
            return new List<SuggestionRecord>();

        if (new FileInfo(ioPath).Length == 0)
        {
            PreserveCorruptFile();
            return new List<SuggestionRecord>();
        }

        var json = DataDirectorySecurity.ReadTextWithinLimit(ioPath, MaxSuggestionStoreBytes, StreamingReadFileShare);
        if (json is null)
        {
            PreserveCorruptFile();
            return new List<SuggestionRecord>();
        }

        if (string.IsNullOrWhiteSpace(json))
            return new List<SuggestionRecord>();

        try
        {
            var records = JsonSerializer.Deserialize<List<SuggestionRecord>>(json, s_readOptions)
                          ?? new List<SuggestionRecord>();
            NormalizeRecordDefaults(records);
            return records;
        }
        catch (JsonException)
        {
            // Corrupt file — preserve as .bak so history is not lost.
            // 壊れたファイル — 履歴を失わないよう .bak として保存。
            PreserveCorruptFile();
            return new List<SuggestionRecord>();
        }
        // IOException is NOT caught here — it propagates to the caller (fail-closed).
        // IOException はここでキャッチしない — 呼び出し元に伝播する（fail-closed）。
    }

    private static void NormalizeRecordDefaults(List<SuggestionRecord> records)
    {
        foreach (var record in records)
        {
            NormalizeLegacyFields(record);
            if (string.IsNullOrWhiteSpace(record.CreatedByAgent))
                record.CreatedByAgent = "unknown";
            if (string.IsNullOrWhiteSpace(record.SessionId))
                record.SessionId = "unknown";
            if (string.IsNullOrWhiteSpace(record.ClientVersion))
                record.ClientVersion = "unknown";
        }
    }

    private static (SuggestionRecord? Record, double? Score) FindDuplicate(
        IReadOnlyList<SuggestionRecord> existing,
        SuggestionRecord candidate,
        double threshold)
    {
        var exact = existing.FirstOrDefault(s => s.Hash == candidate.Hash);
        if (exact != null)
            return (exact, 1.0);

        var bestScore = 0.0;
        SuggestionRecord? best = null;
        foreach (var record in existing
            .Where(s => SameDedupScope(s, candidate))
            .OrderByDescending(s => s.CreatedAt)
            .Take(FuzzyDedupRecentLimit))
        {
            var score = ComputeDescriptionSimilarity(record.Description, candidate.Description);
            if (score > bestScore)
            {
                bestScore = score;
                best = record;
            }
        }

        if (best != null && bestScore >= threshold)
        {
            WriteFuzzyDuplicateWarning(best.Hash, bestScore, threshold);
            return (best, bestScore);
        }

        return (null, null);
    }

    private static void WriteFuzzyDuplicateWarning(string hash, double score, double threshold)
    {
        try
        {
            Console.Error.WriteLine(
                $"cdidx: fuzzy suggestion duplicate matched hash {hash} with score {score:0.###} (threshold {threshold:0.###})");
        }
        catch (ObjectDisposedException)
        {
            // Best-effort diagnostic only; suggestion deduplication must not fail
            // because another thread or test fixture temporarily replaced stderr.
        }
        catch (IOException)
        {
            // Same rationale as ObjectDisposedException.
        }
    }

    private static bool SameDedupScope(SuggestionRecord left, SuggestionRecord right)
        => string.Equals(left.Category?.Trim(), right.Category?.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Language?.Trim() ?? string.Empty, right.Language?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    internal static double ComputeDescriptionSimilarity(string left, string right)
    {
        var leftTokens = TokenizeForDedup(left);
        var rightTokens = TokenizeForDedup(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        if (union == 0)
            return 0.0;

        return (double)intersection / union;
    }

    private static HashSet<string> TokenizeForDedup(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                continue;
            }

            AddToken(builder, tokens);
        }

        AddToken(builder, tokens);
        return tokens;
    }

    private static void AddToken(StringBuilder builder, HashSet<string> tokens)
    {
        if (builder.Length == 0)
            return;

        var rawToken = builder.ToString();
        var token = s_dedupStopWords.Contains(rawToken) ? string.Empty : NormalizeDedupToken(rawToken);
        builder.Clear();
        if (token.Length >= 2 && !s_dedupStopWords.Contains(token))
            tokens.Add(token);
    }

    private static string NormalizeDedupToken(string token)
    {
        if (token.EndsWith("unsupported", StringComparison.Ordinal))
            return "support";
        if (token.EndsWith("supported", StringComparison.Ordinal))
            return token[..^2];
        if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5)
            return token[..^3];
        if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4)
            return token[..^2];
        if (token.EndsWith("s", StringComparison.Ordinal) && token.Length > 3)
            return token[..^1];
        return token;
    }

    private static double ResolveDedupThreshold()
    {
        var raw = Environment.GetEnvironmentVariable(DedupThresholdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultDedupThreshold;

        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value >= 0
            && value <= 1)
        {
            return value;
        }

        return DefaultDedupThreshold;
    }

    /// <summary>
    /// Stream suggestions without acquiring the lock (caller must hold it for write-side read-modify-write).
    /// Query readers call this directly because they do not mutate the store.
    /// ロックを取得せずに提案をストリーミング読み込みする（書き込み側の read-modify-write では呼び出し元がロックを保持すること）。
    /// クエリ読み取りはストアを変更しないため直接呼び出す。
    /// </summary>
    private List<SuggestionRecord> ReadFilteredUnlocked(
        Func<SuggestionRecord, bool> predicate,
        int skip = 0,
        int? take = null)
    {
        var ioPath = LongPath.EnsureWindowsPrefix(_filePath);
        if (!File.Exists(ioPath))
            return new List<SuggestionRecord>();

        var snapshot = DataDirectorySecurity.ReadBytesWithinLimit(ioPath, MaxSuggestionStoreBytes, StreamingReadFileShare);
        if (snapshot is null)
        {
            PreserveCorruptFile();
            return new List<SuggestionRecord>();
        }

        if (snapshot.Length == 0)
        {
            PreserveCorruptFile();
            return new List<SuggestionRecord>();
        }

        if (IsEmptyOrJsonWhitespace(snapshot))
            return new List<SuggestionRecord>();

        try
        {
            return ReadFilteredSnapshotAsync(snapshot, predicate, skip, take).GetAwaiter().GetResult();
        }
        catch (JsonException)
        {
            PreserveCorruptFile();
            return new List<SuggestionRecord>();
        }
        // IOException is NOT caught here — it propagates to the caller (fail-closed).
        // IOException はここでキャッチしない — 呼び出し元に伝播する（fail-closed）。
    }

    private static bool IsEmptyOrJsonWhitespace(byte[] bytes)
    {
        var offset = bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF
            ? 3
            : 0;

        for (var i = offset; i < bytes.Length; i++)
        {
            switch (bytes[i])
            {
                case (byte)' ':
                case (byte)'\t':
                case (byte)'\r':
                case (byte)'\n':
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static async Task<List<SuggestionRecord>> ReadFilteredSnapshotAsync(
        byte[] snapshot,
        Func<SuggestionRecord, bool> predicate,
        int skip,
        int? take)
    {
        var results = new List<SuggestionRecord>();
        var skipped = 0;

        await using var stream = new MemoryStream(snapshot, writable: false);

        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<SuggestionRecord>(stream, s_readOptions))
        {
            if (record == null || !predicate(record))
                continue;

            if (skipped < skip)
            {
                skipped++;
                continue;
            }

            results.Add(record);
            if (take.HasValue && results.Count >= take.Value)
                break;
        }

        return results;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        };
    }

    /// <summary>
    /// Write suggestions atomically without acquiring the lock (caller must hold it).
    /// Uses write-to-temp-and-rename to prevent partial writes. If either the write
    /// or the rename throws, the temp file is best-effort deleted so that repeated
    /// failures do not accumulate orphaned <c>.tmp</c> files in <c>.cdidx/</c>.
    /// ロックを取得せずにアトミックに提案を書き込む（呼び出し元がロックを保持していること）。
    /// 部分書き込みを防ぐため一時ファイル→リネームを使用。
    /// write または rename が失敗した場合、一時ファイルをベストエフォートで削除して
    /// <c>.cdidx/</c> に孤児 <c>.tmp</c> が蓄積するのを防ぐ。
    /// </summary>
    private void SaveUnlocked(List<SuggestionRecord> records)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        NormalizeRecordDefaults(records);
        AtomicFileWriter.WriteJson(_filePath, records, s_jsonOptions);
    }

    private static bool HasUpstreamSubmission(SuggestionRecord record) =>
        record.Status != SuggestionStatus.Draft
        || record.SubmittedToGitHub == true
        || !string.IsNullOrWhiteSpace(record.UpstreamUrl)
        || !string.IsNullOrWhiteSpace(record.GitHubIssueUrl);

    private static void MarkSubmitted(SuggestionRecord record, string issueUrl, DateTime timestamp)
    {
        record.Status = SuggestionStatus.SubmittedPendingTriage;
        record.UpstreamUrl = issueUrl;
        record.UpstreamIssueNumber = TryParseIssueNumber(issueUrl);
        record.LastSyncedAt = timestamp;
        record.LastSubmitError = null;
        record.NextRetryAt = null;
        record.SubmittedToGitHub = null;
        record.GitHubIssueUrl = null;
    }

    private bool ShouldAttemptSubmit(SuggestionRecord record)
    {
        if (record.NextRetryAt == null)
            return true;

        return record.NextRetryAt.Value <= GetUtcNow();
    }

    private void StampCreatedAt(SuggestionRecord record) => record.CreatedAt = GetUtcNow();

    private DateTime GetUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private bool PruneUnlocked(List<SuggestionRecord> records)
    {
        var maxAge = ResolveMaxAge();
        var maxCount = ResolveMaxCount();
        var cutoff = GetUtcNow().Subtract(maxAge);
        var pruned = records
            .Where(record => record.CreatedAt != default && record.CreatedAt < cutoff)
            .ToList();

        foreach (var record in pruned)
            records.Remove(record);

        var overflow = records.Count - maxCount;
        if (overflow > 0)
        {
            var excess = records
                .OrderBy(record => record.CreatedAt == default ? DateTime.MinValue : record.CreatedAt)
                .Take(overflow)
                .ToList();
            pruned.AddRange(excess);
            foreach (var record in excess)
                records.Remove(record);
        }

        if (pruned.Count == 0)
            return false;

        ArchivePrunedRecords(pruned);
        try
        {
            ConsoleUi.TryWriteErrorLine($"[cdidx] Pruned {pruned.Count} stale suggestion record(s) to {_archivePath}.");
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    private void ArchivePrunedRecords(IEnumerable<SuggestionRecord> records)
    {
        var dir = Path.GetDirectoryName(_archivePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var stream = new FileStream(_archivePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var record in records)
            writer.WriteLine(JsonSerializer.Serialize(record, s_jsonOptions));
    }

    internal static TimeSpan ResolveMaxAge()
    {
        var raw = Environment.GetEnvironmentVariable(MaxAgeDaysEnvironmentVariable);
        return int.TryParse(raw, out var days) && days is > 0 and <= MaximumMaxAgeDays
            ? TimeSpan.FromDays(days)
            : TimeSpan.FromDays(DefaultMaxAgeDays);
    }

    internal static int ResolveMaxCount()
    {
        var raw = Environment.GetEnvironmentVariable(MaxCountEnvironmentVariable);
        return int.TryParse(raw, out var count) && count is > 0 and <= MaximumMaxCount
            ? count
            : DefaultMaxCount;
    }

    private static void StampSubmitAttempt(SuggestionRecord record, DateTime timestamp, string? error, DateTime? nextRetryAt)
    {
        record.LastSubmitAttempt = timestamp;
        record.SubmitAttemptCount++;
        record.LastSubmitError = string.IsNullOrWhiteSpace(error) ? null : error;
        record.NextRetryAt = nextRetryAt;
    }

    private static void StampSubmitResult(SuggestionRecord record, SubmitAttemptResult result)
    {
        record.LastSubmitError = string.IsNullOrWhiteSpace(result.Error) ? null : result.Error;
        record.NextRetryAt = result.NextRetryAt;
    }

    private static SuggestionRecord CloneForSubmit(SuggestionRecord record) => new()
    {
        Category = record.Category,
        Language = record.Language,
        Description = record.Description,
        Context = record.Context,
        Agent = record.Agent,
        Hash = record.Hash,
        CreatedAt = record.CreatedAt,
        Status = record.Status,
        CreatedByAgent = record.CreatedByAgent,
        SessionId = record.SessionId,
        ClientVersion = record.ClientVersion,
        McpClientName = record.McpClientName,
        McpClientVersion = record.McpClientVersion,
        ToolInvocationContext = record.ToolInvocationContext,
        UpstreamIssueNumber = record.UpstreamIssueNumber,
        UpstreamUrl = record.UpstreamUrl,
        LastSyncedAt = record.LastSyncedAt,
        LastSubmitError = record.LastSubmitError,
        LastSubmitAttempt = record.LastSubmitAttempt,
        NextRetryAt = record.NextRetryAt,
        SubmitAttemptCount = record.SubmitAttemptCount,
        ResolvedAt = record.ResolvedAt,
        Supersedes = record.Supersedes,
        SupersededBy = record.SupersededBy,
        SubmittedToGitHub = record.SubmittedToGitHub,
        GitHubIssueUrl = record.GitHubIssueUrl,
    };

    internal static string RedactSensitiveText(string text, out IReadOnlyCollection<string> redactedTypes)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        var redacted = s_awsAccessKeyRegex.Replace(text, match =>
        {
            types.Add("aws_access_key");
            return RedactedAwsAccessKey;
        });
        redacted = s_bearerTokenRegex.Replace(redacted, match =>
        {
            types.Add("bearer_token");
            return RedactedBearerToken;
        });
        redacted = s_namedSecretRegex.Replace(redacted, match =>
        {
            types.Add("credential");
            return $"{match.Groups[1].Value}={RedactedCredential}";
        });
        redacted = s_highEntropyTokenRegex.Replace(redacted, match =>
        {
            if (match.Value.StartsWith("[REDACTED:", StringComparison.Ordinal))
                return match.Value;
            types.Add("high_entropy_token");
            return RedactedHighEntropyToken;
        });

        redactedTypes = types;
        return redacted;
    }

    private static SuggestionRecord RedactRecordForPersistence(SuggestionRecord record)
    {
        var redactedDescription = RedactNullable(record.Description, out var descriptionTypes) ?? string.Empty;
        var redactedContext = RedactNullable(record.Context, out var contextTypes);
        var redactedToolInvocationContext = RedactNullable(record.ToolInvocationContext, out var toolInvocationTypes);
        var allTypes = descriptionTypes.Concat(contextTypes).Concat(toolInvocationTypes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        if (allTypes.Length == 0)
            return record;

        WriteRedactionWarning(allTypes);
        var copy = CloneForSubmit(record);
        copy.Description = redactedDescription;
        copy.Context = redactedContext;
        copy.ToolInvocationContext = redactedToolInvocationContext;
        copy.Hash = ComputeHash(copy.Category, copy.Language, copy.Description);
        return copy;
    }

    private static string? RedactNullable(string? value, out IReadOnlyCollection<string> redactedTypes)
    {
        if (value == null)
        {
            redactedTypes = Array.Empty<string>();
            return null;
        }

        return RedactSensitiveText(value, out redactedTypes);
    }

    private static void WriteRedactionWarning(IReadOnlyCollection<string> redactedTypes)
    {
        try
        {
            Console.Error.WriteLine($"[cdidx] Redacted sensitive suggestion text before local persistence/GitHub submission: {string.Join(", ", redactedTypes)}.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed record SubmitReservation(
        bool IsNew,
        bool AlreadySubmitted,
        string Hash,
        SuggestionStatus Status,
        string? UpstreamUrl,
        SuggestionRecord? RecordToSubmit,
        DateTime? AttemptedAt,
        string? DuplicateOfHash,
        double? DuplicateScore);

    private static void NormalizeLegacyFields(SuggestionRecord record)
    {
        if (record.SubmittedToGitHub == true && record.Status == SuggestionStatus.Draft)
            record.Status = SuggestionStatus.SubmittedPendingTriage;

        if (string.IsNullOrWhiteSpace(record.UpstreamUrl) && !string.IsNullOrWhiteSpace(record.GitHubIssueUrl))
            record.UpstreamUrl = record.GitHubIssueUrl;

        if (record.UpstreamIssueNumber == null && !string.IsNullOrWhiteSpace(record.UpstreamUrl))
            record.UpstreamIssueNumber = TryParseIssueNumber(record.UpstreamUrl);

        record.SubmittedToGitHub = null;
        record.GitHubIssueUrl = null;
    }

    private static int? TryParseIssueNumber(string issueUrl)
    {
        if (!Uri.TryCreate(issueUrl, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.Segments;
        if (segments.Length == 0)
            return null;

        var last = segments[^1].Trim('/');
        return int.TryParse(last, out var number) ? number : null;
    }

    /// <summary>
    /// Execute an action while holding an exclusive file lock.
    /// Uses a separate .lock file to serialize concurrent access.
    /// 排他ファイルロックを保持した状態でアクションを実行する。
    /// 並行アクセスをシリアライズするため別の .lock ファイルを使用。
    /// </summary>
    private T WithFileLock<T>(Func<T> action)
    {
        var dir = Path.GetDirectoryName(_lockPath);
        if (!string.IsNullOrEmpty(dir))
            DataDirectorySecurity.CreatePrivateDirectory(dir);

        // FileShare.None provides exclusive access across processes on all platforms.
        // The lock is held for the lifetime of the FileStream (released on Dispose).
        // FileShare.None は全プラットフォームでプロセス間の排他アクセスを提供する。
        // ロックは FileStream の寿命の間保持される（Dispose で解放）。
        using var lockFile = new FileStream(
            _lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        return action();
    }

    /// <summary>
    /// Execute an action while holding an exclusive file lock (void variant).
    /// 排他ファイルロックを保持した状態でアクションを実行する（void版）。
    /// </summary>
    private void WithFileLock(Action action)
    {
        WithFileLock(() => { action(); return 0; });
    }

    /// <summary>
    /// Preserve a corrupt suggestions file by renaming it to .bak.
    /// This prevents silent data loss — the corrupt file can be inspected
    /// or recovered manually.
    /// 破損した suggestions ファイルを .bak にリネームして保存する。
    /// サイレントなデータ消失を防ぐ — 破損ファイルは手動で検査・復旧できる。
    /// </summary>
    private void PreserveCorruptFile()
    {
        try
        {
            var backupPath = _filePath + ".bak";
            if (File.Exists(LongPath.EnsureWindowsPrefix(_filePath)))
                File.Move(LongPath.EnsureWindowsPrefix(_filePath), LongPath.EnsureWindowsPrefix(backupPath), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — if we can't rename, continue with empty list.
            // ベストエフォート — リネームできなければ空リストで続行。
        }
    }
}
