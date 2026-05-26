# Webora — technická dokumentace

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
| `Webora.Domain` | Entity, hodnotové objekty, doménová pravidla. Bez frameworkových závislostí. | — |
| `Webora.Application` | Případy užití a Wolverine handlery zpráv. | WolverineFx |
| `Webora.Infrastructure` | EF Core/Postgres perzistence, Redis cache, ASP.NET Identity, OpenIddict, geokódování. | EF Core, Npgsql, StackExchange.Redis, OpenIddict |
| `Webora.Web` | Host: Blazor Web App (Auto), SignalR, Serilog, Wolverine + RabbitMQ, OpenIddict server, údržba. | Serilog, WolverineFx.RabbitMQ, OpenIddict.AspNetCore |
| `Webora.Web.Client` | Komponenty Blazor WebAssembly klienta. | — |

## Nasazení

Jediný předpoklad je **Docker** (.NET 10 SDK je potřeba jen pro hostitelský postup ve [Vývoji](#vývoj)).

```bash
docker compose up --build
```

Sestaví image aplikace a spustí ji vedle Postgresu, Redisu, RabbitMQ a smtp4dev. Kontejner běží
v prostředí `Development`, takže při startu aplikuje EF migrace a naseeduje účet administrátora.

| Služba | URL |
| --- | --- |
| Aplikace | http://localhost:8080 |
| Zachycené e-maily (smtp4dev) | http://localhost:5099 |
| RabbitMQ management | http://localhost:15672 (`guest` / `guest`) |

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
| **Okno špičky** | čas začátku / konce |
| **Časování (min)** | cutoff pro uvolnění, ochranná lhůta no-show, předstih připomínky, interval údržby |
| **Rezidenti** | denní čas držení, body/hod předstihu, strop odměny, max. příděl sdílení, % násobiče, % vratky |
| **Faktor vzdálenosti** | souřadnice parkoviště, základní body, referenční km, max. násobič |
| **Ověřování a limity** | auto-ověření + limit vzdálenosti, max. odměněných uvolnění/den, max. rozsah uvolnění (dny) |

Možnosti na úrovni infrastruktury jsou v `appsettings.json`:

```jsonc
"Geocoding": { "NominatimBaseUrl": "https://nominatim.openstreetmap.org", "UserAgent": "Webora/1.0 (parking)" },
"Distance":  { "Provider": "Haversine", "OsrmBaseUrl": "https://router.project-osrm.org" }
```

> **Poznámka k produkci:** odchozí přístup ke geokódovací (a případně routovací) službě musí být povolen
> síťovou politikou a je nutné respektovat pravidla Nominatimu (rate limit, identifikující User-Agent).
> Ukládání domácích adres je osobní údaj — získejte souhlas a nastavte retenční politiku.

## Vývoj

Pro ladění lze spustit jen podpůrné služby a engine z SDK na hostiteli:

```bash
# 1. Pouze podpůrné služby
docker compose up -d postgres redis rabbitmq smtp4dev

# 2. Spuštění enginu (Development automaticky aplikuje migrace a naseeduje administrátora)
dotnet run --project src/Webora.Web
```

Podpůrné služby se konfigurují přes `ConnectionStrings` v `src/Webora.Web/appsettings.json`
(`Postgres`, `Redis`, `RabbitMq`). Zapojení RabbitMQ se aktivuje jen při nastaveném connection stringu;
vyprázdnění `ConnectionStrings:RabbitMq` (a `:Redis`) spustí aplikaci jen nad Postgresem. E-maily míří na
lokální záchytku smtp4dev (`localhost:2525`, bez autentizace); pro produkci nastavte `Smtp:Authentication`
na `Basic` nebo `OAuth2`.

**Databázové migrace** (prostředí `Development` je aplikuje při startu):

```bash
# aplikace nejnovějšího schématu
dotnet ef database update --project src/Webora.Infrastructure --startup-project src/Webora.Web

# přidání migrace po změně modelu
dotnet ef migrations add <Nazev> --project src/Webora.Infrastructure --startup-project src/Webora.Web
```

Nástroj `dotnet-ef` se obnoví přes `dotnet tool restore` (připnutý v `dotnet-tools.json`).

## Technické poznámky

- **Blazor Web App** s oběma interaktivními režimy; parkovací stránky se vykreslují na serveru
  (`InteractiveServer`), zvoneček notifikací běží na WebAssembly.
- **Lokalizace:** řetězce UI v `Webora.Web/Resources/SharedResource.*.resx`; serverové texty notifikací
  v `Webora.Infrastructure/Resources/ParkingMessages.*.resx`.
- **Autentizace:** ASP.NET Core Identity (cookie přihlášení) + OpenIddict server + RBAC dle oprávnění.
- **Údržba na pozadí:** `ParkingMaintenanceService` v intervalu `SweepInterval` řeší no-shows,
  připomínky, rekonciliaci sdílení, měsíční příděl, frontu, rozklad reputace, adaptivní ceny a graf důvěry.
