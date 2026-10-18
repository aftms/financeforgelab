using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class EmailProvenanceTests
{
    [Fact] public async Task OriginalEmailHeadersAreRenderedWithSubjectAndLocalDate()
    {
        var reference=EmailProvenance.FromRetrieved(Encoding.UTF8.GetBytes("""
            {"account":"hotmail","folder":"PayPal","messageId":"mail-123","message":{"from":{"emailAddress":{"address":"service@paypal.com"}},"receivedDateTime":"2026-09-03T12:57:37Z","subject":"Recibo <script> do pagamento à EMEL"}}
            """));
        await using var services=new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>(await renderer.RenderComponentAsync<EmailSourceSummary>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"Email",reference}}))).ToHtmlString());
        Assert.Contains("service@paypal.com",html);Assert.Contains("03/09/2026 13:57",html);
        Assert.Contains("&lt;script&gt;",html);Assert.DoesNotContain("<script>",html);
        Assert.Contains("PayPal",html);Assert.Equal("mail-123",reference.MessageId);
        Assert.Contains("Copiar assunto para pesquisar",html);
    }
    [Fact] public void OriginalMessageLinksOnlyAllowMailProvidersOverHttps()
    {
        var e=new EmailSourceReference("hotmail","Inbox","id","sender",null,"subject");
        Assert.Null((e with{WebLink="javascript:alert(1)"}).SafeWebLink);
        Assert.Null((e with{WebLink="https://outlook.live.com.evil.example/mail"}).SafeWebLink);
        Assert.Null((e with{WebLink="http://outlook.live.com/mail"}).SafeWebLink);
        Assert.Equal("https://outlook.live.com/mail/id/example",(e with{WebLink="https://outlook.live.com/mail/id/example"}).SafeWebLink);
    }
    [Fact] public void OlderSourcesRemainReadableWithoutInventedEmailMetadata()
    {
        Assert.Null(EmailProvenance.Read("{\"Description\":\"Recibo\"}"));
        Assert.Null(EmailProvenance.Read("invalid"));
        var e=new PurchaseEvidence{Email=new("hotmail","PayPal","id","sender",null,"Subject")};
        Assert.Equal(e.Email,EmailProvenance.Read(JsonSerializer.Serialize(e)));
    }
}
