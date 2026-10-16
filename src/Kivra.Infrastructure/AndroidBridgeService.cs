using System.Data;
using System.Security.Cryptography;
using System.Text;
using Kivra.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kivra.Infrastructure;

public sealed record BridgePairResult(Guid DeviceId, string DeviceName, string Token);
public sealed record BridgeDeviceSummary(
    Guid Id,
    string Name,
    bool Active,
    DateTimeOffset? LastSeenAt,
    string? AppVersion,
    string? Platform,
    bool Online,
    DateTimeOffset CreatedAt);
public sealed record BridgeJob(
    Guid JobId,
    string LabelCode,
    string ItemName,
    string PrinterName,
    string IpAddress,
    int TcpPort,
    string Payload,
    DateTimeOffset LeaseExpiresAt);

public sealed class AndroidBridgeService(KivraDbContext db)
{
    static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(45);

    public async Task<(string Code, DateTimeOffset ExpiresAt)> CreatePairingCodeAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = (await db.BridgePairingCodes.ToListAsync(ct))
            .Where(x => x.ExpiresAt < now || x.UsedAt is not null)
            .ToList();
        db.BridgePairingCodes.RemoveRange(expired);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var pairing = new BridgePairingCode
        {
            CodeHash = Hash(code),
            ExpiresAt = now.Add(PairingLifetime),
            CreatedByUserId = userId
        };
        db.Add(pairing);
        await db.SaveChangesAsync(ct);
        return (code, pairing.ExpiresAt);
    }

    public async Task<BridgePairResult?> PairAsync(string code, string deviceName, string? appVersion, string? platform, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(deviceName)) return null;
        var now = DateTimeOffset.UtcNow;
        var codeHash = Hash(code.Trim());
        var pairing = await db.BridgePairingCodes.SingleOrDefaultAsync(x => x.CodeHash == codeHash, ct);
        if (pairing is null || pairing.UsedAt is not null || pairing.ExpiresAt <= now) return null;

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new BridgeDevice
        {
            Name = deviceName.Trim(),
            TokenHash = Hash(token),
            LastSeenAt = now,
            AppVersion = appVersion?.Trim(),
            Platform = platform?.Trim()
        };
        pairing.UsedAt = now;
        pairing.UpdatedAt = now;
        db.Add(device);
        db.Add(new AuditLog { Action = "PrintBridgePaired", EntityName = nameof(BridgeDevice), EntityId = device.Id, NewValues = device.Name });
        await db.SaveChangesAsync(ct);
        return new(device.Id, device.Name, token);
    }

    public async Task<BridgeDevice?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var tokenHash = Hash(token.Trim());
        return await db.BridgeDevices.SingleOrDefaultAsync(x => x.TokenHash == tokenHash && x.Active && x.RevokedAt == null, ct);
    }

    public async Task<bool> HeartbeatAsync(BridgeDevice device, string? appVersion, CancellationToken ct)
    {
        device.LastSeenAt = DateTimeOffset.UtcNow;
        device.AppVersion = appVersion?.Trim() ?? device.AppVersion;
        device.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<BridgeJob?> ClaimAsync(BridgeDevice device, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var abandonedBefore = now.AddMinutes(-5);
        var leasedJobs = db.PrintJobs.Where(x => x.Status == PrintJobStatus.Sending);
        List<PrintJob> expiredJobs;
        if (db.Database.IsSqlite())
        {
            // SQLite cannot compare DateTimeOffset values in LINQ. Inspect only
            // lease metadata, keeping every pending TSPL payload out of the scan.
            var leases = await leasedJobs.Select(x => new { x.Id, x.LeaseExpiresAt, x.RetryCount, x.BridgeDeviceId }).ToListAsync(ct);
            var expiredIds = leases.Where(x => x.LeaseExpiresAt < now &&
                (x.RetryCount >= 3 || (x.BridgeDeviceId != device.Id && x.LeaseExpiresAt < abandonedBefore)))
                .Select(x => x.Id).ToArray();
            expiredJobs = expiredIds.Length == 0 ? [] : await leasedJobs.Where(x => expiredIds.Contains(x.Id)).ToListAsync(ct);
        }
        else
        {
            expiredJobs = await leasedJobs.Where(x => x.LeaseExpiresAt < now &&
                (x.RetryCount >= 3 || (x.BridgeDeviceId != device.Id && x.LeaseExpiresAt < abandonedBefore))).ToListAsync(ct);
        }
        foreach (var expired in expiredJobs)
        {
            expired.Status = PrintJobStatus.Failed;
            expired.CompletedAt = now;
            expired.LeaseExpiresAt = null;
            expired.FailureReason = "The Android Print Bridge did not acknowledge the job after three attempts.";
            expired.UpdatedAt = now;
        }
        if (expiredJobs.Count > 0) await db.SaveChangesAsync(ct);

        // Npgsql's retrying execution strategy requires user-created transactions
        // to execute as one retriable unit. Starting the transaction outside the
        // strategy causes every production bridge claim to fail with HTTP 500.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var supportedDrivers = string.Equals(device.Platform, "windows", StringComparison.OrdinalIgnoreCase)
                ? new[] { "TscTsplUsb", "EpsonEscPosUsb" }
                : new[] { "AndroidBridge" };
            var candidates = (
                from job in db.PrintJobs
                join printer in db.Printers on job.PrinterId equals printer.Id
                where supportedDrivers.Contains(printer.Driver) && printer.Enabled &&
                      (job.Status == PrintJobStatus.Queued ||
                       (job.Status == PrintJobStatus.Sending && job.BridgeDeviceId == device.Id && job.RetryCount < 3))
                select new { Job = job, Printer = printer, Label = job.Label! });
            if (db.Database.IsSqlite())
            {
                var pending = await candidates.Select(x => new { x.Job.Id, x.Job.Status, x.Job.LeaseExpiresAt, x.Job.RequestedAt }).ToListAsync(ct);
                var candidateId = pending.Where(x => x.Status == PrintJobStatus.Queued || x.LeaseExpiresAt < now)
                    .OrderBy(x => x.RequestedAt).ThenBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefault();
                candidates = candidates.Where(x => x.Job.Id == candidateId);
            }
            else
            {
                // Filter, order and limit in PostgreSQL. Previously every claim
                // downloaded and tracked the entire queue, including all payloads.
                candidates = candidates.Where(x => x.Job.Status == PrintJobStatus.Queued || x.Job.LeaseExpiresAt < now);
            }
            var candidate = db.Database.IsSqlite()
                ? await candidates.FirstOrDefaultAsync(ct)
                : await candidates.OrderBy(x => x.Job.RequestedAt).ThenBy(x => x.Job.Id).FirstOrDefaultAsync(ct);

            if (candidate is null)
            {
                await transaction.CommitAsync(ct);
                return null;
            }

            var retrying = candidate.Job.Status == PrintJobStatus.Sending;
            candidate.Job.Status = PrintJobStatus.Sending;
            candidate.Job.StartedAt ??= now;
            candidate.Job.BridgeDeviceId = device.Id;
            candidate.Job.LeaseExpiresAt = now.Add(LeaseLifetime);
            candidate.Job.RetryCount += retrying ? 1 : 0;
            candidate.Job.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new BridgeJob(
                candidate.Job.Id,
                candidate.Label.LabelCode,
                candidate.Label.ItemNameSnapshot,
                candidate.Printer.Name,
                candidate.Printer.IpAddress,
                candidate.Printer.TcpPort,
                candidate.Job.Payload ?? string.Empty,
                candidate.Job.LeaseExpiresAt.Value);
        });
    }

    public async Task<bool> CompleteAsync(BridgeDevice device, Guid jobId, bool success, string? error, CancellationToken ct)
    {
        var job = await db.PrintJobs.Include(x => x.Label).SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null || job.BridgeDeviceId != device.Id) return false;
        if (job.Status == PrintJobStatus.Printed) return true;
        if (job.Status != PrintJobStatus.Sending) return false;

        var now = DateTimeOffset.UtcNow;
        job.CompletedAt = now;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;
        job.FailureReason = success ? null : TrimError(error);
        job.Status = success ? PrintJobStatus.Printed : PrintJobStatus.Failed;
        if (success && job.Label is not null)
        {
            job.Label.SuccessfulPrintCount++;
            job.Label.LastSuccessfulPrintAt = now;
            job.Label.UpdatedAt = now;
        }
        db.Add(new AuditLog
        {
            Action = success ? "BridgeLabelPrinted" : "BridgeLabelPrintFailed",
            EntityName = nameof(PrintJob),
            EntityId = job.Id,
            NewValues = success ? device.Name : job.FailureReason
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevokeAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await db.BridgeDevices.FindAsync([deviceId], ct);
        if (device is null) return false;
        device.Active = false;
        device.RevokedAt = device.UpdatedAt = DateTimeOffset.UtcNow;
        db.Add(new AuditLog { Action = "PrintBridgeRevoked", EntityName = nameof(BridgeDevice), EntityId = device.Id, NewValues = device.Name });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BridgeDeviceSummary>> ListAsync(CancellationToken ct)
    {
        var onlineAfter = DateTimeOffset.UtcNow.AddMinutes(-1);
        var devices = await db.BridgeDevices.Where(x => x.Active).ToListAsync(ct);
        return devices.OrderByDescending(x => x.LastSeenAt).Select(x => new BridgeDeviceSummary(
            x.Id,
            x.Name,
            x.Active,
            x.LastSeenAt,
            x.AppVersion,
            x.Platform,
            x.Active && x.LastSeenAt >= onlineAfter,
            x.CreatedAt)).ToList();
    }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string TrimError(string? value) => string.IsNullOrWhiteSpace(value) ? "The printer rejected the job." : value.Trim()[..Math.Min(value.Trim().Length, 500)];
}
