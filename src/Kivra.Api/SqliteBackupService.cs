using Microsoft.Data.Sqlite;

namespace Kivra.Api;

public sealed record SqliteBackupOptions(string ConnectionString, string Directory);

public sealed class SqliteBackupState
{
    private readonly object gate = new();
    private DateTimeOffset? lastSuccessfulAt;
    private string? lastFile;
    private string? lastError;

    public void Success(DateTimeOffset at, string file)
    {
        lock (gate)
        {
            lastSuccessfulAt = at;
            lastFile = file;
            lastError = null;
        }
    }

    public void Failure(string error)
    {
        lock (gate) lastError = error;
    }

    public object Snapshot()
    {
        lock (gate)
            return new { healthy = lastSuccessfulAt.HasValue && lastError is null, lastSuccessfulAt, lastFile, lastError };
    }
}

public sealed class SqliteBackupService(
    SqliteBackupOptions options,
    SqliteBackupState state,
    ILogger<SqliteBackupService> logger) : BackgroundService
{
    private readonly SemaphoreSlim backupLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CreateBackupAsync("rolling", stoppingToken);
    }

    public async Task CreateBackupAsync(string reason, CancellationToken cancellationToken)
    {
        await backupLock.WaitAsync(cancellationToken);
        try
        {
            System.IO.Directory.CreateDirectory(options.Directory);
            var now = DateTimeOffset.Now;
            var finalPath = Path.Combine(options.Directory, $"kivra-{now:yyyyMMdd-HHmmss}-{reason}.db");
            var temporaryPath = finalPath + ".tmp";

            await using (var source = new SqliteConnection(options.ConnectionString))
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ConnectionString))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);

                await using var check = destination.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                var result = (await check.ExecuteScalarAsync(cancellationToken))?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SQLite backup integrity check failed: {result}");
            }

            File.Move(temporaryPath, finalPath);
            CreateDailySnapshot(finalPath, now);
            PruneBackups();
            state.Success(now, finalPath);
            logger.LogInformation("Verified SQLite backup created ({Reason}): {BackupPath}", reason, finalPath);
        }
        catch (Exception ex)
        {
            state.Failure(ex.Message);
            logger.LogError(ex, "SQLite backup failed ({Reason})", reason);
        }
        finally
        {
            backupLock.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await CreateBackupAsync("shutdown", cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private void CreateDailySnapshot(string source, DateTimeOffset now)
    {
        var dailyPath = Path.Combine(options.Directory, $"kivra-daily-{now:yyyyMMdd}.db");
        if (!File.Exists(dailyPath)) File.Copy(source, dailyPath);
    }

    private void PruneBackups()
    {
        var directory = new DirectoryInfo(options.Directory);
        foreach (var file in directory.GetFiles("kivra-*.db")
                     .Where(file => !file.Name.StartsWith("kivra-daily-", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(120))
            file.Delete();

        foreach (var file in directory.GetFiles("kivra-daily-*.db")
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(30))
            file.Delete();
    }

}
