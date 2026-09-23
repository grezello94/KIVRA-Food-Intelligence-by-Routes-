using Kivra.Domain;
using Kivra.Infrastructure;
using Xunit;
namespace Kivra.Tests;
public sealed class TsplGeneratorTests {
 static readonly TimeZoneInfo Zone=TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
 static Label Label()=>new() { LabelCode="RL-260923-0001",ItemNameSnapshot="Chicken Pakora",CategorySnapshot="Prepared Food",ClassificationSnapshot=Classification.NonVeg,DateTerminologySnapshot="PREPARED",OperationalDateTime=new DateTimeOffset(2026,9,23,14,30,0,TimeSpan.FromHours(5.5)),ExpiryDateTime=new DateTimeOffset(2026,9,25,14,30,0,TimeSpan.FromHours(5.5)),ShelfLifeRuleSnapshot="2 Days",StorageLocationSnapshot="Chiller 2" };
 static Printer Printer(decimal width=50,decimal height=30,int dpi=203,string sensing="Gap",decimal gap=2,string method="DirectThermal")=>new(){Name="TSC",Model=dpi==203?"TSC TE210":"TSC TE310",Driver="Fake",Dpi=dpi,LabelWidthMm=width,LabelHeightMm=height,MediaSensingMode=sensing,GapMm=gap,PrintMethod=method};

 [Fact] public void Emits_50x30_tspl_with_critical_label_content() { var output=new TsplGenerator().Generate(Label(),Printer(),Zone);Assert.Contains("SIZE 50 mm,30 mm",output);Assert.Contains("CHICKEN PAKORA",output);Assert.Contains("NON-VEG",output);Assert.Contains("USE BY",output);Assert.Contains("PRINT 1,1",output); }

 [Theory]
 [InlineData(50,30,203)]
 [InlineData(50,25,203)]
 [InlineData(40,30,203)]
 [InlineData(50,30,300)]
 public void Scales_layout_to_stock_and_resolution(int width,int height,int dpi) {
  var output=new TsplGenerator().Generate(Label(),Printer(width,height,dpi),Zone);
  Assert.Contains($"SIZE {width} mm,{height} mm",output);
  var labelWidthDots=(int)Math.Round(width*(dpi/25.4m),MidpointRounding.AwayFromZero);
  var labelHeightDots=(int)Math.Round(height*(dpi/25.4m),MidpointRounding.AwayFromZero);
  foreach(var line in output.Split("\r\n").Where(x=>x.StartsWith("BAR "))){var values=line[4..].Split(',').Select(int.Parse).ToArray();Assert.True(values[0]+values[2]<=labelWidthDots,$"BAR exceeds width: {line}");Assert.True(values[1]+values[3]<=labelHeightDots,$"BAR exceeds height: {line}");}
  foreach(var line in output.Split("\r\n").Where(x=>x.StartsWith("TEXT "))){var coordinates=line[5..].Split(',',3).Take(2).Select(int.Parse).ToArray();Assert.InRange(coordinates[0],0,labelWidthDots-1);Assert.InRange(coordinates[1],0,labelHeightDots-1);}
  Assert.Contains(dpi==300?"\"4\",0,1,1":"\"3\",0,1,1",output);
 }

 [Theory]
 [InlineData("Gap",2,"GAP 2 mm,0 mm")]
 [InlineData("BlackMark",3,"BLINE 3 mm,0 mm")]
 [InlineData("Continuous",0,"GAP 0 mm,0 mm")]
 public void Emits_selected_media_sensing(string mode,int gap,string expected){var output=new TsplGenerator().Generate(Label(),Printer(sensing:mode,gap:gap),Zone);Assert.Contains(expected,output);}

 [Theory]
 [InlineData("DirectThermal","SET RIBBON OFF")]
 [InlineData("ThermalTransfer","SET RIBBON ON")]
 public void Emits_selected_print_method(string method,string expected){var output=new TsplGenerator().Generate(Label(),Printer(method:method),Zone);Assert.Contains(expected,output);}
}
