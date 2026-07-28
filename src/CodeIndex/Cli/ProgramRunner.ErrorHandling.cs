using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    private static bool IsTruthyEnvironmentVariable(string name)
    {
        var value = CdidxEnvironment.GetEnvironmentVariable(name);
        return value != null
               && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }

    internal static int MapCodeIndexExceptionExitCode(string code) => code switch
    {
        CommandErrorCodes.DbNotFound => CommandExitCodes.NotFound,
        CommandErrorCodes.CheckpointNotFound => CommandExitCodes.NotFound,
        CommandErrorCodes.DbLocked => CommandExitCodes.TransientDatabaseError,
        CommandErrorCodes.DbNotWritable => CommandExitCodes.DatabaseError,
        CommandErrorCodes.DbIntegrityFailed => CommandExitCodes.DatabaseError,
        CommandErrorCodes.SchemaTooNew => CommandExitCodes.DatabaseError,
        CommandErrorCodes.TempStoreExhausted => CommandExitCodes.DatabaseError,
        CommandErrorCodes.DbError => CommandExitCodes.DatabaseError,
        CommandErrorCodes.DbNotDatabase => CommandExitCodes.DatabaseError,
        CommandErrorCodes.DirectoryNotFound => CommandExitCodes.NotFound,
        CommandErrorCodes.FeatureUnavailable => CommandExitCodes.FeatureUnavailable,
        CommandErrorCodes.UsageError => CommandExitCodes.InvalidArgument,
        CommandErrorCodes.Interrupted => CommandExitCodes.CancelledBySignal,
        _ => CommandExitCodes.DatabaseError,
    };

    internal static int MapUnhandledExceptionExitCode(Exception ex)
    {
        var sqliteException = FindSqliteException(ex);
        if (sqliteException is null)
            return CommandExitCodes.UnhandledException;

        return sqliteException.SqliteErrorCode switch
        {
            5 or 6 or 8 => CommandExitCodes.TransientDatabaseError,
            _ => CommandExitCodes.DatabaseError,
        };
    }

    private static SqliteException? FindSqliteException(Exception ex)
    {
        if (ex is SqliteException sqliteException)
            return sqliteException;
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var found = FindSqliteException(inner);
                if (found is not null)
                    return found;
            }
        }

        return ex.InnerException is null ? null : FindSqliteException(ex.InnerException);
    }

    private sealed class QuietStderrScope : IDisposable
    {
        private readonly TextWriter _originalError;
        private readonly TextWriter _replacementError;
        private readonly IDisposable _ownership;

        private QuietStderrScope(
            TextWriter originalError,
            TextWriter replacementError,
            IDisposable ownership)
        {
            _originalError = originalError;
            _replacementError = replacementError;
            _ownership = ownership;
        }

        public static QuietStderrScope Start()
        {
            var ownership = ConsoleStreamOwnership.Enter();
            try
            {
                var originalError = Console.Error;
                var replacementError = new ErrorOnlyTextWriter(originalError);
                Console.SetError(replacementError);
                return new QuietStderrScope(originalError, replacementError, ownership);
            }
            catch
            {
                ownership.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                _replacementError.Flush();
                ConsoleStreamOwnership.RestoreError(_originalError);
            }
            finally
            {
                _ownership.Dispose();
            }
        }
    }

    private sealed class ErrorOnlyTextWriter(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _lineBuffer = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (value == '\r')
                return;

            if (value == '\n')
            {
                FlushBufferedLine();
                return;
            }

            _lineBuffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;

            foreach (var ch in value)
                Write(ch);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            FlushBufferedLine();
        }

        public override void Flush()
        {
            FlushBufferedLine();
            inner.Flush();
        }

        private void FlushBufferedLine()
        {
            if (_lineBuffer.Length == 0)
                return;

            var line = _lineBuffer.ToString();
            _lineBuffer.Clear();
            if (IsErrorLine(line))
                inner.WriteLine(line);
        }

        private static bool IsErrorLine(string line)
            => line.StartsWith("Error", StringComparison.Ordinal);
    }
}
