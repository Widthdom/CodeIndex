namespace CodeIndex.Tests;

public sealed class ExternalProcessTheoryAttribute : TheoryAttribute
{
    public ExternalProcessTheoryAttribute()
    {
#if !NET8_0
        Skip = ExternalProcessTestTarget.SecondaryTargetSkipReason;
#endif
    }
}
