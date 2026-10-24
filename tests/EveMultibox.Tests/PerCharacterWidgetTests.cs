using EveMultibox.Config;
using EveMultibox.Trade;
using EveMultibox.Widget;

namespace EveMultibox.Tests;

public sealed class PerCharacterWidgetTests
{
    private const long Kayla = 1, Pheonix = 2;

    private static TradeRun Run(double cargo) => new(cargo, 0, 1e9, _ => false);

    [Fact]
    public void EachCharacterKeepsTheirOwnRun()
    {
        var runs = new TradeRuns();
        var kayla = Run(435);
        var pheonix = Run(7_300);
        runs.Start(Kayla, kayla);
        runs.Start(Pheonix, pheonix);
        Assert.Same(pheonix, runs.Current);

        Assert.True(runs.Switch(Kayla));
        Assert.Same(kayla, runs.Current);
        Assert.Equal(Kayla, runs.CurrentFor);
    }

    [Fact]
    public void SwitchingToSomeoneWithoutARunLeavesTheCurrentOne()
    {
        var runs = new TradeRuns();
        var kayla = Run(435);
        runs.Start(Kayla, kayla);
        Assert.False(runs.Switch(Pheonix));
        Assert.Same(kayla, runs.Current);
    }

    [Fact]
    public void ResetForgetsEveryRun()
    {
        var runs = new TradeRuns();
        runs.Start(Kayla, Run(435));
        runs.Start(Pheonix, Run(7_300));
        runs.Reset();
        Assert.Null(runs.Current);
        Assert.Null(runs.For(Kayla));
        Assert.False(runs.AnyItems);
    }

    [Fact]
    public void AWidgetOpensAtItsCharactersSavedSpot()
    {
        var settings = new WidgetSettings { Positions = { [Kayla] = new WindowSpot { X = 100, Y = 200 } } };
        var spot = WidgetPlacement.For(settings, Kayla, new[] { new WindowSpot { X = 100, Y = 200 } }, 340, 3440, 0);
        Assert.Equal((100.0, 200.0), (spot.X, spot.Y));
    }

    [Fact]
    public void ANewCharactersWidgetStartsAtTheDefaultSpot()
    {
        var spot = WidgetPlacement.For(new WidgetSettings(), Pheonix, Array.Empty<WindowSpot>(), 340, 2000, 0);
        Assert.Equal((2000 - 340 - 80.0, 120.0), (spot.X, spot.Y));
    }

    [Fact]
    public void ANewWidgetCascadesPastOnesAlreadyOpen()
    {
        var settings = new WidgetSettings { Position = new WindowSpot { X = 1000, Y = 100 } };
        var open = new[] { new WindowSpot { X = 1000, Y = 100 }, new WindowSpot { X = 1000 - WidgetPlacement.Step, Y = 100 + WidgetPlacement.Step } };
        var spot = WidgetPlacement.For(settings, Pheonix, open, 340, 3440, 0);
        Assert.Equal((1000 - 2 * WidgetPlacement.Step, 100 + 2 * WidgetPlacement.Step), (spot.X, spot.Y));
    }
}
