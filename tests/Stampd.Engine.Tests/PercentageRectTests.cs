using Stampd.Core;

using Xunit;

namespace Stampd.Engine.Tests;

public sealed class PercentageRectTests
{
    [Fact]
    public void EnsureValid_AcceptsRectInsidePage()
    {
        var rect = new PercentageRect(X: 10, Y: 20, Width: 30, Height: 40);
        rect.EnsureValid(); // does not throw
    }

    [Fact]
    public void EnsureValid_AcceptsFullPage()
    {
        var rect = new PercentageRect(X: 0, Y: 0, Width: 100, Height: 100);
        rect.EnsureValid();
    }

    [Theory]
    [InlineData(-0.01, 0, 10, 10)]
    [InlineData(0, -0.01, 10, 10)]
    [InlineData(0, 0, -0.01, 10)]
    [InlineData(0, 0, 10, -0.01)]
    [InlineData(100.01, 0, 0, 0)]
    [InlineData(0, 100.01, 0, 0)]
    public void EnsureValid_RejectsOutOfRangeComponent(double x, double y, double w, double h)
    {
        var rect = new PercentageRect(x, y, w, h);
        Assert.Throws<ArgumentOutOfRangeException>(rect.EnsureValid);
    }

    [Theory]
    [InlineData(60, 0, 50, 10)]   // X + Width = 110 > 100
    [InlineData(0, 60, 10, 50)]   // Y + Height = 110 > 100
    public void EnsureValid_RejectsRectExtendingPastPageEdge(double x, double y, double w, double h)
    {
        var rect = new PercentageRect(x, y, w, h);
        Assert.Throws<ArgumentOutOfRangeException>(rect.EnsureValid);
    }
}
