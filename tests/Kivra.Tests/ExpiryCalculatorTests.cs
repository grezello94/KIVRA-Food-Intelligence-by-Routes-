using Kivra.Application;
using Kivra.Domain;
using Xunit;
namespace Kivra.Tests;
public sealed class ExpiryCalculatorTests {
 [Fact] public void Two_days_from_prepared_time_preserves_local_clock_time() { var tz=TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); var start=new DateTimeOffset(2026,9,23,14,30,0,TimeSpan.FromHours(5.5)); var expiry=new ExpiryCalculator().Calculate(start,2,ShelfLifeUnit.Days,tz); Assert.Equal(new DateTimeOffset(2026,9,25,14,30,0,TimeSpan.FromHours(5.5)),expiry); }
 [Theory][InlineData(ShelfLifeUnit.Minutes, 90, 16, 0)][InlineData(ShelfLifeUnit.Hours, 4, 18, 30)] public void Sub_day_rules_calculate_correctly(ShelfLifeUnit unit,int amount,int hour,int minute) { var tz=TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); var start=new DateTimeOffset(2026,9,23,14,30,0,TimeSpan.FromHours(5.5)); var expiry=new ExpiryCalculator().Calculate(start,amount,unit,tz); Assert.Equal(hour,expiry.Hour); Assert.Equal(minute,expiry.Minute); }
 [Fact] public void Label_code_is_human_readable_and_daily_sequential() => Assert.Equal("RL-260923-0041",new LabelCodeGenerator().Next(new DateTimeOffset(2026,9,23,14,30,0,TimeSpan.FromHours(5.5)),41));
}
