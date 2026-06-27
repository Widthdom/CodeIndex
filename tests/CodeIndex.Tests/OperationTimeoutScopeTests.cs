using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public sealed class OperationTimeoutScopeTests
{
    [Fact]
    public async Task Token_RecordsTimeoutCancellation_Issue3998()
    {
        using var scope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.McpRequest,
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Task.Delay(TimeSpan.FromSeconds(5), scope.Token));

        Assert.True(scope.IsTimeoutCancellationRequested);
        Assert.Equal(OperationTimeoutCategories.McpRequest, scope.Category);
    }

    [Fact]
    public async Task Token_DistinguishesCallerCancellation_Issue3998()
    {
        using var cts = new CancellationTokenSource();
        using var scope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.McpRequest,
            TimeSpan.FromMinutes(5),
            cts.Token);

        cts.Cancel();
        Assert.True(scope.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Task.Delay(TimeSpan.FromSeconds(5), scope.Token));

        Assert.False(scope.IsTimeoutCancellationRequested);
    }
}
