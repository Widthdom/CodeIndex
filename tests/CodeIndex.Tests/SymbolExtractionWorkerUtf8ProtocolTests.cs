using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public sealed class SymbolExtractionWorkerUtf8ProtocolTests
{
    [Fact]
    public void RawUtf8Input_PreservesUnicodeCrLfAndFinalEofFrames()
    {
        var projectRoot = Directory.CreateTempSubdirectory("cdidx-symbol-worker-utf8-").FullName;
        try
        {
            var firstRequest = new SymbolExtractionWorker.WorkerRequest(
                1,
                "csharp",
                "public class 顧客 { }\n",
                Path.Combine(projectRoot, "顧客.cs"),
                projectRoot);
            var secondRequest = new SymbolExtractionWorker.WorkerRequest(
                2,
                "python",
                "class Invoice:\n    pass\n",
                Path.Combine(projectRoot, "invoice.py"),
                projectRoot);
            using var input = new MemoryStream();
            JsonSerializer.Serialize(input, firstRequest, SymbolExtractionWorker.JsonOptions);
            input.WriteByte((byte)'\r');
            input.WriteByte((byte)'\n');
            JsonSerializer.Serialize(input, secondRequest, SymbolExtractionWorker.JsonOptions);
            input.Position = 0;
            using var output = new MemoryStream();
            using var error = new StringWriter();

            bool handled;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                handled = SymbolExtractionWorker.TryRunCommand(
                    [SymbolExtractionWorker.CommandName],
                    input,
                    output,
                    error,
                    out exitCode);
            }

            Assert.True(handled);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            var responseUtf8 = output.ToArray();
            Assert.Equal((byte)'{', responseUtf8[0]);
            var responses = Encoding.UTF8.GetString(responseUtf8)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<SymbolExtractionWorker.WorkerResponse>(
                    line,
                    SymbolExtractionWorker.JsonOptions)!)
                .ToArray();
            Assert.Collection(
                responses,
                response => Assert.Contains(response.Symbols!, symbol => symbol.Name == "顧客"),
                response => Assert.Contains(response.Symbols!, symbol => symbol.Name == "Invoice"));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void RawUtf8Input_InvalidUtf8AndMalformedJsonDoNotEchoPayload()
    {
        const string secret = "SECRET_RAW_SYMBOL_WORKER_UTF8";
        using var input = new MemoryStream();
        input.Write(Encoding.UTF8.GetBytes("{\"Content\":\"" + secret + "-JSON\n"));
        input.Write(Encoding.UTF8.GetBytes("{\"Content\":\"" + secret + "-UTF8"));
        input.WriteByte(0xff);
        input.WriteByte((byte)'\n');
        input.Position = 0;
        using var output = new MemoryStream();
        using var error = new StringWriter();

        bool handled;
        int exitCode;
        lock (TestConsoleLock.Gate)
        {
            handled = SymbolExtractionWorker.TryRunCommand(
                [SymbolExtractionWorker.CommandName],
                input,
                output,
                error,
                out exitCode);
        }

        Assert.True(handled);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        var responseText = Encoding.UTF8.GetString(output.ToArray());
        var responses = responseText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, responses.Length);
        foreach (var responseTextLine in responses)
        {
            using var response = JsonDocument.Parse(responseTextLine);
            Assert.Equal(
                "worker_protocol_error: JsonException",
                response.RootElement.GetProperty("WorkerError").GetString());
        }

        Assert.DoesNotContain(secret, responseText, StringComparison.Ordinal);
    }

    [Fact]
    public void RawUtf8Input_EnforcesByteFrameLimit()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("abcdef\n"));
        using var output = new MemoryStream();
        using var error = new StringWriter();

        bool handled;
        int exitCode;
        lock (TestConsoleLock.Gate)
        {
            handled = SymbolExtractionWorker.TryRunCommand(
                [SymbolExtractionWorker.CommandName],
                input,
                output,
                error,
                out exitCode,
                maxProtocolLineCharacters: 5,
                maxProtocolLineUtf8Bytes: 5);
        }

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var response = JsonDocument.Parse(output.ToArray().AsMemory(0, checked((int)output.Length - 1)));
        Assert.Equal(
            "worker_protocol_error: BoundedLineLengthException",
            response.RootElement.GetProperty("WorkerError").GetString());
    }

    [Fact]
    public void RawUtf8Validator_PreservesPayloadPropertyDepthAndStringLimits()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var multibyteJson = Encoding.UTF8.GetBytes("{\"x\":\"あ\"}");
                Assert.False(WorkerProtocolJsonValidator.TryValidate(
                    multibyteJson,
                    maxPayloadCharacters: 8,
                    maxUtf8Bytes: multibyteJson.Length,
                    out var payloadError));
                Assert.Equal("worker_protocol_error: json_payload_length_exceeded", payloadError);

                WorkerProtocolJsonValidator.MaxJsonPropertiesForTesting = 1;
                AssertValidationError(
                    "{\"FileId\":0,\"Lang\":\"csharp\"}",
                    "worker_protocol_error: json_property_limit_exceeded");
                WorkerProtocolJsonValidator.MaxJsonPropertiesForTesting = null;

                WorkerProtocolJsonValidator.MaxJsonDepthForTesting = 4;
                AssertValidationError(
                    "{\"FileId\":0,\"Lang\":{\"nested\":{\"too\":{\"deep\":{\"overflow\":\"csharp\"}}}}}",
                    "worker_protocol_error: JsonException");
                WorkerProtocolJsonValidator.MaxJsonDepthForTesting = null;

                WorkerProtocolJsonValidator.MaxStringCharactersForTesting = 4;
                AssertValidationError(
                    "{\"Content\":\"too long\"}",
                    "worker_protocol_error: json_string_length_exceeded");
            }
            finally
            {
                WorkerProtocolJsonValidator.MaxJsonPropertiesForTesting = null;
                WorkerProtocolJsonValidator.MaxJsonDepthForTesting = null;
                WorkerProtocolJsonValidator.MaxStringCharactersForTesting = null;
            }
        }
    }

    [Fact]
    public void RawUtf8Input_CancellationInterruptsPendingRead()
    {
        using var cts = new CancellationTokenSource();
        using var input = new StalledReadStream(cts.Cancel);
        using var output = new MemoryStream();
        using var error = new StringWriter();

        bool handled;
        int exitCode;
        lock (TestConsoleLock.Gate)
        {
            handled = SymbolExtractionWorker.TryRunCommand(
                [SymbolExtractionWorker.CommandName],
                input,
                output,
                error,
                out exitCode,
                cancellationToken: cts.Token);
        }

        Assert.True(handled);
        Assert.True(input.ReadStarted);
        Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
        Assert.Equal(0, output.Length);
        Assert.Equal(string.Empty, error.ToString());
    }

    private static void AssertValidationError(string json, string expectedError)
    {
        var utf8Json = Encoding.UTF8.GetBytes(json);
        Assert.False(WorkerProtocolJsonValidator.TryValidate(
            utf8Json,
            maxPayloadCharacters: utf8Json.Length,
            maxUtf8Bytes: utf8Json.Length,
            out var error));
        Assert.Equal(expectedError, error);
    }

    private sealed class StalledReadStream(Action onReadStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        internal bool ReadStarted { get; private set; }
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            onReadStarted();
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
