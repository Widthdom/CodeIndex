namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class EnvironmentVariableScopeTests
{
    [Fact]
    public void Dispose_RestoresPresentAndMissingOriginalValues()
    {
        foreach (var originalValue in new string?[] { "before", null })
        {
            var name = $"CDIDX_TEST_SCOPE_{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable(name, originalValue);
            try
            {
                using (var env = EnvironmentVariableScope.Capture(name))
                {
                    env.Set(name, "during");
                    Assert.Equal("during", Environment.GetEnvironmentVariable(name));
                }

                Assert.Equal(originalValue, Environment.GetEnvironmentVariable(name));
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }
}
