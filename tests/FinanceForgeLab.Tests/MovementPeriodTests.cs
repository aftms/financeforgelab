using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Components;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class MovementPeriodTests
{
    [Theory]
    [InlineData(2024, 2, 29)]
    [InlineData(2025, 2, 28)]
    [InlineData(2026, 10, 31)]
    [InlineData(9999, 12, 31)]
    public void DefaultsToTheCompleteCurrentMonth(int year, int month, int lastDay)
    {
        var period = new MovementPeriod(new(year, month, 16));
        Assert.Equal(MovementPeriodMode.Month, period.Mode);
        Assert.Equal(((DateOnly?)new DateOnly(year, month, 1), (DateOnly?)new DateOnly(year, month, lastDay)), period.Resolve());
    }

    [Fact] public void WholeYearAndWholeHistoryAreExplicitChoices()
    {
        var period = new MovementPeriod(new(2026, 10, 16)) { Year = 2019, Month = 0 };
        Assert.Equal(((DateOnly?)new DateOnly(2019, 1, 1), (DateOnly?)new DateOnly(2019, 12, 31)), period.Resolve());
        period.Mode = MovementPeriodMode.All;
        Assert.Equal(((DateOnly?)null, (DateOnly?)null), period.Resolve());
    }

    [Fact] public void DateRangesAllowOpenBoundsAndRejectInvalidPeriods()
    {
        var period = new MovementPeriod(new(2026, 10, 16)) { Mode = MovementPeriodMode.Dates, From = new(2025, 12, 30), To = new(2026, 1, 2) };
        Assert.Equal((period.From, period.To), period.Resolve());
        period.To = null; Assert.Equal((period.From, (DateOnly?)null), period.Resolve());
        period.To = period.From; period.From = null; Assert.Equal(((DateOnly?)null, period.To), period.Resolve());
        period.To = null; Assert.Throws<FinanceRuleException>(() => period.Resolve());
        period.From = new(2026, 10, 2); period.To = new(2026, 10, 1);
        Assert.Throws<FinanceRuleException>(() => period.Resolve());
        period.Mode = MovementPeriodMode.Month; period.Month = 13;
        Assert.Throws<FinanceRuleException>(() => period.Resolve());
    }
}
