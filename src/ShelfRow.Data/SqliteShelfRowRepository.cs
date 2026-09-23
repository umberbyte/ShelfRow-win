using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShelfRow.Core.Interfaces;
using ShelfRow.Core.Models;

namespace ShelfRow.Data;

public class SqliteShelfRowRepository : IShelfRowRepository, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

    public SqliteShelfRowRepository(string? databasePath = null)
    {
        string path = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShelfRow",
            "shelfrow.db"
        );
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private async Task<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken);
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken);
        }
        return _connection;
    }

    private async Task<IDisposable> EnterDatabaseAsync(CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        return new DatabaseLease(_databaseGate);
    }

    private sealed class DatabaseLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS Volumes (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                LastKnownPath TEXT,
                WindowsMountPath TEXT,
                CloudKitRecordName TEXT,
                CloudKitChangeTag TEXT,
                PendingUpload INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Shelves (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Icon INTEGER NOT NULL,
                Type INTEGER NOT NULL,
                SortOrder INTEGER NOT NULL,
                SortAscending INTEGER NOT NULL,
                SortKey TEXT NOT NULL,
                SmartConditionsJson TEXT,
                CloudKitRecordName TEXT,
                CloudKitChangeTag TEXT,
                PendingUpload INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Items (
                Id TEXT PRIMARY KEY,
                LegacyId INTEGER,
                VolumeId TEXT,
                RelativePath TEXT NOT NULL,
                Title TEXT NOT NULL,
                Author TEXT,
                Rating INTEGER NOT NULL,
                IsUnread INTEGER NOT NULL,
                Genre TEXT,
                Relation TEXT,
                KeywordA TEXT,
                KeywordB TEXT,
                Memo TEXT,
                CoverImageName TEXT,
                CoverImagePath TEXT,
                AddedDate TEXT NOT NULL,
                LastReadDate TEXT,
                Pages INTEGER NOT NULL,
                BookType INTEGER NOT NULL,
                FileType INTEGER NOT NULL,
                CoverVersion INTEGER NOT NULL,
                CoverBytes INTEGER NOT NULL,
                VolumeRecordName TEXT,
                CloudKitRecordName TEXT,
                CloudKitChangeTag TEXT,
                PendingUpload INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (VolumeId) REFERENCES Volumes(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS ItemShelves (
                ItemId TEXT NOT NULL,
                ShelfId TEXT NOT NULL,
                CloudKitRecordName TEXT,
                PendingOperation INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (ItemId, ShelfId),
                FOREIGN KEY (ItemId) REFERENCES Items(Id) ON DELETE CASCADE,
                FOREIGN KEY (ShelfId) REFERENCES Shelves(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SyncMetadata (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );

            CREATE TABLE IF NOT EXISTS PendingCloudKitDeletions (
                RecordName TEXT PRIMARY KEY,
                RecordType TEXT NOT NULL
            );

            -- Device-local cover bookkeeping. Deliberately has no PendingUpload
            -- integration: these rows must never reach CloudKit.
            CREATE TABLE IF NOT EXISTS LocalCoverStates (
                ItemId TEXT PRIMARY KEY,
                Version INTEGER NOT NULL DEFAULT 0,
                Bytes INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PendingUpload INTEGER NOT NULL DEFAULT 0,
                Attempts INTEGER NOT NULL DEFAULT 0,
                AttemptedVersion INTEGER NOT NULL DEFAULT 0,
                LastErrorCode INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_Items_Title ON Items(Title);
            CREATE INDEX IF NOT EXISTS IX_Items_Author ON Items(Author);
            CREATE INDEX IF NOT EXISTS IX_Items_Rating ON Items(Rating);
            CREATE INDEX IF NOT EXISTS IX_Items_IsUnread ON Items(IsUnread);
            CREATE INDEX IF NOT EXISTS IX_Items_LegacyId ON Items(LegacyId);
            CREATE INDEX IF NOT EXISTS IX_ItemShelves_ShelfId ON ItemShelves(ShelfId);
            CREATE INDEX IF NOT EXISTS IX_Items_CKRecordName ON Items(CloudKitRecordName);
            CREATE INDEX IF NOT EXISTS IX_Items_VolumeRecordName ON Items(VolumeRecordName);
            CREATE INDEX IF NOT EXISTS IX_Shelves_CKRecordName ON Shelves(CloudKitRecordName);
            CREATE INDEX IF NOT EXISTS IX_Volumes_CKRecordName ON Volumes(CloudKitRecordName);
            CREATE INDEX IF NOT EXISTS IX_ItemShelves_CKRecordName ON ItemShelves(CloudKitRecordName);
        ";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        await AddMissingColumnsAsync(conn, cancellationToken);

        // Rows created by older Windows builds had no upload marker. A link with
        // no CloudKit identity can only be local, so queue it once endpoints exist.
        using var queueLegacyLinks = conn.CreateCommand();
        queueLegacyLinks.CommandText = @"
            UPDATE ItemShelves SET PendingOperation = 1
            WHERE CloudKitRecordName IS NULL AND PendingOperation = 0";
        await queueLegacyLinks.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS leaves an older database without the CloudKit columns,
    /// and that database may hold a Stackroom import worth keeping.
    /// </summary>
    private static async Task AddMissingColumnsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        (string Table, string Column, string Type)[] required =
        [
            ("Volumes", "CloudKitRecordName", "TEXT"),
            ("Volumes", "CloudKitChangeTag", "TEXT"),
            ("Volumes", "PendingUpload", "INTEGER NOT NULL DEFAULT 0"),
            ("Shelves", "CloudKitRecordName", "TEXT"),
            ("Shelves", "CloudKitChangeTag", "TEXT"),
            ("Shelves", "PendingUpload", "INTEGER NOT NULL DEFAULT 0"),
            ("Items", "VolumeRecordName", "TEXT"),
            ("Items", "CloudKitRecordName", "TEXT"),
            ("Items", "CloudKitChangeTag", "TEXT"),
            ("Items", "PendingUpload", "INTEGER NOT NULL DEFAULT 0"),
            ("ItemShelves", "CloudKitRecordName", "TEXT"),
            ("ItemShelves", "PendingOperation", "INTEGER NOT NULL DEFAULT 0")
        ];

        foreach (var (table, column, type) in required)
        {
            using var check = conn.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @Column";
            check.Parameters.AddWithValue("@Column", column);

            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) > 0)
                continue;

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<Item?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Items WHERE Id = @Id LIMIT 1";
        cmd.Parameters.AddWithValue("@Id", id.ToString("D"));

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var item = MapItem(reader);
            item.ShelfIds = await GetShelfIdsForItemAsync(id, cancellationToken);
            return item;
        }
        return null;
    }

    public async Task<Item?> GetItemByLegacyIdAsync(int legacyId, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Items WHERE LegacyId = @LegacyId LIMIT 1";
        cmd.Parameters.AddWithValue("@LegacyId", legacyId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var item = MapItem(reader);
            item.ShelfIds = await GetShelfIdsForItemAsync(item.Id, cancellationToken);
            return item;
        }
        return null;
    }

    public async Task<IReadOnlyList<Item>> GetItemsForImportMergeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        var items = new List<Item>();
        var itemsById = new Dictionary<Guid, Item>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Items";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = MapItem(reader);
                items.Add(item);
                itemsById[item.Id] = item;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ItemId, ShelfId FROM ItemShelves WHERE PendingOperation <> 2";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Guid.TryParse(reader.GetString(0), out var itemId)
                    && Guid.TryParse(reader.GetString(1), out var shelfId)
                    && itemsById.TryGetValue(itemId, out var item))
                {
                    item.ShelfIds.Add(shelfId);
                }
            }
        }

        return items;
    }

    public async Task<IReadOnlyList<Item>> GetItemsAsync(int skip = 0, int take = 100, Guid? shelfId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();

        string query = "SELECT i.* FROM Items i";

        var smartConditions = shelfId.HasValue
            ? await GetSmartConditionsAsync(shelfId.Value, cancellationToken)
            : null;

        if (smartConditions != null)
        {
            // A smart shelf has no stored membership; its contents are whatever matches.
            query += " WHERE 1=1" + BuildSmartConditionSql(smartConditions, cmd, DateTime.UtcNow);
        }
        else if (shelfId.HasValue)
        {
            query += " INNER JOIN ItemShelves s ON i.Id = s.ItemId WHERE s.ShelfId = @ShelfId AND s.PendingOperation <> 2";
            cmd.Parameters.AddWithValue("@ShelfId", shelfId.Value.ToString("D"));
        }
        else
        {
            query += " WHERE 1=1";
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query += " AND (i.Title LIKE @Search OR i.Author LIKE @Search OR i.KeywordA LIKE @Search OR i.KeywordB LIKE @Search)";
            cmd.Parameters.AddWithValue("@Search", $"%{search}%");
        }

        query += " ORDER BY i.AddedDate DESC LIMIT @Take OFFSET @Skip";
        cmd.Parameters.AddWithValue("@Take", take);
        cmd.Parameters.AddWithValue("@Skip", skip);
        cmd.CommandText = query;

        var results = new List<Item>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapItem(reader));
        }
        return results;
    }

    public async Task<int> GetItemCountAsync(Guid? shelfId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();

        string query = "SELECT COUNT(*) FROM Items i";

        var smartConditions = shelfId.HasValue
            ? await GetSmartConditionsAsync(shelfId.Value, cancellationToken)
            : null;

        if (smartConditions != null)
        {
            query += " WHERE 1=1" + BuildSmartConditionSql(smartConditions, cmd, DateTime.UtcNow);
        }
        else if (shelfId.HasValue)
        {
            query += " INNER JOIN ItemShelves s ON i.Id = s.ItemId WHERE s.ShelfId = @ShelfId AND s.PendingOperation <> 2";
            cmd.Parameters.AddWithValue("@ShelfId", shelfId.Value.ToString("D"));
        }
        else
        {
            query += " WHERE 1=1";
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query += " AND (i.Title LIKE @Search OR i.Author LIKE @Search OR i.KeywordA LIKE @Search OR i.KeywordB LIKE @Search)";
            cmd.Parameters.AddWithValue("@Search", $"%{search}%");
        }

        cmd.CommandText = query;
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar);
    }

    public async Task UpsertItemAsync(Item item, bool markPendingUpload = true, CancellationToken cancellationToken = default)
    {
        await UpsertItemsBatchAsync(new[] { item }, markPendingUpload, cancellationToken);
    }

    public async Task UpsertItemsBatchAsync(IEnumerable<Item> items, bool markPendingUpload = true, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        using var cmdItem = conn.CreateCommand();
        cmdItem.Transaction = tx;
        cmdItem.CommandText = @"
            INSERT INTO Items (
                Id, LegacyId, VolumeId, RelativePath, Title, Author, Rating, IsUnread,
                Genre, Relation, KeywordA, KeywordB, Memo, CoverImageName, CoverImagePath,
                AddedDate, LastReadDate, Pages, BookType, FileType, CoverVersion, CoverBytes,
                VolumeRecordName, CloudKitRecordName, CloudKitChangeTag, PendingUpload
            ) VALUES (
                @Id, @LegacyId, @VolumeId, @RelativePath, @Title, @Author, @Rating, @IsUnread,
                @Genre, @Relation, @KeywordA, @KeywordB, @Memo, @CoverImageName, @CoverImagePath,
                @AddedDate, @LastReadDate, @Pages, @BookType, @FileType, @CoverVersion, @CoverBytes,
                @VolumeRecordName, @CloudKitRecordName, @CloudKitChangeTag, @PendingUpload
            )
            ON CONFLICT(Id) DO UPDATE SET
                LegacyId = excluded.LegacyId,
                VolumeId = excluded.VolumeId,
                RelativePath = excluded.RelativePath,
                Title = excluded.Title,
                Author = excluded.Author,
                Rating = excluded.Rating,
                IsUnread = excluded.IsUnread,
                Genre = excluded.Genre,
                Relation = excluded.Relation,
                KeywordA = excluded.KeywordA,
                KeywordB = excluded.KeywordB,
                Memo = excluded.Memo,
                CoverImageName = excluded.CoverImageName,
                CoverImagePath = excluded.CoverImagePath,
                AddedDate = excluded.AddedDate,
                LastReadDate = excluded.LastReadDate,
                Pages = excluded.Pages,
                BookType = excluded.BookType,
                FileType = excluded.FileType,
                CoverVersion = excluded.CoverVersion,
                CoverBytes = excluded.CoverBytes,
                VolumeRecordName = excluded.VolumeRecordName,
                CloudKitRecordName = excluded.CloudKitRecordName,
                CloudKitChangeTag = excluded.CloudKitChangeTag,
                -- A row already waiting to upload stays waiting: an incoming copy from
                -- sync must not silently discard an edit made here that never went up.
                PendingUpload = MAX(Items.PendingUpload, excluded.PendingUpload);
        ";

        var pId = cmdItem.Parameters.Add("@Id", SqliteType.Text);
        var pLeg = cmdItem.Parameters.Add("@LegacyId", SqliteType.Integer);
        var pVol = cmdItem.Parameters.Add("@VolumeId", SqliteType.Text);
        var pRel = cmdItem.Parameters.Add("@RelativePath", SqliteType.Text);
        var pTit = cmdItem.Parameters.Add("@Title", SqliteType.Text);
        var pAut = cmdItem.Parameters.Add("@Author", SqliteType.Text);
        var pRat = cmdItem.Parameters.Add("@Rating", SqliteType.Integer);
        var pUnr = cmdItem.Parameters.Add("@IsUnread", SqliteType.Integer);
        var pGen = cmdItem.Parameters.Add("@Genre", SqliteType.Text);
        var pRln = cmdItem.Parameters.Add("@Relation", SqliteType.Text);
        var pKa = cmdItem.Parameters.Add("@KeywordA", SqliteType.Text);
        var pKb = cmdItem.Parameters.Add("@KeywordB", SqliteType.Text);
        var pMem = cmdItem.Parameters.Add("@Memo", SqliteType.Text);
        var pCin = cmdItem.Parameters.Add("@CoverImageName", SqliteType.Text);
        var pCip = cmdItem.Parameters.Add("@CoverImagePath", SqliteType.Text);
        var pAdd = cmdItem.Parameters.Add("@AddedDate", SqliteType.Text);
        var pLrd = cmdItem.Parameters.Add("@LastReadDate", SqliteType.Text);
        var pPag = cmdItem.Parameters.Add("@Pages", SqliteType.Integer);
        var pBkt = cmdItem.Parameters.Add("@BookType", SqliteType.Integer);
        var pFlt = cmdItem.Parameters.Add("@FileType", SqliteType.Integer);
        var pCvv = cmdItem.Parameters.Add("@CoverVersion", SqliteType.Integer);
        var pCvb = cmdItem.Parameters.Add("@CoverBytes", SqliteType.Integer);
        var pVrn = cmdItem.Parameters.Add("@VolumeRecordName", SqliteType.Text);
        var pCkr = cmdItem.Parameters.Add("@CloudKitRecordName", SqliteType.Text);
        var pCkt = cmdItem.Parameters.Add("@CloudKitChangeTag", SqliteType.Text);
        cmdItem.Parameters.AddWithValue("@PendingUpload", markPendingUpload ? 1 : 0);

        foreach (var item in items)
        {
            string itemIdStr = item.Id.ToString("D");
            pId.Value = itemIdStr;
            pLeg.Value = (object?)item.LegacyId ?? DBNull.Value;
            pVol.Value = item.VolumeId.HasValue ? item.VolumeId.Value.ToString("D") : DBNull.Value;
            pRel.Value = item.RelativePath ?? string.Empty;
            pTit.Value = item.Title ?? string.Empty;
            pAut.Value = item.Author ?? string.Empty;
            pRat.Value = item.Rating;
            pUnr.Value = item.IsUnread ? 1 : 0;
            pGen.Value = item.Genre ?? string.Empty;
            pRln.Value = item.Relation ?? string.Empty;
            pKa.Value = item.KeywordA ?? string.Empty;
            pKb.Value = item.KeywordB ?? string.Empty;
            pMem.Value = item.Memo ?? string.Empty;
            pCin.Value = item.CoverImageName ?? string.Empty;
            pCip.Value = item.CoverImagePath ?? string.Empty;
            pAdd.Value = item.AddedDate.ToString("O");
            pLrd.Value = item.LastReadDate.HasValue ? item.LastReadDate.Value.ToString("O") : DBNull.Value;
            pPag.Value = item.Pages;
            pBkt.Value = item.BookType;
            pFlt.Value = item.FileType;
            pCvv.Value = item.CoverVersion;
            pCvb.Value = item.CoverBytes;
            pVrn.Value = (object?)item.VolumeRecordName ?? DBNull.Value;
            pCkr.Value = (object?)item.CloudKitRecordName ?? DBNull.Value;
            pCkt.Value = (object?)item.CloudKitChangeTag ?? DBNull.Value;

            await cmdItem.ExecuteNonQueryAsync(cancellationToken);

            if (markPendingUpload)
                await ReconcileItemShelvesAsync(conn, tx, item, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO PendingCloudKitDeletions (RecordName, RecordType)
            SELECT CloudKitRecordName, 'CDMR' FROM ItemShelves
            WHERE ItemId = @Id AND CloudKitRecordName IS NOT NULL;
            INSERT OR IGNORE INTO PendingCloudKitDeletions (RecordName, RecordType)
            SELECT CloudKitRecordName, 'CD_Item' FROM Items
            WHERE Id = @Id AND CloudKitRecordName IS NOT NULL;
            DELETE FROM Items WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetAllItemIdsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Items";

        var ids = new List<Guid>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var id))
                ids.Add(id);
        }
        return ids;
    }

    public async Task<IReadOnlyList<LocalCoverState>> GetLocalCoverStatesAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM LocalCoverStates";

        var states = new List<LocalCoverState>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            states.Add(MapLocalCoverState(reader));
        return states;
    }

    public async Task<LocalCoverState?> GetLocalCoverStateAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM LocalCoverStates WHERE ItemId = @ItemId LIMIT 1";
        cmd.Parameters.AddWithValue("@ItemId", itemId.ToString("D"));

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapLocalCoverState(reader) : null;
    }

    public async Task UpsertLocalCoverStatesAsync(IEnumerable<LocalCoverState> states, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO LocalCoverStates
                (ItemId, Version, Bytes, UpdatedAt, PendingUpload, Attempts, AttemptedVersion, LastErrorCode)
            VALUES
                (@ItemId, @Version, @Bytes, @UpdatedAt, @PendingUpload, @Attempts, @AttemptedVersion, @LastErrorCode)
            ON CONFLICT(ItemId) DO UPDATE SET
                Version = excluded.Version,
                Bytes = excluded.Bytes,
                UpdatedAt = excluded.UpdatedAt,
                PendingUpload = excluded.PendingUpload,
                Attempts = excluded.Attempts,
                AttemptedVersion = excluded.AttemptedVersion,
                LastErrorCode = excluded.LastErrorCode;";

        var itemId = cmd.Parameters.Add("@ItemId", SqliteType.Text);
        var version = cmd.Parameters.Add("@Version", SqliteType.Integer);
        var bytes = cmd.Parameters.Add("@Bytes", SqliteType.Integer);
        var updatedAt = cmd.Parameters.Add("@UpdatedAt", SqliteType.Text);
        var pendingUpload = cmd.Parameters.Add("@PendingUpload", SqliteType.Integer);
        var attempts = cmd.Parameters.Add("@Attempts", SqliteType.Integer);
        var attemptedVersion = cmd.Parameters.Add("@AttemptedVersion", SqliteType.Integer);
        var lastErrorCode = cmd.Parameters.Add("@LastErrorCode", SqliteType.Integer);

        foreach (var state in states)
        {
            itemId.Value = state.ItemId.ToString("D");
            version.Value = state.Version;
            bytes.Value = state.Bytes;
            updatedAt.Value = state.UpdatedAt.ToString("O");
            pendingUpload.Value = state.PendingUpload ? 1 : 0;
            attempts.Value = state.Attempts;
            attemptedVersion.Value = state.AttemptedVersion;
            lastErrorCode.Value = state.LastErrorCode;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task ReconcileItemShelvesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        Item item,
        CancellationToken cancellationToken)
    {
        var existing = new Dictionary<Guid, string?>();
        using (var query = conn.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT ShelfId, CloudKitRecordName FROM ItemShelves WHERE ItemId = @ItemId";
            query.Parameters.AddWithValue("@ItemId", item.Id.ToString("D"));
            using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existing[Guid.Parse(reader.GetString(0))] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var desired = new HashSet<Guid>(item.ShelfIds ?? new List<Guid>());
        foreach (var (shelfId, recordName) in existing)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (desired.Contains(shelfId))
            {
                cmd.CommandText = "UPDATE ItemShelves SET PendingOperation = 0 WHERE ItemId = @ItemId AND ShelfId = @ShelfId AND PendingOperation = 2";
            }
            else if (recordName is null)
            {
                cmd.CommandText = "DELETE FROM ItemShelves WHERE ItemId = @ItemId AND ShelfId = @ShelfId";
            }
            else
            {
                cmd.CommandText = "UPDATE ItemShelves SET PendingOperation = 2 WHERE ItemId = @ItemId AND ShelfId = @ShelfId";
            }
            cmd.Parameters.AddWithValue("@ItemId", item.Id.ToString("D"));
            cmd.Parameters.AddWithValue("@ShelfId", shelfId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var shelfId in desired.Where(id => !existing.ContainsKey(id)))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO ItemShelves (ItemId, ShelfId, PendingOperation) VALUES (@ItemId, @ShelfId, 1)";
            cmd.Parameters.AddWithValue("@ItemId", item.Id.ToString("D"));
            cmd.Parameters.AddWithValue("@ShelfId", shelfId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Shelf>> GetShelvesAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Shelves ORDER BY SortOrder ASC";

        var list = new List<Shelf>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Shelf
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
                Title = reader.GetString(reader.GetOrdinal("Title")),
                Icon = reader.GetInt32(reader.GetOrdinal("Icon")),
                Type = reader.GetInt32(reader.GetOrdinal("Type")),
                SortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder")),
                SortAscending = reader.GetInt32(reader.GetOrdinal("SortAscending")) == 1,
                SortKey = reader.GetString(reader.GetOrdinal("SortKey")),
                SmartConditionsJson = reader.IsDBNull(reader.GetOrdinal("SmartConditionsJson")) ? null : reader.GetString(reader.GetOrdinal("SmartConditionsJson"))
            });
        }
        return list;
    }

    public async Task UpsertShelfAsync(Shelf shelf, bool markPendingUpload = true, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Shelves (Id, Title, Icon, Type, SortOrder, SortAscending, SortKey, SmartConditionsJson,
                CloudKitRecordName, CloudKitChangeTag, PendingUpload)
            VALUES (@Id, @Title, @Icon, @Type, @SortOrder, @SortAscending, @SortKey, @SmartConditionsJson,
                @CloudKitRecordName, @CloudKitChangeTag, @PendingUpload)
            ON CONFLICT(Id) DO UPDATE SET
                Title = excluded.Title,
                Icon = excluded.Icon,
                Type = excluded.Type,
                SortOrder = excluded.SortOrder,
                SortAscending = excluded.SortAscending,
                SortKey = excluded.SortKey,
                SmartConditionsJson = excluded.SmartConditionsJson,
                CloudKitRecordName = excluded.CloudKitRecordName,
                CloudKitChangeTag = excluded.CloudKitChangeTag,
                PendingUpload = MAX(Shelves.PendingUpload, excluded.PendingUpload);
        ";
        cmd.Parameters.AddWithValue("@Id", shelf.Id.ToString("D"));
        cmd.Parameters.AddWithValue("@Title", shelf.Title);
        cmd.Parameters.AddWithValue("@Icon", shelf.Icon);
        cmd.Parameters.AddWithValue("@Type", shelf.Type);
        cmd.Parameters.AddWithValue("@SortOrder", shelf.SortOrder);
        cmd.Parameters.AddWithValue("@SortAscending", shelf.SortAscending ? 1 : 0);
        cmd.Parameters.AddWithValue("@SortKey", shelf.SortKey);
        cmd.Parameters.AddWithValue("@SmartConditionsJson", (object?)shelf.SmartConditionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CloudKitRecordName", (object?)shelf.CloudKitRecordName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CloudKitChangeTag", (object?)shelf.CloudKitChangeTag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PendingUpload", markPendingUpload ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteShelfAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO PendingCloudKitDeletions (RecordName, RecordType)
            SELECT CloudKitRecordName, 'CDMR' FROM ItemShelves
            WHERE ShelfId = @Id AND CloudKitRecordName IS NOT NULL;
            INSERT OR IGNORE INTO PendingCloudKitDeletions (RecordName, RecordType)
            SELECT CloudKitRecordName, 'CD_Shelf' FROM Shelves
            WHERE Id = @Id AND CloudKitRecordName IS NOT NULL;
            DELETE FROM Shelves WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Volume>> GetVolumesAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Volumes";

        var list = new List<Volume>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Volume
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                LastKnownPath = reader.IsDBNull(reader.GetOrdinal("LastKnownPath")) ? string.Empty : reader.GetString(reader.GetOrdinal("LastKnownPath")),
                WindowsMountPath = reader.IsDBNull(reader.GetOrdinal("WindowsMountPath")) ? null : reader.GetString(reader.GetOrdinal("WindowsMountPath"))
            });
        }
        return list;
    }

    public async Task<Volume?> GetVolumeByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Volumes WHERE Id = @Id LIMIT 1";
        cmd.Parameters.AddWithValue("@Id", id.ToString("D"));

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new Volume
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                LastKnownPath = reader.IsDBNull(reader.GetOrdinal("LastKnownPath")) ? string.Empty : reader.GetString(reader.GetOrdinal("LastKnownPath")),
                WindowsMountPath = reader.IsDBNull(reader.GetOrdinal("WindowsMountPath")) ? null : reader.GetString(reader.GetOrdinal("WindowsMountPath"))
            };
        }
        return null;
    }

    public async Task UpsertVolumeAsync(Volume volume, bool markPendingUpload = true, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Volumes (Id, Name, LastKnownPath, WindowsMountPath, CloudKitRecordName, CloudKitChangeTag, PendingUpload)
            VALUES (@Id, @Name, @LastKnownPath, @WindowsMountPath, @CloudKitRecordName, @CloudKitChangeTag, @PendingUpload)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                LastKnownPath = excluded.LastKnownPath,
                -- The Windows mount path is this machine's own and never travels through
                -- iCloud, so a record arriving from sync must not erase it.
                WindowsMountPath = COALESCE(excluded.WindowsMountPath, Volumes.WindowsMountPath),
                CloudKitRecordName = excluded.CloudKitRecordName,
                CloudKitChangeTag = excluded.CloudKitChangeTag,
                PendingUpload = MAX(Volumes.PendingUpload, excluded.PendingUpload);
        ";
        cmd.Parameters.AddWithValue("@PendingUpload", markPendingUpload ? 1 : 0);
        cmd.Parameters.AddWithValue("@Id", volume.Id.ToString("D"));
        cmd.Parameters.AddWithValue("@Name", volume.Name);
        cmd.Parameters.AddWithValue("@LastKnownPath", volume.LastKnownPath);
        cmd.Parameters.AddWithValue("@WindowsMountPath", (object?)volume.WindowsMountPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CloudKitRecordName", (object?)volume.CloudKitRecordName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CloudKitChangeTag", (object?)volume.CloudKitChangeTag ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSyncMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM SyncMetadata WHERE Key = @Key LIMIT 1";
        cmd.Parameters.AddWithValue("@Key", key);
        var val = await cmd.ExecuteScalarAsync(cancellationToken);
        return val?.ToString();
    }

    public async Task SetSyncMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SyncMetadata (Key, Value) VALUES (@Key, @Value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
        ";
        cmd.Parameters.AddWithValue("@Key", key);
        cmd.Parameters.AddWithValue("@Value", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<Guid>> GetShelfIdsForItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ShelfId FROM ItemShelves WHERE ItemId = @ItemId AND PendingOperation <> 2";
        cmd.Parameters.AddWithValue("@ItemId", itemId.ToString("D"));

        var list = new List<Guid>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Guid.Parse(reader.GetString(0)));
        }
        return list;
    }

    private static Item MapItem(DbDataReader reader)
    {
        return new Item
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            LegacyId = reader.IsDBNull(reader.GetOrdinal("LegacyId")) ? null : reader.GetInt32(reader.GetOrdinal("LegacyId")),
            VolumeId = reader.IsDBNull(reader.GetOrdinal("VolumeId")) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal("VolumeId"))),
            RelativePath = reader.GetString(reader.GetOrdinal("RelativePath")),
            Title = reader.GetString(reader.GetOrdinal("Title")),
            Author = reader.GetString(reader.GetOrdinal("Author")),
            Rating = reader.GetInt32(reader.GetOrdinal("Rating")),
            IsUnread = reader.GetInt32(reader.GetOrdinal("IsUnread")) == 1,
            Genre = reader.IsDBNull(reader.GetOrdinal("Genre")) ? "" : reader.GetString(reader.GetOrdinal("Genre")),
            Relation = reader.IsDBNull(reader.GetOrdinal("Relation")) ? "" : reader.GetString(reader.GetOrdinal("Relation")),
            KeywordA = reader.IsDBNull(reader.GetOrdinal("KeywordA")) ? "" : reader.GetString(reader.GetOrdinal("KeywordA")),
            KeywordB = reader.IsDBNull(reader.GetOrdinal("KeywordB")) ? "" : reader.GetString(reader.GetOrdinal("KeywordB")),
            Memo = reader.IsDBNull(reader.GetOrdinal("Memo")) ? "" : reader.GetString(reader.GetOrdinal("Memo")),
            CoverImageName = reader.IsDBNull(reader.GetOrdinal("CoverImageName")) ? "" : reader.GetString(reader.GetOrdinal("CoverImageName")),
            CoverImagePath = reader.IsDBNull(reader.GetOrdinal("CoverImagePath")) ? "" : reader.GetString(reader.GetOrdinal("CoverImagePath")),
            AddedDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("AddedDate"))),
            LastReadDate = reader.IsDBNull(reader.GetOrdinal("LastReadDate")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastReadDate"))),
            Pages = reader.GetInt32(reader.GetOrdinal("Pages")),
            BookType = reader.GetInt32(reader.GetOrdinal("BookType")),
            FileType = reader.GetInt32(reader.GetOrdinal("FileType")),
            CoverVersion = reader.GetInt32(reader.GetOrdinal("CoverVersion")),
            CoverBytes = reader.GetInt64(reader.GetOrdinal("CoverBytes")),
            VolumeRecordName = reader.IsDBNull(reader.GetOrdinal("VolumeRecordName")) ? null : reader.GetString(reader.GetOrdinal("VolumeRecordName")),
            CloudKitRecordName = reader.IsDBNull(reader.GetOrdinal("CloudKitRecordName")) ? null : reader.GetString(reader.GetOrdinal("CloudKitRecordName")),
            CloudKitChangeTag = reader.IsDBNull(reader.GetOrdinal("CloudKitChangeTag")) ? null : reader.GetString(reader.GetOrdinal("CloudKitChangeTag"))
        };
    }

    private static Shelf MapShelf(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        Title = reader.GetString(reader.GetOrdinal("Title")),
        Icon = reader.GetInt32(reader.GetOrdinal("Icon")),
        Type = reader.GetInt32(reader.GetOrdinal("Type")),
        SortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder")),
        SortAscending = reader.GetInt32(reader.GetOrdinal("SortAscending")) == 1,
        SortKey = reader.GetString(reader.GetOrdinal("SortKey")),
        SmartConditionsJson = reader.IsDBNull(reader.GetOrdinal("SmartConditionsJson")) ? null : reader.GetString(reader.GetOrdinal("SmartConditionsJson")),
        CloudKitRecordName = reader.IsDBNull(reader.GetOrdinal("CloudKitRecordName")) ? null : reader.GetString(reader.GetOrdinal("CloudKitRecordName")),
        CloudKitChangeTag = reader.IsDBNull(reader.GetOrdinal("CloudKitChangeTag")) ? null : reader.GetString(reader.GetOrdinal("CloudKitChangeTag"))
    };

    private static Volume MapVolume(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        LastKnownPath = reader.IsDBNull(reader.GetOrdinal("LastKnownPath")) ? string.Empty : reader.GetString(reader.GetOrdinal("LastKnownPath")),
        WindowsMountPath = reader.IsDBNull(reader.GetOrdinal("WindowsMountPath")) ? null : reader.GetString(reader.GetOrdinal("WindowsMountPath")),
        CloudKitRecordName = reader.IsDBNull(reader.GetOrdinal("CloudKitRecordName")) ? null : reader.GetString(reader.GetOrdinal("CloudKitRecordName")),
        CloudKitChangeTag = reader.IsDBNull(reader.GetOrdinal("CloudKitChangeTag")) ? null : reader.GetString(reader.GetOrdinal("CloudKitChangeTag"))
    };

    private static LocalCoverState MapLocalCoverState(DbDataReader reader) => new()
    {
        ItemId = Guid.Parse(reader.GetString(reader.GetOrdinal("ItemId"))),
        Version = reader.GetInt32(reader.GetOrdinal("Version")),
        Bytes = reader.GetInt64(reader.GetOrdinal("Bytes")),
        UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        PendingUpload = reader.GetInt32(reader.GetOrdinal("PendingUpload")) != 0,
        Attempts = reader.GetInt32(reader.GetOrdinal("Attempts")),
        AttemptedVersion = reader.GetInt32(reader.GetOrdinal("AttemptedVersion")),
        LastErrorCode = reader.GetInt32(reader.GetOrdinal("LastErrorCode"))
    };

    /// <summary>
    /// Turns a smart shelf's conditions into a SQL predicate. Evaluating them in memory
    /// would mean loading the whole library for every shelf click, so they are pushed
    /// down to the query the way the ordinary filters are.
    /// Conditions combine with AND, matching the Mac app.
    /// </summary>
    private static string BuildSmartConditionSql(SmartConditions conditions, SqliteCommand cmd, DateTime now)
    {
        var sql = new StringBuilder();

        if (conditions.Keyword is { } keyword && keyword.Text.Length > 0)
        {
            string column = KeywordColumn(keyword.Field);
            cmd.Parameters.AddWithValue("@SmartKeyword", keyword.Text);
            cmd.Parameters.AddWithValue("@SmartKeywordLike", $"%{keyword.Text}%");

            sql.Append(keyword.Mode switch
            {
                1 => $" AND {column} NOT LIKE @SmartKeywordLike",
                2 => $" AND {column} = @SmartKeyword",
                _ => $" AND {column} LIKE @SmartKeywordLike"
            });
        }

        if (conditions.Date is { } date)
        {
            // Dates are stored as round-trip ("O") strings, which compare correctly as text.
            string column = date.Field == 1 ? "i.LastReadDate" : "i.AddedDate";
            cmd.Parameters.AddWithValue("@SmartCutoff", now.AddDays(-date.Days).ToString("O"));

            sql.Append(date.Mode == 1
                ? $" AND {column} IS NOT NULL AND {column} < @SmartCutoff"
                : $" AND {column} IS NOT NULL AND {column} >= @SmartCutoff");
        }

        if (conditions.Types is { Count: > 0 } types)
            sql.Append($" AND i.BookType IN ({string.Join(",", types)})");

        if (conditions.Rates is { Count: > 0 } rates)
            sql.Append($" AND i.Rating IN ({string.Join(",", rates)})");

        if (conditions.UnreadOnly)
            sql.Append(" AND i.IsUnread = 1");

        return sql.ToString();
    }

    private static string KeywordColumn(string field) => field.ToLowerInvariant() switch
    {
        "title" => "i.Title",
        "author" => "i.Author",
        "genre" => "i.Genre",
        "relation" => "i.Relation",
        "keyword a" or "keyworda" => "i.KeywordA",
        "keyword b" or "keywordb" => "i.KeywordB",
        "neta" or "memo" => "i.Memo",
        // An unrecognised legacy field searches every text column, as on the Mac.
        _ => "(i.Title || CHAR(10) || i.Author || CHAR(10) || i.Genre || CHAR(10) || i.Relation || CHAR(10) || i.KeywordA || CHAR(10) || i.KeywordB || CHAR(10) || i.Memo)"
    };

    private async Task<SmartConditions?> GetSmartConditionsAsync(Guid shelfId, CancellationToken cancellationToken)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Type, SmartConditionsJson FROM Shelves WHERE Id = @Id LIMIT 1";
        cmd.Parameters.AddWithValue("@Id", shelfId.ToString("D"));

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 1)
            return null;

        return reader.IsDBNull(1) ? new SmartConditions() : SmartConditionsCodec.Decode(reader.GetString(1));
    }

    /// <summary>
    /// Records item-shelf membership read from CDMR join records. The pair is expressed
    /// as CloudKit record names, which are resolved here against the rows that carry them,
    /// so links survive arriving before the items or shelves they connect.
    /// </summary>
    public async Task<int> ApplyItemShelfLinksAsync(IEnumerable<ItemShelfLink> links, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO ItemShelves (ItemId, ShelfId, CloudKitRecordName, PendingOperation)
            SELECT i.Id, s.Id, @RecordName
                 , 0
            FROM Items i, Shelves s
            WHERE i.CloudKitRecordName = @ItemRecordName
              AND s.CloudKitRecordName = @ShelfRecordName
            ON CONFLICT(ItemId, ShelfId) DO UPDATE SET
                CloudKitRecordName = excluded.CloudKitRecordName,
                PendingOperation = CASE WHEN ItemShelves.PendingOperation = 2 THEN 2 ELSE 0 END;
        ";
        var pRecord = cmd.Parameters.Add("@RecordName", SqliteType.Text);
        var pItem = cmd.Parameters.Add("@ItemRecordName", SqliteType.Text);
        var pShelf = cmd.Parameters.Add("@ShelfRecordName", SqliteType.Text);

        int applied = 0;
        foreach (var link in links)
        {
            pRecord.Value = link.RecordName;
            pItem.Value = link.ItemRecordName;
            pShelf.Value = link.ShelfRecordName;
            applied += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return applied;
    }

    /// <summary>
    /// Fills in VolumeId for items whose volume arrived as a record name, which is the
    /// only form the zone carries.
    /// </summary>
    public async Task<int> ResolveVolumeReferencesAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Items
            SET VolumeId = (SELECT v.Id FROM Volumes v WHERE v.CloudKitRecordName = Items.VolumeRecordName)
            WHERE VolumeRecordName IS NOT NULL
              AND (VolumeId IS NULL
                   OR VolumeId <> (SELECT v.Id FROM Volumes v WHERE v.CloudKitRecordName = Items.VolumeRecordName))
              AND EXISTS (SELECT 1 FROM Volumes v WHERE v.CloudKitRecordName = Items.VolumeRecordName);
        ";
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Rows edited here that iCloud has not taken yet.
    /// </summary>
    public async Task<PendingUploads> GetPendingUploadsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);

        var items = new List<Item>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Items WHERE PendingUpload = 1 LIMIT @Limit";
            cmd.Parameters.AddWithValue("@Limit", limit);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(MapItem(reader));
        }

        var shelves = new List<Shelf>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Shelves WHERE PendingUpload = 1 LIMIT @Limit";
            cmd.Parameters.AddWithValue("@Limit", limit);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                shelves.Add(MapShelf(reader));
        }

        var volumes = new List<Volume>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Volumes WHERE PendingUpload = 1 LIMIT @Limit";
            cmd.Parameters.AddWithValue("@Limit", limit);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                volumes.Add(MapVolume(reader));
        }

        var itemShelfChanges = new List<PendingItemShelfChange>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT x.ItemId, x.ShelfId, x.CloudKitRecordName,
                       i.CloudKitRecordName, s.CloudKitRecordName, x.PendingOperation
                FROM ItemShelves x
                JOIN Items i ON i.Id = x.ItemId
                JOIN Shelves s ON s.Id = x.ShelfId
                WHERE x.PendingOperation <> 0
                  AND i.CloudKitRecordName IS NOT NULL
                  AND s.CloudKitRecordName IS NOT NULL
                LIMIT @Limit";
            cmd.Parameters.AddWithValue("@Limit", limit);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                itemShelfChanges.Add(new PendingItemShelfChange(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5) == 2));
            }
        }

        var deletions = new List<PendingCloudKitDeletion>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT RecordName, RecordType FROM PendingCloudKitDeletions LIMIT @Limit";
            cmd.Parameters.AddWithValue("@Limit", limit);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                deletions.Add(new PendingCloudKitDeletion(reader.GetString(0), reader.GetString(1)));
        }

        return new PendingUploads(items, shelves, volumes, itemShelfChanges, deletions);
    }

    /// <summary>
    /// Marks a row as taken by iCloud and stores the record name and version the server
    /// answered with, which the next write of that row has to quote.
    /// </summary>
    public async Task ConfirmUploadedAsync(string table, Guid id, string recordName, string? changeTag, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        if (table is not ("Items" or "Shelves" or "Volumes"))
            throw new ArgumentException($"Unknown table '{table}'.", nameof(table));

        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {table}
            SET PendingUpload = 0,
                CloudKitRecordName = @RecordName,
                CloudKitChangeTag = @ChangeTag
            WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@RecordName", recordName);
        cmd.Parameters.AddWithValue("@ChangeTag", (object?)changeTag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteSyncMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SyncMetadata WHERE Key = @Key";
        cmd.Parameters.AddWithValue("@Key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ConfirmItemShelfUploadedAsync(
        Guid itemId,
        Guid shelfId,
        string recordName,
        bool wasDelete,
        CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = wasDelete
            ? "DELETE FROM ItemShelves WHERE ItemId = @ItemId AND ShelfId = @ShelfId"
            : @"UPDATE ItemShelves SET CloudKitRecordName = @RecordName, PendingOperation = 0
                WHERE ItemId = @ItemId AND ShelfId = @ShelfId";
        cmd.Parameters.AddWithValue("@ItemId", itemId.ToString("D"));
        cmd.Parameters.AddWithValue("@ShelfId", shelfId.ToString("D"));
        cmd.Parameters.AddWithValue("@RecordName", recordName);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ConfirmDeletionUploadedAsync(string recordName, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PendingCloudKitDeletions WHERE RecordName = @RecordName";
        cmd.Parameters.AddWithValue("@RecordName", recordName);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Queues every row for upload, for the "resend everything" repair in Preferences.
    /// </summary>
    /// <summary>Backs up the live SQLite database before atomically preparing the chosen library.</summary>
    public async Task<string> PrepareCloudSyncAsync(bool replaceLocalLibrary, string environment, CancellationToken cancellationToken = default)
    {
        if (environment is not ("production" or "development"))
            throw new ArgumentException("Unknown CloudKit environment.", nameof(environment));
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        string backupDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(conn.DataSource))!, "Backups");
        using (var previousEnvironment = conn.CreateCommand())
        {
            previousEnvironment.CommandText = "SELECT Value FROM SyncMetadata WHERE Key = 'CloudKit_Environment'";
            var previous = await previousEnvironment.ExecuteScalarAsync(cancellationToken) as string;
            if (!replaceLocalLibrary && previous != null && previous != environment)
                throw new InvalidOperationException("別環境と同期した蔵書を1台目として送信できません。元の環境に戻すか、2台目以降として置き換えてください。");
        }
        Directory.CreateDirectory(backupDirectory);
        string backupPath = Path.Combine(backupDirectory, $"before-cloud-switch-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        cancellationToken.ThrowIfCancellationRequested();
        // SQLite's backup API includes committed WAL pages; copying the .db file does not.
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString()))
        {
            await backup.OpenAsync(cancellationToken);
            conn.BackupDatabase(backup);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = replaceLocalLibrary
            ? "DELETE FROM ItemShelves; DELETE FROM Items; DELETE FROM Shelves; DELETE FROM Volumes; DELETE FROM LocalCoverStates; DELETE FROM PendingCloudKitDeletions; DELETE FROM SyncMetadata;"
            : "UPDATE Items SET PendingUpload = 1; UPDATE Shelves SET PendingUpload = 1; UPDATE Volumes SET PendingUpload = 1; UPDATE ItemShelves SET PendingOperation = 1 WHERE CloudKitRecordName IS NULL; DELETE FROM SyncMetadata WHERE Key = 'CloudKit_SyncToken';";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        cmd.CommandText = "INSERT OR REPLACE INTO SyncMetadata (Key, Value) VALUES ('CloudKit_Mode', @mode), ('CloudKit_Environment', @environment), ('CloudKit_InitialDownload', @pending)";
        cmd.Parameters.AddWithValue("@mode", replaceLocalLibrary ? "replica" : "primary");
        cmd.Parameters.AddWithValue("@environment", environment);
        cmd.Parameters.AddWithValue("@pending", replaceLocalLibrary ? "1" : "0");
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return backupPath;
    }

    public async Task MarkAllPendingUploadAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        foreach (string table in new[] { "Items", "Shelves", "Volumes" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET PendingUpload = 1";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        using var links = conn.CreateCommand();
        links.CommandText = "UPDATE ItemShelves SET PendingOperation = 1 WHERE CloudKitRecordName IS NULL";
        await links.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Applies a deletion from the zone. A deleted record arrives as a record name with no
    /// type, so every table that can hold one is checked.
    /// </summary>
    public async Task DeleteByCloudKitRecordNameAsync(string recordName, CancellationToken cancellationToken = default)
    {
        using var lease = await EnterDatabaseAsync(cancellationToken);
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        foreach (string table in new[] { "ItemShelves", "Items", "Shelves", "Volumes" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE CloudKitRecordName = @RecordName";
            cmd.Parameters.AddWithValue("@RecordName", recordName);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
