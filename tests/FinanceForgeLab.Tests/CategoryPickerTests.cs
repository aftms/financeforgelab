using System.Reflection;
using FinanceForgeLab.Core;
using FinanceForgeLab.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class CategoryPickerTests
{
    [Fact] public async Task SelectionSeparatesParentAndChildAndChangingParentClearsTheOldChild()
    {
        var categories = new List<Category> {
            new() { Id=1, Name="Saúde", Kind=MovementKind.Expense },
            new() { Id=2, Name="Casa", Kind=MovementKind.Expense },
            new() { Id=3, Name="Trabalho", Kind=MovementKind.Income },
            new() { Id=11, Name="Consultas", Kind=MovementKind.Expense, ParentId=1 },
            new() { Id=12, Name="Antiga", Kind=MovementKind.Expense, ParentId=1, Active=false },
            new() { Id=21, Name="Água", Kind=MovementKind.Expense, ParentId=2 }
        };
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPicker? picker = null; long? value = 11;
            var root = await renderer.RenderComponentAsync<TestPicker>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                [nameof(TestPicker.Categories)]=categories, [nameof(TestPicker.Kind)]=MovementKind.Expense, [nameof(TestPicker.Value)]=value,
                [nameof(TestPicker.Capture)]=(Action<TestPicker>)(p=>picker=p),
                [nameof(TestPicker.ValueChanged)]=EventCallback.Factory.Create<long?>(new object(), id=>value=id)
            }));
            Assert.Equal(1, picker!.Root); Assert.Equal(11, picker.Child);
            Assert.Contains("Consultas", root.ToHtmlString()); Assert.DoesNotContain("Antiga", root.ToHtmlString()); Assert.DoesNotContain("Trabalho", root.ToHtmlString());
            await picker.Change("ChangeRoot", "2"); Assert.Equal(2, value);
            await picker.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(TestPicker.Value)]=value }));
            Assert.Equal(2, picker.Root); Assert.Null(picker.Child);
            Assert.DoesNotContain("Consultas", root.ToHtmlString());
            await picker.Change("ChangeChild", "21"); Assert.Equal(21, value);
            await picker.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(TestPicker.Value)]=value }));
            Assert.Equal(2, picker.Root); Assert.Equal(21, picker.Child);
            await picker.Change("ChangeChild", "11"); Assert.Equal(2, value); // A child from another parent is rejected.
            await picker.Change("ChangeRoot", ""); Assert.Null(value);
        });
    }
    public sealed class TestPicker : CategoryPicker
    {
        [Parameter] public Action<TestPicker>? Capture {get;set;}
        public long? Root => (long?)typeof(CategoryPicker).GetProperty("RootId", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(this);
        public long? Child => (long?)typeof(CategoryPicker).GetProperty("ChildId", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(this);
        protected override void OnInitialized()=>Capture?.Invoke(this);
        public async Task Change(string method,string value)
        {
            await (Task)typeof(CategoryPicker).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(this,[new ChangeEventArgs {Value=value}])!;
        }
    }
}
