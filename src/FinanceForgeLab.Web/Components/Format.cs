namespace FinanceForgeLab.Web.Components;
public static class Format
{
    public static string Money(long units) => Core.Money.FromUnits(units).ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("pt-PT"));
    public static string Kind(Core.MovementKind kind) => kind switch { Core.MovementKind.Income => "Receita", Core.MovementKind.Expense => "Despesa", Core.MovementKind.Transfer => "Transferência", Core.MovementKind.Opening => "Abertura", _ => "Ajuste" };
    public static string Month(int month) => new DateTime(2000, month, 1).ToString("MMM", System.Globalization.CultureInfo.GetCultureInfo("pt-PT"));
}
