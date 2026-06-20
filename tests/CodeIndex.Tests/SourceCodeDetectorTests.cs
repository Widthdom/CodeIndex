using System.Text;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for SourceCodeDetector (source code leak prevention).
/// SourceCodeDetectorのテスト（ソースコード漏洩防止）。
///
/// These tests verify that:
/// - Natural-language descriptions of gaps/errors are ALLOWED (return false)
/// - Pasted source code blocks are REJECTED (return true)
/// - Short inline code examples are ALLOWED
/// - Edge cases are handled correctly
/// </summary>
public class SourceCodeDetectorTests
{
    // ================================================================
    // ALLOWED inputs — these should NOT be flagged as source code.
    // 許容される入力 — ソースコードとしてフラグされるべきではない。
    // ================================================================

    [Theory]
    [InlineData("TypeScript の arrow function がシンボル抽出で拾えない")]
    [InlineData("Symbol extraction misses Kotlin data classes")]
    [InlineData("class keyword is incorrectly recognized as record")]
    [InlineData("cdidx search で NullReferenceException が発生した")]
    [InlineData("The search ranking puts test files above source files")]
    [InlineData("Reference extraction does not work for Go interfaces")]
    public void AllowsNaturalLanguageDescriptions(string text)
    {
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsShortInlineCodeExample()
    {
        // A single backtick-wrapped example should be allowed.
        // バッククォートで囲まれた短い例示は許容されるべき。
        var text = "Symbol extraction misses arrow functions like `const foo = () => {}`";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsSingleLineCodeMention()
    {
        // Mentioning a single line of code in a sentence is fine.
        // 文中で1行のコードに言及するのは問題ない。
        var text = "When I write `public class MyRecord : IDisposable`, the symbol extractor misses it.";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsErrorMessageDescription()
    {
        // Describing an error message is not source code.
        // エラーメッセージの記述はソースコードではない。
        var text = "The tool crashed with: System.NullReferenceException: Object reference not set to an instance of an object.\n"
                 + "This happened when searching for symbols in a large TypeScript file.";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsShortBulletList()
    {
        // A bullet list of issues is not source code.
        // 課題の箇条書きはソースコードではない。
        var text = "Problems observed:\n"
                 + "- Arrow functions not detected\n"
                 + "- Class expressions ignored\n"
                 + "- Decorators cause parse errors\n"
                 + "- Default exports not indexed";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsLargeNaturalLanguageWithoutWholeTextSplit_Issue3068()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 6000; i++)
            builder.Append("Search ranking should keep implementation files ahead of generated fixtures. Line ").Append(i).Append('\n');

        Assert.False(SourceCodeDetector.ContainsSourceCode(builder.ToString()));
    }

    [Fact]
    public void AllowsEmptyOrWhitespace()
    {
        Assert.False(SourceCodeDetector.ContainsSourceCode(""));
        Assert.False(SourceCodeDetector.ContainsSourceCode("   "));
        Assert.False(SourceCodeDetector.ContainsSourceCode(null!));
        Assert.Null(SourceCodeDetector.Detect(null).ReasonCode);
    }

    [Fact]
    public void AllowsTwoLineCodeSnippet()
    {
        // Two lines of code-like text should not trigger (threshold is 3).
        // 2行のコード的テキストでは発動しない（しきい値は3）。
        var text = "Example pattern that fails:\n"
                 + "    const handler = (e) => {\n"
                 + "The above is not extracted as a symbol.";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsProseWithBackticksAndTildes_Issue3830()
    {
        var text = "The docs mention `inline code`, ``` fences, and ~~~ fences in prose, "
                 + "but no fenced block is present.";

        var result = SourceCodeDetector.Detect(text);

        Assert.False(result.ContainsSourceCode);
        Assert.Null(result.ReasonCode);
    }

    // ================================================================
    // REJECTED inputs — these SHOULD be flagged as source code.
    // 拒否される入力 — ソースコードとしてフラグされるべき。
    // ================================================================

    [Fact]
    public void RejectsMultiLineCodeBlock()
    {
        // A typical C# method pasted verbatim.
        // C# のメソッドがそのままコピペされた典型例。
        var text = "public void ProcessFile(string path)\n"
                 + "{\n"
                 + "    var content = File.ReadAllText(path);\n"
                 + "    var lines = content.Split('\\n');\n"
                 + "    foreach (var line in lines)\n"
                 + "    {\n"
                 + "        Console.WriteLine(line);\n"
                 + "    }\n"
                 + "}";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Theory]
    [InlineData(
        "alpha;\nbeta;\ngamma;\ndelta;\nepsilon;",
        SourceCodeDetector.ReasonStatementEnding)]
    [InlineData(
        "    var current = 1\n    return current\n    result.ToString()",
        SourceCodeDetector.ReasonIndentedCodeLines)]
    [InlineData(
        "section {\nalpha\nbeta\ngamma\n}",
        SourceCodeDetector.ReasonBlockStructure)]
    [InlineData(
        "import alpha\nimport beta\nimport gamma",
        SourceCodeDetector.ReasonRepeatedImports)]
    [InlineData(
        "def process():\n    return 1",
        SourceCodeDetector.ReasonFunctionDefinition)]
    [InlineData(
        "Here is the snippet:\n~~~csharp\nreturn token;\n~~~",
        SourceCodeDetector.ReasonFencedCodeBlock)]
    public void Detect_ReturnsStableReasonCode_Issue3830(string text, string expectedReason)
    {
        var result = SourceCodeDetector.Detect(text);

        Assert.True(result.ContainsSourceCode);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsCrLfMultiLineCodeBlock_Issue3068()
    {
        var text = "public void ProcessFile(string path)\r\n"
                 + "{\r\n"
                 + "    var content = File.ReadAllText(path);\r\n"
                 + "    return;\r\n"
                 + "    Console.WriteLine(content);\r\n"
                 + "}";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsJavaScriptFunction()
    {
        var text = "function calculateTotal(items) {\n"
                 + "    let total = 0;\n"
                 + "    for (const item of items) {\n"
                 + "        total += item.price;\n"
                 + "    }\n"
                 + "    return total;\n"
                 + "}";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsPythonFunction()
    {
        var text = "def process_data(data):\n"
                 + "    result = []\n"
                 + "    for item in data:\n"
                 + "        if item.is_valid():\n"
                 + "            result.append(item)\n"
                 + "    return result";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsImportBlock()
    {
        // A block of import statements (top of a file).
        // import 文のブロック（ファイル先頭のコピペ）。
        var text = "import React from 'react';\n"
                 + "import { useState, useEffect } from 'react';\n"
                 + "import axios from 'axios';\n"
                 + "import { Button } from './components';";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsUsingBlock()
    {
        var text = "using System;\n"
                 + "using System.Collections.Generic;\n"
                 + "using System.Linq;\n"
                 + "using Microsoft.Data.Sqlite;";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsClassDefinition()
    {
        var text = "public class UserService {\n"
                 + "    private readonly ILogger _logger;\n"
                 + "    private readonly IUserRepository _repo;\n"
                 + "    public UserService(ILogger logger, IUserRepository repo) {\n"
                 + "        _logger = logger;\n"
                 + "        _repo = repo;\n"
                 + "    }\n"
                 + "}";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsStatementHeavyText()
    {
        // Text where most lines end with semicolons.
        // ほとんどの行がセミコロンで終わるテキスト。
        var text = "var x = 1;\n"
                 + "var y = 2;\n"
                 + "var z = x + y;\n"
                 + "Console.WriteLine(z);\n"
                 + "Console.WriteLine(x);\n"
                 + "Console.WriteLine(y);";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsRustFunction()
    {
        var text = "fn process(input: &str) -> Result<String, Error> {\n"
                 + "    let parsed = parse_input(input)?;\n"
                 + "    let result = transform(parsed);\n"
                 + "    Ok(result.to_string())\n"
                 + "}";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsGoFunction()
    {
        var text = "func handleRequest(w http.ResponseWriter, r *http.Request) {\n"
                 + "    body, err := io.ReadAll(r.Body)\n"
                 + "    if err != nil {\n"
                 + "        http.Error(w, err.Error(), 500)\n"
                 + "        return\n"
                 + "    }\n"
                 + "    w.Write(body)\n"
                 + "}";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsIncludeBlock()
    {
        var text = "#include <stdio.h>\n"
                 + "#include <stdlib.h>\n"
                 + "#include <string.h>";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsPythonImportBlock()
    {
        var text = "from pathlib import Path\n"
                 + "from typing import List, Optional\n"
                 + "from dataclasses import dataclass";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsPythonExpressionOnlyBody()
    {
        // Python function with print() calls — no braces, no semicolons.
        // The expression-only lines (print, append) must still be detected.
        // print() 呼び出しを含む Python 関数 — 波括弧もセミコロンもない。
        // expression-only 行（print, append）も検出されなければならない。
        var text = "def greet(names):\n"
                 + "    for name in names:\n"
                 + "        print(name)\n"
                 + "    print('done')";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsPythonMethodChainBody()
    {
        // Python with method calls like result.append() — no assignment operators.
        // result.append() のようなメソッド呼び出しを含む Python — 代入演算子なし。
        var text = "def process(items):\n"
                 + "    result = []\n"
                 + "    for item in items:\n"
                 + "        result.append(item)\n"
                 + "    result.sort()";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsRubyExpressionBody()
    {
        var text = "def hello\n"
                 + "    puts 'hello'\n"
                 + "    puts 'world'\n"
                 + "    puts 'done'\n"
                 + "end";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    // ================================================================
    // Edge cases / エッジケース
    // ================================================================

    [Fact]
    public void AllowsDescriptionWithCodeKeywords()
    {
        // Using code keywords in natural language sentences should be fine.
        // 自然言語文中でのコードキーワード使用は問題ない。
        var text = "The 'return' keyword inside a lambda is not detected as a symbol.\n"
                 + "Also, 'if' expressions in Kotlin are treated as statements.\n"
                 + "This affects how 'var' declarations are parsed.";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsMarkdownFormatting()
    {
        // Markdown-style formatting in descriptions should be fine.
        // 説明内のMarkdown形式は問題ない。
        var text = "## Problem\n"
                 + "When indexing TypeScript files:\n"
                 + "- Arrow functions `=>` are not detected\n"
                 + "- Template literals `${}` cause issues\n"
                 + "\n"
                 + "## Expected\n"
                 + "Both should be handled correctly.";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsSingleImportMention()
    {
        // Mentioning one or two imports is fine (threshold is 3).
        // 1〜2行の import 言及は問題ない（しきい値は3）。
        var text = "The line `import React from 'react'` is not detected.\n"
                 + "Also `import { useState } from 'react'` is missed.";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    // ================================================================
    // Fenced code block tests / フェンスドコードブロックテスト
    // ================================================================

    [Fact]
    public void RejectsFencedCodeBlock()
    {
        // A markdown fenced code block should be detected.
        // マークダウンのフェンスドコードブロックは検出されるべき。
        var text = "Here is the issue:\n"
                 + "```ts\n"
                 + "const token = issueToken(user);\n"
                 + "return token;\n"
                 + "```";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsFencedCodeBlockNoLanguageTag()
    {
        var text = "```\n"
                 + "var x = 1;\n"
                 + "```";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsFencedCodeBlockMultipleLines()
    {
        var text = "The problem:\n"
                 + "```python\n"
                 + "def foo():\n"
                 + "    return 42\n"
                 + "```\n"
                 + "This function is not detected.";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void RejectsTildeFencedCodeBlock_Issue3830()
    {
        var text = "The problem:\n"
                 + "~~~python\n"
                 + "token\n"
                 + "~~~";

        var result = SourceCodeDetector.Detect(text);

        Assert.True(result.ContainsSourceCode);
        Assert.Equal(SourceCodeDetector.ReasonFencedCodeBlock, result.ReasonCode);
    }

    [Fact]
    public void RejectsIndentedTildeFencedCodeBlock_Issue3830()
    {
        var text = "The problem:\n"
                 + "   ~~~python\n"
                 + "token\n"
                 + "   ~~~";

        var result = SourceCodeDetector.Detect(text);

        Assert.True(result.ContainsSourceCode);
        Assert.Equal(SourceCodeDetector.ReasonFencedCodeBlock, result.ReasonCode);
    }

    [Theory]
    [InlineData("    ```csharp\nreturn token;\n    ```")]
    [InlineData("\t```csharp\nreturn token;\n\t```")]
    [InlineData("    ~~~csharp\nreturn token;\n    ~~~")]
    [InlineData("\t~~~csharp\nreturn token;\n\t~~~")]
    public void RejectsListIndentedFencedCodeBlocks_Issue3830(string text)
    {
        var result = SourceCodeDetector.Detect(text);

        Assert.True(result.ContainsSourceCode);
        Assert.Equal(SourceCodeDetector.ReasonFencedCodeBlock, result.ReasonCode);
    }

    [Fact]
    public void AllowsEmptyFencedBlock()
    {
        // An empty fenced block (no content lines) should be allowed.
        // 空のフェンスドブロック（内容行なし）は許容されるべき。
        var text = "See:\n"
                 + "```\n"
                 + "```\n"
                 + "Nothing there.";
        Assert.False(SourceCodeDetector.ContainsSourceCode(text));
    }

    [Fact]
    public void AllowsEmptyTildeFencedBlock_Issue3830()
    {
        var text = "See:\n"
                 + "~~~\n"
                 + "~~~\n"
                 + "Nothing there.";

        var result = SourceCodeDetector.Detect(text);

        Assert.False(result.ContainsSourceCode);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public void RejectsShortUnindentedCodeInFence()
    {
        // Even short snippets inside fences should be caught.
        // フェンス内の短いスニペットも検出されるべき。
        var text = "```\nreturn x;\n```";
        Assert.True(SourceCodeDetector.ContainsSourceCode(text));
    }
}
