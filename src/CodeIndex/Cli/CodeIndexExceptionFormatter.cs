using System.Text.Json;

namespace CodeIndex.Cli;

/// <summary>
/// Uniform CLI formatter for <see cref="CodeIndexException"/> so the structured
/// fields (Code, Category, Path, Hint) end up on stderr (or, when --json was
/// requested, in a <see cref="CommandErrorJsonResult"/> envelope on stdout)
/// instead of being lost behind a generic stack trace (#1580).
/// CodeIndexException 用の CLI 出力フォーマッタ。stderr または --json 時の構造化エンベロープ
/// に Code / Category / Path / Hint を一律な形で出すことで、汎用スタックトレース越しに
/// 構造化情報が失われないようにする (#1580)。
/// </summary>
internal static class CodeIndexExceptionFormatter
{
    public static void Write(CodeIndexException ex, string[] args, JsonSerializerOptions jsonOptions)
    {
        if (HasJsonFlag(args))
        {
            var payload = new CommandErrorJsonResult(
                Status: "error",
                Message: ex.Message,
                Hint: ex.Hint,
                ErrorCode: ex.Code,
                Path: ex.Path,
                Category: ex.Category);
            Console.WriteLine(JsonSerializer.Serialize(
                payload,
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult));
            return;
        }

        // Keep human output close to the existing `Error [Exxx]: ...` shape that
        // QueryCommandRunner / IndexCommandRunner already emit so downstream
        // parsers do not need a second format.
        // 既存の `Error [Exxx]: ...` 形に揃え、parser の差分を最小化する。
        CommandErrorWriter.WriteStderr($"Error [{ex.Code}]: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.Path))
            CommandErrorWriter.WriteStderr($"Path: {ex.Path}");
        if (!string.IsNullOrEmpty(ex.Hint))
            CommandErrorWriter.WriteStderr($"Hint: {ex.Hint}");
    }

    internal static bool HasJsonFlag(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == "--")
                return false;
            if (arg == "--json")
                return true;
        }
        return false;
    }
}
