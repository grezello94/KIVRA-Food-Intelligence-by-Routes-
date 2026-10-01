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
public sealed class LabelCodeGenerator : ILabelCodeGenerator { public string Next(DateTimeOffset localTime, int sequence) => $"RL-{localTime:yyMMdd}-{sequence:D4}"; }
