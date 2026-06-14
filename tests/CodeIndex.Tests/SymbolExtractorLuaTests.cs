using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_Lua_DetectsFunctionsAndRequire()
    {
        // Lua: function, local function, assignment forms, require / Lua: 関数、ローカル関数、代入形式、require
        var content = """
            local http = require('socket.http')

            local helper = function(x)
              return x
            end

            M.named = function(name)
              return "hello " .. name
            end

            function M:method_form(arg)
              return arg
            end

            function M.dot_form(arg)
              return arg
            end

            function M.deep.table_key(arg, ...)
              return arg
            end

            local function top_local(x)
              return x
            end

            function plain_function(a)
              return a
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "lua", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "socket.http");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M.named");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M:method_form");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M.dot_form");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M.deep.table_key");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "top_local");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plain_function");
    }
}
