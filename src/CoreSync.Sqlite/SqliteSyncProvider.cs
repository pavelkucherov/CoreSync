﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Data.Sqlite;

namespace CoreSync.Sqlite
{
    public class SqliteSyncProvider : ISyncProvider
    {
        public SqliteSyncConfiguration Configuration { get; }
        public SqliteSyncProvider(SqliteSyncConfiguration configuration)
        {
            Configuration = configuration;
        }

        bool _initialized = false;
        private async Task InitializeAsync()
        {
            if (_initialized)
                return;

            using (var connection = new SqliteConnection(Configuration.ConnectionString))
            {
                await connection.OpenAsync();

                //1. discover tables
                using (var cmd = connection.CreateCommand())
                {
                    foreach (SqliteSyncTable table in Configuration.Tables)
                    {
                        cmd.CommandText = $"PRAGMA table_info('{table.Name}')";
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            /*
                            cid         name        type        notnull     dflt_value  pk        
                            ----------  ----------  ----------  ----------  ----------  ----------
                            */
                            while (await reader.ReadAsync())
                            {
                                var colName = reader.GetString(1);
                                var colType = reader.GetString(2);
                                var pk = reader.GetBoolean(5);

                                table.Columns.Add(new SqliteColumn(colName, colType, pk));
                            }
                        }
                    }
                }

                //2. create ct table
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"CREATE TABLE IF NOT EXISTS __CORE_SYNC_CT (ID INTEGER PRIMARY KEY, TBL TEXT NOT NULL, OP CHAR NOT NULL, PK TEXT NOT NULL)";
                    await cmd.ExecuteNonQueryAsync();
                }

                //3. create triggers
                using (var cmd = connection.CreateCommand())
                {
                    foreach (var table in Configuration.Tables.Cast<SqliteSyncTable>().Where(_ => _.Columns.Any()))
                    {
                        var primaryKeyColumns = table.Columns.Where(_ => _.IsPrimaryKey);

                        var commandTextBase = new Func<string, string>((op) => $@"CREATE TRIGGER IF NOT EXISTS [__{table.Name}_ct-{op}__] 
AFTER {op} ON [{table.Schema}].[{table.Name}]
FOR EACH ROW
BEGIN
    INSERT INTO [__CORE_SYNC_CT] (TBL, OP, PK) VALUES ('{table.Schema}.{table.Name}', '{op[0]}', printf('{string.Join("", primaryKeyColumns.Select(_ => TypeToPrintFormat(_.Type)))}', {string.Join(", ", primaryKeyColumns.Select(_ => (op == "DELETE" ? "OLD" : "NEW") + ".[" + _.Name + "]"))}));
END");
                        cmd.CommandText = commandTextBase("INSERT");
                        await cmd.ExecuteNonQueryAsync();

                        cmd.CommandText = commandTextBase("UPDATE");
                        await cmd.ExecuteNonQueryAsync();

                        cmd.CommandText = commandTextBase("DELETE");
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }

            //4. Insert/Update/Delete query templates
            foreach (SqliteSyncTable table in Configuration.Tables)
            {
                var primaryKeyColumns = table.Columns.Where(_ => _.IsPrimaryKey).ToList();
                var tableColumns = table.Columns.Where(_ => !_.IsPrimaryKey).ToList();

                table.InsertQuery = $@"INSERT OR IGNORE INTO [{table.Schema}].[{table.Name}] ({string.Join(", ", table.Columns.Select(_ => "[" + _.Name + "]"))}) VALUES ({string.Join(", ", table.Columns.Select(_ => "@" + _.Name.Replace(' ', '_')))});";

                table.UpdateQuery = $@"UPDATE [{table.Schema}].[{table.Name}] 
SET {string.Join(", ", tableColumns.Select(_ => "[" + _.Name + "] = @" + _.Name.Replace(' ', '_')))} 
WHERE ({string.Join(", ", primaryKeyColumns.Select(_ => $"[{table.Schema}].[{table.Name}].[{_.Name}] = @{_.Name.Replace(' ', '_')}"))}) 
AND (@sync_force_write = 1 OR EXISTS (SELECT * FROM [{table.Schema}].[{table.Name}] AS T INNER JOIN __CORE_SYNC_CT AS CT ON (printf('{string.Join("", primaryKeyColumns.Select(_ => TypeToPrintFormat(_.Type)))}', {string.Join(", ", primaryKeyColumns.Select(_ => "T.[" + _.Name + "]"))}) = CT.[PK]) 
AND CT.ID > @last_sync_version))";

                table.DeleteQuery = $@"DELETE FROM [{table.Schema}].[{table.Name}] 
WHERE ({string.Join(", ", primaryKeyColumns.Select(_ => $"[{table.Schema}].[{table.Name}].[{_.Name}] = @{_.Name.Replace(' ', '_')}"))}) 
AND (@sync_force_write = 1 OR EXISTS (SELECT * FROM [{table.Schema}].[{table.Name}] AS T INNER JOIN __CORE_SYNC_CT AS CT ON (printf('{string.Join("", primaryKeyColumns.Select(_ => TypeToPrintFormat(_.Type)))}', {string.Join(", ", primaryKeyColumns.Select(_ => "T.[" + _.Name + "]"))}) = CT.[PK]) 
AND CT.ID > @last_sync_version))";
            }

            _initialized = true;
        }

        private static string TypeToPrintFormat(string type)
        {
            if (type == "INTEGER")
                return "%d";
            if (type == "TEXT")
                return "%s";

            return "%s";
        }

        public async Task<SyncAnchor> ApplyChangesAsync([NotNull] SyncChangeSet changeSet, [CanBeNull] Func<SyncItem, ConflictResolution> onConflictFunc = null)
        {
            Validate.NotNull(changeSet, nameof(changeSet));

            if (!(changeSet.Anchor is SqliteSyncAnchor sqliteAnchor))
                throw new ArgumentException("Incompatible anchor", nameof(changeSet));

            await InitializeAsync();

            using (var c = new SqliteConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                using (var cmd = new SqliteCommand())
                {
                    using (var tr = c.BeginTransaction())
                    {
                        cmd.Connection = c;
                        cmd.Transaction = tr;

                        cmd.CommandText = "SELECT MAX(ID) FROM  __CORE_SYNC_CT";
                        var version = await cmd.ExecuteLongScalarAsync();

                        cmd.CommandText = "SELECT MIN(ID) FROM  __CORE_SYNC_CT";
                        var minVersion = await cmd.ExecuteLongScalarAsync();

                        if (sqliteAnchor.Version < minVersion - 1)
                            throw new InvalidOperationException($"Unable to apply changes, version of data requested ({sqliteAnchor.Version}) is too old (min valid version {minVersion})");

                        foreach (var item in changeSet.Items)
                        {
                            var table = (SqliteSyncTable) Configuration.Tables.First(_ => _.Name == item.Table.Name);

                            bool syncForceWrite = false;
                            var itemChangeType = item.ChangeType;

                            retryWrite:
                            cmd.Parameters.Clear();

                            switch (itemChangeType)
                            {
                                case ChangeType.Insert:
                                    cmd.CommandText = table.InsertQuery;
                                    break;
                                case ChangeType.Update:
                                    cmd.CommandText = table.UpdateQuery;
                                    break;
                                case ChangeType.Delete:
                                    cmd.CommandText = table.DeleteQuery;
                                    break;
                            }

                            cmd.Parameters.Add(new SqliteParameter("@last_sync_version", sqliteAnchor.Version));
                            cmd.Parameters.Add(new SqliteParameter("@sync_force_write", syncForceWrite));

                            foreach (var valueItem in item.Values)
                                cmd.Parameters.Add(new SqliteParameter("@" + valueItem.Key.Replace(" ", "_"), valueItem.Value ?? DBNull.Value));

                            var affectedRows = cmd.ExecuteNonQuery();

                            if (affectedRows == 0)
                            {
                                if (itemChangeType == ChangeType.Insert)
                                {
                                    //If we can't apply an insert means that we already
                                    //applied the insert or another record with same values (see primary key)
                                    //is already present in table.
                                    //In any case we can't proceed
                                    throw new InvalidSyncOperationException(new SqliteSyncAnchor(sqliteAnchor.Version + 1));
                                }
                                else if (itemChangeType == ChangeType.Update ||
                                    itemChangeType == ChangeType.Delete)
                                {
                                    if (syncForceWrite)
                                    {
                                        if (itemChangeType == ChangeType.Delete)
                                        {
                                            //item is already deleted in data store
                                            //so this means that we're going to delete a already deleted record
                                            //i.e. nothing to do
                                        }
                                        else
                                        {
                                            //if user wants to update forcely a delete record means
                                            //he wants to actually insert it again in store
                                            itemChangeType = ChangeType.Insert;
                                            goto retryWrite;
                                        }
                                    }
                                    //conflict detected
                                    var res = onConflictFunc?.Invoke(item);
                                    if (res.HasValue && res.Value == ConflictResolution.ForceWrite)
                                    {
                                        syncForceWrite = true;
                                        goto retryWrite;
                                    }
                                }
                            }

                        }

                        cmd.CommandText = "SELECT MAX(ID) FROM  __CORE_SYNC_CT";
                        version = await cmd.ExecuteLongScalarAsync();

                        tr.Commit();

                        return new SqliteSyncAnchor(version);


                    }
                }
            }

        }

        public async Task<SyncChangeSet> GetIncreamentalChangesAsync([NotNull] SyncAnchor anchor)
        {
            Validate.NotNull(anchor, nameof(anchor));

            var sqliteAnchor = anchor as SqliteSyncAnchor;
            if (sqliteAnchor == null)
                throw new ArgumentException("Incompatible anchor", nameof(anchor));

            await InitializeAsync();

            using (var c = new SqliteConnection(Configuration.ConnectionString))
            {
                await c.OpenAsync();

                using (var cmd = new SqliteCommand())
                {
                    var items = new List<SqliteSyncItem>();

                    using (var tr = c.BeginTransaction())
                    {
                        cmd.Connection = c;
                        cmd.Transaction = tr;

                        cmd.CommandText = "SELECT MAX(ID) FROM  __CORE_SYNC_CT";
                        var version = await cmd.ExecuteLongScalarAsync();

                        cmd.CommandText = "SELECT MIN(ID) FROM  __CORE_SYNC_CT";
                        var minVersion = await cmd.ExecuteLongScalarAsync();
                        
                        if (sqliteAnchor.Version < minVersion - 1)
                            throw new InvalidOperationException($"Unable to get changes, version of data requested ({sqliteAnchor.Version}) is too old (min valid version {minVersion})");

                        foreach (var table in Configuration.Tables.Cast<SqliteSyncTable>().Where(_=>_.Columns.Any()))
                        {
                            var primaryKeyColumns = table.Columns.Where(_ => _.IsPrimaryKey);

                            cmd.CommandText = $@"SELECT DISTINCT {string.Join(",", table.Columns.Select(_ => "T.[" + _.Name + "]"))}, MIN(CT.OP) AS OP FROM [{table.Schema}].[{table.Name}] AS T INNER JOIN __CORE_SYNC_CT AS CT ON printf('{string.Join("", primaryKeyColumns.Select(_ => TypeToPrintFormat(_.Type)))}', {string.Join(", ", primaryKeyColumns.Select(_ => "T.[" + _.Name + "]"))}) = CT.PK WHERE CT.Id > {sqliteAnchor.Version}";

                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    var values = Enumerable.Range(0, r.FieldCount).ToDictionary(_ => r.GetName(_), _ => GetValueFromRecord(table, r.GetName(_), _, r));
                                    if (values["OP"] != null)
                                        items.Add(new SqliteSyncItem(table, DetectChangeType(values), values));
                                }
                            }
                        }

                        tr.Commit();

                        return new SyncChangeSet(new SqliteSyncAnchor(version), items);

                    }
                }
            }
        }

        private static object GetValueFromRecord(SqliteSyncTable table, string columnName, int columnOrdinal, SqliteDataReader r)
        {
            if (r.IsDBNull(columnOrdinal))
                return null;

            if (table.RecordType == null)
                return r.GetValue(columnOrdinal);

            var property = table.RecordType.GetProperty(columnName);
            if (property != null)
                return GetValueFromRecord(r, columnOrdinal, property.PropertyType);
            
            //fallback to getvalue
            return r.GetValue(columnOrdinal);
        }

        private static object GetValueFromRecord(SqliteDataReader r, int columnOrdinal, Type propertyType)
        {
            if (propertyType == typeof(string))
                return r.GetString(columnOrdinal);
            if (propertyType == typeof(DateTime))
                return r.GetDateTime(columnOrdinal);
            if (propertyType == typeof(int))
                return r.GetInt32(columnOrdinal);
            if (propertyType == typeof(bool))
                return r.GetBoolean(columnOrdinal);
            if (propertyType == typeof(byte))
                return r.GetByte(columnOrdinal);
            if (propertyType == typeof(char))
                return r.GetChar(columnOrdinal);
            if (propertyType == typeof(short))
                return r.GetInt16(columnOrdinal);
            if (propertyType == typeof(long))
                return r.GetInt64(columnOrdinal);
            if (propertyType == typeof(decimal))
                return r.GetDecimal(columnOrdinal);
            if (propertyType == typeof(double))
                return r.GetDouble(columnOrdinal);
            if (propertyType == typeof(float))
                return r.GetFloat(columnOrdinal);


            //fallback to getvalue
            return r.GetValue(columnOrdinal);

        }

        public async Task<SyncChangeSet> GetInitialSetAsync()
        {
            await InitializeAsync();

            using (var connection = new SqliteConnection(Configuration.ConnectionString))
            {
                await connection.OpenAsync();
                using (var cmd = connection.CreateCommand())
                {
                    var items = new List<SqliteSyncItem>();

                    using (var tr = connection.BeginTransaction())
                    {
                        cmd.Transaction = tr;

                        cmd.CommandText = "SELECT MAX(ID) FROM  __CORE_SYNC_CT";
                        var version = await cmd.ExecuteLongScalarAsync();

                        foreach (SqliteSyncTable table in Configuration.Tables)
                        {
                            cmd.CommandText = $@"SELECT {string.Join(", ", table.Columns.Select(_ => "[" + _.Name + "]"))} FROM [{table.Name}]";

                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    var values = Enumerable.Range(0, r.FieldCount).ToDictionary(_ => r.GetName(_), _ => GetValueFromRecord(table, r.GetName(_), _, r));
                                    items.Add(new SqliteSyncItem(table, DetectChangeType(values), values));
                                }
                            }
                        }
                        tr.Commit();

                        return new SyncChangeSet(new SqliteSyncAnchor(version), items);
                    }
                }
            }
        }

        private static ChangeType DetectChangeType(Dictionary<string, object> values)
        {
            if (values.ContainsKey("OP"))
            {
                switch ((string)values["OP"])
                {
                    case "I":
                        return ChangeType.Insert;
                    case "U":
                        return ChangeType.Update;
                    case "D":
                        return ChangeType.Delete;
                }

                throw new NotSupportedException();
            }


            return ChangeType.Insert;
        }


    }
}
