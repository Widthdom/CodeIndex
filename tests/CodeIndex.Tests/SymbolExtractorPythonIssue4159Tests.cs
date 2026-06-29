using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_Python_UsesLogicalHeaderColonForAnnotatedSpans_Issue4159()
    {
        const string content = """
            def evaluate_bash_command(command: str, *, strict: bool = False) -> bool:
                if strict:
                    return command.startswith("safe")
                return True

            def _split_command(
                command: str,
                fallback: str | None = None,
            ) -> list[str]:
                parts = command.split()
                return parts

            class CommandEvaluator(
                BaseEvaluator,
            ):
                def run(self) -> None:
                    pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        var evaluate = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "evaluate_bash_command");
        Assert.Equal(1, evaluate.StartLine);
        Assert.Equal(4, evaluate.EndLine);
        Assert.Equal(2, evaluate.BodyStartLine);
        Assert.Equal(4, evaluate.BodyEndLine);

        var split = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "_split_command");
        Assert.Equal(6, split.StartLine);
        Assert.Equal(11, split.EndLine);
        Assert.Equal(10, split.BodyStartLine);
        Assert.Equal(11, split.BodyEndLine);

        var evaluator = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "CommandEvaluator");
        Assert.Equal(13, evaluator.StartLine);
        Assert.Equal(17, evaluator.EndLine);
        Assert.Equal(16, evaluator.BodyStartLine);
        Assert.Equal(17, evaluator.BodyEndLine);
    }
}
