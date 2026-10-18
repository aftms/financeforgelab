using System.Net;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class ExpenseCategoryChartTests : IAsyncLifetime
{
    readonly string folder=Path.Combine(Path.GetTempPath(),"ffl-category-chart-"+Guid.NewGuid().ToString("N"));
    FinanceDb Open()=>FinanceDb.Open(Path.Combine(folder,"test.sqlite"));
    FinanceService Service=>new(Open);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);await using var db=Open();await db.Database.MigrateAsync();
        db.Add(new Household{Id=1});db.AddRange(new Account{Id=1,HouseholdId=1,Name="Banco"},new Account{Id=2,HouseholdId=1,Name="Poupança"});
        db.AddRange(new Category{Id=1,HouseholdId=1,Name="Casa",Kind=MovementKind.Expense},new Category{Id=2,HouseholdId=1,Name="Transportes",Kind=MovementKind.Expense},new Category{Id=3,HouseholdId=1,Name="Salário",Kind=MovementKind.Income});
        await db.SaveChangesAsync();
        db.AddRange(new Category{Id=11,HouseholdId=1,ParentId=1,Name="Condomínio",Kind=MovementKind.Expense},new Category{Id=12,HouseholdId=1,ParentId=1,Name="Limpeza",Kind=MovementKind.Expense},new Category{Id=21,HouseholdId=1,ParentId=2,Name="Condomínio",Kind=MovementKind.Expense});
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);return Task.CompletedTask;}
    Task<long> Expense(long category,decimal amount,string description,int year=2026)=>Service.SaveMovementAsync(new(){Date=new(year,5,15),Kind=MovementKind.Expense,AccountId=1,CategoryId=category,Amount=amount,Description=description});
    [Fact]public async Task ChildTotalsKeepParentTotalsAndExactFiltersAcrossRefundsYearsAndArchivedClassifications()
    {
        await Expense(11,50,"Quota");await Expense(12,30,"Limpeza");await Expense(1,5,"Casa sem subcategoria");await Expense(11,-10,"Devolução");await Expense(21,7,"Outra categoria");await Expense(11,99,"Ano anterior",2025);
        await Service.SaveMovementAsync(new(){Date=new(2026,5,15),Kind=MovementKind.Income,AccountId=1,CategoryId=3,Amount=100,Description="Receita"});
        await Service.SaveMovementAsync(new(){Date=new(2026,5,15),Kind=MovementKind.Transfer,AccountId=1,DestinationAccountId=2,Amount=20,Description="Transferência"});
        var voided=await Expense(11,40,"Anulado");await Service.VoidMovementAsync(voided,1,Guid.NewGuid());
        await using(var db=Open()){(await db.Categories.SingleAsync(c=>c.Id==12)).Active=false;await db.SaveChangesAsync();}
        var summary=await Service.DashboardAsync(2026);Assert.Equal(820000,summary.ExpenseUnits);
        var home=summary.Expenses.Single(c=>c.CategoryId==1);Assert.Equal(750000,home.Units);Assert.Equal(50000,home.DirectUnits);Assert.Equal(2,home.Subcategories.Count);
        Assert.Equal(400000,home.Subcategories.Single(c=>c.CategoryId==11).Units);Assert.Equal(300000,home.Subcategories.Single(c=>c.CategoryId==12).Units);
        foreach(var parent in summary.Expenses)
        {
            Assert.Equal(parent.Units,parent.DirectUnits+parent.Subcategories.Sum(c=>c.Units));
            foreach(var child in parent.Subcategories)
            {
                var filtered=await Service.MovementsAsync(new(){CategoryId=child.CategoryId,Kind=MovementKind.Expense,From=new(2026,1,1),To=new(2026,12,31)});
                Assert.All(filtered.Rows,r=>Assert.Equal(child.CategoryId,r.CategoryId));Assert.Equal(child.Units,-filtered.Rows.Sum(r=>r.Units));
            }
        }
        Assert.Equal(990000,(await Service.DashboardAsync(2025)).Expenses.Single().Subcategories.Single().Units);
    }
    [Fact]public async Task EveryParentIsCollapsedAndChildLinksRetainYearKindAndSpecificCategory()
    {
        var categories=new List<CategorySummary>{new("Casa",1000000){CategoryId=1,DirectUnits=100000,Subcategories=[new("Condomínio",600000){CategoryId=11},new("Limpeza",300000){CategoryId=12}]},new("Transportes",70000){CategoryId=2,Subcategories=[new("Condomínio",70000){CategoryId=21}]}};
        await using var services=new ServiceCollection().AddLogging().BuildServiceProvider();await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(int year)=>await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ExpenseCategoryChart>(ParameterView.FromDictionary(new Dictionary<string,object?>{{nameof(ExpenseCategoryChart.Categories),categories},{nameof(ExpenseCategoryChart.Year),year}}))).ToHtmlString()));
        var html=await Render(2026);Assert.Equal(2,Regex.Matches(html,"<details class=\"expense-category\"").Count);Assert.DoesNotMatch("<details[^>]*\\sopen",html);
        foreach(var id in new[]{11,12,21})Assert.Contains($"href=\"/movimentos?ano=2026&tipo=Expense&categoria={id}\"",html);
        Assert.Contains("Consultar movimentos de Casa / Condomínio em 2026",html);Assert.Contains("Consultar movimentos de Transportes / Condomínio em 2026",html);
        Assert.Contains("Ver todos os movimentos de Casa",html);Assert.Contains("Sem subcategoria",html);Assert.Contains("width:60%",html);
        var old=await Render(2025);Assert.Contains("ano=2025&tipo=Expense&categoria=11",old);Assert.DoesNotContain("ano=2026",old);
    }
    [Fact]public async Task EmptyAndRefundOnlyGroupsDoNotProduceInvalidBarWidths()
    {
        await using var services=new ServiceCollection().AddLogging().BuildServiceProvider();await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ExpenseCategoryChart>(ParameterView.FromDictionary(new Dictionary<string,object?>{
            {nameof(ExpenseCategoryChart.Year),2026},{nameof(ExpenseCategoryChart.Categories),new List<CategorySummary>{new("Casa",-200000){CategoryId=1,Subcategories=[new("Devolução",-200000){CategoryId=11}]}}}}))).ToHtmlString()));
        Assert.Contains("category-negative",html);Assert.Contains("width:100%",html);Assert.DoesNotContain("width:-",html);
        var empty=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ExpenseCategoryChart>()).ToHtmlString()));
        Assert.Contains("Ainda não há despesas neste período",empty);Assert.DoesNotContain("<details",empty);
    }
}
