using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_Solidity_DetectsContractsMembersAndRanges()
    {
        const string content = """
            pragma solidity ^0.8.20;

            abstract contract Vault is Ownable {
                event Deposit(address indexed account, uint256 amount);
                error Unauthorized(address account);

                struct Position { uint256 amount; }
                enum State { Open, Closed }

                modifier onlyOwner() { _; }

                constructor(address owner) onlyOwner {}
                function deposit(uint256 amount) external onlyOwner { emit Deposit(msg.sender, amount); }
                fallback() external payable {}
                receive() external payable {}
            }

            interface IERC20 {
                function transfer(address to, uint256 amount) external returns (bool);
            }

            library SafeMath {
                function add(uint256 a, uint256 b) internal pure returns (uint256) { return a + b; }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "solidity", content);

        var vault = Assert.Single(symbols.Where(symbol => symbol.Name == "Vault"));
        Assert.Equal("class", vault.Kind);
        Assert.Equal("contract", vault.SubKind);
        Assert.Equal(3, vault.StartLine);
        Assert.Equal(16, vault.EndLine);
        Assert.Equal(3, vault.BodyStartLine);
        Assert.Equal(16, vault.BodyEndLine);

        Assert.Contains(symbols, symbol => symbol.Name == "IERC20" && symbol.Kind == "interface" && symbol.SubKind == "interface");
        Assert.Contains(symbols, symbol => symbol.Name == "SafeMath" && symbol.Kind == "class" && symbol.SubKind == "library");
        Assert.Contains(symbols, symbol => symbol.Name == "Deposit" && symbol.Kind == "event" && symbol.SubKind == "event" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "Unauthorized" && symbol.Kind == "type" && symbol.SubKind == "error" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "Position" && symbol.Kind == "struct" && symbol.SubKind == "struct" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "State" && symbol.Kind == "enum" && symbol.SubKind == "enum" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "onlyOwner" && symbol.Kind == "function" && symbol.SubKind == "modifier" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "constructor" && symbol.Kind == "function" && symbol.SubKind == "constructor" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "deposit" && symbol.Kind == "function" && symbol.SubKind == "function" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "fallback" && symbol.Kind == "function" && symbol.SubKind == "fallback" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "receive" && symbol.Kind == "function" && symbol.SubKind == "receive" && symbol.ContainerName == "Vault");
        Assert.Contains(symbols, symbol => symbol.Name == "transfer" && symbol.Kind == "function" && symbol.ContainerName == "IERC20");
        Assert.Contains(symbols, symbol => symbol.Name == "add" && symbol.Kind == "function" && symbol.ContainerName == "SafeMath");
    }

    [Fact]
    public void Extract_Solidity_IgnoresCommentsAndStrings()
    {
        const string content = """
            // contract Fake {}
            contract Real {
                string constant Text = "function Nope() public {}";
                /*
                event Phantom(address indexed account);
                */
                function run() external {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "solidity", content);

        Assert.Contains(symbols, symbol => symbol.Name == "Real" && symbol.Kind == "class");
        Assert.Contains(symbols, symbol => symbol.Name == "run" && symbol.Kind == "function");
        Assert.DoesNotContain(symbols, symbol => symbol.Name is "Fake" or "Nope" or "Phantom");
    }
}
