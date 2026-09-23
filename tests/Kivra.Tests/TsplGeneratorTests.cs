using Kivra.Domain;
using Kivra.Infrastructure;
using Xunit;
namespace Kivra.Tests;
public sealed class TsplGeneratorTests { [Fact] public void Emits_50x30_tspl_with_critical_label_content() { var label=new Label { LabelCode="RL-260923-0001",ItemNameSnapshot="Chicken Pakora",CategorySnapshot="Prepared Food",ClassificationSnapshot=Classification.NonVeg,DateTerminologySnapshot="PREPARED",OperationalDateTime=new DateTimeOffset(2026,9,23,14,30,0,TimeSpan.FromHours(5.5)),ExpiryDateTime=new DateTimeOffset(2026,9,25,14,30,0,TimeSpan.FromHours(5.5)),ShelfLifeRuleSnapshot="2 Days",StorageLocationSnapshot="Chiller 2" }; var output=new TsplGenerator().Generate(label,new Printer { Name="Fake",Model="Fake",Driver="Fake" },TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")); Assert.Contains("SIZE 50 mm,30 mm",output); Assert.Contains("CHICKEN PAKORA",output); Assert.Contains("USE BY",output); Assert.Contains("PRINT 1,1",output); } }
