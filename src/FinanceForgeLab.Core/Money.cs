namespace FinanceForgeLab.Core;

public static class Money
{
    public const long Scale = 10_000;

    public static long ToUnits(decimal value)
    {
        var scaled = checked(value * Scale);
        if (decimal.Truncate(scaled) != scaled)
            throw new ArgumentOutOfRangeException(nameof(value), "O valor exige mais de quatro casas decimais.");
        return checked((long)scaled);
    }

    public static decimal FromUnits(long units) => units / (decimal)Scale;
    public static long SignedUnits(decimal legacyValue, MovementKind kind) => kind switch
    {
        MovementKind.Income => ToUnits(legacyValue),
        MovementKind.Expense => checked(-ToUnits(legacyValue)),
        _ => throw new ArgumentException("Tipo legado desconhecido.", nameof(kind))
    };
}

public enum MovementKind { Income = 1, Expense = 2, Transfer = 3, Opening = 4, Adjustment = 5 }
