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
        var leasedJobs = await db.PrintJobs
            .Where(x => x.Status == PrintJobStatus.Sending)
            .ToListAsync(ct);
        foreach (var expired in leasedJobs.Where(x =>
                     x.LeaseExpiresAt < now &&
                     (x.RetryCount >= 3 ||
                      (x.BridgeDeviceId != device.Id && x.LeaseExpiresAt < now.AddMinutes(-5)))))
        {
            expired.Status = PrintJobStatus.Failed;
            expired.CompletedAt = now;
            expired.LeaseExpiresAt = null;
            expired.FailureReason = "The Android Print Bridge did not acknowledge the job after three attempts.";
            expired.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var candidates = await (
            from job in db.PrintJobs
            join printer in db.Printers on job.PrinterId equals printer.Id
            where printer.Driver == "AndroidBridge" && printer.Enabled &&
                  (job.Status == PrintJobStatus.Queued || job.Status == PrintJobStatus.Sending)
            select new { Job = job, Printer = printer, Label = job.Label! })
            .ToListAsync(ct);
        var candidate = candidates
            .Where(x => x.Job.Status == PrintJobStatus.Queued ||
                        (x.Job.BridgeDeviceId == device.Id &&
                         x.Job.LeaseExpiresAt < now &&
                         x.Job.RetryCount < 3))
            .OrderBy(x => x.Job.RequestedAt)
            .FirstOrDefault();

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

        return new(
            candidate.Job.Id,
            candidate.Label.LabelCode,
            candidate.Label.ItemNameSnapshot,
            candidate.Printer.Name,
            candidate.Printer.IpAddress,
            candidate.Printer.TcpPort,
            candidate.Job.Payload ?? string.Empty,
            candidate.Job.LeaseExpiresAt.Value);
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
