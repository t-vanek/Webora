using System.Net;
using System.Net.Http.Json;
using D3Parking.Application.Notifications;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Hosting;
using D3Parking.Web.Parking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class DeploymentTests
{
    [TestCase("https://127.0.0.1/private", false)]
    [TestCase("https://10.0.0.1/private", false)]
    [TestCase("https://fcm.googleapis.com.evil.test/send", false)]
    [TestCase("https://evil@fcm.googleapis.com/send", false)]
    [TestCase("https://fcm.googleapis.com:8443/send", false)]
    [TestCase("http://fcm.googleapis.com/send", false)]
    [TestCase("https://fcm.googleapis.com/send/token", true)]
    [TestCase("https://updates.push.services.mozilla.com/wpush/v2/token", true)]
    [TestCase("https://web.push.apple.com/token", true)]
    [TestCase("https://wns.notify.windows.com/token", true)]
    public void Push_targets_cannot_be_arbitrary_server_side_requests(string url, bool allowed) =>
        Assert.That(PushEndpointPolicy.IsAllowed(url), Is.EqualTo(allowed));

    [Test]
    public void Database_history_must_be_an_exact_prefix_of_release_migrations()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DeploymentDatabase.IsPrefix(["a", "b"], []), Is.True);
            Assert.That(DeploymentDatabase.IsPrefix(["a", "b"], ["a"]), Is.True);
            Assert.That(DeploymentDatabase.IsPrefix(["a", "b"], ["a", "b"]), Is.True);
            Assert.That(DeploymentDatabase.IsPrefix(["a", "b"], ["b"]), Is.False);
            Assert.That(DeploymentDatabase.IsPrefix(["a"], ["a", "b"]), Is.False);
            Assert.That(DeploymentDatabase.IsPrefix(["a"], ["other"]), Is.False);
        });
    }

    [Test]
    public void Production_fails_closed_without_shared_configuration()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => DeploymentConfiguration.Validate(config, "Production"));
        Assert.DoesNotThrow(() => DeploymentConfiguration.Validate(config, "Development"));
    }

    [Test]
    public async Task Operational_routes_distinguish_live_from_ready_without_exposing_database_errors()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContextFactory<D3ParkingDbContext>(o => o.UseSqlServer(
            "Server=127.0.0.1,1;Database=Unavailable;User Id=probe;Password=not-a-secret;Connect Timeout=1;ConnectRetryCount=0"));
        await using var app = builder.Build();
        app.UseOperationalEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        Assert.That((await http.GetAsync("/health/live")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var version = await http.GetFromJsonAsync<ReleaseInformation>("/version");
        Assert.That(version!.Environment, Is.EqualTo("Development"));
        var ready = await http.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var body = await ready.Content.ReadAsStringAsync();
        Assert.That(body, Does.Not.Contain("not-a-secret").And.Not.Contain("SqlException"));
        Assert.That(ready.Headers.CacheControl!.NoStore, Is.True);
        Assert.That((await http.PostAsync("/version", null)).StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
    }

    [Test]
    public void Historical_html_upload_cannot_execute_from_photo_endpoint()
    {
        var http = new DefaultHttpContext();
        var result = MismatchPhotoEndpoints.SafePhoto(http,
            new D3Parking.Application.Parking.MismatchPhotoDto("<script>alert(1)</script>"u8.ToArray(), "text/html"));
        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.NotFound>());
        Assert.That(http.Response.Headers.XContentTypeOptions.ToString(), Is.EqualTo("nosniff"));
        Assert.That(http.Response.Headers.CacheControl.ToString(), Is.EqualTo("private, no-store"));
    }
}
