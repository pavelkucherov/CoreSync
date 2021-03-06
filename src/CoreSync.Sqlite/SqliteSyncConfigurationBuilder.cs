﻿using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CoreSync.Sqlite
{
    public class SqliteSyncConfigurationBuilder
    {
        private readonly string _connectionString;
        private List<SqliteSyncTable> _tables = new List<SqliteSyncTable>();

        public SqliteSyncConfigurationBuilder([NotNull] string connectionString)
        {
            Validate.NotNullOrEmptyOrWhiteSpace(connectionString, nameof(connectionString));
            _connectionString = connectionString;
        }

        public SqliteSyncConfigurationBuilder Table([NotNull] string name, Type recordType = null, bool bidirectional = true, string schema = "main")
        {
            Validate.NotNullOrEmptyOrWhiteSpace(name, nameof(name));

            name = name.Trim();
            if (_tables.Any(_ => String.CompareOrdinal(_.Name, name) == 0))
                throw new InvalidOperationException($"Table with name '{name}' already added");

            _tables.Add(new SqliteSyncTable(name, recordType: recordType, bidirectional: bidirectional, schema: schema));
            return this;
        }

        public SqliteSyncConfigurationBuilder Table<T>([NotNull] string name, bool bidirectional = true, string schema = "main")
        {
            return Table(name, typeof(T), bidirectional, schema);
        }

        public SqliteSyncConfiguration Configuration
        {
            get { return new SqliteSyncConfiguration(_connectionString, _tables.ToArray()); }
        }
    }
}
