using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS Volumes (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                LastKnownPath TEXT,
                WindowsMountPath TEXT
            );

            CREATE TABLE IF NOT EXISTS Shelves (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Icon INTEGER NOT NULL,
                Type INTEGER NOT NULL,
                SortOrder INTEGER NOT NULL,
                SortAscending INTEGER NOT NULL,
                SortKey TEXT NOT NULL,
                SmartConditionsJson TEXT
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
                FOREIGN KEY (VolumeId) REFERENCES Volumes(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS ItemShelves (
                ItemId TEXT NOT NULL,
                ShelfId TEXT NOT NULL,
                PRIMARY KEY (ItemId, ShelfId),
                FOREIGN KEY (ItemId) REFERENCES Items(Id) ON DELETE CASCADE,
                FOREIGN KEY (ShelfId) REFERENCES Shelves(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SyncMetadata (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_Items_Title ON Items(Title);
            CREATE INDEX IF NOT EXISTS IX_Items_Author ON Items(Author);
            CREATE INDEX IF NOT EXISTS IX_Items_Rating ON Items(Rating);
            CREATE INDEX IF NOT EXISTS IX_Items_IsUnread ON Items(IsUnread);
            CREATE INDEX IF NOT EXISTS IX_Items_LegacyId ON Items(LegacyId);
            CREATE INDEX IF NOT EXISTS IX_ItemShelves_ShelfId ON ItemShelves(ShelfId);
        ";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Item?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
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

    public async Task<IReadOnlyList<Item>> GetItemsAsync(int skip = 0, int take = 100, Guid? shelfId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();

        string query = @"
            SELECT i.* FROM Items i
        ";

        if (shelfId.HasValue)
        {
            query += " INNER JOIN ItemShelves s ON i.Id = s.ItemId WHERE s.ShelfId = @ShelfId";
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
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();

        string query = "SELECT COUNT(*) FROM Items i";
        if (shelfId.HasValue)
        {
            query += " INNER JOIN ItemShelves s ON i.Id = s.ItemId WHERE s.ShelfId = @ShelfId";
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

    public async Task UpsertItemAsync(Item item, CancellationToken cancellationToken = default)
    {
        await UpsertItemsBatchAsync(new[] { item }, cancellationToken);
    }

    public async Task UpsertItemsBatchAsync(IEnumerable<Item> items, CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        using var cmdItem = conn.CreateCommand();
        cmdItem.Transaction = tx;
        cmdItem.CommandText = @"
            INSERT INTO Items (
                Id, LegacyId, VolumeId, RelativePath, Title, Author, Rating, IsUnread,
                Genre, Relation, KeywordA, KeywordB, Memo, CoverImageName, CoverImagePath,
                AddedDate, LastReadDate, Pages, BookType, FileType, CoverVersion, CoverBytes
            ) VALUES (
                @Id, @LegacyId, @VolumeId, @RelativePath, @Title, @Author, @Rating, @IsUnread,
                @Genre, @Relation, @KeywordA, @KeywordB, @Memo, @CoverImageName, @CoverImagePath,
                @AddedDate, @LastReadDate, @Pages, @BookType, @FileType, @CoverVersion, @CoverBytes
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
                CoverBytes = excluded.CoverBytes;
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

        using var cmdDeleteShelves = conn.CreateCommand();
        cmdDeleteShelves.Transaction = tx;
        cmdDeleteShelves.CommandText = "DELETE FROM ItemShelves WHERE ItemId = @ItemId";
        var pDelItemId = cmdDeleteShelves.Parameters.Add("@ItemId", SqliteType.Text);

        using var cmdInsertShelf = conn.CreateCommand();
        cmdInsertShelf.Transaction = tx;
        cmdInsertShelf.CommandText = "INSERT OR IGNORE INTO ItemShelves (ItemId, ShelfId) VALUES (@ItemId, @ShelfId)";
        var pInsItemId = cmdInsertShelf.Parameters.Add("@ItemId", SqliteType.Text);
        var pInsShelfId = cmdInsertShelf.Parameters.Add("@ShelfId", SqliteType.Text);

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

            await cmdItem.ExecuteNonQueryAsync(cancellationToken);

            if (item.ShelfIds != null && item.ShelfIds.Count > 0)
            {
                pDelItemId.Value = itemIdStr;
                await cmdDeleteShelves.ExecuteNonQueryAsync(cancellationToken);

                pInsItemId.Value = itemIdStr;
                foreach (var sId in item.ShelfIds)
                {
                    pInsShelfId.Value = sId.ToString("D");
                    await cmdInsertShelf.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Items WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Shelf>> GetShelvesAsync(CancellationToken cancellationToken = default)
    {
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

    public async Task UpsertShelfAsync(Shelf shelf, CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Shelves (Id, Title, Icon, Type, SortOrder, SortAscending, SortKey, SmartConditionsJson)
            VALUES (@Id, @Title, @Icon, @Type, @SortOrder, @SortAscending, @SortKey, @SmartConditionsJson)
            ON CONFLICT(Id) DO UPDATE SET
                Title = excluded.Title,
                Icon = excluded.Icon,
                Type = excluded.Type,
                SortOrder = excluded.SortOrder,
                SortAscending = excluded.SortAscending,
                SortKey = excluded.SortKey,
                SmartConditionsJson = excluded.SmartConditionsJson;
        ";
        cmd.Parameters.AddWithValue("@Id", shelf.Id.ToString("D"));
        cmd.Parameters.AddWithValue("@Title", shelf.Title);
        cmd.Parameters.AddWithValue("@Icon", shelf.Icon);
        cmd.Parameters.AddWithValue("@Type", shelf.Type);
        cmd.Parameters.AddWithValue("@SortOrder", shelf.SortOrder);
        cmd.Parameters.AddWithValue("@SortAscending", shelf.SortAscending ? 1 : 0);
        cmd.Parameters.AddWithValue("@SortKey", shelf.SortKey);
        cmd.Parameters.AddWithValue("@SmartConditionsJson", (object?)shelf.SmartConditionsJson ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteShelfAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Shelves WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Volume>> GetVolumesAsync(CancellationToken cancellationToken = default)
    {
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

    public async Task UpsertVolumeAsync(Volume volume, CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Volumes (Id, Name, LastKnownPath, WindowsMountPath)
            VALUES (@Id, @Name, @LastKnownPath, @WindowsMountPath)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                LastKnownPath = excluded.LastKnownPath,
                WindowsMountPath = excluded.WindowsMountPath;
        ";
        cmd.Parameters.AddWithValue("@Id", volume.Id.ToString("D"));
        cmd.Parameters.AddWithValue("@Name", volume.Name);
        cmd.Parameters.AddWithValue("@LastKnownPath", volume.LastKnownPath);
        cmd.Parameters.AddWithValue("@WindowsMountPath", (object?)volume.WindowsMountPath ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSyncMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM SyncMetadata WHERE Key = @Key LIMIT 1";
        cmd.Parameters.AddWithValue("@Key", key);
        var val = await cmd.ExecuteScalarAsync(cancellationToken);
        return val?.ToString();
    }

    public async Task SetSyncMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
    {
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
        cmd.CommandText = "SELECT ShelfId FROM ItemShelves WHERE ItemId = @ItemId";
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
            CoverBytes = reader.GetInt64(reader.GetOrdinal("CoverBytes"))
        };
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
