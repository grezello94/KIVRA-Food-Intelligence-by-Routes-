using Kivra.WindowsBridge;
using Xunit;

namespace Kivra.Tests;

public sealed class PrinterQueueSelectionTests
{
    [Theory]
    [InlineData("TSC TA220", true)]
    [InlineData("ta220", true)]
    [InlineData("Kitchen TE210", true)]
    [InlineData("TTP-244", true)]
    [InlineData("Microsoft Print to PDF", false)]
    [InlineData("Epson Receipt", false)]
    [InlineData("NOTSC Office", false)]
    public void Detects_supported_label_printer_names(string name, bool expected) =>
        Assert.Equal(expected, PrinterQueueSelection.IsCompatible(name));

    [Theory]
    [InlineData("Kitchen Printer", "TSC TA220", true)]
    [InlineData("Kitchen Printer", "TSC TE210", true)]
    [InlineData("Kitchen Printer", "Seagull TTP-244 Pro", true)]
    [InlineData("Kitchen Printer", "EPSON TM-T82", false)]
    [InlineData("Kitchen Printer", "Microsoft Print To PDF", false)]
    [InlineData("TSC Kitchen", "EPSON TM-T82", false)]
    [InlineData("TSC Preview", "Microsoft Print To PDF", false)]
    public void Detects_renamed_printers_using_the_installed_driver(string queue, string driver, bool expected) =>
        Assert.Equal(expected, PrinterQueueSelection.IsCompatible(queue, driver));

    [Fact]
    public void Fresh_install_auto_selects_a_renamed_tsc_queue_from_driver_metadata() =>
        Assert.Equal("Kitchen Printer", PrinterQueueSelection.SelectDefault(null,
            ["Kitchen Printer", "Microsoft Print to PDF"],
            new Dictionary<string, string> { ["Kitchen Printer"] = "TSC TA220", ["Microsoft Print to PDF"] = "Microsoft Print To PDF" }));

    [Fact]
    public void Fresh_install_selects_the_single_supported_queue_without_cloud_credentials() =>
        Assert.Equal("TSC TA220", PrinterQueueSelection.SelectDefault(null, ["Microsoft Print to PDF", "TSC TA220"]));

    [Fact]
    public void Retains_the_saved_installed_queue_when_other_printers_are_added() =>
        Assert.Equal("TSC Kitchen", PrinterQueueSelection.SelectDefault("tsc kitchen", ["TSC Prep", "TSC Kitchen"]));

    [Fact]
    public void Renamed_or_reconnected_single_printer_replaces_an_unavailable_queue() =>
        Assert.Equal("TSC TA220 (Copy 1)", PrinterQueueSelection.SelectDefault("TSC TA220", ["TSC TA220 (Copy 1)"]));

    [Fact]
    public void Multiple_supported_queues_require_an_app_choice() =>
        Assert.Equal("", PrinterQueueSelection.SelectDefault(null, ["TSC Kitchen", "TSC Prep"]));

    [Fact]
    public void An_unavailable_saved_queue_is_retained_until_a_safe_replacement_exists() =>
        Assert.Equal("TSC Kitchen", PrinterQueueSelection.SelectDefault("TSC Kitchen", ["Microsoft Print to PDF"]));

    [Fact]
    public void Existing_non_tsc_cloud_queue_is_preserved_without_auto_selecting_it_for_new_installs()
    {
        Assert.Equal("Epson Receipt", PrinterQueueSelection.SelectDefault("Epson Receipt", ["Epson Receipt", "TSC Kitchen"]));
        Assert.Equal("", PrinterQueueSelection.SelectDefault(null, ["Epson Receipt"]));
    }
}
