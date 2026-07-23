namespace CodeIndex.Tests;

internal static class ProductionCliTestTarget
{
    internal const string SecondaryTargetSkipReason =
        "Production CLI installer tests run only on net8.0; install.sh is target-framework independent and the production CLI targets net8.0.";

    internal const string WindowsSkipReason =
        "Production CLI installer and container-entrypoint tests require a Unix shell.";
}
