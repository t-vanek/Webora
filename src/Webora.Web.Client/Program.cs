using System.Globalization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Webora.Web.Client.Api;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// HttpClient pointed at the host so client components can call the notification API.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Typed API clients keep the HTTP plumbing out of components; AntiforgeryTokenProvider fetches and
// caches the RequestVerificationToken used on unsafe verbs.
builder.Services.AddScoped<AntiforgeryTokenProvider>();
builder.Services.AddScoped<NotificationsApiClient>();

// Mirror the server's authentication state on the WebAssembly client (deserialized from the
// state the server persists), so AuthorizeView and policies work in client-rendered components.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

builder.Services.AddLocalization();

var host = builder.Build();

// Adopt the culture chosen on the server (stored in the .AspNetCore.Culture cookie).
var js = host.Services.GetRequiredService<IJSRuntime>();
var culture = await js.InvokeAsync<string?>("weboraGetCulture");
if (!string.IsNullOrEmpty(culture))
{
    var cultureInfo = new CultureInfo(culture);
    CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
    CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
}

await host.RunAsync();
