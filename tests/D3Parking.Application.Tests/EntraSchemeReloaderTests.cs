using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using D3Parking.Application.Identity;
using D3Parking.Web.Identity;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// The scheme map is per process, so every instance reconciles itself on a timer rather than only
/// on the save it happened to handle. That makes "nothing changed" the overwhelmingly common call,
/// and it has to be genuinely free: republishing would throw away the handler's cached discovery
/// document and reopen the window in which the scheme does not exist at all.
/// </summary>
[TestFixture]
public class EntraSchemeReloaderTests
{
    private const string Scheme = EntraAuthenticationExtensions.Scheme;

    private AuthenticationSchemeProvider _schemes = null!;
    private OptionsCache<OpenIdConnectOptions> _options = null!;
    private EntraSchemeReloader _reloader = null!;

    [SetUp]
    public void SetUp()
    {
        _schemes = new AuthenticationSchemeProvider(Options.Create(new AuthenticationOptions()));
        _options = new OptionsCache<OpenIdConnectOptions>();
        _reloader = new EntraSchemeReloader(_schemes, _options, NullLogger<EntraSchemeReloader>.Instance);
    }

    [Test]
    public async Task An_installation_without_a_tenant_publishes_no_scheme()
    {
        await _reloader.ReloadAsync(new EntraIdOptions());

        Assert.That(await _schemes.GetSchemeAsync(Scheme), Is.Null,
            "A published scheme answers every request and would resolve options that are not there.");
    }

    [Test]
    public async Task Configuring_sign_in_publishes_the_scheme_under_its_display_name()
    {
        await _reloader.ReloadAsync(Configured());

        var scheme = await _schemes.GetSchemeAsync(Scheme);
        Assert.That(scheme, Is.Not.Null);
        Assert.That(scheme!.DisplayName, Is.EqualTo("Contoso"));
        Assert.That(scheme.HandlerType, Is.EqualTo(typeof(OpenIdConnectHandler)));
    }

    [Test]
    public async Task Reconciling_unchanged_settings_leaves_the_registration_alone()
    {
        await _reloader.ReloadAsync(Configured());
        Assert.That(_options.TryAdd(Scheme, new OpenIdConnectOptions()), Is.True);

        // What the poller does, over and over, on every instance.
        await _reloader.ReloadAsync(Configured());
        await _reloader.ReloadAsync(Configured());

        Assert.That(await _schemes.GetSchemeAsync(Scheme), Is.Not.Null);
        Assert.That(_options.TryAdd(Scheme, new OpenIdConnectOptions()), Is.False,
            "A no-op reconcile must not evict the resolved options — that is the handler's cached metadata.");
    }

    [Test]
    public async Task A_changed_setting_republishes_the_scheme()
    {
        await _reloader.ReloadAsync(Configured());
        Assert.That(_options.TryAdd(Scheme, new OpenIdConnectOptions()), Is.True);

        await _reloader.ReloadAsync(Configured(clientId: "another-client"));

        Assert.That(await _schemes.GetSchemeAsync(Scheme), Is.Not.Null);
        Assert.That(_options.TryAdd(Scheme, new OpenIdConnectOptions()), Is.True,
            "Changed settings must drop the cached options, or the handler keeps using the old ones.");
    }

    [Test]
    public async Task A_renamed_button_republishes_the_scheme()
    {
        await _reloader.ReloadAsync(Configured());

        await _reloader.ReloadAsync(Configured(displayName: "Microsoft"));

        var scheme = await _schemes.GetSchemeAsync(Scheme);
        Assert.That(scheme!.DisplayName, Is.EqualTo("Microsoft"));
    }

    [Test]
    public async Task Switching_sign_in_off_withdraws_the_scheme()
    {
        await _reloader.ReloadAsync(Configured());

        await _reloader.ReloadAsync(Configured(enabled: false));

        Assert.That(await _schemes.GetSchemeAsync(Scheme), Is.Null,
            "An instance that keeps the scheme keeps accepting federated sign-ins after they were turned off.");
    }

    private static EntraIdOptions Configured(
        string clientId = "client",
        string displayName = "Contoso",
        bool enabled = true) => new()
        {
            Enabled = enabled,
            TenantId = "tenant-a",
            ClientId = clientId,
            ClientSecret = "secret",
            DisplayName = displayName,
        };
}
