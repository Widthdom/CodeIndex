using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Upsert a metadata key/value into `codeindex_meta`.
    /// codeindex_meta への key/value の upsert。
    /// </summary>
    public void SetMeta(string key, string? value)
    {
        if (!HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            ExecuteReusableControlStatement("SAVEPOINT set_meta_atomic");
            try
            {
                SetMetaCore(key, value);
                ExecuteReusableControlStatement("RELEASE SAVEPOINT set_meta_atomic");
            }
            catch
            {
                try { ExecuteReusableControlStatement("ROLLBACK TO SAVEPOINT set_meta_atomic"); }
                catch (SqliteException) { /* best effort */ }
                try { ExecuteReusableControlStatement("RELEASE SAVEPOINT set_meta_atomic"); }
                catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        SetMetaCore(key, value);
    }

    private void SetMetaCore(string key, string? value)
    {
        var cmd = RentCommand(
            @"INSERT INTO codeindex_meta (key, value) VALUES (@key, @value)
                            ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            static c =>
            {
                c.Parameters.Add("@key", SqliteType.Text);
                c.Parameters.Add("@value", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@value"].Value = (object?)value ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public void SetMetaValues(params (string Key, string? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0 || !HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            ExecuteReusableControlStatement("SAVEPOINT set_meta_values_atomic");
            try
            {
                SetMetaValuesCore(values);
                ExecuteReusableControlStatement("RELEASE SAVEPOINT set_meta_values_atomic");
            }
            catch
            {
                try { ExecuteReusableControlStatement("ROLLBACK TO SAVEPOINT set_meta_values_atomic"); }
                catch (SqliteException) { /* best effort */ }
                try { ExecuteReusableControlStatement("RELEASE SAVEPOINT set_meta_values_atomic"); }
                catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        SetMetaValuesCore(values);
    }

    private void SetMetaValuesCore(IReadOnlyList<(string Key, string? Value)> values)
    {
        var keyParameterNames = new string[values.Count];
        var valueParameterNames = new string[values.Count];
        var rows = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            keyParameterNames[i] = "@meta_key" + suffix;
            valueParameterNames[i] = "@meta_value" + suffix;
            rows[i] = "(" + keyParameterNames[i] + ", " + valueParameterNames[i] + ")";
        }

        var sql = "INSERT INTO codeindex_meta (key, value) VALUES " + string.Join(", ", rows)
            + " ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        var cmd = RentCommand(
            sql,
            c =>
            {
                for (var i = 0; i < keyParameterNames.Length; i++)
                {
                    c.Parameters.Add(keyParameterNames[i], SqliteType.Text);
                    c.Parameters.Add(valueParameterNames[i], SqliteType.Text);
                }
            });
        try
        {
            for (var i = 0; i < values.Count; i++)
            {
                cmd.Parameters[keyParameterNames[i]].Value = values[i].Key;
                cmd.Parameters[valueParameterNames[i]].Value = (object?)values[i].Value ?? DBNull.Value;
            }
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private void ClearMetaKeys(params string[] keys)
    {
        if (keys.Length == 0 || !HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            ExecuteReusableControlStatement("SAVEPOINT clear_meta_keys_atomic");
            try
            {
                ClearMetaKeysCore(keys);
                ExecuteReusableControlStatement("RELEASE SAVEPOINT clear_meta_keys_atomic");
            }
            catch
            {
                try { ExecuteReusableControlStatement("ROLLBACK TO SAVEPOINT clear_meta_keys_atomic"); }
                catch (SqliteException) { /* best effort */ }
                try { ExecuteReusableControlStatement("RELEASE SAVEPOINT clear_meta_keys_atomic"); }
                catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        ClearMetaKeysCore(keys);
    }

    private void ClearMetaKeysCore(IReadOnlyList<string> keys)
    {
        var values = new (string Key, string? Value)[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            values[i] = (keys[i], null);

        SetMetaValuesCore(values);
    }

    private string? GetMetaString(string key)
    {
        var cmd = RentCommand(
            "SELECT value FROM codeindex_meta WHERE key = @key",
            static c => c.Parameters.Add("@key", SqliteType.Text));
        try
        {
            cmd.Parameters["@key"].Value = key;
            return cmd.ExecuteScalar() as string;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasMetaTable() => TableExists("codeindex_meta");

    private bool TableExists(string name)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            static c => c.Parameters.Add("@name", SqliteType.Text));
        try
        {
            cmd.Parameters["@name"].Value = name;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }
}
