# Architektura a vývoj

Popis vychází ze zdrojového kódu. Provozní postup je v [ADMIN-GUIDE.md](ADMIN-GUIDE.md), release v [DEPLOYMENT.md](DEPLOYMENT.md), nastavení v [CONFIGURATION.md](CONFIGURATION.md).

## Projekty

| Projekt | Skutečná odpovědnost |
|---|---|
| Domain | Entity a pravidla rezervací, kalendáře, peněženky, rezidentů, účtů a dohledu; bez frameworkových balíčků. |
| Contracts | Datové kontrakty oznámení sdílené s prohlížečem. |
| Application | Rozhraní služeb, DTO, validace, kalendářní renderer, Mapperly a Wolverine handlery; reference Domain a Contracts. |
| Infrastructure | Implementace většiny případů užití, EF Core, Identity stores, Entra, SMTP, push a geokódování. |
| Web | Program.cs, DI, cookie autentizace, autorizace, Blazor stránky, endpointy, SignalR a background služby. |
| Web.Client | WASM NotificationBell a klient oznámení; reference Contracts. |
| Application.Tests | Doménové, servisní, bezpečnostní, provozní a SQL integrační testy; referencují také Infrastructure a Web. |
| E2E.Tests | NUnit/Playwright nad skutečným hostem a izolovanou dočasnou DB. |

Názvy projektů mají prefix `D3Parking.`. Velká část aplikační logiky je v Infrastructure, nikoli v Application. Cyklické projektové reference audit neodhalil. NuGet verze jsou centrální v `Directory.Packages.props`, uzamčené závislosti v `packages.lock.json`, SDK v `global.json`.

## Funkce podle implementace

| Oblast | Implementace a hranice |
|---|---|
| Plánovač | `/parking`: časové okno/celý den, horizont, povolené dny, svátky, týdenní limit, rezervace bez check-in/out. |
| Rozpočet | `BaseReservationCost=0` vypíná kredity; jinak pevná cena. `ComputeReservationCost` ignoruje obsazenost. Obnova dorovnává cílový rozpočet podle denní/týdenní/měsíční/roční periody. |
| Fronta | Nabídky míst mají expiraci, převzetí znovu ověřuje pravidla a dostupnost. Historický DB stav `Reserved` odpovídá plánované rezervaci. |
| Rezidenti | Sdílení dní, týdenní plán, více rezidentů, ochrana sdílených rezervací, alternativní místo a adresné předání (`ResidentSpotHandoffService`). |
| Ocenění | Pozitivní trvalé příspěvky a `/parking/achievements`. Bez aktuálního veřejného žebříčku, reputačních penalizací a cenových výhod úrovní. |
| Neshody | Fotografie zablokovaného místa, přesun/refundace. Kupón jen při zapnutých kreditech; oprávněný recenzent nesmí schválit vlastní. Platnost v `ApologyVoucherValidity` je 90 dní. |
| Dohled | `/admin/parking/oversight`: vlastník, lhůty, historie, doplnění a odvolání. Staré druhy případů mohou zůstat v historii. |
| Vozidla/návštěvy | SPZ, párovací kód e-mailem, rezidence firemního vozidla, návštěvy bez účtu přes recepci. |
| Mapa | Jeden orientační PNG/JPEG/WebP v ParkingSettings, max. 12 MiB. Editor tvarů a rezervace klikáním na kreslené místo jsou odstraněné. |
| Notifikace | DB schránka, SignalR, volitelný Web Push, uživatelské preference a firemní pravidla doručování. |
| Kalendář | Vlastní `.ics` export a odběr s odvolatelným tokenem, ETag a stabilním UID/revizí. Pouze čtení. |
| Lokalizace/PWA | cs/en, parkovací stránky InteractiveServer, zvoneček WASM, offline stránka bez offline rezervování. |

Adaptivní ceny, reputace a graf důvěry se v aktuální údržbě nepočítají. CollusionService zůstává pro historické `Completed` záznamy; aktuální administrace nastavení neutralizuje. Starý sloupec ani název oprávnění neznamenají aktivní funkci.

## Autentizace a autorizace

Identity používá cookie, unikátní username a kontrolu unikátního e-mailu. Pět chybných hesel uzamkne účet na pět minut. Security stamp se u cookie i interaktivních circuitů kontroluje po pěti minutách. Produkční aplikační cookie je Secure. JSON zápisy oznámení a externí signout explicitně ověřují antiforgery token; formuláře používají antiforgery infrastrukturu Blazoru.

Role: Administrator, LotManager, FrontDesk, IncentiveCoordinator, Analyst, UserManager, Auditor, Employee. Oprávnění se skládají přes skupiny a materializují do role claims (`DefaultRoleGroups`, `DefaultPermissionGroups`). Built-in role/skupiny se synchronizují při startu. Bootstrap existujícího uživatele nepovýší ani neodblokuje. Administrativní služby chrání posledního administrátora a omezují udělování silnějších oprávnění.

Entra ID je volitelný poskytovatel: tenant a stabilní object ID, JIT a SCIM Users (ne Groups). Konfigurační sekce EntraId přepisuje odpovídající DB hodnoty; DB tajemství chrání Data Protection. Propojený účet může mít místní heslo; bez SCIM/místní blokace odchod z adresáře místní heslo nezneplatní.

**OpenIddict není hotový autorizační server:** stores a registrace protokolových cest existují, jejich aplikační obsluha a registrace klientů chybí. Ve výchozím stavu je vypnutý; podporovaný produkční profil zapnutí odmítá. Entra přihlášení je nezávislé.

## HTTP hranice

| Cesty | Přístup |
|---|---|
| GET `/health/live`, `/health/ready`, `/version` | Minimální veřejné provozní informace; ready ověřuje SQL schéma a čtení modelových sloupců. Bez stacktrace/connection stringů. |
| `/api/notifications` a podcesty | Přihlášení; seznam, počet, preference a push odběry. Přesné route/verb mapy: NotificationEndpoints.cs. |
| GET `/api/antiforgery/token` | Přihlášený WASM klient. |
| `/hubs/notifications` | Autorizovaný SignalR, cílení na uživatele. |
| GET `/api/parking/reservations/{id:guid}/calendar` | Pouze vlastní živá rezervace. |
| GET `/api/parking/calendar/{token}.ics` | Bez cookie; tajný 256bitový token, DB obsahuje pouze jeho hash. |
| GET `/api/parking/orientation-map` | Parking.View, rastrový obrázek a nosniff; žádný ETag ani veřejná cache. |
| GET `/api/parking/mismatches/{id:guid}/photo` | Parking.ReviewMismatches; detekovaný rastr, private/no-store. |
| GET `/api/parking/defects/{id:guid}/photo` | Parking.ManageSpots; ochrana i historických uploadů. |
| `/account/external/*`, `/account/signout`, `/signin-entra`, `/signout-entra` | Externí challenge/callback/signout a OIDC middleware; callback cesty lze konfigurovat. |
| `/scim/v2/Users`, `/{id}`, `/scim/v2/ServiceProviderConfig` | Zapnutý SCIM, bearer token s konstantním časem porovnání. Mapy verbů: ScimEndpoints.cs. |
| GET `/manifest.webmanifest`, `/culture/set` | Manifest a jazyk s místním návratem. |

Parkovací zápisy převážně obsluhují serverové služby přes Blazor circuit; nejde o obecné REST API. Stránky jsou deklarované přes `@page` v `Components/Account`, `Admin`, `Parking` a `Pages`.

## Persistence, souběh, integrace

SQL Server a `D3ParkingDbContext`. Seznam migrací generuje release ze sestavené assembly. Obrázky, rezervace, oznámení, audit a nastavení leží v DB; aplikace nepíše uploady do release.

DbContextFactory poskytuje kontext pro každou operaci, Identity používá scoped kontext. Kritické rezervace/refundace mají serializable transakce, rowversion a opakování rozpoznaných konfliktů. Unikátní indexy chrání SPZ/členství/deduplikaci fotek, další indexy rezervace, frontu a ledger. Seeder používá transakci a SQL aplikační zámek. Podporovaný profil je jedna instance: MaintenanceGate není distribuovaný zámek.

`ParkingMaintenanceService` spouští připomínky, plán rezidentů, rozpočet, frontu, historický collusion scan, kapacitní kampaně a dohled; selhání kroku nevyřadí následující. `NotificationDeliveryWorker` doručuje SQL outbox s lease/backoff a maže dokončené záznamy po 30 dnech. `EntraSchemeSynchronizer` obnovuje nastavení po 30 sekundách.

Účtové e-maily zůstávají ve Wolverine **paměťové** frontě: restart ztratí čekající zprávy. Notifikační e-maily mají vlastní SQL outbox. Samotné `UseEntityFrameworkCoreTransactions` nevytváří durable Wolverine storage. Přibalený Roslyn vytváří interní handlery bez nainstalovaného SDK; neprobíhá build zdrojového projektu na serveru.

Nominatim geokóduje adresy. Haversine počítá vzdálenost offline; volitelný OSRM má fallback. HTTP timeouty jsou 15 s. SMTP používá MailKit s nastavitelným timeoutem. HTTPS, SQL a SMTP prochází deployment kontrolou; Entra, push a geokódování vyžadují i funkční test na skutečné síti.

## Vývoj a testy

```powershell
dotnet restore D3Parking.slnx
dotnet tool restore --tool-manifest dotnet-tools.json
dotnet run --project src/D3Parking.Web

$env:ConnectionStrings__SqlServer = 'Server=(localdb)\MSSQLLocalDB;Database=unused;Trusted_Connection=True;TrustServerCertificate=True'
dotnet test tests/D3Parking.Application.Tests -c Release --artifacts-path artifacts/tests
dotnet test tests/D3Parking.E2E.Tests -c Release --artifacts-path artifacts/e2e
pwsh -File deployment/tests.ps1

dotnet ef migrations add Nazev --project src/D3Parking.Infrastructure --startup-project src/D3Parking.Web
```

Manifest nástroje je historicky v kořeni (`dotnet-tools.json`), proto je restore výslovný. Po uvedeném restore funguje `dotnet ef`; ověřeno příkazem `dotnet ef --version` (10.0.8). Produkční deployment tento vývojový nástroj nepotřebuje.

Bez ConnectionStrings__SqlServer se SQL testy v Application.Tests explicitně přeskočí. Testy zakládají GUID databáze, které po běhu maží. E2E automaticky volí volný loopback port a vlastní DB. BASE_URL používejte jen pro vyhrazené testovací prostředí; suite mění účty, nastavení a data. Viz [E2E README](../tests/D3Parking.E2E.Tests/README.md).

`PublishedReleaseTests` navíc vyžaduje `D3PARKING_RELEASE_APP` s absolutní cestou ke složce `app` rozbaleného testovacího release. Spouští skutečné EXE dvakrát s izolovaným Development profilem/DB/klíčenkou a PATH bez SDK, kontroluje health, login, statické assety a zachování šifrovaných keys. Bez této proměnné se tento jediný smoke test přeskočí. `DeploymentCommandTests` provádí skutečný SQL backup do dočasné místní složky; je určený pro LocalDB nebo místní testovací SQL s přístupem do ní, ne pro vzdálený produkční SQL.

Design-time kontext čte `D3PARKING_DESIGN_CONNECTION`, jinak LocalDB; nepřebírá automaticky produkční shared config. `scripts/2026-07-28-localtime-backfill.sql` je historická oprava konkrétního wall-clock/UTC problému, nikoli povinný krok čisté instalace. Na stará data jen po ověření původu a backupu.

`.codex`/`.claude` jsou historické vývojové helpery; některé umějí kontejnery. Produkční balíček je neobsahuje a tento postup je nespouští. Výše uvedené příkazy používají nativní SQL Server.
