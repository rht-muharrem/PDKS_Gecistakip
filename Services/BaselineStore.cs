using Microsoft.Data.Sqlite;

namespace ServerMonitor.Services;

public sealed class BaselineStore
{
    private readonly string _connectionString;
    private readonly ILogger<BaselineStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public BaselineStore(IHostEnvironment env, ILogger<BaselineStore> logger)
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
            CREATE TABLE IF NOT EXISTS baseline (
                server_id TEXT NOT NULL,
                kind      TEXT NOT NULL,
                data      TEXT NOT NULL,
                updated   TEXT NOT NULL,
                PRIMARY KEY (server_id, kind)
            );
            """;
        command.ExecuteNonQuery();
    }


    public async Task<string?> GetAsync(string serverId, string kind, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM baseline WHERE server_id = $id AND kind = $kind;";
            command.Parameters.AddWithValue("$id", serverId);
            command.Parameters.AddWithValue("$kind", kind);
            var result = await command.ExecuteScalarAsync(ct);
            return result as string;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Baseline okunamadi: {Server}/{Kind}", serverId, kind);
            return null;
        }
    }

    public async Task SetAsync(string serverId, string kind, string data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO baseline (server_id, kind, data, updated) VALUES ($id, $kind, $data, $ts)
                ON CONFLICT(server_id, kind) DO UPDATE SET data = $data, updated = $ts;
                """;
            command.Parameters.AddWithValue("$id", serverId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$data", data);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Baseline yazilamadi: {Server}/{Kind}", serverId, kind);
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
            command.CommandText = "DELETE FROM baseline WHERE server_id = $id;";
            command.Parameters.AddWithValue("$id", serverId);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Baseline silinemedi: {Server}", serverId);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
