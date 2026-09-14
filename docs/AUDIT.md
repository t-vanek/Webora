# Audit připravenosti D3Parking / Webora

Datum: 2026-09-14. Rozhodující podklady: Program/DI, projekty a balíčky, EF model/migrace, služby, routy a komponenty, bezpečnostní hranice, testy a deployment soubory. Dokumentace byla porovnána až s tímto stavem. Audit není záruka absence dalších chyb ani penetrační test infrastruktury.

## Stav před úpravami

Šest runtime projektů na .NET 10, dva testovací projekty, SQL Server/EF, Blazor Server + WASM, cookie Identity, volitelná Entra, SignalR/SMTP/push. Chyběl verzovaný release, deployment nástroj, readiness/verze a rollback. Technický návod zaměňoval nasazení za dotnet ef + dotnet run. LocalDB byl v základním configu; Data Protection neměla explicitně stálý aplikační identifikátor a sdílené úložiště. Dokumentace dále popisovala dynamické ceny a neaktivní bodový model.

## Nálezy

Každý záznam uvádí ID, Severity, Category, Location, Problem, Evidence, Impact, Recommendation, prioritu a stav. Umístění jsou relativní vůči repozitáři; historie před opravou je v Git diffu.

### DEPLOY-001

- **Severity:** Critical; **Category:** Deployment; **Priority:** P0; **Status:** implementováno, cílová služba vyžaduje staging ověření.
- **Location:** původní docs/TECHNICAL.md; nyní build-release.ps1 a deployment/*.ps1.
- **Problem:** Nebyl hotový artifact, preflight, řízený update ani návrat verze.
- **Evidence:** Návod používal dotnet ef a dotnet run; repo neobsahovalo provozní deployment skripty.
- **Impact:** Ruční zásahy do produkce bez ověřitelného výsledku/obnovy.
- **Recommendation:** Self-contained ZIP, manifest/hash, samostatný config, verzované adresáře, řízená služba a health; implementováno.

### DEPLOY-002

- **Severity:** High; **Category:** Database; **Priority:** P0; **Status:** ochrany implementovány.
- **Location:** Persistence/Migrations/20260812125006_RemoveLotMapEditorModel.cs, 20260820064820_ConvertReservationsToPlanner.cs; DeploymentDatabase/DeploymentCommands.
- **Problem:** Historie obsahuje drop tabulek a přepis dat, běžný binary rollback není ekvivalent DB obnovy.
- **Evidence:** Up maže MapShapes/LotMaps, další Up aktualizuje stavy rezervací a nuluje pravidla.
- **Impact:** Ztráta historických dat nebo start staré aplikace nad nekompatibilní DB.
- **Recommendation:** Přesné pending ID, review rizikových operací, ověřený backup, zákaz automatického Down a rollbacku přes jiné schéma; implementováno. Skutečný restore nacvičit s DBA.

### CONFIG-001

- **Severity:** High; **Category:** Secret persistence; **Priority:** P0; **Status:** opraveno pro nové instalace, migrace staré klíčenky je provozní úkol.
- **Location:** EntraSettingsService.cs, původní Program.cs; nyní DeploymentConfiguration.cs.
- **Problem:** DB tajemství používají Data Protection, ale chyběla explicitní trvalá klíčenka a identifikátor nezávislý na release cestě.
- **Evidence:** CreateProtector("D3Parking.EntraSettings.v1"), původně bez SetApplicationName/PersistKeysToFileSystem.
- **Impact:** Nová cesta/identita hostitele mohla zneplatnit cookies a zabránit dešifrování Entra tajemství.
- **Recommendation:** Pevný application name, data/keys, RSA ochrana a ACL; implementováno. U starého hostitele zachovat původní klíče/discriminator nebo tajemství znovu zadat.

### SEC-001

- **Severity:** High; **Category:** Stored XSS / uploads; **Priority:** P0; **Status:** opraveno.
- **Location:** OversightService.ReportDefectAsync, ReservationService.ReportBlockedSpotCoreAsync, MismatchPhotoEndpoints.cs.
- **Problem:** Typ fotografie pocházel z uživatelského vstupu; závada nepovolovala jen rastry. Endpoint vracel uložený ContentType.
- **Evidence:** new SpotDefectPhoto(... photo.ContentType ...), Results.File(... photo.ContentType).
- **Impact:** HTML/SVG nahrané jako příloha mohlo být při přímém otevření doručeno ze stejného originu správci.
- **Recommendation:** Magic-byte detekce PNG/JPEG/WebP při zápisu i čtení starých dat, nosniff, private/no-store a restriktivní CSP; implementováno a doplněny regresní testy.

### SEC-002

- **Severity:** High; **Category:** SSRF / push; **Priority:** P0; **Status:** opraveno.
- **Location:** NotificationEndpoints.IsValidSubscription, NotificationService.SubscribeToPushAsync, WebPushNotificationPublisher.
- **Problem:** Každá HTTPS adresa mohla být uložena a později použita pro serverový push POST.
- **Evidence:** Původní validace kontrolovala pouze URI scheme/délku; publisher bez omezení předal Endpoint HTTP klientu.
- **Impact:** Autentizovaný uživatel mohl vyvolat požadavky na interní HTTPS služby.
- **Recommendation:** Omezení konkrétních browser push poskytovatelů na portu 443, kontrola uložených záznamů i nových vstupů, zákaz redirectů; implementováno. Nového legitimního poskytovatele přidat vědomě s testem.

### SEC-003

- **Severity:** High; **Category:** Authorization / bootstrap; **Priority:** P0; **Status:** opraveno.
- **Location:** IdentitySeeder.SeedAdminAsync.
- **Problem:** Restart s ponechaným bootstrap configem aktivoval existující seed účet a znovu mu přidělil Administrator.
- **Evidence:** else if Status != Active → Active; následné AddToRoleAsync.
- **Impact:** Restart mohl zvrátit úmyslné zablokování nebo odebrání práv.
- **Recommendation:** Bootstrap pouze vytváří nový účet, chyby založení/role zastaví start; existující účet nemění. Ověřeno SQL regresním testem.

### START-001

- **Severity:** High; **Category:** Startup / first install; **Priority:** P0; **Status:** opraveno.
- **Location:** Program.cs a IdentitySeedingExtensions.cs.
- **Problem:** Čtení uložených Entra settings předcházelo development migraci; produkce neměla explicitní schema guard.
- **Evidence:** UseEntraIdAuthenticationAsync bylo před SeedIdentityAsync.
- **Impact:** První start mohl číst neexistující tabulky; nesprávné produkční schéma selhávalo nepřehledně.
- **Recommendation:** Migrace ve vývoji/schema kontrola v produkci před seeding/settings a background processing; implementováno.

### OPS-001

- **Severity:** High; **Category:** Configuration/diagnostics; **Priority:** P1; **Status:** opraveno.
- **Location:** původní appsettings.json, Program.cs; DeploymentConfiguration/OperationalEndpoints/ReleaseInformation.
- **Problem:** Výchozí LocalDB/localhost config, žádné health/version, pouze konzolové logy a chybějící produkční validace.
- **Evidence:** LocalDB v base configu, AllowedHosts="*", žádné provozní endpointy.
- **Impact:** Nasazení mohlo mířit na špatné prostředí; správce nedokázal určit release ani jeho připravenost.
- **Recommendation:** Povinná shared konfigurace, SQL/TLS/host/environment guardy, log rotace, ready/live/version; implementováno.

### AUTH-001

- **Severity:** Medium; **Category:** Incomplete integration; **Priority:** P1; **Status:** deaktivováno a zdokumentováno.
- **Location:** Web/DependencyInjection.AddIdentityServer.
- **Problem:** OpenIddict byl registrován jako server, ale aplikační obsluha connect endpointů a klienti chyběli.
- **Evidence:** SetAuthorization/Token/UserInfoEndpointUris + Enable*Passthrough, žádné odpovídající handlery/routy.
- **Impact:** Dokumentace budila dojem funkčního OAuth/OIDC serveru; zbytečné vývojové certifikáty v produkci.
- **Recommendation:** Netvrdit hotovou funkcionalitu; výchozí registraci vypnout a produkční zapnutí odmítnout do dokončení protokolu. Entra/cookie zůstaly zachované.

### TEST-001

- **Severity:** Medium; **Category:** Tests; **Priority:** P1; **Status:** opraveno a doplněno.
- **Location:** SQL fixtures, VoucherApprovalTests, ResidentUsagePlanTests, původní LotMap* a ReserveMapTests, WebAppFixture.
- **Problem:** SQL testy bez proměnné přeskakovaly, používaly pevná mazatelná jména DB; zastaralé feature defaults a odstraněný map editor; E2E mohlo převzít cizí localhost instanci.
- **Evidence:** Baseline 129 úspěšných/208 přeskočených; EnsureDeleted nad pevnými názvy; E2E hledalo /admin/parking/map a .map-shape.
- **Impact:** Zelená sada bez SQL negarantovala backend, souběžné testy mohly rušit jiné běhy, staré E2E testovalo neexistující produkt.
- **Recommendation:** GUID databáze, izolovaný port/host, explicitní testovací politika kreditů, migrace a bootstrap test, aktuální orientační mapa, testy integrity archivů; implementováno.

### DOC-001

- **Severity:** Medium; **Category:** Documentation; **Priority:** P1; **Status:** přepsáno podle výsledného kódu.
- **Location:** README.md, docs/TECHNICAL.md, E2E README.
- **Problem:** Dynamické ceny, body/úrovně/graf důvěry, 30denní kupón, veřejná cache mapy a produkční dotnet run neodpovídaly implementaci.
- **Evidence:** ComputeReservationCost vrací pevný BaseReservationCost, ApologyVoucherValidity=90 dní, mapa má RequireAuthorization a nemá ETag; údržba nevolá přepočet reputace/adaptivních cen.
- **Impact:** Správce i uživatelé dostávali nesprávné očekávání a neúplný instalační postup.
- **Recommendation:** README rozcestník a oddělené technické/provozní dokumenty; implementováno.

### MAIL-001 — opraveno

- **Severity:** Medium; **Category:** Durability; **Priority:** P1; **Status:** opraveno v navazující změně.
- **Location:** DurableEmailSender.cs, EmailDeliveryDispatcher.cs, EmailDeliveryWorker.cs, migrace AddDurableEmailOutbox.
- **Problem:** Účtové e-maily původně používaly paměťovou Wolverine frontu, zatímco oznámení měla SQL outbox.
- **Evidence:** Původní QueuedEmailSender/EmailHandler odstraněny; IEmailSender nyní potvrzuje SQL commit do EmailDeliveries. Obsah chrání Data Protection.
- **Impact:** Již potvrzená zpráva přežije restart; zpracování obnoví expirovaný lease. Selhání SQL při enqueue se vrací volajícímu.
- **Recommendation / řešení:** Pět pokusů s backoffem, atomické lease s ochranou proti pozdnímu zápisu, stabilní Message-ID, expirace 24 hodin a odstranění citlivého payloadu po dokončení. SQL testy pokrývají nový kontext/klíčenku, retry, souběh, přerušení, opožděný worker, expiraci a bezpečný upgrade. SMTP může po pádu mezi přijetím a zápisem výsledku doručit duplicitu; platnost tokenů se neprodlužuje. Business změna a enqueue nejsou univerzálně jedna transakce.

### SCALE-001 — zbývá

- **Severity:** Medium; **Category:** Concurrency/architecture; **Priority:** P2 pro zvolenou jednu instanci.
- **Location:** ReservationService.MaintenanceGate, memory settings cache, EntraSchemeReloader.
- **Problem:** Část koordinace/cache je procesová, přesto některé komentáře mluvily o více instancích.
- **Evidence:** static SemaphoreSlim; IMemoryCache; scheme map v procesu.
- **Impact:** Více hostů není tímto release postupem garantováno.
- **Recommendation:** Provozovat jednu instanci; před škálováním audit distribuovaných zámků/leases a cache. Bez nové infrastruktury v této změně.

### ARCH-001 — zbývá

- **Severity:** Low; **Category:** Maintainability; **Priority:** P2.
- **Location:** ReservationService (~1900 řádků), OversightService (~1700), velké Razor Reserve/LotDashboard; EntraSettingsService.GetEffective.
- **Problem:** Velké služby/komponenty kombinují více případů užití; sync fallback settings blokuje async DB při studené cache.
- **Evidence:** Rozsah souborů a GetEffectiveAsync().GetAwaiter().GetResult(); ostatní nalezené .Result typicky následují po Task.WhenAll a nejsou samy sync-over-async chybou.
- **Impact:** Náročnější revize, možné krátké blokování při inicializaci OIDC options.
- **Recommendation:** Postupný rozklad podle transakčních hranic a přednačtený snapshot; neměnit plošně business chování jen kvůli kosmetice.

## Audit dokumentace — kategorie

| Kategorie | Ověřené příklady a výsledek |
|---|---|
| Správně | .NET/Blazor/SQL, cs/en, role/skupiny, Entra konfigurace v DB, SCIM Users, kalendářní token a SQL notifikační outbox. Zachováno, zpřesněno. |
| Zastaralé | Bodové úrovně, graf důvěry, automatické adaptivní ceny, editor map; aktuální UI/maintenance je nenabízí. Odstraněna tvrzení o aktivních funkcích. |
| Chybné | Dynamická cena proti pevnému ComputeReservationCost; kupón 30 proti 90 dnům; ETag/veřejná cache mapy; hotový OpenIddict server; produkční run. Opraveno. |
| Chybějící | Contracts projekt, named handoffs, přesné provozní informace, trvalé keys, release/integrita/backup/rollback. Doplněno. |
| Nejasné | LocalDB jako produkční předpoklad, schema vs binary rollback, smíchání config/secrets/release, rozdíl obou e-mailových front, historie backfill SQL. Vysvětleno krokově. |

Staré screenshoty/output mapy jsou ponechané jako soubory, nový README je nepoužívá jako důkaz aktuálního produktu. Agentní `.codex/.claude` helpery jsou vývojové; nevstupují do produkčního artefaktu.

## Ověření navazující opravy MAIL-001

Po implementaci prošlo 365/365 aplikačních testů s SQL (bez přeskočených; samostatný PublishedReleaseTests se spouští až nad sestaveným ZIPem) a 52/52 Chromium E2E. Výsledky: `mail-full.trx` a `mail-e2e.trx` v TestResults příslušných projektů. Deset nových testů DurableEmailTests pokrývá trvalé šifrované uložení, retry/Message-ID, souběh, restart, pozdní dokončení, vyčerpaný lease, expiraci, retenci a selhání enqueue. Další test provádí skutečný upgrade předchozího schématu a ověřuje zachování dat. Migrační řetězec má nyní 44 položek.

## Ověření prvního auditu a provozní přejímka

Ověřeno na Windows x64, SDK 10.0.401, runtime 10.0.12 a nativní LocalDB 15.x:

- Závěrečná aplikační sada: 355/355 úspěšných testů, žádný přeskočený, včetně deployment příkazů a skutečného EXE z rozbaleného ZIPu. Výsledek: tests/D3Parking.Application.Tests/TestResults/audit-final.trx.
- Chromium E2E: 52 úspěšných, žádný přeskočený. Opravena chybná indikace interaktivity: test čeká na klientské OnRenderCompleted, nikoli pouze dva příchozí websocketové rámce. Testy mění jen izolovanou GUID DB.
- PowerShell: 10 kontrol syntaxe, cest, integrity ZIPu, podvržených souborů a atomické výměny stavu prošlo (`deployment/tests.ps1`). Nejde o simulaci skutečného SCM/ACL tokenu.
- Self-contained win-x64 publish a spuštění EXE bez SDK v PATH: prošlo. Dva starty ověřily readiness, /version, login, Blazor asset, log mimo release a zachovanou šifrovanou klíčenku.
- Výsledný lokální ZIP 0.1.0-audit.1: 97,5 MiB, 43 migrací, 1 177 souborů v manifestu. SHA-256 ZIPu i inventář po rozbalení prošly; integrity kontrola se opakovala po smoke testu. Balíček i .sha256 jsou v artifacts/releases.
- SQL: kompletní migrační řetězec, opakovaný seeding bez povýšení zablokovaného účtu, read-only preflight, odmítnutí změněného očekávaného schématu, selhání backupu před migrací a úspěšný BACKUP/RESTORE VERIFYONLY + migrace prošly.
- NuGet audit přímých i tranzitivních balíčků nevrátil známé zranitelné balíčky; výstup artifacts/audit-build/vulnerabilities.json. To není záruka absence zranitelností.
- `dotnet tool restore --tool-manifest dotnet-tools.json` a `dotnet ef --version` fungují. Provozní endpointy/callback cesty byly porovnány se skutečnými mapami a options, nikoli převzaty ze staré dokumentace.

Logy sestavení a zkoušek jsou v artifacts/audit-build. Testovaný release vzniká s AllowDirty a je určen pouze k lokální validaci; ostrý builder vyžaduje prověřený čistý commit. Čistý řetězec migrací neprokazuje upgrade libovolné historické DB, skutečný RESTORE databáze ani byte-for-byte reprodukovatelnost na jiném build stroji.

Tento účet nemá administrátorský token Windows, proto nelze pravdivě označit za ověřené vytvoření/start/přepnutí skutečné Windows služby, ACL pod jejím tokenem nebo produkční HTTPS/SMTP/SQL backup na cílové infrastruktuře. Skripty nesmějí tato oprávnění obcházet. Před ostrým nasazením je nutný průchod initialize → deploy → update → rollback → řízené selhání → recover na Staging s reálnými certifikáty a účty. Také Entra/SCIM, SMTP doručení, push a geokódování se ověřují proti skutečným integracím.

Nejhodnotnější další krok: provést staging přejímku, obnovu SQL backupu a cílový test doručení účtového e-mailu po restartu. Durable outbox účtových e-mailů je doplněn; jeho chování a provozní hranice popisuje MAIL-001 a TECHNICAL.md. Automatický SQL restore není úmyslně součástí nástroje, protože může přepsat novější data.
