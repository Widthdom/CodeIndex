using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Read reference-extraction completeness without initializing the full query reader.
    /// The indexing finalization path already owns the current schema and transaction, so
    /// probing every query capability again would turn a constant-size metadata read into
    /// work proportional to a large index. The caller supplies the issue-readiness state it
    /// already owns so a scoped update cannot promote a degraded snapshot to authoritative.
    /// query reader 全体を初期化せず、参照抽出の完全性を読み取る。index finalize は現在の
    /// schema と transaction を把握済みなので、全 query capability の再検査を避ける。
    /// 呼び出し元が保持する issue-readiness も渡し、degraded snapshot を昇格させない。
    /// </summary>
    internal ReferenceExtractionCapHitSummary GetReferenceExtractionCapHits(bool issuesStateAvailable)
        => DbReader.ReadReferenceExtractionCapHits(
            _conn,
            hasIssuesTable: issuesStateAvailable,
            _activeTransaction);
}
