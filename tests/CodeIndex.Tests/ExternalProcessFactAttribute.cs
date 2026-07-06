namespace CodeIndex.Tests;

public sealed class ExternalProcessFactAttribute : FactAttribute
{
    public ExternalProcessFactAttribute()
    {
#if !NET8_0
        Skip = ExternalProcessTestTarget.SecondaryTargetSkipReason;
#endif
    }
}
