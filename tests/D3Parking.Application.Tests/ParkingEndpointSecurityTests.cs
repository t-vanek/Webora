using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using D3Parking.Application.Notifications;
using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Authorization;
using D3Parking.Web;
using D3Parking.Web.Notifications;
using D3Parking.Web.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Real HTTP requests through the production route mappings and permission/antiforgery filters.
/// Authentication and data services are test doubles: these tests prove the HTTP boundary, not
/// Identity cookies or SQL ownership queries (covered separately by SQL-backed service tests).
/// </summary>
[TestFixture]
[NonParallelizable]
public class ParkingEndpointSecurityTests
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly List<(string Method, object?[] Arguments)> _calls = [];
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly byte[] PhotoBytes = [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3];

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(ParkingEndpointSecurityTests).Assembly.FullName,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
        builder.Services.AddPermissionAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(Proxy<IParkingSpotService>());
        builder.Services.AddSingleton(Proxy<IOversightService>());
        builder.Services.AddSingleton(Proxy<IReservationService>());
        builder.Services.AddSingleton(Proxy<ICalendarSubscriptionService>());
        builder.Services.AddSingleton(Proxy<INotificationService>());
        builder.Services.AddSingleton<ISiteSettingsService, FakeSiteSettings>();
        builder.Services.Configure<WebPushOptions>(_ => { });

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseAntiforgery();
        _app.MapMismatchPhotoApi();
        _app.MapCalendarApi();
        _app.MapNotificationApi();
        // Same antiforgery-token operation as Program.cs, exposed only in this test host.
        _app.MapGet("/test/token", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!)).RequireAuthorization();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    [SetUp]
    public void Reset() => _calls.Clear();

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    [TestCase("mismatches", null, HttpStatusCode.Unauthorized)]
    [TestCase("mismatches", Permissions.Parking.ManageSpots, HttpStatusCode.Forbidden)]
    [TestCase("mismatches", Permissions.Parking.ReviewMismatches, HttpStatusCode.OK)]
    [TestCase("defects", null, HttpStatusCode.Unauthorized)]
    [TestCase("defects", Permissions.Parking.ReviewMismatches, HttpStatusCode.Forbidden)]
    [TestCase("defects", Permissions.Parking.ManageSpots, HttpStatusCode.OK)]
    public async Task Photo_routes_enforce_their_distinct_permissions_before_reading_data(
        string kind, string? permission, HttpStatusCode expected)
    {
        var id = Guid.NewGuid();
        using var request = Request(HttpMethod.Get, $"/api/parking/{kind}/{id}/photo", permission);
        using var response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(expected));
        if (expected == HttpStatusCode.OK)
        {
            Assert.That(_calls, Has.Count.EqualTo(1));
            Assert.That(_calls[0].Arguments[0], Is.EqualTo(id));
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(PhotoBytes));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/jpeg"));
        }
        else
        {
            Assert.That(_calls, Is.Empty, "A denied route must never query the evidence service.");
        }
    }

    [Test]
    public async Task Calendar_export_uses_the_authenticated_subject_and_ignores_a_substituted_owner()
    {
        var reservationId = Guid.NewGuid();
        using var request = Request(HttpMethod.Get,
            $"/api/parking/reservations/{reservationId}/calendar?userId={Guid.NewGuid()}", Permissions.Parking.Reserve);
        using var response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_calls, Has.Count.EqualTo(1));
        Assert.That(_calls[0].Method, Is.EqualTo(nameof(IReservationService.GetMyReservationAsync)));
        Assert.That(_calls[0].Arguments.Take(2), Is.EqualTo(new object[] { UserId, reservationId }));
    }

    [Test]
    public async Task Notification_mutation_rejects_missing_csrf_then_accepts_the_issued_token()
    {
        using var unprotected = Request(HttpMethod.Post, "/api/notifications/read-all", Permissions.Parking.View);
        using var denied = await _client.SendAsync(unprotected);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_calls, Is.Empty);

        using var getToken = Request(HttpMethod.Get, "/test/token", Permissions.Parking.View);
        using var tokenResponse = await _client.SendAsync(getToken);
        Assert.That(tokenResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var token = await tokenResponse.Content.ReadAsStringAsync();
        using var protectedRequest = Request(HttpMethod.Post, "/api/notifications/read-all", Permissions.Parking.View);
        protectedRequest.Headers.Add("RequestVerificationToken", token);
        using var allowed = await _client.SendAsync(protectedRequest);
        Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(_calls, Has.Count.EqualTo(1));
        Assert.That(_calls[0].Arguments[0], Is.EqualTo(UserId));
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? permission)
    {
        var request = new HttpRequestMessage(method, path);
        if (permission is not null) request.Headers.Add("X-Test-Permission", permission);
        return request;
    }

    private T Proxy<T>() where T : class
    {
        var service = DispatchProxy.Create<T, BoundaryProxy>();
        ((BoundaryProxy)(object)service).Handler = (method, arguments) =>
        {
            _calls.Add((method.Name, arguments));
            return method.Name switch
            {
                nameof(IParkingSpotService.GetMismatchPhotoAsync) or nameof(IOversightService.GetDefectPhotoAsync) =>
                    Task.FromResult<MismatchPhotoDto?>(new(PhotoBytes, "image/jpeg")),
                nameof(IReservationService.GetMyReservationAsync) => Task.FromResult<ReservationDto?>(null),
                nameof(INotificationService.MarkAllReadAsync) => Task.CompletedTask,
                _ => throw new InvalidOperationException($"Unexpected data access: {method.Name}"),
            };
        };
        return service;
    }

    public class BoundaryProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Permission", out var permission))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[]
            {
                new Claim("sub", UserId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(D3ParkingClaimTypes.Permission, permission.ToString()),
            }, "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
