namespace CodeIndex.Database;

/// <summary>
/// Declares why a SQLite database is being opened so read paths cannot silently acquire
/// write capabilities or run repair work.
/// SQLite DB を開く目的を宣言し、read path が暗黙に write 権限や repair 処理を得ないようにする。
/// </summary>
public enum DbOpenIntent
{
    QueryOnly,
    WriteIndex,
    Migration,
    Repair,
}
