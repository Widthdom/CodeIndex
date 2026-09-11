using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;

namespace CodeIndex.Database;

public sealed record XmlSettingsAuditEvidence(
    string State, string Reason, string DtdProcessing, string Resolver,
    long? MaxCharactersInDocument, long? MaxCharactersFromEntities,
    List<XmlSettingsAuditSource> Sources)
{
    public string VulnerabilityConfidence => "not_established";
    public int DepthLimit => 3;
    public int NodeLimit => 128;
    public int SourceByteLimit => 1048576;
}

public sealed record XmlSettingsAuditSource(string Path, int Line, string Checksum);

public partial class DbReader
{
    internal XmlSettingsAuditSession CreateXmlSettingsAuditSession() => new(this);

    // A deliberately small source language: direct initializers, single-return
    // factories, literal enum arguments and positive integer constant expressions.
    // Anything outside it is evidence requiring review, never an inferred guard.
    internal sealed class XmlSettingsAuditSession(DbReader owner)
    {
        private const int FileBytes = 262144;
        private const int TotalBytes = 1048576;
        private readonly Dictionary<string, XmlSource> sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SameSymbolGuardRange>> containers = new(StringComparer.Ordinal);
        private int bytes;
        private int nodes;
        private bool? ready;
        private sealed class Unavailable(string reason) : Exception(reason);
        private sealed record XmlSource(string Path, string Code, string[] Lines, string Checksum);
        private sealed record Target(long Id, string Path, string Name, int Start, int End, string Signature);

        internal XmlSettingsAuditEvidence Classify(string path, string? language, IReadOnlyList<int> matchLines)
        {
            var provenance = new List<XmlSettingsAuditSource>();
            try
            {
                owner.ThrowIfCancellationRequested();
                if (language != "csharp") throw new Unavailable("unsupported_language");
                if (ready == null)
                {
                    ready = false;
                    owner.ValidateSameSymbolGuardContract();
                    ready = owner.GetPersistedIndexGenerationReadiness().GraphDataCurrent;
                    using var binding = owner._conn.CreateCommand();
                    binding.CommandText = """
                        SELECT 1 FROM symbols WHERE name IN ('XmlReader','XmlReaderSettings','XmlUrlResolver','DtdProcessing')
                          AND kind IN ('class','struct','enum','interface','type','alias','type_alias') LIMIT 1
                        """;
                    if (binding.ExecuteScalar() != null) ready = false;
                }
                if (ready != true) throw new Unavailable("graph_metadata_incomplete");
                if (matchLines.Count is 0 or > 16) throw new Unavailable("match_site_budget_or_context_missing");
                XmlSettingsAuditEvidence? result = null;
                foreach (var line in matchLines.Distinct())
                {
                    Step();
                    var source = ReadSource(path);
                    var target = Container(path, line);
                    ValidateValueBindings(target);
                    var body = Body(source, target);
                    AddSource(provenance, source, line);
                    // A row can represent several occurrences. It may be filtered
                    // only when every represented occurrence proves the same guards.
                    var evidence = Observe(source, target, body, line, provenance);
                    if (result != null && (result.State != evidence.State || result.DtdProcessing != evidence.DtdProcessing
                        || result.Resolver != evidence.Resolver || result.MaxCharactersInDocument != evidence.MaxCharactersInDocument
                        || result.MaxCharactersFromEntities != evidence.MaxCharactersFromEntities))
                        throw new Unavailable("mixed_observations");
                    result = evidence;
                }
                return result!;
            }
            catch (Unavailable ex) { return Unknown(ex.Message, provenance); }
            catch (CodeIndexException) { return Unknown("source_or_metadata_unavailable", provenance); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                or FileIndexer.FileTooLargeSkippedException or FileIndexer.BinaryFileSkippedException)
            { return Unknown("source_stale_missing_or_over_budget", provenance); }
            catch (RegexMatchTimeoutException) { return Unknown("syntax_budget_exceeded", provenance); }
        }

        private static XmlSettingsAuditEvidence Unknown(string reason, List<XmlSettingsAuditSource> sources)
            => new("needs_review", reason, "unknown", "unknown", null, null, sources);

        private void Step()
        {
            owner.ThrowIfCancellationRequested();
            if (++nodes > 128) throw new Unavailable("node_budget_exceeded");
        }

        private XmlSource ReadSource(string path)
        {
            if (sources.TryGetValue(path, out var cached)) return cached;
            Step();
            if (sources.Count >= 16 || bytes >= TotalBytes) throw new Unavailable("source_byte_budget_exceeded");
            var root = owner.GetIndexedProjectRoot();
            if (string.IsNullOrEmpty(root) || Path.IsPathRooted(path)) throw new Unavailable("source_root_unavailable");
            var absolute = Path.GetFullPath(Path.Combine(root, path));
            var relative = Path.GetRelativePath(root, absolute);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new Unavailable("source_path_unavailable");
            var loaded = new FileContentLoader(Math.Min(FileBytes, TotalBytes - bytes)).Load(absolute, path, path, owner._cancellation);
            bytes += Encoding.UTF8.GetByteCount(loaded.Content);
            if (bytes > TotalBytes) throw new Unavailable("source_byte_budget_exceeded");
            using var cmd = owner._conn.CreateCommand();
            cmd.CommandText = "SELECT checksum FROM files WHERE path = @path";
            SqliteCommandPolicy.Add(cmd, "@path", path);
            if (!string.Equals(cmd.ExecuteScalar() as string, loaded.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new Unavailable("source_checksum_mismatch");
            var code = MaskCSharpNonCode(loaded.Content);
            // Aliases, directives and interpolation can change binding or hide
            // executable expressions. Do not infer a compiler binding for them.
            if (code.Contains('#') || code.Contains('\\')
                || code.Any(ch => char.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                || Matches(code, @"\busing\s+\w+\s*=").Count > 0
                || Matches(code, @"\b[A-Za-z_]\w*(?:\s*\.\s*\w+)*(?:\s*<[^;{}()]+>)?\s*[?]?\s+@?(?:DtdProcessing|XmlReader|XmlReaderSettings|XmlUrlResolver)\s*[,;)=]").Count > 0)
                throw new Unavailable("alias_or_lexical_context_unsupported");
            var lines = code.Split('\n');
            if (lines.Length > 4096) throw new Unavailable("source_line_budget_exceeded");
            var source = new XmlSource(path, code, lines, loaded.Checksum);
            sources.Add(path, source);
            return source;
        }

        private void ValidateValueBindings(Target target)
        {
            using (var scope = owner._conn.CreateCommand())
            {
                // Base members can shadow framework names. This subset does not
                // resolve inheritance: reject base lists on any same-named scope
                // declaration, including partial declarations in other files.
                scope.CommandText = """
                    SELECT substr(caller.container_name, 1, 513), COUNT(s.id),
                      MAX(CASE WHEN s.signature IS NULL OR instr(s.signature, ':') > 0 THEN 1 ELSE 0 END)
                    FROM symbols caller
                    LEFT JOIN symbols s ON s.kind IN ('class','struct','interface','record')
                      AND instr('.' || caller.container_name || '.', '.' || s.name || '.') > 0
                    WHERE caller.id = @id GROUP BY caller.id
                    """;
                SqliteCommandPolicy.Add(scope, "@id", target.Id);
                using var rows = scope.ExecuteTrackedReader();
                if (!rows.TrackedRead() || rows.IsDBNull(0)) throw new Unavailable("type_binding_metadata_missing");
                if (rows.GetString(0).Length > 512 || rows.GetInt64(1) is 0 or > 512 || rows.GetInt64(2) != 0
                    || Matches(rows.GetString(0), @"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$").Count != 1)
                    throw new Unavailable("inherited_or_complex_type_binding_unresolved");
            }
            using var cmd = owner._conn.CreateCommand();
            // Include enclosing and partial types, whose fields may be in a
            // different file. Local/parameter shadows are rejected in ReadSource.
            cmd.CommandText = """
                SELECT 1 FROM symbols s, symbols caller
                WHERE caller.id = @id AND s.name IN ('DtdProcessing','XmlReader','XmlReaderSettings','XmlUrlResolver')
                  AND s.kind IN ('field','property','event')
                  AND (COALESCE(caller.container_qualified_name, caller.container_name) = COALESCE(s.container_qualified_name, s.container_name)
                    OR substr(COALESCE(caller.container_qualified_name, caller.container_name), 1,
                        length(COALESCE(s.container_qualified_name, s.container_name)) + 1)
                       = COALESCE(s.container_qualified_name, s.container_name) || '.')
                LIMIT 1
                """;
            SqliteCommandPolicy.Add(cmd, "@id", target.Id);
            if (cmd.ExecuteScalar() != null) throw new Unavailable("framework_value_binding_unresolved");
        }

        private Target Container(string path, int line)
        {
            if (!containers.TryGetValue(path, out var ranges))
            {
                ranges = [];
                using var cmd = owner._conn.CreateCommand();
                cmd.CommandText = """
                    SELECT s.id, substr(s.name, 1, 128), s.start_line, s.end_line, s.kind
                    FROM symbols s JOIN files f ON f.id = s.file_id
                    WHERE f.path = @path AND s.kind IN ('function','method','test.method','property','accessor','lambda')
                    LIMIT 513
                    """;
                SqliteCommandPolicy.Add(cmd, "@path", path);
                using var rows = cmd.ExecuteTrackedReader();
                while (rows.TrackedRead())
                {
                    owner.ThrowIfCancellationRequested();
                    if (ranges.Count >= 512 || rows.IsDBNull(2) || rows.IsDBNull(3))
                        throw new Unavailable("symbol_range_budget_or_metadata_missing");
                    ranges.Add(new SameSymbolGuardRange(rows.GetInt64(0), rows.GetString(1), rows.GetInt32(2), rows.GetInt32(3), rows.GetString(4)));
                }
                containers.Add(path, ranges);
            }
            var range = ResolveSameSymbolGuardContainer(ranges, line)
                ?? throw new Unavailable("container_unresolved");
            if (range.Kind is not ("function" or "method" or "test.method")) throw new Unavailable("container_unsupported");
            return new Target(range.SymbolId, path, range.Name, range.StartLine, range.EndLine, "");
        }

        private static string Body(XmlSource source, Target target)
        {
            if (target.Start < 1 || target.End > source.Lines.Length || target.End - target.Start > 512)
                throw new Unavailable("container_budget_or_range_invalid");
            var body = string.Join('\n', source.Lines[(target.Start - 1)..target.End]);
            var open = body.IndexOf('{');
            var arrow = body.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0 && (open < 0 || arrow < open))
            {
                if (!body.TrimEnd().EndsWith(';')) throw new Unavailable("container_incomplete");
                return body;
            }
            var close = open < 0 ? -1 : FindMatchingDelimiter(body, open, '{', '}');
            if (close < 0 || body[(close + 1)..].Trim().Length != 0 || arrow >= 0)
                throw new Unavailable("container_incomplete_or_nested");
            return body;
        }

        private XmlSettingsAuditEvidence Observe(XmlSource source, Target target, string body, int line,
            List<XmlSettingsAuditSource> provenance)
        {
            var readers = Matches(body, @"\b(?:System\s*\.\s*Xml\s*\.\s*)?XmlReader\s*\.\s*Create\s*\(");
            string expression;
            if (readers.Count == 1)
            {
                var open = body.IndexOf('(', readers[0].Index);
                var close = FindMatchingDelimiter(body, open, '(', ')');
                if (close < 0) throw new Unavailable("reader_arguments_incomplete");
                var args = SplitTopLevelArguments(body[(open + 1)..close]);
                if (args.Count != 2) throw new Unavailable("reader_overload_unsupported");
                expression = args[1].Trim();
                var expressionStart = body.IndexOf(expression, open, StringComparison.Ordinal);
                var expressionEnd = expressionStart + expression.Length;
                var matchOffset = body.Split('\n').Take(line - target.Start).Sum(text => text.Length + 1);
                var boundStart = readers[0].Index;
                var boundEnd = close;
                if (Matches(expression, @"^[A-Za-z_]\w*$").Count == 1)
                {
                    if (body.Contains('$')) throw new Unavailable("interpolated_alias_context_unsupported");
                    var name = expression;
                    var declarations = Matches(body, @"\b(?:var|XmlReaderSettings|System\.Xml\.XmlReaderSettings)\s+" + Regex.Escape(name) + @"\s*=\s*");
                    if (declarations.Count != 1 || Matches(body, @"\b" + Regex.Escape(name) + @"\b").Count != 2
                        || declarations[0].Index >= readers[0].Index)
                        throw new Unavailable("settings_alias_mutation_or_reassignment");
                    var start = declarations[0].Index + declarations[0].Length;
                    var end = FindTopLevelStatementEnd(body, start);
                    expression = body[start..end].Trim().TrimEnd(';');
                    expressionStart = declarations[0].Index;
                    expressionEnd = end;
                    if (matchOffset < readers[0].Index)
                    {
                        boundStart = declarations[0].Index;
                        boundEnd = end;
                    }
                }
                // Only the settings expression (or its declaration) can explain
                // a matched XML setting; unrelated matches in the method stay unknown.
                var localLine = line - target.Start;
                var lineText = body.Split('\n').ElementAtOrDefault(localLine) ?? "";
                if (matchOffset > boundEnd || matchOffset + lineText.Length < boundStart)
                    throw new Unavailable("match_not_bound_to_settings");
                var unrelated = body[..expressionStart] + body[expressionEnd..];
                if (unrelated.Contains("XmlReaderSettings", StringComparison.Ordinal)
                    || unrelated.Contains("DtdProcessing", StringComparison.Ordinal)
                    || unrelated.Contains("XmlResolver", StringComparison.Ordinal))
                    throw new Unavailable("other_xml_settings_in_container");
                if (!lineText.Contains("XmlReaderSettings", StringComparison.Ordinal)
                    && !lineText.Contains("DtdProcessing", StringComparison.Ordinal)
                    && !lineText.Contains("XmlResolver", StringComparison.Ordinal))
                    throw new Unavailable("match_not_bound_to_settings");
            }
            else if (readers.Count == 0)
            {
                expression = ReturnExpression(body);
            }
            else throw new Unavailable("multiple_reader_operations");

            return Evaluate(source, target, body, expression, new Dictionary<string, string>(StringComparer.Ordinal),
                0, provenance);
        }

        private XmlSettingsAuditEvidence Evaluate(XmlSource source, Target target, string body, string expression,
            Dictionary<string, string> arguments, int depth, List<XmlSettingsAuditSource> provenance)
        {
            Step();
            if (depth > 3) throw new Unavailable("depth_budget_exceeded");
            expression = expression.Trim();
            var initializer = Matches(expression, @"^new\s+(?:System\s*\.\s*Xml\s*\.\s*)?XmlReaderSettings\s*(?:\(\s*\))?\s*\{");
            if (initializer.Count == 1)
            {
                var open = expression.IndexOf('{');
                var close = FindMatchingDelimiter(expression, open, '{', '}');
                if (close < 0 || expression[(close + 1)..].Trim().Length != 0) throw new Unavailable("initializer_incomplete");
                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in SplitTopLevelArguments(expression[(open + 1)..close]))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    var pair = entry.Split('=', 2);
                    if (pair.Length != 2 || !properties.TryAdd(pair[0].Trim(), pair[1].Trim()))
                        throw new Unavailable("initializer_ambiguous");
                }
                // Only value-only properties are admitted, avoiding initializer
                // callbacks or assignments that can mutate an earlier guard.
                foreach (var pair in properties)
                    if (pair.Key is not ("DtdProcessing" or "XmlResolver" or "MaxCharactersInDocument" or "MaxCharactersFromEntities")
                        && (pair.Key is not ("IgnoreComments" or "IgnoreWhitespace" or "IgnoreProcessingInstructions" or "CheckCharacters" or "CloseInput" or "Async")
                            || pair.Value is not ("true" or "false"))) throw new Unavailable("initializer_side_effects_unknown");
                var dtd = properties.GetValueOrDefault("DtdProcessing", "unknown");
                dtd = arguments.GetValueOrDefault(dtd, dtd);
                dtd = Regex.Replace(dtd, @"\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (dtd.StartsWith("System.Xml.", StringComparison.Ordinal)) dtd = dtd[11..];
                var resolver = properties.GetValueOrDefault("XmlResolver", "unknown");
                var document = Number(source, target, body, properties.GetValueOrDefault("MaxCharactersInDocument"), 0, provenance);
                var entities = Number(source, target, body, properties.GetValueOrDefault("MaxCharactersFromEntities"), 0, provenance);
                var state = (dtd is "DtdProcessing.Ignore" or "DtdProcessing.Prohibit") && resolver == "null" && document > 0 && entities > 0
                    ? "safe_under_observed_guards"
                    : dtd == "DtdProcessing.Parse" && Matches(resolver, @"^new\s+(?:System\.Xml\.)?XmlUrlResolver\s*\(\s*\)$").Count == 1
                        ? "confirmed_unsafe_configuration" : "needs_review";
                AddSource(provenance, source, target.Start);
                return new(state, state == "safe_under_observed_guards" ? "explicit_dtd_resolver_and_size_guards"
                    : state == "confirmed_unsafe_configuration" ? "dtd_parse_with_external_url_resolver" : "guard_combination_unproven",
                    dtd is "DtdProcessing.Ignore" or "DtdProcessing.Prohibit" or "DtdProcessing.Parse" ? dtd[14..] : "unknown",
                    resolver == "null" ? "disabled" : state == "confirmed_unsafe_configuration" ? "external_url_resolver" : "unknown",
                    document, entities, provenance);
            }

            var call = Matches(expression, @"^(?:(?:[A-Za-z_]\w*)\s*\.\s*)*(?<name>[A-Za-z_]\w*)\s*\(");
            if (call.Count != 1) throw new Unavailable("settings_expression_unresolved");
            var paren = expression.IndexOf('(');
            var endParen = FindMatchingDelimiter(expression, paren, '(', ')');
            if (endParen < 0 || expression[(endParen + 1)..].Trim().Length != 0) throw new Unavailable("factory_expression_ambiguous");
            var name = call[0].Groups["name"].Value;
            var factory = ResolveFactory(target, name);
            var receiver = expression[..call[0].Groups["name"].Index].Trim().TrimEnd('.').Trim();
            if (receiver.Length > 0)
            {
                using var binding = owner._conn.CreateCommand();
                binding.CommandText = "SELECT container_name FROM symbols WHERE id = @id";
                SqliteCommandPolicy.Add(binding, "@id", factory.Id);
                var container = binding.ExecuteScalar() as string;
                if (container == null || (receiver != container && receiver != container.Split('.').Last()))
                    throw new Unavailable("factory_receiver_unresolved");
            }
            var factorySource = ReadSource(factory.Path);
            ValidateValueBindings(factory);
            var factoryBody = Body(factorySource, factory);
            var parameterNames = ParseParameterNames(factory.Signature);
            var values = SplitTopLevelArguments(expression[(paren + 1)..endParen]);
            if (values.Count != parameterNames.Count) throw new Unavailable("factory_arguments_unresolved");
            var bound = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Count; i++)
            {
                var parameter = ResolveInvocationParameterName(values, i, parameterNames);
                if (parameter == null) throw new Unavailable("factory_arguments_unresolved");
                var value = RemoveNamedArgumentPrefix(values[i]).Trim();
                value = arguments.GetValueOrDefault(value, value);
                if (!bound.TryAdd(parameter, value) || Matches(factoryBody, @"\b" + Regex.Escape(parameter) + @"\s*(?:[+\-*/]?=|\+\+|--)").Count > 0)
                    throw new Unavailable("factory_parameter_mutated");
            }
            AddSource(provenance, factorySource, factory.Start);
            return Evaluate(factorySource, factory, factoryBody, ReturnExpression(factoryBody), bound, depth + 1, provenance);
        }

        private Target ResolveFactory(Target caller, string name)
        {
            Step();
            using var cmd = owner._conn.CreateCommand();
            // Require both a unique indexed definition and an actual graph edge
            // from this caller. A helper name never establishes a policy.
            cmd.CommandText = """
                SELECT s.id, f.path, s.name, s.start_line, s.end_line, s.signature,
                  EXISTS(SELECT 1 FROM symbol_references r WHERE r.source_symbol_id = @caller
                    AND r.target_symbol_id = s.id AND r.reference_kind = 'call')
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE s.name = @name COLLATE BINARY AND s.kind IN ('function','method') LIMIT 2
                """;
            SqliteCommandPolicy.Add(cmd, "@caller", caller.Id);
            SqliteCommandPolicy.Add(cmd, "@name", name);
            using var rows = cmd.ExecuteTrackedReader();
            if (!rows.TrackedRead()) throw new Unavailable("factory_target_missing");
            var target = new Target(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3), rows.GetInt32(4), rows.IsDBNull(5) ? "" : rows.GetString(5));
            if (rows.GetInt64(6) != 1 || rows.TrackedRead()) throw new Unavailable("factory_target_ambiguous");
            return target;
        }

        private static string ReturnExpression(string body)
        {
            if (body.Contains('$')) throw new Unavailable("interpolated_factory_unsupported");
            var returns = Matches(body, @"\breturn\s+");
            if (returns.Count == 1)
            {
                var start = returns[0].Index + returns[0].Length;
                var end = FindTopLevelStatementEnd(body, start);
                // No later mutation/finally block may change a returned object.
                if (body[end..].Trim() != "}") throw new Unavailable("factory_return_flow_unsupported");
                var prefix = body[..returns[0].Index];
                var open = prefix.IndexOf('{');
                var statements = open < 0 ? prefix : prefix[(open + 1)..].Trim();
                // The only supported pre-return statement is a pure enum-pattern
                // rejection that throws. All arbitrary calls/control flow fail closed.
                if (statements.Length > 0 && Matches(statements,
                    @"^if\s*\(\s*[A-Za-z_]\w*\s+is\s+not\s*\(\s*DtdProcessing\.Prohibit\s+or\s+DtdProcessing\.Ignore\s*\)\s*\)\s*throw\s+new\s+ArgumentOutOfRangeException\s*\([^;]*\);\s*$").Count != 1)
                    throw new Unavailable("factory_control_flow_unsupported");
                return body[start..end].Trim().TrimEnd(';');
            }
            var arrow = body.IndexOf("=>", StringComparison.Ordinal);
            if (returns.Count == 0 && arrow >= 0 && body.TrimEnd().EndsWith(';')) return body[(arrow + 2)..].Trim().TrimEnd(';');
            throw new Unavailable("factory_return_ambiguous");
        }

        private long? Number(XmlSource source, Target target, string body, string? expression, int depth, List<XmlSettingsAuditSource> provenance)
        {
            Step();
            if (expression == null || depth > 3) return null;
            var value = expression.Trim();
            if (Matches(value, @"^[0-9][0-9_]*[Ll]?$").Count == 1)
                return long.TryParse(value.Replace("_", "", StringComparison.Ordinal).TrimEnd('L', 'l'),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
            var parts = expression.Split('*');
            if (parts.Length is > 1 and <= 4)
            {
                long total = 1;
                foreach (var part in parts)
                {
                    var factor = Number(source, target, body, part, depth + 1, provenance);
                    if (factor == null || factor <= 0 || total > long.MaxValue / factor) return null;
                    total *= factor.Value;
                }
                return total;
            }
            if (Matches(value, @"^[A-Za-z_]\w*$").Count != 1) return null;
            // A declaration plus its use is already two identifier occurrences,
            // regardless of generic/tuple/qualified parameter or local type syntax.
            // Repeated field references conservatively stay unknown as well.
            if (Matches(body, @"\b" + Regex.Escape(value) + @"\b").Count > 1) return null;
            int declarationLine;
            using (var binding = owner._conn.CreateCommand())
            {
                binding.CommandText = """
                    SELECT s.start_line, s.end_line FROM symbols s JOIN files f ON s.file_id = f.id
                    WHERE f.path = @path AND s.name = @name COLLATE BINARY AND s.kind = 'field'
                      AND COALESCE(s.container_qualified_name, s.container_name) =
                        (SELECT COALESCE(container_qualified_name, container_name) FROM symbols WHERE id = @target) COLLATE BINARY
                    LIMIT 2
                    """;
                SqliteCommandPolicy.Add(binding, "@path", source.Path);
                SqliteCommandPolicy.Add(binding, "@name", value);
                SqliteCommandPolicy.Add(binding, "@target", target.Id);
                using var rows = binding.ExecuteTrackedReader();
                if (!rows.TrackedRead() || rows.IsDBNull(0) || rows.IsDBNull(1)) return null;
                declarationLine = rows.GetInt32(0);
                if (declarationLine != rows.GetInt32(1) || declarationLine < 1 || declarationLine > source.Lines.Length
                    || rows.TrackedRead()) return null;
            }
            // Require the exact field's whole source line, not another type's
            // same-named constant elsewhere in this file or on the same line.
            var constants = Matches(source.Lines[declarationLine - 1],
                @"^\s*(?:(?:public|private|internal|protected|new)\s+)*const\s+(?:int|long)\s+"
                + Regex.Escape(value) + @"\s*=\s*(?<value>[^;]+);\s*$");
            if (constants.Count != 1) return null;
            AddSource(provenance, source, declarationLine);
            return Number(source, target, body, constants[0].Groups["value"].Value, depth + 1, provenance);
        }

        private static MatchCollection Matches(string text, string pattern)
            => Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        private static void AddSource(List<XmlSettingsAuditSource> provenance, XmlSource source, int line)
        {
            if (source.Path.Length > 512) throw new Unavailable("source_path_budget_exceeded");
            var safePath = new string(source.Path.Select(ch => char.IsControl(ch) ? '?' : ch).ToArray());
            var item = new XmlSettingsAuditSource(safePath, line, source.Checksum);
            if (provenance.Contains(item)) return;
            if (provenance.Count >= 16) throw new Unavailable("provenance_budget_exceeded");
            provenance.Add(item);
        }
    }
}
