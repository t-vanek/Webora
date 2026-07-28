using System.Globalization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using D3Parking.Web.Client.Api;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// HttpClient pointed at the host so client components can call the notification API.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Fluent UI services for WASM-rendered components. Mirrors the server registration.
builder.Services.AddFluentUIComponents();

// Typed API clients keep the HTTP plumbing out of components; AntiforgeryTokenProvider fetches and
// caches the RequestVerificationToken used on unsafe verbs.
builder.Services.AddScoped<AntiforgeryTokenProvider>();
builder.Services.AddScoped<NotificationsApiClient>();

// Mirror the server's authentication state on the WebAssembly client (deserialized from the
// state the server persists), so AuthorizeView and policies work in client-rendered components.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

// ResourcesPath has to match where the resx actually lives (Resources/ClientResource.resx),
// exactly like the server registration. Without it the localizer looks for an embedded resource
// named "…Web.Client.ClientResource" while the compiler emits
// "…Web.Client.Resources.ClientResource" — nothing matches and every string renders as its key.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

var host = builder.Build();

// Adopt the culture chosen on the server (stored in the .AspNetCore.Culture cookie).
var js = host.Services.GetRequiredService<IJSRuntime>();
var culture = await js.InvokeAsync<string?>("d3parkingGetCulture");
if (!string.IsNullOrEmpty(culture))
{
    var cultureInfo = new CultureInfo(culture);
    CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
    CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
}

await host.RunAsync();
