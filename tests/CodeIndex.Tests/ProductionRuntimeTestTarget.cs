namespace CodeIndex.Tests;

internal static class ProductionRuntimeTestTarget
{
    internal const string SecondaryTargetSkipReason =
        "Production runtime integration tests run only on net8.0; production binaries target net8.0 and focused metadata tests keep cross-target coverage.";
}
