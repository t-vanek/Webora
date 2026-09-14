# Konfigurace

Podporovaný profil: jedna Windows x64 služba, Kestrel HTTPS, externí SQL Server. Bez IIS. Kořen určuje `-InstallPath`; příklady používají `C:\D3Parking`.

```text
D3Parking/
  releases/<version>/app/     hotové binárky pouze ke čtení
  config/appsettings.json    sdílená provozní konfigurace
  config/deployment.json     služba, prostředí, adresy, migrační politika
  secrets/secrets.json       SQL aplikace, SMTP, hesla PFX, bootstrap
  secrets/deployment.json    oddělený SQL účet pro nasazení
  secrets/*.pfx              HTTPS a ochranné certifikáty
  data/keys/                 trvalá šifrovaná klíčenka Data Protection
  logs/                      aplikační logy, deployment logy/reporty
  backups/                   lokální zálohy konfigurace; SQL .bak leží NA SQL SERVERU
  state/                     aktuální/předchozí verze, zámek, rozpracovaná operace
```

## Přednost nastavení

Bez `Deployment:InstallPath` platí standardní .NET konfigurace; slouží vývoji. Mimo Development je kořen instalace povinný. Při jeho zadání aplikace načte povinný `config/appsettings.json`, poté `secrets/secrets.json`. Oba mají přednost i před zděděnými proměnnými a obecnými argumenty, aby kontrola i služba používaly stejné hodnoty. Shared soubory nejsou reloadované; změna vyžaduje restart. Deployment příkazy navíc čtou administrátorský `secrets/deployment.json`. Hesla se nepředávají command line.

## Povinné klíče

| Klíč | Význam |
|---|---|
| Deployment:Environment | Production/Staging, shodně se službou a deployment.json. |
| ConnectionStrings:SqlServer | V secrets.json: samostatná existující DB, SQL login, Encrypt=True, TrustServerCertificate=False. Produkce odmítá LocalDB, systémové DB a integrované ověření. |
| secrets/deployment.json → ConnectionStrings:SqlServer | Jiný účet pro backup/migrace, stejný server a DB, db_owner jen této DB. |
| Account:BaseUrl | Veřejný HTTPS origin, např. https://parking.company.cz:8443; též odkazy v e-mailech. Bez podadresáře. |
| AllowedHosts | Veřejný hostname a 127.0.0.1, oddělené středníkem, bez wildcard. |
| Kestrel:Endpoints:Public:Url | HTTPS listener, např. https://0.0.0.0:8443. |
| Kestrel:Endpoints:Public:Certificate:Path / Password | Absolutní cesta k PFX a heslo. Kontrola privátního klíče, platnosti, SAN hostname a důvěry řetězce. |
| Kestrel:Endpoints:Health:Url | Loopback HTTP, např. http://127.0.0.1:5081. Na portu jsou pouze health/version cesty. |
| DataProtection:Certificate:Path / Password | RSA PFX pro klíčenku. Initializer vytvoří na pět let. Zálohujte certifikát, heslo i keys. |
| IdentitySeed:AdminEmail / AdminPassword | První administrátor. Heslo 12+ znaků, velká/malá písmena, číslice a symbol. Po ověření přístupu oba klíče odstraňte. |
| Smtp:Host / Port / SenderEmail | Skutečný relay a povolený odesílatel, nahraďte placeholdery. |
| Smtp:Security / Authentication | None/Auto/StartTls/SslOnConnect a None/Basic/OAuth2. S autentizací je nutný StartTls/SslOnConnect. |
| Smtp:UserName / Password | Pro Basic, patří do secrets.json. |
| Smtp:OAuth2:* | Pro OAuth2: HTTPS TokenEndpoint, ClientId, ClientSecret, případně Scope. |
| Smtp:TimeoutSeconds | 5–300, výchozí 30. Preflight ověří spojení/TLS/autentizaci, nic neodesílá. |

`config/deployment.json`: ServiceName, Environment, HealthUrl, PublicUrl, SqlBackupDirectory a pole ApprovedMigrations. Adresy a prostředí se musí shodovat s appsettings. **SqlBackupDirectory je cesta na SQL Serveru**, ne na webovém serveru. DBA adresář předem vytvoří a povolí zápis SQL službě. COPY_ONLY backup s CHECKSUM a RESTORE VERIFYONLY neprokazuje skutečně vyzkoušený restore.

Runtime SQL účet potřebuje SELECT/INSERT/UPDATE/DELETE na DB. Deployment účet db_owner jen této DB. Nepoužívejte sa/serverové sysadmin. DB, účty a SQL certifikát připravuje DBA jednorázově. Integrované ověření není automatizovaným profilem podporované: přístup pod správcem by neověřil identitu služby.

## Volitelné integrace

- EntraId: DB nastavení nebo konfigurační overrides; ClientSecret a Scim:BearerToken patří do secrets. Přihlášení a SCIM mají samostatné přepínače. Funkční test skutečného tenantu je nutný; readiness interaktivní přihlášení netestuje.
- WebPush: Subject, PublicKey, PrivateKey. Bez obou VAPID klíčů vypnuto. Vývojový pár nepoužívat v produkci. Povolené cíle: FCM, Mozilla, Apple a Windows push; redirecty jsou vypnuté.
- Geocoding: NominatimBaseUrl a identifikující UserAgent. Distance:Provider=Haversine funguje offline; Osrm používá OsrmBaseUrl s fallbackem. Nejsou tvrdou readiness závislostí, ověřte vyhledání adresy na cílové síti.
- ForwardedHeaders: výchozí důvěra loopbacku; případně konkrétní KnownProxies/KnownNetworks. TrustAllProxies je v produkci odmítnuté. Přímý Kestrel externí proxy nepotřebuje.
- IdentityServer:Enabled: false/neuvedeno. Nehotové protokolové handlery nejsou produkční funkce; nesouvisí se zapnutím Entra.

## Oprávnění a životnost dat

Initializer omezí ACL na administrátory a SYSTEM. `NT SERVICE\<ServiceName>` čte binárky, config a vlastní secrets.json/PFX, zapisuje do logs/data. Deployment SQL tajemství a stav instalace mu nejsou přístupné. Nedávejte oprávnění Users/Everyone.

Data Protection má stálý application name `D3Parking`, klíče mimo release a RSA ochranu. Převod staré instalace vyžaduje zachovat její klíčenku, discriminator i šifrování, nebo znovu zadat Entra tajemství a přihlásit uživatele. Samotná DB nestačí. Rotace ochranného PFX musí zachovat dešifrování starých klíčů; prostá výměna souboru není bezpečná.

Stejná klíčenka chrání čekající účtové e-maily v tabulce EmailDeliveries. Po obnově DB jsou pro jejich dešifrování nutné odpovídající keys/PFX. Fronta má pevně pět pokusů, odstupy 1/5/30/120 minut a expiraci 24 hodin; konfigurace SMTP zůstává stejná. Dokončený obsah se odstraňuje, metadata mají retenci 30 dní. Stav Failed vyžaduje opravu příčiny a novou žádost uživatele o zprávu, nikoli ruční obnovení starého tokenu.

Log aplikace rotuje po dnech/20 MB, nejvýše 30 souborů. Deployment logy, reporty a SQL backupy se automaticky nemažou; nastavte archivaci a sledujte disk. Logy obsahují provozní údaje a mohou obsahovat e-mailové adresy. SQL zálohy a secrets/keys ukládejte také mimo tento server do chráněného úložiště.
