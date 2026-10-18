using System.Net;
using System.Reflection;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Components;
using FinanceForgeLab.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class MovementsPageTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ffl-period-" + Guid.NewGuid().ToString("N"));
    private FinanceDb Open() => FinanceDb.Open(Path.Combine(folder, "test.sqlite"));
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        await using var db = Open(); await db.Database.MigrateAsync();
        var household = new Household(); db.Add(household); await db.SaveChangesAsync();
        var account = new Account { HouseholdId = household.Id, Name = "Conta de teste" };
        var category = new Category { HouseholdId = household.Id, Name = "Despesas", Kind = MovementKind.Expense };
        db.AddRange(account, category); await db.SaveChangesAsync();
        var dates = Enumerable.Repeat(new DateOnly(2026, 10, 16), 41)
            .Concat([new(2026, 10, 1), new(2026, 10, 31), new(2026, 9, 30), new(2026, 11, 1), new(2019, 12, 5)]).ToList();
        foreach (var date in dates)
        {
            var movement = new FinancialTransaction { HouseholdId = household.Id, Date = date, Kind = MovementKind.Expense, Description = $"Registo de {date:yyyy-MM-dd}" };
            db.Add(movement); await db.SaveChangesAsync();
            db.Add(new Posting { TransactionId = movement.Id, Date = date, AccountId = account.Id, CategoryId = category.Id, AmountUnits = -100_000 });
        }
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); return Task.CompletedTask; }

    [Fact] public async Task InitialListAndCsvIncludeOnlyTheCurrentMonthWithInclusiveBounds()
    {
        await WithPage(async (page, html) =>
        {
            Assert.Equal(43, page.Results.Total);
            Assert.Equal(new(2026, 10, 1), page.Applied.From); Assert.Equal(new(2026, 10, 31), page.Applied.To);
            Assert.Contains("01/10/2026 – 31/10/2026", html()); Assert.Contains("from=2026-10-01", html());
            Assert.DoesNotContain("Registo de 2026-11-01", html()); Assert.DoesNotContain("Registo de 2019-12-05", html());
            await page.Call("Next");
            Assert.Equal(2, page.Results.Page); Assert.Equal(3, page.Results.Rows.Count);
            Assert.Contains("Registo de 2026-10-01", html()); Assert.DoesNotContain("Registo de 2026-09-30", html());
        });
    }
    [Fact] public async Task DashboardLinksSelectTheRequestedYearAndMovementKind()
    {
        await using var services=new ServiceCollection().AddLogging().AddSingleton(new FinanceService(Open)).AddSingleton<IJSRuntime,StaticJs>().BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>
        {
            TestPage? page=null;
            await renderer.RenderComponentAsync<TestPage>(ParameterView.FromDictionary(new Dictionary<string,object?>{
                [nameof(TestPage.InitialYear)]=2026,[nameof(TestPage.InitialKind)]="Expense",
                [nameof(TestPage.Capture)]=(Action<TestPage>)(p=>page=p)}));
            Assert.Equal(45,page!.Results.Total);Assert.Equal(new(2026,1,1),page.Applied.From);Assert.Equal(new(2026,12,31),page.Applied.To);
            Assert.Equal(MovementKind.Expense,page.Applied.Kind);Assert.Contains("kind=Expense",page.CsvUrl);
        });
    }

    [Fact] public async Task ApplyingYearOrDatesResetsPaginationAndRejectsInvertedDates()
    {
        await WithPage(async (page, html) =>
        {
            await page.Call("Next");
            page.Period.Year = 2019; page.Period.Month = 0; await page.Call("Apply");
            Assert.Equal(1, page.Results.Page); Assert.Single(page.Results.Rows); Assert.Contains("Registo de 2019-12-05", html());
            page.Period.Mode = MovementPeriodMode.Dates; page.Period.From = new(2026, 9, 30); page.Period.To = new(2026, 10, 1);
            await page.Call("Apply"); Assert.Equal(2, page.Results.Total);
            Assert.Contains("Registo de 2026-09-30", html()); Assert.Contains("Registo de 2026-10-01", html());
            page.Period.From = new(2026, 11, 1); await page.Call("Apply");
            Assert.Contains("A data inicial deve ser anterior ou igual à data final.", html());
            Assert.Equal(2, page.Results.Total); Assert.Contains("from=2026-09-30", html());
        });
    }

    [Fact] public async Task DraftFiltersDoNotChangePaginationOrCsvUntilAppliedAndClearRestoresCurrentMonth()
    {
        await WithPage(async (page, html) =>
        {
            page.Filter.Search = "ainda não aplicado"; page.Period.Mode = MovementPeriodMode.All;
            await page.Call("Next");
            Assert.Equal(2, page.Results.Page); Assert.Equal(43, page.Results.Total);
            Assert.Contains("from=2026-10-01", html()); Assert.DoesNotContain("search=ainda", html());
            page.Filter.Search = null; await page.Call("Apply"); Assert.Equal(46, page.Results.Total);
            Assert.DoesNotContain("from=", page.CsvUrl); Assert.Contains("Todo o histórico", html());
            await page.Call("Clear");
            var expected = new MovementPeriod(MovementPeriod.TodayInLisbon).Resolve();
            Assert.Equal(expected.From, page.Applied.From); Assert.Equal(expected.To, page.Applied.To); Assert.Null(page.Applied.Search);
        });
    }

    [Fact] public async Task EntryFormKeepsItsFieldsInTheCompactLayout()
    {
        await WithPage(async (page, html) =>
        {
            await page.Call("New");
            Assert.Contains("movement-editor", html()); Assert.Contains("movement-notes-field", html());
            foreach (var label in new[] { "Tipo", "Data", "Montante em EUR", "Conta de origem", "Categoria", "Pessoa", "Descrição", "Observações", "Guardar movimento" })
                Assert.Contains(label, html());
            Assert.Contains("rows=\"1\"", html());
        });
    }

    [Fact] public async Task AutomaticSearchUsesLatestTextAndClearCancelsPendingSearch()
    {
        await WithPage(async (page, html) =>
        {
            Assert.DoesNotContain(">Pesquisar</button>", html());
            await page.Call("Next");
            page.Filter.Search="sem correspondência";var first=page.Call("SearchChanged");
            page.Filter.Search="2026-10-31";var latest=page.Call("SearchChanged");
            await Task.WhenAll(first,latest);
            Assert.Single(page.Results.Rows);Assert.Equal(1,page.Results.Page);
            Assert.Contains("search=2026-10-31",page.CsvUrl);
            page.Filter.Search="não deve voltar";var pending=page.Call("SearchChanged");
            await page.Call("Clear");await pending;
            Assert.Null(page.Applied.Search);Assert.DoesNotContain("search=",page.CsvUrl);
        });
    }

    [Fact] public async Task FilterChangeDuringAnUpdateIsAppliedAfterwards()
    {
        await WithPage(async (page, html) =>
        {
            page.Filter.Search="2026-10-31";var first=page.Call("Apply");
            page.Filter.Search="2026-10-01";var latest=page.Call("Apply");
            await Task.WhenAll(first,latest);
            Assert.Single(page.Results.Rows);Assert.Equal(new(2026,10,1),page.Results.Rows[0].Date);
            Assert.Contains("search=2026-10-01",page.CsvUrl);
        });
    }

    [Fact] public async Task HeadersToggleOrderingResetThePageAndKeepAppliedFilters()
    {
        await WithPage(async (page, html) =>
        {
            Assert.Contains("aria-sort=\"descending\"", html());
            Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(html(), "class=\"sort-heading\"").Count);
            await page.Call("Next");
            page.Filter.Search = "rascunho";
            await page.Call("SortBy", MovementSort.Date);
            Assert.Equal(1, page.Results.Page); Assert.Equal(43, page.Results.Total);
            Assert.Equal(new(2026, 10, 1), page.Results.Rows[0].Date);
            Assert.Contains("aria-sort=\"ascending\"", html());
            Assert.Contains("sort=Date&descending=False", page.CsvUrl); Assert.DoesNotContain("rascunho", page.CsvUrl);
            await page.Call("Next"); Assert.Equal(new(2026, 10, 31), page.Results.Rows[^1].Date);
            await page.Call("SortBy", MovementSort.Date);
            Assert.Equal(new(2026, 10, 31), page.Results.Rows[0].Date); Assert.True(page.Applied.Descending);
            await page.Call("SortBy", MovementSort.Description);
            Assert.False(page.Applied.Descending); Assert.Equal(MovementSort.Description, page.Applied.Sort);
            page.Filter.Search = null; await page.Call("Apply");
            Assert.Equal(MovementSort.Description, page.Applied.Sort); Assert.False(page.Applied.Descending);
        });
    }

    private async Task WithPage(Func<TestPage, Func<string>, Task> check)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton(new FinanceService(Open))
            .AddSingleton<IJSRuntime, StaticJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPage? page = null;
            var root = await renderer.RenderComponentAsync<TestPage>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(TestPage.Capture)] = (Action<TestPage>)(p => page = p) }));
            await check(page!, () => WebUtility.HtmlDecode(root.ToHtmlString()));
        });
    }

    public sealed class TestPage : Movements
    {
        [Parameter] public int? InitialYear { get; set; }
        [Parameter] public string? InitialKind { get; set; }
        [Parameter] public Action<TestPage>? Capture { get; set; }
        private T Field<T>(string name) => (T)typeof(Movements).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public MovementFilter Filter => Field<MovementFilter>("filter");
        public MovementFilter Applied => Field<MovementFilter>("appliedFilter");
        public MovementPeriod Period => Field<MovementPeriod>("period");
        public MovementPage Results => Field<MovementPage>("page");
        public string CsvUrl => (string)typeof(Movements).GetProperty("ExportUrl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        protected override async Task OnInitializedAsync()
        {
            YearRequested=InitialYear;KindRequested=InitialKind;
            typeof(Movements).GetField("period", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, new MovementPeriod(new(2026, 10, 16)));
            await base.OnInitializedAsync(); Capture?.Invoke(this);
        }
        public async Task Call(string method, params object[] args)
        {
            var result = typeof(Movements).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, args);
            if (result is Task task) await task;
            StateHasChanged();
        }
    }
    private sealed class StaticJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
