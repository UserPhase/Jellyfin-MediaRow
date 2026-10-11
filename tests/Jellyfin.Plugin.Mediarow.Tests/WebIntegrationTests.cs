using System.Text;
using Jellyfin.Plugin.Mediarow.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Mediarow.Tests;

public sealed class WebIntegrationTests
{
    [Theory]
    [InlineData("/", "", "")]
    [InlineData("/web/", "", "")]
    [InlineData("/web/index.html", "", "")]
    [InlineData("/jellyfin/web/", "", "/jellyfin")]
    [InlineData("/jellyfin/web/index.html", "", "/jellyfin")]
    [InlineData("/web/index.html", "/jellyfin", "/jellyfin")]
    [InlineData("/index.html", "/jellyfin", "/jellyfin")]
    public async Task WebIndex_InjectsAssetsUnderServerBasePath(string path, string pathBase, string expectedBase)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        new MediarowWebIntegrationStartupFilter().Configure(app =>
        {
            app.Run(context => context.Response.WriteAsync("<html><head></head><body>Home</body></html>"));
        })(builder);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.PathBase = pathBase;
        using var output = new MemoryStream();
        context.Response.Body = output;

        await builder.Build()(context);

        string html = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains($"href=\"{expectedBase}/Mediarow/Home/mediarow-home.css?", html);
        Assert.Contains($"src=\"{expectedBase}/Mediarow/Home/mediarow-home.js?", html);
        Assert.Contains("mediarow-web=1", context.Response.Headers.SetCookie.ToString());
    }
}
