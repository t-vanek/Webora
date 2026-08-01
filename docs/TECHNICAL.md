# D3Parking — technická dokumentace

Architektura, nasazení, konfigurace, vývoj a technické poznámky. Produktový a funkční popis
(motivační systém, kreditová ekonomika, fronta, žebříčky…) najdeš v hlavním [README](../README.md).

## Obsah

- [Architektura](#architektura)
- [Nasazení](#nasazení)
- [Konfigurace](#konfigurace)
- [Vývoj](#vývoj)
- [Technické poznámky](#technické-poznámky)

## Architektura

Řešení v **.NET 10** organizované podle Clean Architecture; tok závislostí je
`Domain ← Application ← Infrastructure ← Web`.

| Projekt | Odpovědnost | Klíčové závislosti |
| --- | --- | --- |
| `D3Parking.Domain` | Entity, hodnotové objekty, doménová pravidla. Bez frameworkových závislostí. | — |
| `D3Parking.Application` | Případy užití a Wolverine handlery zpráv. | WolverineFx |
| `D3Parking.Infrastructure` | EF Core/SQL Server perzistence, ASP.NET Identity, OpenIddict, SMTP, geokódování. | EF Core, Microsoft.EntityFrameworkCore.SqlServer, MailKit, OpenIddict |
| `D3Parking.Web` | Host: Blazor Web App (Auto), SignalR, Serilog, Wolverine, OpenIddict server, údržba. | Serilog, WolverineFx, OpenIddict.AspNetCore |
| `D3Parking.Web.Client` | Komponenty Blazor WebAssembly klienta. | — |

## Nasazení

Předpoklady jsou **.NET 10 SDK** (runtime pro produkci) a **Microsoft SQL Server** — stačí
SQL Server Express nebo LocalDB, které je součástí Visual Studia a .NET workloadu pro data.
Aplikace běží přímo na hostiteli, žádná kontejnerizace se nepoužívá.

```bash
# 1. Vytvoření/aktualizace schématu (v Development se aplikuje i automaticky při startu)
dotnet ef database update --project src/D3Parking.Infrastructure --startup-project src/D3Parking.Web

# 2. Spuštění
dotnet run --project src/D3Parking.Web
```

Připojení k databázi je v `ConnectionStrings:SqlServer`; výchozí hodnota míří na LocalDB:

```jsonc
// LocalDB (výchozí, vývoj na Windows)
"Server=(localdb)\\MSSQLLocalDB;Database=D3Parking;Trusted_Connection=True;TrustServerCertificate=True"

// Pojmenovaná instance / SQL Server Express s integrovaným ověřením
"Server=.\\SQLEXPRESS;Database=D3Parking;Trusted_Connection=True;TrustServerCertificate=True"

// Samostatný server s SQL ověřením
"Server=sql.example.com,1433;Database=D3Parking;User Id=d3parking;Password=***;Encrypt=True"
```

Pro produkci je vhodné publikovat (`dotnet publish -c Release`) a hostovat pod IIS nebo jako
službu Windows / systemd za reverzní proxy; přeposílané hlavičky se konfigurují v sekci
`ForwardedHeaders`. Migrace se v produkci aplikují explicitně krokem 1, ne při startu.

## Konfigurace

Většina chování je **uložena v databázi a editovatelná za běhu** na `/admin/parking/settings`
(`Parking.ManageIncentives`) — bez nasazování:

| Skupina | Volby |
| --- | --- |
| **Ekonomika rezervací** | základní cena, přirážka za špičku (%), přirážka za obsazenost (%), max. cena, měsíční příděl kreditů, držení místa z fronty (min) |
| **Body** | uvolnění, bonus mimo špičku, penalizace za no-show |
| **Tresty za no-show z fronty** | srážka bodů, kreditová pokuta, zákaz fronty (dní), srážka příštího přídělu |
| **Poptávkové odměny za uvolnění** | přirážka za obsazenost (%), bonus za čekajícího ve frontě, max. odměna |
| **Série a úrovně** | bonus za sérii (na úroveň), strop bonusu, hranice Stříbro/Zlato/Platina (bodů), rozklad reputace (%) + interval (dní) |
| **Výhody úrovní** | přednost ve frontě / úroveň (min), bonus k přídělu / úroveň, sleva na cenu / úroveň (%) |
| **Adaptivní ceny** | zapnout, cílová obsazenost (%), interval, zesílení, pásmo necitlivosti, max. krok, dolní/horní mez přirážky |
| **Graf důvěry** | zapnout, interval přepočtu (hodin), práh odznaku Důvěryhodný |
| **Anti-collusion** | zapnout, min. vzájemných interakcí, práh koncentrace (%), strop váhy hrany v důvěře, interval skenu |
| **Provozní dohled** | lhůty pro kritickou/vysokou/běžnou/nízkou prioritu (hodin), práh a okno opakovaných hlášení na místě, hodina denního souhrnu, lhůta na odpověď řidiče (dní), lhůta na napadení nedostavení (dní), přijímat hlášení závad od uživatelů |
| **Okno špičky** | čas začátku / konce |
| **Časování (min)** | cutoff pro uvolnění, ochranná lhůta no-show, předstih připomínky, interval údržby |
| **Rezidenti** | denní čas držení, body/hod předstihu, strop odměny, max. příděl sdílení, % násobiče, % vratky, horizont plánu využití (dní) |
| **Faktor vzdálenosti** | souřadnice parkoviště, základní body, referenční km, max. násobič |
| **Ověřování a limity** | auto-ověření + limit vzdálenosti, max. odměněných uvolnění/den, max. rozsah uvolnění (dny) |

### Microsoft Entra ID

Napojení na Entra ID se spravuje na `/admin/settings` v záložce **Entra ID** (`Settings.View` /
`Settings.Edit`), uloženo v databázi a auditované. Přihlašování a provisioning jsou dva nezávislé
přepínače; dokud není zapnutý ani jeden, aplikace běží jen s místními hesly jako dřív.

| Skupina | Volby |
| --- | --- |
| **Přihlašování** | zapnout, ID adresáře (tenant), ID aplikace (client), client secret, popisek tlačítka, authority, cesta po přihlášení / odhlášení |
| **Zakládání účtů** | párování podle potvrzeného e-mailu, zakládání účtů při prvním přihlášení, výchozí role těchto účtů |
| **Provisioning (SCIM)** | zapnout, bearer token, blokovat odebraný účet |

Změna se projeví **bez restartu**: schéma OpenID Connect se po uložení publikuje nebo odebere za
běhu. Dokud přihlašování není nakonfigurované, žádné schéma v pipeline není.

Uložení ale sladí jen tu instanci, která ho obsloužila — snapshot nastavení i mapa schémat jsou
v paměti procesu. Za víc instancemi to dorovnává `EntraSchemeSynchronizer`, který každých 30 s
načte uložené nastavení a nechá `EntraSchemeReloader` srovnat stav; když se nic nezměnilo, neudělá
nic (jinak by každý cyklus zahodil staženou discovery metadata a otevřel okno, kdy schéma
neexistuje). Změna je tak živá všude do minuty, bez distribuované cache a bez další infrastruktury.

**Tajemství** (client secret, SCIM token) se ukládají zašifrovaná přes ASP.NET Data Protection a
stránka je nikdy nezobrazí zpět — jen řekne, že existují. Prázdné pole znamená „ponechat", odebrat
se musí explicitně. Klíčenka Data Protection proto musí přežít redeploy; jinak se tajemství po
restartu nedají dešifrovat, aplikace to zaloguje jako chybu a chová se, jako by nastavená nebyla.

**Přednost konfigurace:** klíč přítomný v sekci `EntraId` (v `appsettings.json`, proměnné prostředí
`EntraId__TenantId`, vaultu…) **přebíjí** uloženou hodnotu a příslušné pole na stránce zašedne s
poznámkou proč. Prázdný řetězec se nepočítá jako nastavený. Proto v `appsettings.json` žádná sekce
`EntraId` není — vyplněná sekce s výchozími hodnotami by feature zamkla dřív, než by ho šlo nastavit.

Mapování rolí z adresáře na role aplikace zůstává na `/admin/directory`.

**Role při zakládání účtu.** Když adresář o rolích mlčí — přihlášení bez přiřazené app role, nebo
SCIM push, který role nenese — dostane *nově zakládaný* účet **výchozí role** z nastavení. Ty se
zadávají jako role aplikace, ne jako app role adresáře, takže neprocházejí mapovací tabulkou;
evidují se ale jako udělené za adresář, aby je první sync, který o rolích mluví, mohl zase odebrat.
U už existujícího účtu znamená prázdná sada rolí přesně to — odeber, co adresář dřív udělil.
`Administrator` se jako výchozí role neudělí nikdy, ani když ji někdo napíše do konfigurace, kam
validace stránky nedosáhne.

#### Souběh místního přihlášení a adresáře

Obě cesty žijí vedle sebe a uživatel si na `/login` vybere. Na úrovni **účtu** platí, že o tom, co
smí samoobsluha, nerozhoduje federace, ale **jestli účet má místní heslo**:

| Účet | Heslo | Přes adresář | Změna hesla | Reset hesla | Změna e-mailu |
| --- | --- | --- | --- | --- | --- |
| Místní | ✅ | — | ✅ | ✅ | ✅ |
| Založený adresářem (JIT/SCIM) | ❌ | ✅ | ❌ | ❌ | ❌ |
| Propojený (registroval se sám, pak se přihlásil přes adresář) | ✅ | ✅ | ✅ | ✅ | ❌ |

Propojení účet o heslo nepřipraví — kdyby ho směl mít, ale nesměl ho změnit ani obnovit, uvízl by
s přihlašovacím údajem, se kterým nejde nic dělat. **E-mail** je výjimka: ten adresáři patří vždy,
protože se při každém přihlášení přepíše z tokenu, takže jeho změna se federovanému účtu odepře na
stránce i ve službě.

> Souběh počítá s tím, že odchod ze společnosti dojde až sem — přes SCIM, který účet zablokuje a
> zavře obě cesty naráz. **Bez zapnutého provisioningu** se offboarding v adresáři do aplikace
> nedostane a propojenému účtu zůstane funkční místní heslo.

Na `/register` se firemní přihlášení nabízí taky. Bez toho je registrace pro člověka z adresáře
slepá ulička: založí si místní účet s heslem, o kterém adresář neví, a přihlášení přes adresář ho
pak odmítne, dokud e-mail nepotvrdí.

**Odhlášení** federovaného účtu jde přes `POST /account/external/signout`, který ukončí místní
session i tu v adresáři (RP-initiated logout s `id_token_hint`; ten se proto z přihlašovacího
callbacku přenáší do aplikační cookie). Bez toho by další klik na „Přihlásit se přes…" tiše
přihlásil téhož člověka zpět — na sdíleném počítači toho předchozího. Endpoint vyžaduje přihlášení
a antiforgery token, proto `/logout` federovanému účtu vykreslí obyčejný `<form>` místo `EditForm`.
Cesta zpět je `SignedOutCallbackPath` a musí sedět s registrací aplikace v Entře.

Účet, který adresář právě založil, skončí na `/account/welcome` — jeden přeskočitelný krok na SPZ.
Token ji nenese a bez ní účet vypadne z párování s vozovým parkem; krok proto po uložení volá
`SyncUserPlateAsync` i `NotifyPairableAsync`, stejně jako profil a aktivace.

**Přejmenování v adresáři** se propíše, ale jen když projde validací. Když adresář pošle adresu,
kterou tu už drží jiný účet, `UpdateProfileAsync` změnu vrátí zpět a zaloguje — nikoli zapíše
napůl. Unikátní index na `NormalizedEmail` neexistuje, takže polovičatý zápis by účtu natrvalo
rozešel zobrazovanou adresu s tou vyhledávací. Přihlášení samotné to neblokuje.

Možnosti na úrovni infrastruktury jsou v `appsettings.json`:

```jsonc
"Geocoding": { "NominatimBaseUrl": "https://nominatim.openstreetmap.org", "UserAgent": "D3Parking/1.0 (parking)" },
"Distance":  { "Provider": "Haversine", "OsrmBaseUrl": "https://router.project-osrm.org" },
"WebPush":   { "Subject": "mailto:admin@example.com", "PublicKey": "<VAPID>", "PrivateKey": "<VAPID>" },
"IdentityServer": { "SigningCertificatePath": "<pfx>", "SigningCertificatePassword": "…", "EncryptionCertificatePath": "<pfx>", "EncryptionCertificatePassword": "…" }
```

**OpenIddict certifikáty:** bez nakonfigurované sekce `IdentityServer` se používají vývojové
certifikáty (lokálně v pořádku). V produkci se ale regenerují se strojem/kontejnerem — každý
redeploy by zneplatnil vydané tokeny, proto aplikace mimo Development loguje varování, dokud
nejsou podepisovací a šifrovací PFX certifikáty nastavené.

**Web Push (VAPID):** bez klíčů je push vypnutý (přepínač ve zvonečku se neukáže). Vývojový pár je
v `appsettings.Development.json`; pro produkci vygenerujte vlastní (P-256, base64url) a soukromý
klíč držte mimo repozitář (user secrets / proměnné prostředí):

```powershell
$ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
$p = $ec.ExportParameters($true); function B64Url([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_') }
"PublicKey:  $(B64Url ([byte[]](,0x04 + $p.Q.X + $p.Q.Y)))"; "PrivateKey: $(B64Url $p.D)"
```

> **Poznámka k produkci:** odchozí přístup ke geokódovací (a případně routovací) službě musí být povolen
> síťovou politikou a je nutné respektovat pravidla Nominatimu (rate limit, identifikující User-Agent).
> Ukládání domácích adres je osobní údaj — získejte souhlas a nastavte retenční politiku.

## Vývoj

Jedinou vnější závislostí je SQL Server — aplikace nemá žádnou další infrastrukturu:

```bash
# Development automaticky aplikuje migrace a naseeduje administrátora
dotnet run --project src/D3Parking.Web
```

E-maily míří na `localhost:25` bez autentizace, což je výchozí port lokální záchytky
[smtp4dev](https://github.com/rnwood/smtp4dev) — buď desktopové sestavení, nebo .NET nástroj
(`dotnet tool install -g Rnwood.Smtp4dev`). Zachycené zprávy zobrazuje ve svém okně, případně přes
REST API na `/api/messages`. Pro produkci nastavte `Smtp:Host`/`Smtp:Port` na reálný relay
a `Smtp:Authentication` na `Basic` nebo `OAuth2`.

Bez běžící záchytky aplikace funguje dál — odeslání se jen nezdaří na pozadí, request to neshodí
(viz [Odesílání e-mailů](#technické-poznámky)).

**Databázové migrace** (prostředí `Development` je aplikuje při startu):

```bash
# aplikace nejnovějšího schématu
dotnet ef database update --project src/D3Parking.Infrastructure --startup-project src/D3Parking.Web

# přidání migrace po změně modelu
dotnet ef migrations add <Nazev> --project src/D3Parking.Infrastructure --startup-project src/D3Parking.Web
```

Nástroj `dotnet-ef` se obnoví přes `dotnet tool restore` (připnutý v `dotnet-tools.json`).

**Jednorázové opravy dat** jsou ve složce `scripts/`. Nejsou součástí EF migrací, protože závisí na
konfiguraci za běhu a pouštějí se ručně. Nasazujete-li přechod na místní čas nad existujícími daty,
je nutné spustit [2026-07-28-localtime-backfill.sql](../scripts/2026-07-28-localtime-backfill.sql) —
časy zadané před tou změnou se ukládaly, jako by wall-clock byl UTC. Skript má zkušební režim
(`@Apply = 0`) a je idempotentní.

## Technické poznámky

- **Blazor Web App** s oběma interaktivními režimy; parkovací stránky se vykreslují na serveru
  (`InteractiveServer`), zvoneček notifikací běží na WebAssembly.
- **PWA:** aplikaci lze nainstalovat na plochu telefonu i počítače. Manifest generuje endpoint
  `/manifest.webmanifest` — název a popis přebírá z Nastavení webu, jazyk z kultury requestu.
  `wwwroot/service-worker.js` má záměrně konzervativní strategii: HTML se nikdy necachuje
  (personalizovaný obsah, serverová interaktivita stejně potřebuje síť), statické assety jdou přes
  stale-while-revalidate a při výpadku sítě dostane navigace předcachovanou `wwwroot/offline.html`.
  Realtime a datové endpointy (`/_blazor`, `/hubs/`, `/api/`, `/connect/`, `/culture/`) jdou mimo
  service worker. Ikony (běžné, maskable, apple-touch) jsou ve `wwwroot/icons/`; při změně strategie
  nebo precache seznamu je potřeba zvýšit verzi cache v `service-worker.js`.

  **Runtime WASM (`/_framework/`) jde cache-first**, ale jen soubory s content fingerprintem
  v názvu — ty jsou immutable, takže nemohou být podány zastaralé; nefingerprintované
  bootstrappery (`blazor.web.js`, `dotnet.js`) chodí dál ze sítě, aby nasazení nešlo zamknout na
  starou verzi. Bez toho pravidla se ve **vývojovém** buildu stahovalo **18,5 MB při každém
  refreshi**: zvoneček notifikací je WASM island, takže se runtime bootuje na každém načtení
  stránky, a tehdy ještě přibalené netrimované balíky ikon Fluent UI (10,3 a 8,6 MB) se nevešly do
  limitu prohlížeče na velikost jedné položky HTTP cache — Cache Storage takový limit nemá.
  Publikovaný build tímhle netrpěl (celé `_framework` má 3,2 MB v Brotli), takže šlo o problém
  vývojové smyčky. Nová verze souboru se do cache uloží a předchozí fingerprinty téhož souboru se
  zahodí, aby cache nerostla s každým buildem.

  **Klientský projekt (`D3Parking.Web.Client`) záměrně nereferencuje balíček ikon.** Jeho jediná
  komponenta potřebuje dva glyfy, a balíček stál island ~22 MB z ~47 MB, které runtime při každém
  bootu přečte; path data zvonečku jsou proto inline v `NotificationBell.razor`. Po odebrání má
  `_framework` ve vývoji **28 MB místo 47 MB**. Blokování hlavního vlákna bootem to ale nezměnilo
  (~377 ms) — to dělá instanciace runtime, ne velikost ikon. Jediné, co ho odstraní, je nebootovat
  WASM vůbec, tedy přesunout zvoneček na `InteractiveServer`.

  Zvoneček si při načtení stránky bere jen to, co zavřený ukazuje (počet nepřečtených
  a ztlumení, paralelně); **seznam notifikací se dotahuje až při prvním otevření panelu**.
- **Web Push:** notifikace se vedle SignalR zvonečku doručují i do zavřené nainstalované aplikace.
  `NotificationService` publikuje přes `CompositeNotificationPublisher` (SignalR + volitelný
  `WebPushNotificationPublisher` nad `Lib.Net.Http.WebPush`), takže ztlumení a rozsah kategorií
  platí pro všechny kanály stejně. Subscriptions jsou v tabulce `PushSubscriptions` (endpoint je
  unikátní; mrtvé subscriptions se mažou při 404/410 od push služby). Přihlášení zařízení řeší
  přepínač ve zvonečku (`push.js` + `PUT/DELETE /api/notifications/push/subscription`); service
  worker OS notifikaci potlačí, když je aplikace zrovna viditelná. Konfigurace: [VAPID klíče](#konfigurace).
- **Editor mapy parkoviště:** kreslení běží celé v prohlížeči (`wwwroot/lot-map-editor.js`, ES modul
  načítaný jen na `/admin/parking/map`), Blazor vlastní model a ukládání. Tažení, změna velikosti,
  otáčení, výběr rámečkem i zoom se odehrávají nad DOMem lokálně; na server jde až **výsledek
  gesta** jednou dávkou (`MoveShapesAsync`) — po SignalR by jinak šly desítky zpráv za sekundu
  a plán o pěti stech tvarech by byl nepoužitelný. Dělba je stálá: Blazor vykresluje `<g class="map-shape">`
  s geometrií v `data-*` atributech a nechává prázdné `<g class="map-overlay">`, do kterého JS kreslí
  úchyty a gumičku — Blazor o těch uzlech neví, takže mu je překreslení nesmaže. Modul zpětně hlásí
  aktivní nástroj v `data-tool` na plátně, což je i jediný spolehlivý signál pro E2E testy, že gesto
  bude interpretováno správným nástrojem.

  Geometrie je `MapRect` — obdélník plus úhel, ne polygon (zdůvodnění v XML komentáři typu).
  Souřadnice se do SVG **musí** formátovat invariantně; česká desetinná čárka v atributu `points` je
  rozbitý polygon, ne detail zaokrouhlení. Server každý příchozí obdélník sanitizuje a validuje
  (`Sanitized()`/`IsValid()`) — čísla přicházejí z tažení myší v prohlížeči, takže NaN se nesmí
  dostat do databáze; dávka se ověřuje celá předem, aby tažení nepřistálo z poloviny.

  **Podklad mapy se určuje z bajtů, ne z hlavičky.** Deklarovaný content type přichází z uploadu,
  tedy od útočníka, a co se uloží, to endpoint servíruje zpátky ze stejné origin — uložené HTML
  vydávané za obrázek by bylo stored XSS. `ImageContentType.Detect` proto čte magic bytes a povoluje
  jen PNG/JPEG/WebP; uloží se detekovaný typ a odpověď nese `X-Content-Type-Options: nosniff`.
  SVG je vynechané schválně, byť by jako podklad bylo nejlepší: je to dokument nesoucí skript.

  **Geometrie se ořezává do souřadnic mapy** (`MapRect.ClampedInto`, měřeno přes obálku, takže
  natočený tvar zůstane celý uvnitř) při kreslení, posunu, tvorbě řady, importu i při zmenšení mapy.
  Bez toho skončí tvar mimo plátno: nejde kliknout, zoom je omezený, takže na něj nejde ani najet —
  je to tichá ztráta práce. Tvar se přitom nikdy nezmenšuje, jen posouvá; větší než mapa se přisadí
  do rohu. `MoveShapesAsync` proto vrací **uloženou** geometrii a editor si jí opraví plátno na
  místě — místo přenačtení celé kresby po každém tažení.

  Historie **Zpět** je v komponentě, ne v databázi: bounded seznam inverzních kroků (posun, vznik,
  smazání) vázaný na jednu mapu. Vrácení smazaných tvarů zakládá nové řádky (staré jsou pryč)
  a napojení na místo obnovuje jen tam, kde je místo pořád volné — undo nesmí ukrást obdélník,
  který mezitím nakreslil někdo jiný.

  **Zarovnání nemá vlastní cestu do databáze.** `MapAlign` je čistá funkce, která vrací jen nové
  pozice, a editor je posílá `MoveShapesAsync` — toutéž cestou jako tažení. Ořezání do mapy i krok
  do historie Zpět tím dostane zadarmo a nemůže se s nimi rozejít. Pořadí pro přečíslování řeší
  `MapShapeOrder.Reading`: pásy shora, v pásu zleva, s tolerancí půl typické výšky tvaru, takže
  jedna funkce zvládne řadu, sloupec i blok, aniž by se musela ptát, co bylo nakresleno.

  **Strop na velikost mapy je změřený, ne odhadnutý.** `MaxShapesPerMap` je 1500. Na plánu, pro
  který se to stavělo — 460 stání — trvá výběr jednoho stání kolem 420 ms a značka má 134 KiB;
  obojí roste s počtem tvarů, takže trojnásobek toho plánu je už vteřina a půl na klik. Editor
  kreslí každý tvar do jediného SVG přes Blazor circuit a mapa, která přeroste to, co circuit stihne
  překreslit, se nedá editovat — a jediná cesta ven by bylo ji smazat. Stejné číslo platí i pro
  import z JSON (`MaxImportShapes`), protože ten zakládá celou mapu naráz, a tedy kolem kontroly
  kapacity projde bokem. Areál, který skutečně potřebuje víc, chce mapu na parkoviště; model to umí.

  Dotazy nad tvary **musí mít určené pořadí**. Bez `ORDER BY` vrací SQL Server, co se mu zrovna
  hodí, a duplikace pak vracela kopie v pořadí závislém na zvoleném plánu — v izolaci test prošel,
  v celé sadě padal. `Layered` i `DuplicateShapesAsync` proto řadí přes `MapShapeOrder`; vedlejším
  přínosem je, že pořadí v DOMu zhruba odpovídá tomu, jak mapu čte oko.

  **Zobrazení mapy je vlastní komponenta** (`LotMapView.razor`), sdílená záměrně: plocha správce
  a později rezervace pro řidiče se ptají téže kresby na totéž a liší se jen tím, co znamená klik
  a které stavy barví — obojí je parametr. Modul editoru se v ní spouští v režimu `readOnly`, kde
  přináší jen zoom kolečkem a posun tažením.

  Ten režim se dvěma věcem vyhýbá schválně, protože obě zabíjejí klik na tvar (což je obyčejný
  Blazor handler): `setPointerCapture` přesměruje následné události včetně `click` na zachycující
  element, takže by klik dorazil na `<svg>` místo na `<g>`, a `preventDefault` na `pointerdown`
  podle specifikace Pointer Events potlačí kompatibilní myší události. Posun funguje i bez
  zachycení; označování textu při tažení řeší `user-select` ve stylu.

  Řidičova mapa v `/parking` používá tutéž komponentu s jiným významem parametrů: stav je „volné /
  obsazené" podle posledního hledání (napojení na místo samo o sobě znamená „naše", takže se nic
  dalšího nedotazuje) a klik rezervuje. Obsazené stání se kreslí barevně, ale bez `role`
  a `tabindex` — tabstop, který nic neudělá, je horší než žádný.

  **Import z SVG** (`SvgPlanReader`) není obecný renderer a netváří se tak. Hledá jedinou věc, ze
  které je parkovací plán složený — uzavřené čtyřrohé tvary — ve čtyřech zápisech, kterými je
  exportéry píšou: `rect`, `polygon`, `polyline` a `path` z rovných úseků. Ten poslední je klíčový:
  **PDF převedené do SVG nemá v sobě jediný `rect`**, každý obdélník je cesta z `M`/`L`, takže
  čtečka, která umí jen `rect`, nenajde v nejpravděpodobnějším vstupu vůbec nic.

  Tři vlastnosti reálných exportů rozhodují o tom, jestli import uspěje, nebo tiše přijde o půlku
  plánu:

  - **Jedna cesta, mnoho stání.** Konvertory běžně nacpou celou řadu do jediného `d` jako
    posloupnost podcest. `SvgPathPoints` proto vrací **seznam podcest**, ne jeden bod za druhým —
    každé `M` uzavírá předchozí a začíná další, `Z` vrací pero na začátek podcesty (na čemž závisí
    relativní `m` hned za ním). Číst cestu jako jeden tvar znamená vzít první stání a o zbytek řady
    přijít.
  - **Text bez `x` a `y`.** Atributy jsou nepovinné a výchozí je aktuální textová pozice; export
    z PDF umisťuje každý běh vlastní maticí a `x` nenapíše vůbec. Podstrom `text`/`tspan` se proto
    prochází vcelku, aby span bez vlastní pozice zdědil pozici svého `text` a neskončil v levém
    horním rohu kresby.
  - **Týž obdélník dvakrát.** Výplň a obrys téže cesty přijdou jako dva tvary. Shodné obdélníky —
    shodné na střed, rozměr i úhel v úložné přesnosti, což žádná dvě skutečná stání nejsou — se
    slučují a počítají do `Duplicates`; bez toho je každý počet na mapě dvojnásobný.

  Transformace skupin se **skládají** cestou dolů (`SvgTransform`), protože skutečná poloha stání je
  součin všech skupin nad ním; číst atributy obdélníku a skupiny ignorovat znamená položit každé
  stání jinam. Čtyři rohy se pak převádějí zpět na `MapRect` — je to inverze `MapRect.Corners`:
  sousední strany musí svírat pravý úhel a čtvrtý roh přistát tam, kam ho první tři posílají.
  Popisky se přiřazují podle toho, uvnitř kterého obdélníku text leží, se záložním hledáním
  nejbližšího do vzdálenosti 0,75 jeho rozměru (některé exporty kotví text na účaří kousek pod
  rámečkem).

  Vše, z čeho obdélník nevznikl, se počítá do `SvgPlanWarnings` a vypíše — a počítá se **i to, co
  čtečka odmítla dřív, než se k obdélníku vůbec dostala** (křivka, kružnice, cesta s obloukem).
  Čítač, který zůstane na nule, zatímco tvary mizí, je horší než žádný: import, který přijde
  o čtyřicet stání, musí to říct. Naopak `style`, `image` a další neinkoustové elementy se
  nepočítají, aby jediné varování, na které jde reagovat, nezapadlo v šumu.

  Čtečka je čistá — řetězec dovnitř, obdélníky ven — a vrací nativní souřadnice kresby; vsazení do
  souřadnicového prostoru mapy je rozhodnutí služby, ne čtečky. DTD zpracování zůstává vypnuté
  (výchozí), aby naimportovaný soubor nemohl parser přimět něco stáhnout ani rozbalit entitní bombu.

  Publikovaná mapa je nejvýš jedna, jištěno filtrovaným unikátním indexem. Přepnutí publikace proto
  **nejde** jedním `SaveChanges`: EF zápisy sdruží do dávky a index se kontroluje po příkazech, takže
  pořadí uvnitř dávky umí index porušit v půli. Odpublikování a publikování jsou dva `SaveChanges`
  v jedné transakci.
- **Lokalizace:** řetězce UI v `D3Parking.Web/Resources/SharedResource.*.resx`; serverové texty notifikací
  v `D3Parking.Infrastructure/Resources/ParkingMessages.*.resx`.
- **Autentizace:** ASP.NET Core Identity (cookie přihlášení) + OpenIddict server + RBAC dle oprávnění.
- **Časové zóny:** vše se ukládá a porovnává v UTC, ale pravidla jsou psaná v **místním čase parkoviště**
  — okno špičky, denní držení místa rezidentem i hranice dne (denní limity, uvolnění) se vyhodnocují
  v zóně z `DefaultTimeZoneId` (Nastavení webu → Regionální; bez ní se použije zóna serveru). Převody
  řeší `SiteTime` v doménové vrstvě, offset se dohledává pro každý okamžik zvlášť, takže letní čas
  sedí. Zadaný čas rezervace je místní wall-clock a do UTC se převádí až na vstupu.
- **Odesílání e-mailů:** `IEmailSender` zprávu jen zařadí do lokální Wolverine fronty
  (`QueuedEmailSender`), takže request nečeká na SMTP a nedostupný mailserver ho neshodí.
  Doručení obstará `EmailHandler` přes `IEmailTransport` (`SmtpEmailSender`) s opakováním
  po 5 s / 30 s / 2 min. Fronta je v paměti — zprávy čekající při vypnutí procesu se ztratí;
  všechny e-maily lze vyžádat znovu. Pro garantované doručení přidejte `WolverineFx.SqlServer`
  a `PersistMessagesWithSqlServer` (využije stávající databázi, žádná nová infrastruktura).
- **Údržba na pozadí:** `ParkingMaintenanceService` v intervalu `SweepInterval` řeší no-shows,
  připomínky, rekonciliaci sdílení, měsíční příděl, frontu, rozklad reputace, adaptivní ceny, graf důvěry
  a nakonec provozní dohled (viz níže). Každý krok je izolovaný — selhání jednoho nesmí přeskočit ty za ním.
- **Provozní dohled:** `OversightCase` je obálka nad signálem, nikdy jeho kopie — ukazuje na
  `OccupancyMismatch`, `CollusionFlag`, `SpotDefectReport` nebo rovnou `Reservation` (u sporu
  o nedostavení) přes `(Kind, SubjectId)` a důkazy se čtou ze zdrojových služeb za běhu. Případy
  nad signály, které vyvolalo parkoviště, **nezakládají služby, které je vyvolaly**, ale
  `IOversightService.EnsureCasesAsync` (idempotentní anti-join): hlášení vzniká na kritické cestě
  řidiče a sken v údržbové smyčce a ani jedno nesmí spadnout kvůli frontě. Případy, které otevře
  **člověk** (spor o nedostavení), vznikají přímo tím úkonem — reconciliace by neměla co dohánět,
  protože rezervace tam ležela celou dobu a nezměnilo se na ní nic než to, že se někdo ozval.
  Unikátní index `(Kind, SubjectId)` je zároveň tím, co drží pravidlo „jeden spor na rezervaci". Tentýž kód je zároveň
  migrací pro signály starší než případy. Volá se jak ze smyčky, tak při načtení fronty, aby recenzent
  nečekal na sweep.

  `OversightCaseEvent` je append-only historie; viditelnost (`Internal` / `Participants`) je jediné,
  co dělí pohled správce od pohledu řidiče, a filtruje se **v dotazu**, ne v šabloně. Zápisy ve stejný
  okamžik řadí stínový identity sloupec `Ordinal` — jedna akce jich umí zapsat víc a samotný čas by
  jejich pořadí neurčil.

  Číslo případu je z databázové sekvence `OversightCaseNumbers` (dvě založení v jednom sweepu se
  o číslo nesmí porvat). Souběh dvou recenzentů nad jedním případem hlídá stínový `rowversion` —
  poražený zápis se přečte znovu a narazí na strážce („už rozhodnuto"), místo aby přebil první verdikt.

  Co kdo **vidí** (druh případu) a co kdo **smí** (přiřadit, rozhodnout vůči osobě) nese jediný objekt
  `OversightScope`, takže obrazovka, která by kontrolu zapomněla, stejně narazí na službu. Případ druhu,
  na který volající nevidí, se tváří jako neexistující — „na případ 142 nemáš právo" už prozrazuje, že
  případ 142 existuje.
