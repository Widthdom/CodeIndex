using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_Solidity_EmitsInheritanceLibraryModifierEventAndInterfaceReferences()
    {
        const string content = """
            contract Vault is Ownable, Pausable {
                using SafeMath for uint256;
                event Deposit(address account);
                modifier onlyOwner() { _; }
                constructor() onlyOwner {}
                function deposit(IERC20 token, uint256 amount) external onlyOwner whenOpen {
                    emit Deposit(msg.sender);
                    IERC20(token).transfer(msg.sender, amount);
                }
            }
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
    }

    [Fact]
    public void Extract_Solidity_IgnoresCommentsAndStrings()
    {
        const string content = """
            contract Vault is Ownable {
                function deposit() external onlyOwner {
                    string memory text = "emit Phantom()";
                    // emit CommentOnly();
                    emit Deposit();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "solidity", content);
        var references = ReferenceExtractor.Extract(1, "solidity", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "Ownable" && reference.ReferenceKind == "extends");
        Assert.Contains(references, reference => reference.SymbolName == "onlyOwner" && reference.ReferenceKind == "call");
        Assert.Contains(references, reference => reference.SymbolName == "Deposit" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference => reference.SymbolName is "Phantom" or "CommentOnly");
    }
}
