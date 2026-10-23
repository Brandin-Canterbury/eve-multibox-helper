using EveMultibox.Config;
using EveMultibox.Shell;
using EveMultibox.Trade;

namespace EveMultibox.Tests;

public sealed class NotificationTests
{
    private static readonly CharacterOrder Order =
        new(1, 2629, 10000002, 60003760, "station", false, 199, 10_000, 6_000, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 90);

    private static readonly OrderEvent Sold = new(OrderEventKind.Sold, Order, 4_000, 796_000);
    private static readonly OrderEvent Ended = new(OrderEventKind.Ended, Order, 0, 0, State: "expired");
    private static readonly OrderEvent Wallet = new(OrderEventKind.WalletChanged, null, 0, 0, WalletChange: 796_000);

    private static IReadOnlyDictionary<string, string> Values(OrderEvent e) =>
        NotificationText.ValuesFor(e, "Pheo", _ => "Inferno Light Missile", _ => "Jita 4-4", 1_500_000_000);

    [Fact]
    public void ComposeUsesTheTemplateForEachSwitchedOnEvent()
    {
        var settings = new NotificationSettings { WalletChanges = true };
        var lines = NotificationService.Compose(new[] { Sold, Ended, Wallet }, settings, Values);

        Assert.Equal(new[]
        {
            "Pheo sold 4,000 × Inferno Light Missile for 796.0K ISK · 6,000 left",
            "Pheo's sell order for Inferno Light Missile expired",
            "Pheo's wallet +796.0K ISK → 1.50B ISK",
        }, lines);
    }

    [Fact]
    public void ComposeLeavesOutEventsThatAreSwitchedOff()
    {
        var settings = new NotificationSettings { OrderFills = false, OrderEnded = true, WalletChanges = false };
        var lines = NotificationService.Compose(new[] { Sold, Ended, Wallet }, settings, Values);
        Assert.Equal(new[] { "Pheo's sell order for Inferno Light Missile expired" }, lines);
    }

    [Fact]
    public void ComposeFollowsACustomTemplate()
    {
        var settings = new NotificationSettings { SoldTemplate = "💰 {item}: {qty} @ {price} ({station})" };
        var lines = NotificationService.Compose(new[] { Sold }, settings, Values);
        Assert.Equal("💰 Inferno Light Missile: 4,000 @ 199.00 (Jita 4-4)", Assert.Single(lines));
    }

    [Fact]
    public void ComposeSkipsABlankTemplate()
    {
        var settings = new NotificationSettings { SoldTemplate = "  " };
        Assert.Empty(NotificationService.Compose(new[] { Sold }, settings, Values));
    }

    [Theory]
    [InlineData("1500000", 1_500_000)]
    [InlineData("1,500,000", 1_500_000)]
    [InlineData("1.5M", 1_500_000)]
    [InlineData("250k", 250_000)]
    [InlineData(" 2b ", 2_000_000_000)]
    [InlineData("0", 0)]
    public void IskAmountReadsWhatPeopleType(string text, double expected)
    {
        Assert.True(IskAmount.TryParse(text, out double isk));
        Assert.Equal(expected, isk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("M")]
    public void IskAmountRejectsNonsense(string text) => Assert.False(IskAmount.TryParse(text, out _));
}
