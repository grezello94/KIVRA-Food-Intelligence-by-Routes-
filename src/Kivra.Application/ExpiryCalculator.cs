using Kivra.Domain;
namespace Kivra.Application;
public interface IExpiryCalculator { DateTimeOffset Calculate(DateTimeOffset operationalTime, int value, ShelfLifeUnit unit, TimeZoneInfo timeZone); }
public static class RestaurantOperationalDay {
 public const int RolloverHour = 2;
 public static DateTimeOffset Resolve(DateTimeOffset actualTime, TimeZoneInfo timeZone) {
  var local = TimeZoneInfo.ConvertTime(actualTime, timeZone);
  return local.Hour < RolloverHour ? local.AddDays(-1) : local;
 }
}
public sealed class ExpiryCalculator : IExpiryCalculator {
 public DateTimeOffset Calculate(DateTimeOffset operationalTime, int value, ShelfLifeUnit unit, TimeZoneInfo timeZone) {
  if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
  var local = TimeZoneInfo.ConvertTime(operationalTime, timeZone);
  return unit switch { ShelfLifeUnit.Minutes => local.AddMinutes(value), ShelfLifeUnit.Hours => local.AddHours(value), ShelfLifeUnit.Days => local.AddDays(value), ShelfLifeUnit.Weeks => local.AddDays(value * 7), ShelfLifeUnit.Months => local.AddMonths(value), _ => throw new ArgumentOutOfRangeException(nameof(unit)) };
 }
}
public sealed class LabelCodeGenerator : ILabelCodeGenerator {
 private readonly string? codeNamespace;

 public LabelCodeGenerator(string? codeNamespace = null) {
  if (!string.IsNullOrEmpty(codeNamespace) &&
      (codeNamespace == "OFF" || codeNamespace.Length > 8 || codeNamespace.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9'))))
   throw new ArgumentException("LabelCodes:Namespace must contain 1 to 8 uppercase letters or digits and cannot be the reserved prefix OFF.", nameof(codeNamespace));
  this.codeNamespace = string.IsNullOrEmpty(codeNamespace) ? null : codeNamespace;
 }

 public string Next(DateTimeOffset localTime, int sequence) => codeNamespace is null
  ? $"RL-{localTime:yyMMdd}-{sequence:D4}"
  // A partial recovery may lack prior labels, so its codes must not depend on a history count.
  : $"{codeNamespace}-{localTime:yyMMdd}-{Guid.NewGuid():N}";
}
