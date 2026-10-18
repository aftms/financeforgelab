using FinanceForgeLab.Infrastructure;

namespace FinanceForgeLab.Web.Components;

public enum MovementPeriodMode { Month, Dates, All }

public sealed class MovementPeriod(DateOnly today)
{
    public MovementPeriodMode Mode { get; set; } = MovementPeriodMode.Month;
    public int Year { get; set; } = today.Year;
    public int Month { get; set; } = today.Month; // Zero selects the whole year.
    public DateOnly? From { get; set; } = new(today.Year, today.Month, 1);
    public DateOnly? To { get; set; } = new(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

    public static DateOnly TodayInLisbon => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon")));

    public (DateOnly? From, DateOnly? To) Resolve()
    {
        if (Mode == MovementPeriodMode.All) return (null, null);
        if (Mode == MovementPeriodMode.Dates)
        {
            if (From is null && To is null) throw new FinanceRuleException("Indica a data inicial ou a data final do período.");
            if (From > To) throw new FinanceRuleException("A data inicial deve ser anterior ou igual à data final.");
            return (From, To);
        }
        if (Mode != MovementPeriodMode.Month || Year is < 1 or > 9999 || Month is < 0 or > 12)
            throw new FinanceRuleException("Escolhe um ano e um mês válidos.");
        return Month == 0 ? (new(Year, 1, 1), new(Year, 12, 31))
            : (new(Year, Month, 1), new(Year, Month, DateTime.DaysInMonth(Year, Month)));
    }
}
