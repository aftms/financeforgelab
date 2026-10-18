using System.Text;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Services;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class PurchaseReferenceTests
{
    private static readonly List<Category> Categories = [
        new() { Id = 1, Name = "Livros e imprensa", Kind = MovementKind.Expense },
        new() { Id = 2, Name = "Equipamentos eletrónicos", Kind = MovementKind.Expense }
    ];
    [Fact] public void ListPreservesDecimalPrecisionQuotedDescriptionsAndOrderReferences()
    {
        var rows = PurchaseReferenceParser.Parse("Data;Montante;Descrição;Encomenda\n02/07/2026;29,9001;\"Livro; cozinha\";123-1234567-1234567\n2026-07-03;100.20;Teclado", "Amazon", 4, Categories);
        Assert.Equal(2, rows.Count); Assert.Equal(29.9001m, rows[0].Amount); Assert.Equal(new(2026, 7, 2), rows[0].PurchaseDate);
        Assert.Equal(1, rows[0].CategoryId); Assert.Contains("Livro; cozinha", rows[0].Description); Assert.Contains("123-1234567-1234567", rows[0].Description);
        Assert.Equal("", rows[0].Code); Assert.Equal(2, rows[1].CategoryId); Assert.All(rows, row => Assert.True(row.IsPurchase));
    }
    [Fact] public void ImageTextProducesProposalsWithoutGuessingAmbiguousAmountsOrCategories()
    {
        var rows = PurchaseReferenceParser.Parse("AMAZON\n2 de julho de 2026\n29,90 EUR\nLivro de cozinha\n3 July 2026\n10,00 EUR\n20,00 EUR\nLivro e teclado", "Amazon", 4, Categories);
        Assert.Equal(2, rows.Count); Assert.Equal(new(2026, 7, 2), rows[0].PurchaseDate); Assert.Equal(29.90m, rows[0].Amount); Assert.Equal(1, rows[0].CategoryId);
        Assert.Null(rows[1].Amount); Assert.Equal(0, rows[1].CategoryId);
    }
    [Fact] public async Task TextFilesWorkAndUnsupportedImagesAreRejected()
    {
        Assert.Equal("Lista de compras", await PurchaseEvidenceReader.ReadAsync("list.txt", Encoding.UTF8.GetBytes("Lista de compras")));
        await Assert.ThrowsAsync<FinanceRuleException>(() => PurchaseEvidenceReader.ReadAsync("list.png", Encoding.UTF8.GetBytes("not an image")));
        await Assert.ThrowsAsync<FinanceRuleException>(() => PurchaseEvidenceReader.ReadAsync("list.svg", Encoding.UTF8.GetBytes("<svg></svg>")));
    }
}
