namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static readonly Dictionary<string, string[]> LanguageDisplayAliases = new(StringComparer.Ordinal)
    {
        ["javascript"] = ["js", "jsx", "cjs", "mjs"],
        ["csharp"] = ["c#", "cs", "cshtml", "razor", "blazor"],
        ["java"] = ["jav"],
        ["cpp"] = ["c++", "cplusplus"],
        ["fsharp"] = ["f#", "fs"],
        ["ruby"] = ["rb"],
        ["vb"] = ["vb.net", "vbnet", "visual basic", "visual-basic", "visual_basic", "vbs", "vbscript"],
        ["python"] = ["py", "py3", "python3"],
        ["yaml"] = ["yml"],
        ["typescript"] = ["ts", "tsx", "cts", "mts"],
        ["rust"] = ["rs"],
        ["sql"] = ["tsql", "t-sql", "transact-sql", "transactsql", "sqlserver", "mssql"],
        ["xml"] = ["xaml", "axaml"],
        ["assembly"] = ["asm", "assembler", "nasm", "gas", "gnuasm", "gnu assembler"],
    };
}
