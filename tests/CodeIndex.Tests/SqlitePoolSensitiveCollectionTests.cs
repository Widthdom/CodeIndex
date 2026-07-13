namespace CodeIndex.Tests;

public sealed class SqlitePoolSensitiveCollectionContractTests
{
    [Fact]
    public void Collection_RegistersPoolCleanupFixture()
    {
        Assert.Contains(
            typeof(SqlitePoolSensitiveCollection).GetInterfaces(),
            static type => type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(ICollectionFixture<>) &&
                type.GetGenericArguments()[0] == typeof(SqlitePoolSensitiveFixture));
    }
}

[Collection("SQLite pool sensitive")]
public sealed class SqlitePoolSensitiveCollectionTests
{
    [Fact]
    public async Task FixtureAndCollectionBoundary_ClearPoolsThroughSharedCallback()
    {
        var clearCount = 0;
        using var _ = SqlitePoolCleanup.ReplaceClearAllPoolsForTesting(() => clearCount++);
        var fixture = new SqlitePoolSensitiveFixture();

        await fixture.InitializeAsync();
        await fixture.DisposeAsync();

        Assert.Equal(2, clearCount);
        using var owner = SqlitePoolCleanup.EnterExclusiveOwner();

        SqlitePoolCleanup.ClearPoolsAtCollectionBoundary();

        Assert.Equal(3, clearCount);
    }
}
