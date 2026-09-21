using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using D3Parking.Infrastructure.Email;

namespace D3Parking.Web.Hosting;

public static class DeploymentConfiguration
{
    public static string? Load(WebApplicationBuilder builder)
    {
        var root = builder.Configuration["Deployment:InstallPath"];
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("Deployment:InstallPath must be absolute.");
        root = Path.GetFullPath(root);
        // Production configuration deliberately wins over inherited shell/machine environment.
        // A deployment probe and the Windows service must see the same settings.
        builder.Configuration.AddJsonFile(Path.Combine(root, "config", "appsettings.json"), false, false)
            .AddJsonFile(Path.Combine(root, "secrets", "secrets.json"), false, false);
        builder.Configuration["Deployment:InstallPath"] = root;
        return root;
    }

    public static void Validate(IConfiguration config, string environment)
    {
        if (environment == "Development") return;
        Required(config, "Deployment:InstallPath");
        if (config["Deployment:Environment"] != environment)
            throw new InvalidOperationException("Deployment environment does not match shared configuration.");
        var db = new SqlConnectionStringBuilder(Required(config, "ConnectionStrings:SqlServer"));
        if (string.IsNullOrWhiteSpace(db.InitialCatalog) || new[] { "master", "model", "msdb", "tempdb" }.Contains(db.InitialCatalog.ToLowerInvariant())
            || db.DataSource.Contains("(localdb)", StringComparison.OrdinalIgnoreCase)
            || db.IntegratedSecurity || string.IsNullOrWhiteSpace(db.UserID) || string.IsNullOrWhiteSpace(db.Password)
            || db.TrustServerCertificate || db.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new InvalidOperationException("Production needs a dedicated SQL database, SQL credentials and verified encrypted SQL transport.");
        if (!Uri.TryCreate(Required(config, "Account:BaseUrl"), UriKind.Absolute, out var url)
            || url.Scheme != "https" || url.IsLoopback || url.AbsolutePath != "/" || url.Query.Length != 0 || url.UserInfo.Length != 0)
            throw new InvalidOperationException("Account:BaseUrl must be the public HTTPS origin.");
        var hosts = Required(config, "AllowedHosts").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Any(h => h.Contains('*')) || !hosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("AllowedHosts must list the public hostname explicitly, without wildcards.");
        if (config.GetValue<bool>("ForwardedHeaders:TrustAllProxies"))
            throw new InvalidOperationException("ForwardedHeaders:TrustAllProxies is not supported in production.");
        foreach (var proxy in config.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            if (!System.Net.IPAddress.TryParse(proxy, out _)) throw new InvalidOperationException("Invalid ForwardedHeaders:KnownProxies entry.");
        foreach (var network in config.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
            if (!System.Net.IPNetwork.TryParse(network, out _)) throw new InvalidOperationException("Invalid ForwardedHeaders:KnownNetworks entry.");

        var endpoints = config.GetSection("Kestrel:Endpoints").GetChildren().ToArray();
        if (endpoints.Length != 2 || !endpoints.Any(e => e.Key == "Public") || !endpoints.Any(e => e.Key == "Health"))
            throw new InvalidOperationException("Configure exactly Kestrel:Endpoints:Public and Health.");
        var publicUrl = Required(config, "Kestrel:Endpoints:Public:Url");
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var listener) || listener.Scheme != "https"
            || listener.Port != url.Port || listener.AbsolutePath != "/" || listener.Query.Length != 0 || listener.Fragment.Length != 0 || listener.UserInfo.Length != 0)
            throw new InvalidOperationException("The public listener must use HTTPS on the public origin's port.");
        if (!Uri.TryCreate(Required(config, "Kestrel:Endpoints:Health:Url"), UriKind.Absolute, out var health)
            || health.Scheme != "http" || health.Host != "127.0.0.1" || health.AbsolutePath != "/")
            throw new InvalidOperationException("The health listener must be http://127.0.0.1:<port>.");
        if (!hosts.Contains("127.0.0.1")) throw new InvalidOperationException("AllowedHosts must include 127.0.0.1 for local health probes.");

        using var tls = LoadCertificate(config, "Kestrel:Endpoints:Public:Certificate");
        if (!tls.MatchesHostname(url.Host, allowWildcards: true, allowCommonName: false))
            throw new InvalidOperationException("HTTPS certificate does not match Account:BaseUrl (SAN hostname required).");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        if (!chain.Build(tls)) throw new InvalidOperationException("HTTPS certificate chain/revocation validation failed. Install its trusted CA/intermediate certificates.");
        using var protection = LoadCertificate(config, "DataProtection:Certificate");
        using var rsa = protection.GetRSAPrivateKey();
        if (rsa is null || rsa.KeySize < 2048) throw new InvalidOperationException("Data Protection needs an RSA certificate of at least 2048 bits.");
        if (config.GetValue<bool>("IdentityServer:Enabled"))
            throw new InvalidOperationException("IdentityServer endpoints are unfinished; keep IdentityServer:Enabled=false. Entra sign-in is independent.");
        var smtp = config.GetSection("Smtp").Get<SmtpOptions>() ?? new();
        if (smtp.Authentication != SmtpAuthMode.None && smtp.Security is not (SmtpSecurity.StartTls or SmtpSecurity.SslOnConnect))
            throw new InvalidOperationException("Authenticated SMTP requires StartTls or SslOnConnect.");
        if (smtp.Authentication == SmtpAuthMode.OAuth2)
        {
            Required(config, "Smtp:OAuth2:ClientId");
            Required(config, "Smtp:OAuth2:ClientSecret");
            if (!Uri.TryCreate(Required(config, "Smtp:OAuth2:TokenEndpoint"), UriKind.Absolute, out var token) || token.Scheme != "https")
                throw new InvalidOperationException("Smtp:OAuth2:TokenEndpoint must use HTTPS.");
        }
        var seedEmail = config["IdentitySeed:AdminEmail"];
        var seedPassword = config["IdentitySeed:AdminPassword"];
        if (!string.IsNullOrEmpty(seedEmail) || !string.IsNullOrEmpty(seedPassword))
        {
            if (!System.Net.Mail.MailAddress.TryCreate(seedEmail, out _) || string.IsNullOrEmpty(seedPassword)
                || seedPassword.Length < 12 || !seedPassword.Any(char.IsUpper) || !seedPassword.Any(char.IsLower)
                || !seedPassword.Any(char.IsDigit) || seedPassword.All(char.IsLetterOrDigit))
                throw new InvalidOperationException("Initial administrator needs a valid email and a unique password of 12+ characters with upper/lower case, digit and symbol.");
        }
    }

    public static X509Certificate2 LoadCertificate(IConfiguration config, string section)
    {
        var path = Required(config, section + ":Path");
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException($"{section}:Path must be absolute.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, config[section + ":Password"], X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"{section} must contain a valid, unexpired certificate and its private key.");
        }
        return certificate;
    }

    public static void ConfigurePersistence(WebApplicationBuilder builder, string root)
    {
        var certificate = LoadCertificate(builder.Configuration, "DataProtection:Certificate");
        builder.Services.AddDataProtection()
            .SetApplicationName("D3Parking")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "data", "keys")))
            .ProtectKeysWithCertificate(certificate);
        builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
            Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme,
            options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
    }

    private static string Required(IConfiguration config, string key) =>
        !string.IsNullOrWhiteSpace(config[key]) ? config[key]! : throw new InvalidOperationException($"Missing configuration: {key}.");
}
