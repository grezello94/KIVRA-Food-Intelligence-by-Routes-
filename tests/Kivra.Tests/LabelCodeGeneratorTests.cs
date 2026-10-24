using Kivra.Application;
using Xunit;

namespace Kivra.Tests;

public sealed class LabelCodeGeneratorTests
{
    private static readonly DateTimeOffset LocalTime = new(2026, 10, 24, 14, 30, 0, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unset_namespace_preserves_daily_sequential_codes(string? codeNamespace)
    {
        var generator = new LabelCodeGenerator(codeNamespace);

        Assert.Equal("RL-261024-0001", generator.Next(LocalTime, 1));
        Assert.Equal("RL-261024-0041", generator.Next(LocalTime, 41));
        Assert.Equal("RL-261023-10000", generator.Next(LocalTime.AddDays(-1), 10000));
    }

    [Fact]
    public void Recovery_codes_stay_unique_when_history_count_and_time_are_reused()
    {
        var generator = new LabelCodeGenerator("R261024");
        var codes = Enumerable.Range(0, 1000).Select(_ => generator.Next(LocalTime, 1)).ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches("^R261024-261024-[0-9a-f]{32}$", code));
    }

    [Fact]
    public void Recovery_codes_do_not_reuse_codes_for_backdated_labels_or_another_namespace()
    {
        var recovery = new LabelCodeGenerator("R261024");
        var anotherRecovery = new LabelCodeGenerator("R261025");
        var backdated = LocalTime.AddDays(-30);
        var codes = new[]
        {
            recovery.Next(backdated, 1), recovery.Next(backdated, 1),
            recovery.Next(LocalTime, 1), anotherRecovery.Next(backdated, 1)
        };

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.StartsWith("R261024-260924-", codes[0]);
        Assert.StartsWith("R261024-261024-", codes[2]);
        Assert.StartsWith("R261025-260924-", codes[3]);
        Assert.DoesNotContain(new LabelCodeGenerator().Next(backdated, 1), codes);
    }

    [Fact]
    public void Longest_recovery_namespace_fits_existing_label_code_limits()
    {
        var code = new LabelCodeGenerator("RECOV123").Next(LocalTime, int.MaxValue);

        Assert.Equal(48, code.Length);
        Assert.Matches("^[A-Z0-9]+-[0-9]{6}-[0-9a-f]{32}$", code);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("lower")]
    [InlineData("RECOVERY1")]
    [InlineData("R-261024")]
    [InlineData("OFF")]
    [InlineData("R\r\nPRINT")]
    public void Invalid_namespace_is_rejected_before_generating_codes(string codeNamespace)
    {
        Assert.Throws<ArgumentException>(() => new LabelCodeGenerator(codeNamespace));
    }
}
