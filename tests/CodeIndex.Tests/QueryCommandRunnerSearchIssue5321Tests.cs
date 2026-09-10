using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void SearchMatchClassifier_InterpolationRecoveryAndUnavailability_Issue5321()
    {
        (string Source, string Origin)[] cases =
        [
            ("var s = $\"{value}\"; needle();", "code"),
            ("var s = $\"😀{value}\"; 日本語.needle();", "code"),
            ("var s = $\"{needle()}\";", "code"),
            ("var s = $@\"\n{value}\n\"; needle();", "code"),
            ("var s = @$\"\n{needle()}\n\";", "code"),
            ("var s = $$\"\"\"\n{{value}}\n\"\"\"; needle();", "code"),
            ("var s = $$\"\"\"{{{needle()}}}\"\"\";", "code"),
            ("var s = $$\"\"\"\"\n{{value}} \"\"\" needle\n\"\"\"\";", "string_literal"),
            ("var s = $$\"\"\"\"\n{{value}} \"\"\" text\n\"\"\"\"; needle();", "code"),
            ("var s = $\"{{escaped}} {new { X = Call(\"}\", '\\'') }}\"; needle();", "code"),
            ("var s = $\"{Call($\"{value}\")}\"; needle();", "code"),
            ("var s = $\"{Call(\"needle\")}\";", "string_literal"),
            ("var s = $\"{ /* needle */ value }\";", "comment"),
            ("var s = $\"{ /* } \" */ value }\"; needle();", "code"),
            ("var s = $\"{value:needle}\";", "string_literal"),
            ("var s = $\"{value:000}\"; needle();", "code"),
            ("var s = $\"{value}\"; /*\nneedle\n*/", "comment"),
            ("var s = $\"{value}\"; var t = @\"\nneedle\n\";", "string_literal"),
            ("var s = $\"{value}\"; // 😀\n日本語.needle();", "code"),
            ("var s = $\"{Call(}\";\nneedle();", "unknown"),
            ("var s = $\"{value\";\nneedle();", "unknown"),
            ("var s = $\"{value} missing\nneedle();", "unknown"),
            ("var s = $\"{value:{format}}\"; needle();", "unknown"),
            ("var s = $\"{" + new string('(', 65) + "needle" + new string(')', 65) + "}\";", "unknown"),
        ];
        foreach (var (source, expected) in cases)
        {
            var context = source.Split('\n').Select((text, index) => (text, index))
                .ToDictionary(p => p.index + 1, p => p.text);
            var match = context.Single(p => p.Value.Contains("needle", StringComparison.Ordinal));
            var column = match.Value.IndexOf("needle", StringComparison.Ordinal) + 1;
            var origins = new SearchMatchClassifier.CSharpOriginContext("src/a.cs", context);
            var facet = SearchMatchClassifier.Classify("src/a.cs", "csharp", match.Key, match.Value,
                column, 6, csharpContext: origins);
            Assert.True(expected == facet.Origin, $"{source}: expected {expected}, got {facet.Origin}");
            Assert.Equal(column, facet.Column);
            if (expected == "unknown")
            {
                Assert.NotNull(facet.OriginUnavailable);
                Assert.NotEmpty(facet.OriginUnavailable.Reason);
                Assert.Equal("remaining_file", facet.OriginUnavailable.Extent);
                Assert.Equal(1, facet.OriginUnavailable.StartLine);
            }
            else
                Assert.Null(facet.OriginUnavailable);
        }
    }
}
