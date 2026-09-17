# Konfigurace

Běžné nastavení SQL, SMTP, veřejné adresy, prvního správce a HTTPS provádí samostatný `D3Parking.ps1` volbou Instalace/Nastavení/HTTPS certifikát. Vyplňujete otázky, program ověří odpovědi a vytvoří soubory s českými komentáři. Ruční editace JSON není podmínkou instalace. Změny provozních hodnot se načtou při příštím startu služby.

## Který soubor otevřít

`appsettings` si představte jako seznam pokynů pro aplikaci: kam se připojit, na jaké adrese poslouchat a jak podrobně zapisovat chyby. **Stejný název souboru na různých místech neznamená stejnou úlohu.**

| Soubor | Pro koho je a co v něm nastavovat |
|---|---|
| `src/D3Parking.Web/appsettings.json` | Výchozí hodnoty serveru ve zdrojích. Komentáře vysvětlují geokódování, vzdálenosti, poštu, proxy a logování. Skutečná produkční hesla sem nepatří. |
| `src/D3Parking.Web/appsettings.Development.json` | Pouze vývoj na vlastním PC: LocalDB, známý testovací správce a testovací push klíče. Nejde o předlohu produkčních přihlašovacích údajů. |
| `src/D3Parking.Web.Client/wwwroot/appsettings*.json` | Nastavení klienta, které si může stáhnout každý návštěvník. Zde je jen logování. Nikdy sem nedávejte SQL, hesla ani soukromé klíče. |
| `C:\D3Parking\config\appsettings.json` | Skutečné sdílené nastavení nainstalovaného serveru: veřejná adresa, porty, certifikáty a SMTP. Vytváří a komentuje průvodce. |
| `C:\D3Parking\secrets\secrets.json` | Neveřejná hesla aplikace a připojení k SQL. Průvodce vysvětluje i tyto položky, ale soubor stále obsahuje tajemství. |
| `C:\D3Parking\config\deployment.json` | Pokyny průvodci: Windows služba, adresy kontrol, cesta SQL záloh a schválené migrace. |
| `C:\D3Parking\secrets\deployment.json` | Neveřejný účet SQL pro zálohy a migrace. Běžná aplikace jej nečte. |

Soubory v `releases/<verze>/app` na serveru neupravujte. Patří k ověřenému vydání; změna poruší jeho kontrolní součty. Nastavení instalace je mimo `releases`, aby přežilo aktualizaci.

## Jak číst komentáře a hodnoty

```jsonc
{
  "Smtp": {
    // Port je číslo vstupu do poštovního serveru. Správce pošty potvrdí správnou hodnotu.
    "Port": 587,
    // Název musí přesně odpovídat podporované možnosti; nepřekládejte ho do češtiny.
    "Security": "StartTls"
  }
}
```

Řádek s `//` je jen vysvětlení. Aplikace i PowerShell 7.4+ komentáře načtou a přeskočí; jde o JSON s komentáři (JSONC), i když přípona zůstává `.json`. Přísný obecný JSON validátor může komentáře odmítnout. Nejde o chybu nastavení aplikace.

Název vlevo, například `"Port"`, neměňte. Vpravo je hodnota: text je v dvojitých uvozovkách, číslo bez nich, `true` znamená zapnuto a `false` vypnuto. `[]` je prázdný seznam; `""` je prázdný text. Položky odděluje čárka, za poslední ji nepište. V ručně psané Windows cestě zdvojte lomítka: `"C:\\D3Parking\\secrets\\site.pfx"`. Průvodce toto ošetří za vás.

`REPLACE_...` znamená „tuto ukázku je nutné nahradit“. Hodnoty `firma.cz`, `.example.test` a cesty z návodu jsou příklady; nezajistí vám skutečné DNS, schránku ani databázi. `localhost` a `127.0.0.1` označují počítač, na kterém aplikace právě běží, nikoli automaticky váš notebook nebo SQL server.

Průvodce při uložení znovu vytváří své vestavěné komentáře i ve sdílené konfiguraci a v souborech secrets. Vlastní ručně přidané komentáře nezachovává; provozní poznámky ukládejte zvlášť bez hesel. Ostatní konfigurační klíče a jejich hodnoty zůstávají zachované. Stav instalace, manifest a diagnostické reporty zůstávají běžné JSON bez komentářů.

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

Například když je `Smtp:Host` v základním souboru `localhost` a ve sdíleném souboru `smtp.firma.cz`, instalace použije `smtp.firma.cz`. Kdyby stejný klíč byl také v secrets, vyhraje hodnota ze secrets. Průvodce spravované položky sjednotí tak, aby starší duplicitní hodnota nepřebila nové nastavení.

## Jeden příklad: web, porty a certifikáty

Předpokládejme adresu **https://parking.firma.cz:8443**, kterou už připravilo IT:

| Položka | Hodnota v tomto příkladu | Co potřebuje |
|---|---|---|
| `Account:BaseUrl` a `PublicUrl` | `https://parking.firma.cz:8443` | DNS na webový server; tuto adresu otevřou lidé a dostanou v e-mailu. |
| `AllowedHosts` | `parking.firma.cz;127.0.0.1` | Jen jména/adresy, bez portu a protokolu; místní adresa je potřebná pro kontroly. |
| `Kestrel:Endpoints:Public:Url` | `https://0.0.0.0:8443` | Volný port 8443 a síťová pravidla od IT. `0.0.0.0` znamená všechny místní IPv4 adresy; není to odkaz pro člověka. |
| `Kestrel:Endpoints:Health:Url` a `HealthUrl` | `http://127.0.0.1:5081` | Jiný volný port. Službu kontroluje tento počítač; port do sítě neotevírejte. |
| HTTPS PFX | Cesta k PFX v `secrets` | Platný certifikát pro `parking.firma.cz`, privátní klíč, správné heslo a důvěryhodná CA v počítači. Dodá IT, průvodce nastaví práva. |
| Data Protection PFX | `secrets/protection.pfx` | Vytvoří průvodce. Chrání klíčenku; není určený pro doménu a nevyměňuje se spolu s HTTPS PFX. |

Port je jako číslo dveří na serveru. Doména vás dovede ke správnému domu, port ke správným dveřím. Certifikát potvrzuje jméno webu a umožní šifrované spojení. Ochranný certifikát Data Protection má jinou práci: chrání uložené klíče aplikace. Záloha samotné databáze jej nenahrazuje.

## Pošta: kterou možnost zvolit

| `Authentication` | Co vyplnit | Podmínka správné funkce |
|---|---|---|
| `None` | Host, Port, Security a SenderEmail | Správce pošty povolí odesílání z IP webového serveru a pro zadaného odesílatele. Prázdné heslo samo o sobě takové povolení nezajistí. |
| `Basic` | Navíc UserName a Password do secrets | Poskytovatel musí tento způsob přihlášení podporovat a povolit. Produkce vyžaduje StartTls nebo SslOnConnect. |
| `OAuth2` | UserName (schránka), TokenEndpoint, ClientId, ClientSecret a případný Scope | Správce identity připraví registraci a oprávnění pro `client_credentials` a SMTP. Nestačí pouze registrace pro přihlášení lidí přes Entra. Produkce vyžaduje StartTls nebo SslOnConnect. |

`StartTls` povinně přepne spojení na šifrované; `SslOnConnect` šifruje od začátku. Časté dvojice jsou 587/StartTls a 465/SslOnConnect, ale použijte hodnoty potvrzené správcem pošty. `None` nešifruje. `Auto` může vybrat režim podle serveru/portu a nezaručuje povinné šifrování; průvodce ho nenabízí a produkce ho s přihlášením nepovoluje.

`SenderEmail` je adresa odesílatele, nikoli příjemce. `SenderName` je její čitelný název. `TimeoutSeconds=30` znamená čekat nejvýše 30 sekund na jednu SMTP síťovou operaci; není to interval odesílání fronty. Prázdný OAuth2 Scope se neposílá, požadovaný rozsah musí určit správce poskytovatele.

Kontrola průvodce ověří spojení a přihlášení, **ne skutečné doručení**. Po nasazení vyžádejte potvrzovací e-mail pro testovací účet a ověřte schránku i spam. Pokud zpráva nepřijde, nestačí zvýšit timeout: ověřte účet, TLS, povoleného odesílatele a frontu podle ADMIN-GUIDE.md.

## Povinné klíče

| Klíč | Význam |
|---|---|
| Deployment:Environment | Production/Staging, shodně se službou a deployment.json. |
| ConnectionStrings:SqlServer | V secrets.json: samostatná existující DB, SQL login, Encrypt=True, TrustServerCertificate=False. Produkce odmítá LocalDB, systémové DB a integrované ověření. |
| secrets/deployment.json → ConnectionStrings:SqlServer | Jiný účet pro backup/migrace, stejný server a DB, db_owner cílové DB a CREATE DATABASE v master pro RESTORE VERIFYONLY. |
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

Runtime SQL účet potřebuje SELECT/INSERT/UPDATE/DELETE na DB. Deployment účet potřebuje `db_owner` cílové DB a navíc efektivní `CREATE DATABASE` v `master`: samotné `db_owner` dovolí zálohu, ale nestačí na povinné `RESTORE VERIFYONLY`. Preflight obě oprávnění ověří před odstávkou. Toto další oprávnění umožňuje také zakládat databáze; nejde tedy o účet omezený výhradně na cílovou DB. Nepoužívejte `sa`, `sysadmin` ani zbytečně roli `dbcreator`. Průvodce žádná oprávnění automaticky nepřiděluje.

DBA může pro existující deployment login doplnit následující oprávnění. `parking_deploy` nahraďte skutečným názvem; pokud má login v `master` již uživatele pod jiným jménem, použijte jeho existující mapování a `CREATE USER` vynechte:

```sql
USE master;
-- Pouze pokud toto mapování uživatele na login ještě neexistuje:
CREATE USER [parking_deploy] FOR LOGIN [parking_deploy];
GRANT CREATE DATABASE TO [parking_deploy];
```

Tento grant nepatří účtu aplikace. Pokud provozní politika takové oprávnění deployment účtu nepovoluje, současný automatizovaný profil nelze použít bez samostatného DBA řešení ověření záloh; nevypínejte `VERIFYONLY`. Po změně práv zopakujte preflight a ověřte zálohu na Staging. Požadavek popisuje také [Microsoft](https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/security/create-database-permission-logged).

DB, účty a SQL certifikát připravuje DBA jednorázově. Integrované ověření není automatizovaným profilem podporované: přístup pod správcem by neověřil identitu služby.

V průvodci zadáváte SQL server, název DB, jména obou účtů a hesla zvlášť. Celý připojovací řetězec sestaví sám, včetně správného zápisu hesel obsahujících uvozovky nebo středníky. `Encrypt=True` zapíná šifrované spojení a `TrustServerCertificate=False` vyžaduje ověření certifikátu SQL serveru. Chybu certifikátu řešte s DBA/IT; přepsání druhé hodnoty na True by ověření odstranilo a produkční kontrola to odmítne.

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

## Když nastavení nefunguje

| Příznak | Co zkontrolovat jako první |
|---|---|
| Změnil jsem hodnotu a nic se nestalo | Správná instalační cesta, správný soubor mimo releases, případné přepsání v secrets a provedený řízený restart. |
| Soubor nejde přečíst / je chyba JSON | Spuštění editoru jako správce, uvozovky, čárky, zdvojená lomítka v cestách. Komentáře `//` jsou pro tuto aplikaci v pořádku. |
| Selže kontrola DB | Skutečný SQL server/DB, oba účty, jejich práva, odchozí spojení a důvěryhodný SQL certifikát. Projděte report s DBA. |
| Selže HTTPS | PFX obsahuje privátní klíč, heslo je správné, certifikát nevypršel a SAN obsahuje veřejnou doménu; počítač důvěřuje řetězci a dosáhne na kontrolu odvolání. |
| Lokální kontrola funguje, veřejná ne | DNS ze serveru i uživatelského PC, otevřený veřejný port, správná doména/port a HTTPS certifikát. |
| SMTP se přihlásí, e-mail nepřijde | Povolený SenderEmail, oprávnění účtu posílat zprávy, spam a stav fronty v ADMIN-GUIDE.md. Preflight neposílá testovací e-mail. |
| Entra pole je v aplikaci zamčené | Stejný klíč je v konfiguraci nebo proměnných prostředí; konfigurační override má přednost před uloženou hodnotou v DB. |
| Po obnově nejdou přečíst integrační tajemství / e-maily | Odpovídající `data/keys`, ochranný PFX a jeho heslo. Nezakládejte náhradní prázdnou klíčenku místo obnovy. |
