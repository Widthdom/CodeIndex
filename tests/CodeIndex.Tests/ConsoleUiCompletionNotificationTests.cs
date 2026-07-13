using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class ConsoleUiCompletionNotificationTests
{
    [Fact]
    public void EmitCompletionNotification_AutoSuppressesWhenConsoleOutputIsCaptured_Issue4333()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);

                ConsoleUi.EmitCompletionNotification(CompletionNotificationMode.Auto, "done");

                Assert.Equal(string.Empty, stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void EmitCompletionNotification_NoneSuppressesOutput_Issue4333()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                ConsoleUi.EmitCompletionNotification(CompletionNotificationMode.None, "done");

                Assert.Equal(string.Empty, stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void EmitCompletionNotification_BellWritesOneBoundedSignal_Issue4333()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                ConsoleUi.EmitCompletionNotification(CompletionNotificationMode.Bell, "done\r\nnow");

                Assert.Equal("\a", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void EmitCompletionNotification_Osc9FlattensControlLines_Issue4333()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                ConsoleUi.EmitCompletionNotification(CompletionNotificationMode.Osc9, "done\r\nnow");

                Assert.Equal("\u001b]9;done  now\a", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }
}
