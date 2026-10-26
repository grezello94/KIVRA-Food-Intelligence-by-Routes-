using System.Text.RegularExpressions;

namespace Kivra.WindowsBridge;

internal static class PrinterQueueSelection
{
    static readonly Regex SupportedName = new(@"\bTSC\b|\b(?:TA|TE|TC|DA|TTP|TX|TDP)[ -]?\d{2,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex UnsupportedDriver = new(@"\b(?:EPSON|PDF|XPS|ONENOTE|FAX)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsCompatible(string queueName, string? driverName = null)
    {
        if (!string.IsNullOrWhiteSpace(driverName))
        {
            if (UnsupportedDriver.IsMatch(driverName)) return false;
            if (SupportedName.IsMatch(driverName)) return true;
        }
        return SupportedName.IsMatch(queueName);
    }

    public static string SelectDefault(string? savedQueue, IReadOnlyList<string> installedQueues, IReadOnlyDictionary<string, string>? driverNames = null)
    {
        var saved = installedQueues.FirstOrDefault(name => name.Equals(savedQueue, StringComparison.OrdinalIgnoreCase));
        if (saved is not null) return saved;
        var compatible = installedQueues.Where(name => IsCompatible(name, driverNames?.GetValueOrDefault(name))).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        // One supported queue is safe to select automatically. Multiple label
        // printers need an explicit choice in the app to avoid the wrong device.
        return compatible.Length == 1 ? compatible[0] : savedQueue ?? "";
    }
}
