using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

/// <summary>
/// Reads and writes improvement suggestions to .cdidx/suggestions-*.json.
/// Provides deduplication via SHA256 hash of (category + language + normalized description).
/// All read-modify-write operations are serialized with a file lock to prevent
/// concurrent writers from silently overwriting each other's changes.
/// 改善提案を .cdidx/suggestions-*.json に読み書きする。
/// (category + language + 正規化済み description) のSHA256ハッシュで重複排除する。
/// 全ての read-modify-write 操作はファイルロックでシリアライズされ、
/// 並行書き込み者が互いの変更をサイレントに上書きすることを防ぐ。
/// </summary>
public class SuggestionStore
{
    private readonly string _filePath;
    private readonly string _lockPath;
    internal const FileShare StreamingReadFileShare = FileShare.ReadWrite | FileShare.Delete;

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
    {
        // Derive a safe store filename from the DB identity.
        // DB固有の安全なストアファイル名を導出する。
        var safeName = string.IsNullOrWhiteSpace(dbName) ? "codeindex" : dbName;
        _filePath = Path.Combine(cdidxDir, $"suggestions-{safeName}.json");
        _lockPath = Path.Combine(cdidxDir, $"suggestions-{safeName}.lock");
    }

    /// <summary>
    /// Compute the dedup hash for a suggestion.
    /// The hash is derived from category, language (lowered), and description (trimmed + lowered).
    /// This ensures that trivially different phrasings (e.g. different casing) produce the same hash.
    /// 提案の重複排除用ハッシュを計算する。
    /// category、language（小文字化）、description（trim + 小文字化）から導出する。
    /// 些細な表現差（大小文字等）で同じハッシュが生成されるようにする。
    /// </summary>
    public static string ComputeHash(string category, string? language, string description)
    {
        var normalized = $"{category}|{(language ?? "").ToLowerInvariant()}|{description.Trim().ToLowerInvariant()}";
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
        return WithFileLock(() =>
        {
            var existing = ReadUnlocked();
            if (existing.Any(s => s.Hash == record.Hash))
                return false;

            existing.Add(record);
            SaveUnlocked(existing);
            return true;
        });
    }

    /// <summary>
    /// Result of TryAddAndSubmit: whether the record was new or duplicate,
    /// whether it was already submitted, its lifecycle status, and the upstream URL if known.
    /// TryAddAndSubmit の結果: 新規か重複か、既に送信済みか、lifecycle 状態、判明している upstream URL。
    /// </summary>
    public record AddAndSubmitResult(bool IsNew, bool AlreadySubmitted, SuggestionStatus Status, string? UpstreamUrl);

    /// <summary>
    /// Atomically add a suggestion and attempt GitHub submission, all under one lock.
    /// This prevents concurrent callers from both observing SubmittedToGitHub=false
    /// and creating duplicate GitHub issues.
    /// 提案の追加と GitHub 送信を1つのロック内でアトミックに実行する。
    /// 並行呼び出し者が両方とも SubmittedToGitHub=false を観察して
    /// 重複 GitHub Issue を作成することを防ぐ。
    /// </summary>
    /// <param name="record">The suggestion to add / 追加する提案</param>
    /// <param name="submitToGitHub">
    /// Optional callback to submit to GitHub. Called under lock only when submission
    /// is needed (new record or unsubmitted duplicate). Returns the issue URL on success.
    /// GitHub 送信用のオプションコールバック。送信が必要な場合（新規レコードまたは
    /// 未送信の重複）にのみロック内で呼ばれる。成功時は Issue URL を返す。
    /// </param>
    public AddAndSubmitResult TryAddAndSubmit(SuggestionRecord record, Func<SuggestionRecord, string?>? submitToGitHub)
    {
        return WithFileLock(() =>
        {
            var existing = ReadUnlocked();
            var found = existing.FirstOrDefault(s => s.Hash == record.Hash);

            bool isNew = found == null;
            bool alreadySubmitted = found != null && HasUpstreamSubmission(found);

            if (isNew)
            {
                existing.Add(record);
                SaveUnlocked(existing);
                found = record;
            }

            // Attempt GitHub submission if needed and callback is provided.
            // 必要かつコールバックが提供されている場合、GitHub 送信を試みる。
            string? issueUrl = null;
            if (!alreadySubmitted && submitToGitHub != null)
            {
                try
                {
                    issueUrl = submitToGitHub(found!);
                    if (issueUrl != null)
                    {
                        MarkSubmitted(found!, issueUrl, DateTime.UtcNow);
                        SaveUnlocked(existing);
                    }
                }
                catch
                {
                    // Best-effort — GitHub submission failure does not fail the local operation.
                    // ベストエフォート — GitHub 送信失敗はローカル操作を失敗させない。
                }
            }

            return new AddAndSubmitResult(isNew, alreadySubmitted, found!.Status, issueUrl ?? found.UpstreamUrl);
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

            MarkSubmitted(record, issueUrl, DateTime.UtcNow);
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

        var json = File.ReadAllText(ioPath);
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

        var snapshot = File.ReadAllBytes(ioPath);
        if (snapshot.Length == 0)
            return new List<SuggestionRecord>();

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

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(records, s_jsonOptions);
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup / ベストエフォートのクリーンアップ */ }
            throw;
        }
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
        record.SubmittedToGitHub = null;
        record.GitHubIssueUrl = null;
    }

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
            Directory.CreateDirectory(dir);

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
        catch
        {
            // Best-effort — if we can't rename, continue with empty list.
            // ベストエフォート — リネームできなければ空リストで続行。
        }
    }
}
