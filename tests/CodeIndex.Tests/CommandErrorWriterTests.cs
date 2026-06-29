using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class CommandErrorWriterTests
{
    [Fact]
    public void Write_DoesNotDuplicateExistingUsagePrefix_Issue4244()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                CommandErrorWriter.Write(
                    "unsupported suggestions option.",
                    hint: "retry with a supported option.",
                    usage: "Usage: cdidx suggestions <list|show|export>");
            }
            finally
            {
                Console.SetError(originalError);
            }

            var output = stderr.ToString();
            Assert.Contains("Usage: cdidx suggestions <list|show|export>", output);
            Assert.DoesNotContain("Usage: Usage:", output);
        }
    }
}
