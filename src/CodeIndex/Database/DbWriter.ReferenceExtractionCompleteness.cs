using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Read reference-extraction completeness without initializing the full query reader.
    /// The indexing finalization path already owns the current schema and transaction, so
    /// probing every query capability again would turn a constant-size metadata read into
    /// work proportional to a large index.
    /// query reader 全体を初期化せず、参照抽出の完全性を読み取る。index finalize は現在の
    /// schema と transaction を把握済みなので、全 query capability の再検査を避ける。
    /// </summary>
    internal ReferenceExtractionCapHitSummary GetReferenceExtractionCapHits()
        => DbReader.ReadReferenceExtractionCapHits(
            _conn,
            hasIssuesTable: true,
            _activeTransaction);
}
