using System.Globalization;
using Microsoft.Data.Sqlite;
using ServerMonitor.Models;

namespace ServerMonitor.Services;

/// <summary>
/// Olcumleri history.db (SQLite) dosyasina yazar. Uygulama yeniden baslayinca grafikler
/// buradan doldurulur; uzun donem uptime raporlari da buradan hesaplanir.
/// </summary>
public sealed class HistoryStore
{
    private readonly string _connectionString;
    private readonly ILogger<HistoryStore> _logger;

    // SQLite ayni anda tek yaziciya izin verir; yazmalari siraya sokuyoruz.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public HistoryStore(IHostEnvironment env, ILogger<HistoryStore> logger)
    {
        _logger = logger;
        var path = Path.Combine(env.ContentRootPath, "history.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS measurements (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                server_id   TEXT    NOT NULL,
                ts          TEXT    NOT NULL,
                state       INTEGER NOT NULL,
                response_ms REAL    NULL,
                cpu         REAL    NULL,
                memory      REAL    NULL,
                disk        REAL    NULL
            );

            CREATE INDEX IF NOT EXISTS ix_measurements_server_ts ON measurements (server_id, ts);
            """;
        command.ExecuteNonQuery();
    }

    public async Task AppendAsync(string serverId, HistoryPoint point, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO measurements (server_id, ts, state, response_ms, cpu, memory, disk)
                VALUES ($id, $ts, $state, $response, $cpu, $memory, $disk);
                """;
            command.Parameters.AddWithValue("$id", serverId);
            command.Parameters.AddWithValue("$ts", FormatTs(point.TimestampUtc));
            command.Parameters.AddWithValue("$state", (int)point.State);
            command.Parameters.AddWithValue("$response", (object?)point.ResponseMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$cpu", (object?)point.CpuPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$memory", (object?)point.MemoryPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$disk", (object?)point.DiskPercent ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diske yazilamamasi izlemeyi durdurmamali.
            _logger.LogError(ex, "Olcum veritabanina yazilamadi: {Server}", serverId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Son N olcumu eskiden yeniye sirali doner.</summary>
    public async Task<List<HistoryPoint>> LoadRecentAsync(string serverId, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT ts, state, response_ms, cpu, memory, disk FROM (
                SELECT * FROM measurements WHERE server_id = $id ORDER BY ts DESC LIMIT $limit
            ) ORDER BY ts ASC;
            """;

        return await QueryAsync(sql, ct, ("$id", serverId), ("$limit", Math.Max(1, limit)));
    }

    public async Task<List<HistoryPoint>> LoadSinceAsync(string serverId, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        const string sql = """
            SELECT ts, state, response_ms, cpu, memory, disk
            FROM measurements
            WHERE server_id = $id AND ts >= $since
            ORDER BY ts ASC;
            """;

        return await QueryAsync(sql, ct, ("$id", serverId), ("$since", FormatTs(sinceUtc)));
    }

    /// <summary>Verilen tarihten bu yana sunucu basina toplam/saglikli olcum sayisi.</summary>
    public async Task<Dictionary<string, (int Total, int Healthy)>> GetUptimeAsync(DateTimeOffset sinceUtc, CancellationToken ct)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT server_id,
                       COUNT(*),
                       SUM(CASE WHEN state IN ($up, $warn) THEN 1 ELSE 0 END)
                FROM measurements
                WHERE ts >= $since
                GROUP BY server_id;
                """;
            command.Parameters.AddWithValue("$since", FormatTs(sinceUtc));
            command.Parameters.AddWithValue("$up", (int)HealthState.Up);
            command.Parameters.AddWithValue("$warn", (int)HealthState.Warning);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result[reader.GetString(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Uptime raporu okunamadi.");
        }

        return result;
    }

    public async Task PruneAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays));

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM measurements WHERE ts < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", FormatTs(cutoff));

            var deleted = await command.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                _logger.LogInformation("{Count} eski olcum silindi ({Days} gunden eski).", deleted, retentionDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Eski olcumler silinemedi.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM measurements WHERE server_id = $id;";
            command.Parameters.AddWithValue("$id", serverId);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sunucu gecmisi silinemedi: {Server}", serverId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<List<HistoryPoint>> QueryAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var points = new List<HistoryPoint>();

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                points.Add(new HistoryPoint
                {
                    TimestampUtc = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    State = (HealthState)reader.GetInt32(1),
                    ResponseMs = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                    CpuPercent = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    MemoryPercent = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    DiskPercent = reader.IsDBNull(5) ? null : reader.GetDouble(5)
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Olcum gecmisi okunamadi.");
        }

        return points;
    }

    // Sabit uzunluklu ISO 8601 (UTC): metin olarak siralama = zaman siralamasi.
    private static string FormatTs(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
