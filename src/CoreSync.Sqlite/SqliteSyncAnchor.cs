﻿using System;
using System.Collections.Generic;
using System.Text;

namespace CoreSync.Sqlite
{
    public class SqliteSyncAnchor : SyncAnchor
    {
        public long Version { get; }

        public SqliteSyncAnchor(long version)
        {
            Version = version;
        }
    }
}
