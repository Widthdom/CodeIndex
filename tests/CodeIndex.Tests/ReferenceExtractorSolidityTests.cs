using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_Solidity_ReuseSemanticWhitespaceAndMaskingFixture()
    {
        const string content = """
            contract Vault is Ownable, Pausable {
                using SafeMath for uint256;
                event Deposit(address account);
                modifier onlyOwner() { _; }
                constructor() onlyOwner {}
                function deposit(IERC20 token, uint256 amount) external onlyOwner whenOpen {
                    string memory text = "emit Phantom()";
                    // emit CommentOnly();
                    emit Deposit(msg.sender);
                    IERC20(token).transfer(msg.sender, amount);
                }
            }

            contract Tabbed	is	TabbedBase {}
            interface Child	is	Parent {}
            """;

        var symbols = SymbolExtractor.Extract(1, "solidity", content);
        var references = ReferenceExtractor.Extract(1, "solidity", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "Ownable" && reference.ReferenceKind == "extends" && reference.ContainerName == "Vault");
        Assert.Contains(references, reference => reference.SymbolName == "Pausable" && reference.ReferenceKind == "extends" && reference.ContainerName == "Vault");
        Assert.Contains(references, reference => reference.SymbolName == "SafeMath" && reference.ReferenceKind == "use" && reference.ContainerName == "Vault");
        Assert.Contains(references, reference => reference.SymbolName == "onlyOwner" && reference.ReferenceKind == "call" && reference.ContainerName == "constructor");
        Assert.Contains(references, reference => reference.SymbolName == "onlyOwner" && reference.ReferenceKind == "call" && reference.ContainerName == "deposit");
        Assert.Contains(references, reference => reference.SymbolName == "whenOpen" && reference.ReferenceKind == "call" && reference.ContainerName == "deposit");
        Assert.Contains(references, reference => reference.SymbolName == "Deposit" && reference.ReferenceKind == "call" && reference.ContainerName == "deposit");
        Assert.Contains(references, reference => reference.SymbolName == "IERC20" && reference.ReferenceKind == "type_reference" && reference.ContainerName == "deposit");
        Assert.Contains(references, reference => reference.SymbolName == "transfer" && reference.ReferenceKind == "call" && reference.ContainerName == "deposit");
        Assert.Contains(references, reference => reference.SymbolName == "TabbedBase" && reference.ReferenceKind == "extends" && reference.ContainerName == "Tabbed");
        Assert.Contains(references, reference => reference.SymbolName == "Parent" && reference.ReferenceKind == "extends");
        Assert.DoesNotContain(references, reference => reference.SymbolName is "Phantom" or "CommentOnly");
    }

    [Fact]
    public void ExtractDetailed_SolidityEarlyReturnPreservesSafetyDiagnostics()
    {
        const string content = """
            contract Vault is Ownable {
                function run() external onlyOwner {}
            }
            """;
        var previousLimits = ReferenceExtractor.SafetyLimitsForTesting;
        try
        {
            ReferenceExtractor.SafetyLimitsForTesting = new ReferenceExtractionSafetyLimits
            {
                MaxLookupSymbols = 1,
                MaxLookupLines = 100,
                MaxNamesPerLine = 100,
                MaxContainerCandidates = 1,
            };
            var symbols = SymbolExtractor.Extract(1, "solidity", content);

            var result = ReferenceExtractor.ExtractDetailed(
                1,
                "solidity",
                content,
                symbols);

            Assert.Contains(result.References, reference =>
                reference.SymbolName == "Ownable"
                && reference.ReferenceKind == "extends");
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Kind == "reference_definition_lookup_symbol_budget_exceeded");
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Kind == "reference_container_candidate_budget_exceeded");
        }
        finally
        {
            ReferenceExtractor.SafetyLimitsForTesting = previousLimits;
        }
    }
}
