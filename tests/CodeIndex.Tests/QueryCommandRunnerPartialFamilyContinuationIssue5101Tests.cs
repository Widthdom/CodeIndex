using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void GroupPartials_FamilyMembersContinueWithoutGapsAndRejectInvalidCursors_Issue5101()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_family_cursor_issue5101");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var longNamespace = "N" + new string('x', 4_999);
            for (var index = 0; index < 105; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/One/Wide.{index:D3}.cs",
                    "csharp",
                    index == 104
                        ? $"namespace {longNamespace};\npublic partial class Wide : BaseWide {{ }}"
                        : $"namespace {longNamespace};\npublic partial class Wide {{ }}");
            }
            for (var index = 0; index < 2; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Two/Wide.{index:D3}.cs",
                    "csharp",
                    "namespace Demo.Two;\npublic partial class Wide { }");
            }
            MarkGraphAndFoldReady(dbPath);

            var commonArgs = new[]
            {
                "symbols", "Wide", "--db", dbPath, "--json=array", "--exact-name",
                "--lang", "csharp", "--kind", "class", "--group-partials", "--limit", "5",
            };
            var pageArgs = commonArgs.Concat([
                "--json-envelope",
                "--fields",
                "path,name,partial_family_id,representative_reason,family_members,family_members_truncated,"
                + "family_member_total_count,family_member_total_count_authoritative,"
                + "family_member_returned_count,family_member_omitted_count,"
                + "family_member_remaining_count,family_members_recovery_cursor,family_members_next_cursor",
            ]).ToArray();

            var firstPage = RunIssue5101FamilyPage(pageArgs, cursor: null, expectedTotal: 105);
            Assert.Equal(2, firstPage.TopLevelTotalCount);
            Assert.Equal([2, 105], firstPage.AvailableFamilyTotals.Order().ToArray());
            Assert.Equal(50, firstPage.MemberIds.Count);
            Assert.Equal(55, firstPage.OmittedCount);
            Assert.Equal(55, firstPage.RemainingCount);
            Assert.True(firstPage.TotalCountAuthoritative);
            Assert.Contains("src/One/Wide.104.cs", firstPage.RepresentativePaths);
            Assert.NotNull(firstPage.RecoveryCursor);
            Assert.NotNull(firstPage.NextCursor);
            Assert.InRange(firstPage.NextCursor!.Length, 1, 1_024);
            var nextCursorPayload = DecodeIssue5101Cursor(firstPage.NextCursor);
            Assert.False(nextCursorPayload.ContainsKey("partial_family_key"));
            Assert.Equal(firstPage.PartialFamilyId, nextCursorPayload["partial_family_id"]!.GetValue<string>());

            var memberIds = new List<long>(firstPage.MemberIds);
            var cursor = firstPage.NextCursor;
            var pageCount = 1;
            while (cursor is not null)
            {
                var page = RunIssue5101FamilyPage(pageArgs, cursor, expectedTotal: 105);
                pageCount++;
                Assert.True(pageCount <= 3, "partial-family cursor did not make forward progress");
                Assert.Equal(1, page.TopLevelTotalCount);
                Assert.Equal(firstPage.PartialFamilyId, page.PartialFamilyId);
                memberIds.AddRange(page.MemberIds);
                cursor = page.NextCursor;
            }

            Assert.Equal(3, pageCount);
            Assert.Equal(105, memberIds.Count);
            Assert.Equal(105, memberIds.Distinct().Count());

            var replayedFirstPage = RunIssue5101FamilyPage(
                pageArgs,
                firstPage.RecoveryCursor,
                expectedTotal: 105);
            Assert.Equal(firstPage.MemberIds, replayedFirstPage.MemberIds);

            var (arrayExitCode, arrayStdout, arrayStderr) = CaptureConsole(() =>
                ProgramRunner.Run(commonArgs, _jsonOptions, "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, arrayExitCode);
            Assert.Equal(string.Empty, arrayStderr);
            string arrayNextCursor;
            using (var arrayDocument = JsonDocument.Parse(arrayStdout))
            {
                var arrayFamily = Assert.Single(
                    arrayDocument.RootElement.EnumerateArray(),
                    row => row.GetProperty("family_member_total_count").GetInt32() == 105);
                Assert.Equal(50, arrayFamily.GetProperty("family_members").GetArrayLength());
                arrayNextCursor = arrayFamily.GetProperty("family_members_next_cursor").GetString()!;
            }
            var arrayContinuation = RunIssue5101FamilyPage(
                commonArgs,
                arrayNextCursor,
                expectedTotal: 105);
            Assert.Equal(50, arrayContinuation.MemberIds.Count);
            Assert.Equal(5, arrayContinuation.RemainingCount);

            var (compactExitCode, compactStdout, compactStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    commonArgs.Concat(["--compact", "--max-json-bytes", "20000"]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            Assert.Equal(string.Empty, compactStderr);
            using (var compactDocument = JsonDocument.Parse(compactStdout))
            {
                var compactFamily = Assert.Single(
                    compactDocument.RootElement.GetProperty("symbols").EnumerateArray(),
                    row => row.TryGetProperty("family_member_total_count", out var total)
                           && total.GetInt32() == 105);
                Assert.False(compactFamily.TryGetProperty("family_members", out _));
                Assert.NotNull(compactFamily.GetProperty("family_members_recovery_cursor").GetString());
                Assert.NotNull(compactFamily.GetProperty("family_members_next_cursor").GetString());
            }

            var metadataOnlyArgs = commonArgs.Concat([
                "--json-envelope",
                "--fields",
                "path",
                "--max-json-bytes", "12000",
            ]).ToArray();
            var metadataOnlyPage = RunIssue5101FamilyPage(
                metadataOnlyArgs,
                cursor: null,
                expectedTotal: 105,
                expectMembers: false);
            var recoveredPage = RunIssue5101FamilyPage(
                pageArgs,
                metadataOnlyPage.RecoveryCursor,
                expectedTotal: 105);
            Assert.Equal(firstPage.MemberIds, recoveredPage.MemberIds);

            var (ndjsonExitCode, ndjsonStdout, ndjsonStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunSymbols(
                    commonArgs.Skip(1).Where(arg => arg != "--json=array").Concat(["--json=ndjson"]).ToArray(),
                    _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, ndjsonExitCode);
            Assert.Equal(string.Empty, ndjsonStderr);
            var ndjsonRows = ndjsonStdout
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)!.AsObject())
                .Where(row => !row.ContainsKey("terminal_record"))
                .ToList();
            var ndjsonFamily = Assert.Single(
                ndjsonRows,
                row => row["family_member_total_count"]!.GetValue<int>() == 105);
            Assert.Equal(50, ndjsonFamily["family_member_returned_count"]!.GetValue<int>());
            var ndjsonNextCursor = ndjsonFamily["family_members_next_cursor"]!.GetValue<string>();
            var ndjsonContinuation = RunIssue5101FamilyPage(
                commonArgs.Where(arg => arg != "--json=array").Concat(["--json=ndjson"]).ToArray(),
                ndjsonNextCursor,
                expectedTotal: 105);
            Assert.Equal(50, ndjsonContinuation.MemberIds.Count);
            Assert.Equal(5, ndjsonContinuation.RemainingCount);

            var (fieldsExitCode, fieldsStdout, fieldsStderr) = CaptureConsole(() =>
                ProgramRunner.Run(["symbols", "--fields", "list"], _jsonOptions, "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, fieldsExitCode);
            Assert.Equal(string.Empty, fieldsStderr);
            using (var fieldsDocument = JsonDocument.Parse(fieldsStdout))
            {
                var validFields = fieldsDocument.RootElement.GetProperty("valid_fields")
                    .EnumerateArray()
                    .Select(field => field.GetString())
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("family_member_total_count", validFields);
                Assert.Contains("family_members_recovery_cursor", validFields);
                Assert.Contains("family_members_next_cursor", validFields);
            }

            var (inspectFieldsExitCode, inspectFieldsStdout, inspectFieldsStderr) = CaptureConsole(() =>
                ProgramRunner.Run(["inspect", "--fields", "list"], _jsonOptions, "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, inspectFieldsExitCode);
            Assert.Equal(string.Empty, inspectFieldsStderr);
            using (var inspectFieldsDocument = JsonDocument.Parse(inspectFieldsStdout))
            {
                var validFields = inspectFieldsDocument.RootElement.GetProperty("valid_fields")
                    .EnumerateArray()
                    .Select(field => field.GetString())
                    .ToHashSet(StringComparer.Ordinal);
                Assert.DoesNotContain("definitions.family_member_total_count", validFields);
                Assert.DoesNotContain("definitions.family_members_next_cursor", validFields);
                Assert.DoesNotContain("nearby_symbols.family_member_total_count", validFields);
                Assert.DoesNotContain("nearby_symbols.family_members_next_cursor", validFields);
            }
            var (inspectUnsupportedExitCode, inspectUnsupportedStdout, inspectUnsupportedStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [
                        "inspect", "Wide", "--db", dbPath, "--group-partials",
                        "--fields", "definitions.family_member_total_count",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.UsageError, inspectUnsupportedExitCode);
            Assert.Equal(string.Empty, inspectUnsupportedStderr);
            Assert.Contains("Unknown --fields value", inspectUnsupportedStdout, StringComparison.Ordinal);

            AssertIssue5101CursorFailure(
                pageArgs.Concat([
                    "--cursor",
                    MutateIssue5101Cursor(
                        firstPage.NextCursor!,
                        payload => payload["family_member_offset"] = 51),
                ]).ToArray(),
                "cursor_malformed");
            AssertIssue5101CursorFailure(
                pageArgs.Concat(["--path", "src/One/*", "--cursor", firstPage.NextCursor!]).ToArray(),
                "cursor_mismatch");

            var snapshotRaceHookInvoked = false;
            JsonEnvelopeWrapper.PartialFamilyPageReadForTesting = () =>
            {
                snapshotRaceHookInvoked = true;
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    "src/SnapshotRace.cs",
                    "csharp",
                    "namespace Demo; internal sealed class SnapshotRace { }");
                MarkGraphAndFoldReady(dbPath);
            };
            try
            {
                var (raceExitCode, raceStdout, raceStderr) = CaptureConsole(() =>
                    ProgramRunner.Run(commonArgs, _jsonOptions, "1.0.0-test"));
                Assert.True(snapshotRaceHookInvoked);
                Assert.Equal(CommandExitCodes.UsageError, raceExitCode);
                Assert.Equal(string.Empty, raceStdout);
                Assert.Contains("index generation changed", raceStderr, StringComparison.Ordinal);
            }
            finally
            {
                JsonEnvelopeWrapper.PartialFamilyPageReadForTesting = null;
            }

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GenerationChange.cs",
                "csharp",
                "namespace Demo; internal sealed class GenerationChange { }");
            MarkGraphAndFoldReady(dbPath);
            AssertIssue5101CursorFailure(
                pageArgs.Concat(["--cursor", firstPage.NextCursor!]).ToArray(),
                "cursor_stale");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private FamilyPageIssue5101 RunIssue5101FamilyPage(
        string[] args,
        string? cursor,
        int expectedTotal,
        bool expectMembers = true)
    {
        var effectiveArgs = cursor is null ? args : args.Concat(["--cursor", cursor]).ToArray();
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(effectiveArgs, _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var results = document.RootElement.GetProperty("results").EnumerateArray().ToList();
        var family = Assert.Single(
            results,
            row => row.GetProperty("family_member_total_count").GetInt32() == expectedTotal);
        var members = expectMembers
            ? family.GetProperty("family_members").EnumerateArray().ToList()
            : [];
        if (!expectMembers)
            Assert.False(family.TryGetProperty("family_members", out _));

        return new FamilyPageIssue5101(
            TopLevelTotalCount: document.RootElement.GetProperty("metadata").GetProperty("total_count").GetInt32(),
            AvailableFamilyTotals: results
                .Select(row => row.GetProperty("family_member_total_count").GetInt32())
                .ToList(),
            PartialFamilyId: family.GetProperty("partial_family_id").GetString()!,
            MemberIds: members.Select(member => member.GetProperty("symbol_id").GetInt64()).ToList(),
            RepresentativePaths: members
                .Where(member => member.TryGetProperty("representative", out var representative)
                                 && representative.GetBoolean())
                .Select(member => member.GetProperty("path").GetString()!)
                .ToList(),
            TotalCountAuthoritative: family.TryGetProperty("family_member_total_count_authoritative", out var authoritative)
                                     && authoritative.GetBoolean(),
            OmittedCount: family.GetProperty("family_member_omitted_count").GetInt32(),
            RemainingCount: family.TryGetProperty("family_member_remaining_count", out var remaining)
                ? remaining.GetInt32()
                : 0,
            RecoveryCursor: family.GetProperty("family_members_recovery_cursor").GetString(),
            NextCursor: family.TryGetProperty("family_members_next_cursor", out var nextCursor)
                ? nextCursor.GetString()
                : null);
    }

    private void AssertIssue5101CursorFailure(string[] args, string expectedError)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() =>
            ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(expectedError, stderr, StringComparison.Ordinal);
    }

    private static string MutateIssue5101Cursor(string cursor, Action<JsonObject> mutate)
    {
        var payload = DecodeIssue5101Cursor(cursor);
        mutate(payload);
        var mutated = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "response:v2:" + mutated;
    }

    private static JsonObject DecodeIssue5101Cursor(string cursor)
    {
        const string prefix = "response:v2:";
        Assert.StartsWith(prefix, cursor, StringComparison.Ordinal);
        var encoded = cursor[prefix.Length..].Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))!.AsObject();
    }

    private sealed record FamilyPageIssue5101(
        int TopLevelTotalCount,
        IReadOnlyList<int> AvailableFamilyTotals,
        string PartialFamilyId,
        IReadOnlyList<long> MemberIds,
        IReadOnlyList<string> RepresentativePaths,
        bool TotalCountAuthoritative,
        int OmittedCount,
        int RemainingCount,
        string? RecoveryCursor,
        string? NextCursor);
}
