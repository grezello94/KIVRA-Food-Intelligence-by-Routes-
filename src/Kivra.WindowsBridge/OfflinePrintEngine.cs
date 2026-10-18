using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kivra.Application;
using Kivra.Domain;
using Kivra.Infrastructure;
using FoodLabel = Kivra.Domain.Label;

namespace Kivra.WindowsBridge;

internal sealed record LocalPrinterSettings
{
    public string Driver { get; init; } = "TscTsplUsb";
    public bool Enabled { get; init; } = true;
    public int Dpi { get; init; } = 203;
    public decimal LabelWidthMm { get; init; } = 50;
    public decimal LabelHeightMm { get; init; } = 30;
    public string MediaSensingMode { get; init; } = "Gap";
    public decimal GapMm { get; init; } = 2;
    public string PrintMethod { get; init; } = "DirectThermal";
    public decimal PrintSpeedIps { get; init; } = 3;
    public int PrintDensity { get; init; } = 8;

    internal Printer ToPrinter() => new()
    {
        Name = "Local USB printer", Model = "TSC", Driver = Driver, Enabled = Enabled,
        Dpi = Dpi, LabelWidthMm = LabelWidthMm, LabelHeightMm = LabelHeightMm,
        MediaSensingMode = MediaSensingMode, GapMm = GapMm, PrintMethod = PrintMethod,
        PrintSpeedIps = PrintSpeedIps, PrintDensity = PrintDensity
    };
}

internal sealed record OfflinePrintRequest(
    string IdempotencyKey, Guid LabelId, string LabelCode, Guid ItemId, Guid PrinterId,
    Guid? StorageLocationId, DateTimeOffset OperationalDateTime,
    string ItemNameSnapshot, string CategorySnapshot, Classification ClassificationSnapshot,
    string DateTerminologySnapshot, int ShelfLifeValue, ShelfLifeUnit ShelfLifeUnit,
    string StorageLocationSnapshot, string RestaurantTimeZoneId, LocalPrinterSettings Printer,
    decimal? Quantity = null, string? Unit = null, bool IsReprint = false, DateTimeOffset? ExpiryDateTime = null);

internal sealed record OfflinePrintReceipt(
    OfflinePrintRequest Request, string RequestHash, Guid PrintJobId, string Status,
    DateTimeOffset OperationalDateTime, DateTimeOffset ExpiryDateTime,
    DateTimeOffset? PrintedAt, string Payload, string? FailureReason);

internal sealed record LocalPrintResult(
    string IdempotencyKey, Guid LabelId, string LabelCode, Guid PrintJobId,
    string Status, string? FailureReason, bool Idempotent, OfflinePrintReceipt Receipt);

internal sealed class LocalPrintConflictException(string message) : Exception(message);

// Receipts live on the printer PC, independently of browser storage. Persisting
// Sending before touching the spooler makes a lost response/restart safe to retry.
internal sealed class OfflinePrintEngine
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    readonly string jobsDirectory;
    readonly Func<string> queueName;
    readonly Func<string, byte[], string?> send;
    readonly SemaphoreSlim printGate;
    readonly TimeProvider clock;
    readonly TsplGenerator generator = new();

    internal OfflinePrintEngine(string dataDirectory, Func<string> queueName,
        Func<string, byte[], string?> send, SemaphoreSlim printGate, TimeProvider? clock = null)
    {
        jobsDirectory = Path.Combine(dataDirectory, "offline-jobs");
        Directory.CreateDirectory(jobsDirectory);
        this.queueName = queueName;
        this.send = send;
        this.printGate = printGate;
        this.clock = clock ?? TimeProvider.System;
    }

    internal async Task<LocalPrintResult> PrintAsync(OfflinePrintRequest request, CancellationToken token)
    {
        Validate(request);
        var hash = Hash(JsonSerializer.Serialize(request, JsonOptions));
        await printGate.WaitAsync(token);
        try
        {
            var prior = Read(request.IdempotencyKey);
            if (prior is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(prior.RequestHash), Convert.FromHexString(hash)))
                    throw new LocalPrintConflictException("This print key already belongs to a different label request.");
                return Result(Recover(prior), true);
            }
            var zone = TimeZoneInfo.FindSystemTimeZoneById(request.RestaurantTimeZoneId);
            var operational = (request.IsReprint ? request.OperationalDateTime : RestaurantOperationalDay.Resolve(request.OperationalDateTime, zone)).ToUniversalTime();
            var expiry = new ExpiryCalculator().Calculate(operational, request.ShelfLifeValue, request.ShelfLifeUnit, zone).ToUniversalTime();
            if (request.IsReprint && request.ExpiryDateTime != expiry)
                throw new ArgumentException("The original label expiry does not match its saved shelf-life rule.");
            var label = new FoodLabel
            {
                Id = request.LabelId, LabelCode = request.LabelCode, ItemId = request.ItemId,
                PrinterId = request.PrinterId, ItemNameSnapshot = request.ItemNameSnapshot,
                CategorySnapshot = request.CategorySnapshot, ClassificationSnapshot = request.ClassificationSnapshot,
                DateTerminologySnapshot = request.DateTerminologySnapshot,
                OperationalDateTime = operational, ExpiryDateTime = expiry,
                ShelfLifeRuleSnapshot = $"{request.ShelfLifeValue} {request.ShelfLifeUnit}",
                StorageLocationId = request.StorageLocationId, StorageLocationSnapshot = request.StorageLocationSnapshot,
                Quantity = request.Quantity, Unit = request.Unit
            };
            var payload = generator.Generate(label, request.Printer.ToPrinter(), zone);
            var receipt = new OfflinePrintReceipt(request, hash, Guid.NewGuid(), "Sending", operational, expiry, null, payload, null);
            Save(receipt);
            // The selected Windows queue comes only from the installed bridge's
            // configuration. Browser-supplied queue names are never accepted.
            try
            {
                var error = await Task.Run(() => send(queueName(), Encoding.ASCII.GetBytes(payload)));
                receipt = receipt with
                {
                    Status = error is null ? "Printed" : "Failed",
                    PrintedAt = error is null ? clock.GetUtcNow() : null,
                    FailureReason = error
                };
            }
            catch (Exception ex)
            {
                receipt = receipt with { Status = "Uncertain", FailureReason = $"The printer response was interrupted. Check the label before printing again. {ex.Message}" };
            }
            try { Save(receipt); }
            catch (IOException)
            {
                // The durable Sending receipt remains. Never tell the browser to
                // retry as a new print if the final receipt could not be saved.
                receipt = receipt with { Status = "Uncertain", FailureReason = "Could not save the printer receipt. Check the label before printing again." };
            }
            return Result(receipt, false);
        }
        finally { printGate.Release(); }
    }

    internal async Task<LocalPrintResult?> GetAsync(string key, CancellationToken token)
    {
        ValidateKey(key);
        await printGate.WaitAsync(token);
        try
        {
            var receipt = Read(key);
            return receipt is null ? null : Result(Recover(receipt), true);
        }
        finally { printGate.Release(); }
    }

    OfflinePrintReceipt Recover(OfflinePrintReceipt receipt)
    {
        if (receipt.Status != "Sending") return receipt;
        receipt = receipt with { Status = "Uncertain", FailureReason = "The bridge stopped before confirming this print. Check the physical label; this request will not print again automatically." };
        Save(receipt);
        return receipt;
    }

    static LocalPrintResult Result(OfflinePrintReceipt receipt, bool idempotent) => new(
        receipt.Request.IdempotencyKey, receipt.Request.LabelId, receipt.Request.LabelCode,
        receipt.PrintJobId, receipt.Status, receipt.FailureReason, idempotent, receipt);

    string JobPath(string key) => Path.Combine(jobsDirectory, Hash(key) + ".json");
    OfflinePrintReceipt? Read(string key)
    {
        var path = JobPath(key);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<OfflinePrintReceipt>(File.ReadAllText(path), JsonOptions)
            ?? throw new IOException("The saved printer receipt cannot be read. Check the label before printing again.");
    }

    void Save(OfflinePrintReceipt receipt) => WriteDurable(JobPath(receipt.Request.IdempotencyKey), JsonSerializer.Serialize(receipt, JsonOptions));

    internal static void WriteDurable(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key != key.Trim()) throw new ArgumentException("A print key of at most 128 characters without surrounding whitespace is required.");
    }
    static void Validate(OfflinePrintRequest request)
    {
        ValidateKey(request.IdempotencyKey);
        if (request.LabelId == Guid.Empty || request.ItemId == Guid.Empty || request.PrinterId == Guid.Empty || request.StorageLocationId is null || request.StorageLocationId == Guid.Empty)
            throw new ArgumentException("A label, item, printer and storage location are required.");
        if (request.IsReprint)
        {
            if (string.IsNullOrEmpty(request.LabelCode) || request.LabelCode.Length > 64 || request.LabelCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new ArgumentException("The original label code must contain at most 64 letters, digits or hyphens.");
            if (request.ExpiryDateTime is null) throw new ArgumentException("The original label expiry is required for a reprint.");
        }
        else if (!string.Equals(request.LabelCode, "OFF-" + request.LabelId.ToString("N")[..12], StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The offline label code must match its label ID.");
        foreach (var value in new[] { request.ItemNameSnapshot, request.CategorySnapshot, request.StorageLocationSnapshot })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl)) throw new ArgumentException("Label snapshot fields must contain 1 to 200 characters without control characters.");
        if (string.IsNullOrWhiteSpace(request.DateTerminologySnapshot) || request.DateTerminologySnapshot.Length > 64 || request.DateTerminologySnapshot.Any(char.IsControl))
            throw new ArgumentException("The date terminology must contain 1 to 64 characters without control characters.");
        if (!Enum.IsDefined(request.ClassificationSnapshot) || !Enum.IsDefined(request.ShelfLifeUnit) || request.ShelfLifeValue < 1 || request.ShelfLifeValue > 100000)
            throw new ArgumentException("A valid food classification and shelf-life rule are required.");
        if (request.OperationalDateTime == default || string.IsNullOrWhiteSpace(request.RestaurantTimeZoneId)) throw new ArgumentException("The actual preparation time and restaurant time zone are required.");
        var printer = request.Printer ?? throw new ArgumentException("Printer settings are required.");
        if (printer.Driver != "TscTsplUsb" || !printer.Enabled) throw new ArgumentException("Local offline printing requires an enabled TSC USB printer.");
        if (printer.Dpi is not (203 or 300) || printer.LabelWidthMm < 20 || printer.LabelWidthMm > (printer.Dpi == 300 ? 105.7m : 108m) || printer.LabelHeightMm is < 10 or > 1000)
            throw new ArgumentException("The printer resolution or label size is invalid.");
        if (printer.MediaSensingMode is not ("Gap" or "BlackMark" or "Continuous") || printer.GapMm is < 0 or > 100 || (printer.MediaSensingMode != "Continuous" && printer.GapMm == 0))
            throw new ArgumentException("The printer media settings are invalid.");
        if (printer.PrintMethod is not ("DirectThermal" or "ThermalTransfer") || printer.PrintSpeedIps < 1 || printer.PrintSpeedIps > (printer.Dpi == 300 ? 5 : 6) || printer.PrintDensity is < 0 or > 15)
            throw new ArgumentException("The printer speed, density or print method is invalid.");
    }
}
