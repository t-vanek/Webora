# Revize administrace parkování — 19. 9. 2026

## Rozsah a jistota závěrů

Revize existujícího D3Parking v adresáři Webora. Výchozí pracovní strom byl čistý; nebyly nalezeny
repozitářové `AGENTS.md`, uživatelský soubor instrukcí byl prázdný. Zdrojem závěrů jsou symboly
níže a testy, nikoli screenshoty nebo komentáře. Nebyl proveden commit, push, produkční nasazení,
změna produkčních dat ani instalace závislostí. Při první revizi SQL Server nebyl připojen. V navazující relaci byl na výslovný požadavek
uživatele spuštěn izolovaný SQL Server 2022 Developer; aktuální ověření je na konci dokumentu.

**Doloženo kódem** není totéž jako **reprodukováno na databázi**. První sada kontrol proběhla bez SQL; její výsledky jsou níže zachované jako výchozí stav.
Navazující ověření po zprovoznění serveru nahrazuje dřívější omezení dostupnosti prostředí. Výsledek nepředstavuje uzavření všech nalezených problémů.

## Mapa skutečné aplikace

Stack: .NET 10, serverové interaktivní Blazor komponenty s Fluent UI, EF Core a Microsoft SQL
Server, ASP.NET Identity a permission policies. WebAssembly klient obsluhuje zejména oznámení.
Administrace nemá REST CRUD API pro rezervace: událost komponenty běží na serveru a volá
Application rozhraní implementované v Infrastructure. `NavMenu.razor` a `Home.razor` zobrazují
vstupy podle oprávnění; autoritativní ochrana stránky je `HasPermission`/`HasAnyPermission`.

Cesty v tabulkách jsou relativní k `src/`; názvy symbolů identifikují implementaci i při posunu řádků.

| Oblast | Co dnes umí | Implementace | Kdo k ní smí | Problém nebo omezení |
| --- | --- | --- | --- | --- |
| Provozní přehled | Datum, hledání kódu/držitele, sekce dle prefixu, stavové filtry, detail a kalendář místa, analytika | `D3Parking.Web/Components/Admin/LotDashboard.razor`; `D3Parking.Infrastructure/Parking/LotDashboardService.GetBoardAsync/GetSpotDetailAsync/GetAnalyticsAsync` | `Parking.ViewAnalytics` | Celodenní agregát není příslib dostupnosti konkrétního intervalu ani fyzická přítomnost; detail může zobrazit jména a SPZ |
| Katalog míst | Přirozeně řazené stránky po 25, filtry stav/typ/rezidence, jednotlivé i dávkové vytvoření, typ, aktivita | `Admin/ParkingSpots.razor`; `ParkingSpotService.ListAdminPageAsync/CreateBatchAsync/UpdateDetailsAsync/SetActiveCheckedAsync` | `Parking.ManageSpots` | Pořadí všech ID/kódů se načítá do paměti; neexistuje mazání místa ani samostatná entita zóny |
| Zaměstnanecké rezervace | Vytvoření plánu, vlastní historie, zrušení/uvolnění, fronta, náhrada při neshodě | `Components/Parking/Reserve.razor`; `ReservationService` | Stránka i změny `Parking.Reserve`, vlastník dle serverového subject | Správce hledá rezervaci přes místo; není centrální administrátorský seznam všech rezervací s termínovými filtry |
| Zásah do rezervace | Atomický přesun stejného záznamu, zrušení s plnou vratkou a auditem | `LotDashboardService.MoveReservationCheckedAsync/CancelReservationCheckedAsync` | `Parking.ManageReservations` + vstup do dashboardu | Přesun neřeší změnu termínu; rezidentní výjimka zůstává rozhodovacím bodem níže |
| Návštěvy | Založení pro hosta bez účtu, seznam budoucích, zrušení; jiný fond míst | `Admin/VisitorBookings.razor`; `VisitorBookingService.BookAsync/CancelCheckedAsync` | `Parking.ManageVisitors` | Seznam není stránkovaný; rušení ověřuje čerstvý stav Booked v SQL transakci; nevzniká automatická náhrada při deaktivaci |
| Rezidentní přidělení | Kapacita členství, více rezidentů, denní přidělení, plán využití, sdílení dní, jmenovité předání | `ParkingSpotService`; `ResidentSpotService`; `ResidentAllocation`; `ResidentSpotHandoffService`; `ReservationService.AcceptHandoffAsync` | Správa `ManageSpots`, vlastní operace `Reserve` a kontrola vlastníka/člena | `OwnerId` je kompatibilní ukazatel vedle členství; je nutné posuzovat obojí; nejde o opakované rezervace se sérií |
| Vozový park | Firemní/osobní vůz, spárování řidiče, přidělení místa | `Admin/FleetVehicles.razor`; `FleetService` | `Parking.ManageFleet`, vlastní párování přes účet | Jiná cesta k rezidenci; nutno zachovat invarianty členství při dalších změnách |
| Omezení a nastavení | Neaktivní místo, globální kalendář, režim celý den/čas, pravidla rozpočtu a fronty | `ParkingSettings.razor`; `ParkingSettingsService`; `ReservationWindowRules` | `Parking.ManageIncentives`; aktivita `ManageSpots` | Není model časové blokace, uzavírky lokality ani více parkovišť; nezaváděno |
| Provozní dohled | Případy neshod/závad/podezřelých dvojic, foto, přidělení, rozhodnutí, veřejná a interní historie | `OperationsOversight.razor`, `OversightCaseDetail.razor`; `OversightService`, `OversightScope` | Rozsah podle druhu případu a příslušných policies | Přehled míst nenahrazuje frontu případů; audit případu a audit nastavení jsou dvě existující agendy |
| Uživatelé a oprávnění | Účty, stav, role/skupiny, externí mapování, úklid při odchodu | `Admin/User*.razor`, `RolesAndGroups.razor`; `UserAdminService`, `EmployeeLifecycleCleanup`, `EffectivePermissions` | `Users.*`, `Roles.*`, `PermissionGroups.*` | Historie parkování úmyslně nemá FK na Identity; po smazání účtu již nelze dohledat jméno |
| Audit a doručování | Stránkovaný audit; pravidla/fronta e-mailů, opakování selhání | `Admin/AuditLog.razor`, `NotificationDeliveries.razor`; `AuditService`, `NotificationDeliveryDispatcher` | `Audit.View`, správa doručování `Settings.*` | Audit katalogu dosud nebyl úplný; nově typ/kapacita/aktivita, nikoli automaticky všechny starší operace |
| Orientační mapa | Jeden uložený obrázek | `ParkingSettingsService.GetOrientationMapAsync`; `LotMapEndpoints` | Čtení `Parking.View`, změna `ManageIncentives` | Není editor geometrie míst/lokalit; staré názvy mapových E2E testů nedokazují existenci editoru |

Databázový model je v `D3Parking.Infrastructure/Persistence/D3ParkingDbContext.cs`, migrace a
snapshot v sousední složce `Migrations`. Zásadní vazby: rezervace odkazuje ID na místo a účet;
členství a denní přidělení mají FK na místo, přidělení na členství má `Restrict`; předání má FK na
místo `Restrict` a na rezervaci `SetNull`. Katalog nemá uživatelskou operaci hard delete.
Rezervace, místo, účetní zůstatek, položka fronty a předání používají SQL `rowversion`; návštěva jej
nemá. Unikátní index kódu místa ani `(SpotId, Date)` u uvolnění není ochrana intervalových překryvů.

`ParkingMaintenanceService.RunOnceAsync` postupně a s odděleným zachycením chyb provádí připomínky,
automatická uvolnění rezidentního plánu, příděly rozpočtu, frontu, detekci podezřelých dvojic,
kapacitní kampaně a vznik/sledování případů. `NotificationDeliveryWorker` odbavuje e-mailovou frontu.

## Oprávnění a skutečné vstupní body

Výchozí role jsou `Administrator`, `LotManager`, `FrontDesk`, `IncentiveCoordinator`, `Analyst`,
`UserManager`, `Auditor`, `Employee`. Definují je `Roles`, `DefaultRoleGroups` a
`DefaultPermissionGroups`; rozhoduje rozvinuté oprávnění, protože role lze měnit. Žádný filtr
organizační jednotky/lokality v modelu není. Správci příslušné agendy pracují s celou instalací.

| Operace | Policy | Rozsah dat | Kontrola na serveru |
| --- | --- | --- | --- |
| Přehled a detail | `Parking.ViewAnalytics` | Celé parkoviště včetně držitelů a detailu neshod | Atribut serverové stránky, `PermissionAuthorizationHandler` nad claims |
| Upravit typ/kapacitu, aktivitu | `Parking.ManageSpots` | Vybrané místo | Nové checked metody znovu čtou aktivní účet a role přes `EffectivePermissions.HasActiveUserPermissionAsync`, pak ověří verzi |
| Přesun/zrušení cizí rezervace | `Parking.ManageReservations` | Vybraná živá rezervace | Stejná čerstvá DB kontrola i u původních metod, checked metody navíc token; actor z AuthenticationState, nikoli z formuláře |
| Vytvářet místa, měnit členství | `Parking.ManageSpots` | Celá instalace | Serverový atribut stránky; starší služby samy čerstvou policy neověřují |
| Vlastní rezervace/uvolnění | `Parking.Reserve` | Subject uživatele | Serverová stránka a ve službě lookup vlastníka; cizí ID nesmí najít vlastní rezervaci |
| Návštěvy/vozidla/nastavení | `ManageVisitors` / `ManageFleet` / `ManageIncentives` | Celá agenda | Serverové stránky s konkrétní policy; nejde o veřejné mutační HTTP API |
| Provozní případ | `ReviewMismatches`, `ReviewCollusion`, `ManageSpots`, případně `ManageReservations` | Dle druhu případu | `OversightScope` i uvnitř služby; přiřazení `AssignOversight`, sankce `SanctionOversight` |
| GET foto neshody / závady | `ReviewMismatches` / `ManageSpots` | Záznam podle ID | `MismatchPhotoEndpoints.MapMismatchPhotoApi`, `RequireAuthorization`; neznámé ID vrací 404 |
| GET orientační obrázek | `Parking.View` | Jediná mapa | `LotMapEndpoints`, `RequireAuthorization` |
| GET jedné vlastní rezervace jako ICS | Přihlášení | Subject + ID rezervace | `CalendarEndpoints` volá `GetMyReservationAsync(subject,id)`; cizí záznam 404 |
| Soukromý kalendářový odběr | Tajný odvolatelný token | Pouze účet z hashe tokenu | `CalendarSubscriptionService.ResolveUserAsync`; odchod účtu odběr ruší přes cleanup, živý stav účtu se zde zvlášť nečte |
| Změny oznámení | Přihlášení | Subject uživatele | `NotificationEndpoints` + antiforgery filtr; podvržený user ID se nepřebírá |
| Role a účty | Konkrétní `Users.*`, `Roles.*`, `PermissionGroups.*` | Celá instalace | Existující kontrola, že nelze delegovat více práv než má aktér; ochrana posledního administrátora |

Cookie autentizace a Blazor circuit nejsou libovolně volatelné REST handlery. `Program` zapíná
antiforgery; explicitní mutační HTTP endpointy oznámení validují token. Foto a exporty jsou GET.
Testy `ParkingEndpointSecurityTests` volají skutečně mapované endpointy přes HTTP s testovací
autentizací a deterministickými službami: ověřují policies, oddělení foto oprávnění, subject/ID
exportu a neplatný/platný CSRF. Neprokazují SQL implementaci těchto služeb ani kompletní cookie login.

## Pravidla parkování a čas

- Rezervace je plán, nikoli důkaz příjezdu. Aktivní `Reserved` a kompatibilní `CheckedIn` blokují;
  `Cancelled` a `Released` novou rezervaci neblokují. Proběhlý `Reserved` se nemusí přepsat na jiný
  stav: historii určuje také konec intervalu. Proto jej odchod účtu nesmí dodatečně rušit/refundovat.
- Intervaly jsou polouzavřené: překryv `StartUtc < end && EndUtc > start`. Navazující interval je
  přípustný. `SiteTime.Day` vrací místní půlnoc až další půlnoc, tedy i 23/25hodinový den.
  `ReservationWindowRules` připouští celý místní den nebo časové okno v jednom dni podle režimu;
  koncový den používá `end.AddTicks(-1)`. Interpretace uložených termínů nebyla změněna.
- Dostupnost pro běžné uložení vyhodnocuje `ReservationService.ReserveCoreAsync` spolu s
  `AvailableSpotIdsAsync`, `ResidentAllocation`, kalendářem a aktivními přidrženími fronty.
  Rezident může mít konkrétní dny přidělené přes členství; ostatní využijí místo při uvolnění
  dotčených dní. Rozpočet, globální kalendář a již zavedené limity zůstávají zachované.
- Návštěvnická místa jsou oddělený fond s vlastní tabulkou a kontrolou překryvu
  `VisitorBookingService.BookAsync`. Přechod typu musí chránit obě agendy. Nově jej jedna
  transakce odmítne při živé rezervaci, návštěvě, přidržení nebo nepovolené rezidenci.
- Deaktivace blokuje nové plány a zachovává existující rezervace, návštěvy a historii. Neprovádí
  automatický přesun ani vratku. Nejde o časově plánovanou uzavírku.
- Fronta nově drží SQL serializable transakci od dostupnosti přes expirace až po uložení nabídek;
  deadlock/rowversion vede k novému celému rozhodnutí. Přidržení blokuje jen svůj interval.
  Zprávy se doručují až po commitu a mimo opakování rozhodnutí.
- Přesun mění `SpotId` existující rezervace, zachovává ID, cenu, kalendářovou identitu a historii.
  Kontrola cíle i zápis jsou v jedné serializable transakci. Zrušení, vratka, kupón a audit jsou
  v jednom `SaveChanges`. Nově obě operace odmítají původní token ze zastaralého detailu.

**Jednotný cíl dostupnosti:** jeden serverový rozhodovací postup musí přijímat uživatele, místo a
interval a vracet důvod nedostupnosti (neaktivní, mimo kalendář, rezervace, návštěva, přidržení,
rezidentní den). Náhled je informativní, uložení jej znovu provede v DB transakci. V této změně
byla sjednocena intervalová ochrana fronty a bezpečnost přechodu mezi fondy. Úplné vytažení všech
pravidel z rezervací, matcheru, návštěv a dashboardu do společného helperu je **návrh**, nikoli
hotová implementace; vyžaduje nejprve rozhodnout rezidentní výjimku při administrátorském přesunu.

## Nálezy podle priorit

„Kód“ znamená doložený tok/rozpor bez SQL reprodukce. Závažnost nezohledňuje snadnost opravy.

| Priorita | Důkaz a scénář | Dopad | Oprava a stav |
| --- | --- | --- | --- |
| P0 | Dashboard měl atribut `ViewAnalytics` a nechráněné tlačítko deaktivace volající `SetActiveAsync`; služba nekontrolovala aktéra. Přihlášený Analyst otevře detail a deaktivuje místo. Kód, ne živý útok. | Čtenář může odstavit provozní kapacitu | **Implementováno:** omezení akcí podle policy + čerstvá DB kontrola v checked příkazech; SQL denial scénáře **prošly** |
| P1 | `EmployeeLifecycleCleanup.CleanOperationalAsync` vybíral všechny `Reserved/CheckedIn` bez konce; minulý zaplacený plán zůstává Reserved | Odchod účtu mění minulost a refunduje spotřebovaný kredit | **Implementováno:** výběr jen `EndUtc > now` i v náhledu dopadu; SQL regrese historie **prošla** |
| P1 | `ParkingSpots.ChangeTypeAsync` odesílal starý kód/poznámku; původní `UpdateAsync` četl novou rowversion až při uložení. A načte, B změní, A odešle | Tiché přepsání novějších údajů | **Implementováno:** zobrazený token v DTO, atomický checked update kapacity/typu/detailů, srozumitelný konflikt; SQL scénáře **prošly**; výsledek E2E níže |
| P1 | Původní admin přesun/zrušení přijímal jen ID; dvě potvrzení z téhož starého kalendáře prošla postupně | Druhý zásah mění jinou situaci, než správce potvrdil | **Implementováno:** token rezervace v kalendáři, kontrola před změnou a zachování tokenu při retry; SQL stale a souběžné scénáře **prošly** |
| P1 | `ProcessQueueCoreAsync` četl snapshot mimo transakci, chránil jen queue rowversion a statický semaphore. Mezi čtením a nabídkou se vloží rezervace | Nabídka již zabraného místa; procesový zámek nespolupracuje s rezervacemi ani další instancí | **Implementováno:** serializable celé rozhodnutí + retry; deterministický SQL test blokujícího insertu **prošel** |
| P1 | `ParkingSpotService.UpdateAsync` testoval typ/budoucí rezervace mimo transakci, neviděl nabídku fronty | Souběh může převést rezervované místo do druhého fondu | **Implementováno:** obě tabulky + přidržení + členství v transakci, úzké zachycení unique/concurrency chyby; SQL race a obě agendy **prošly** |
| P1 | `LotDashboard.ReloadBoardAsync` ponechával stará data pod novým datem a dovoloval pozdnímu výsledku přepsat nový | Správce zasahuje podle jiného dne; chyba načtení nedá důvěryhodný výsledek | **Implementováno:** zneplatnění dat při načítání, pořadová čísla požadavků, chybový stav a opakování; běžný přehled vizuálně ověřen, síťové selhání v UI dosud nesimulováno |
| P2 | Matcher držel `HashSet<SpotId>` pro všechna časová okna | I navazující čekatel nedostal volnou kapacitu | **Implementováno:** intervalové porovnání stejné jako překryv rezervací; SQL boundary scénáře **prošly** |
| P2 | Kapacita rezidentů se zapisovala při psaní každé číslice; rizikové akce bez vysvětlení; opakované kliknutí během zápisu | Nechtěné mezistavy, nejasný dopad a opakované odeslání | **Implementováno:** koncept + Uložit, busy do načtení výsledku, potvrzení zrušení/deaktivace, viditelné zprávy v dialogu |
| P2 | `RolesAndGroups.OnTabChanged` reagoval i po otevření detailu. E2E navigační záznam: katalog → detail → katalog; screenshot potvrdil návrat do seznamu | Správce ztrácí právě otevřený detail role/skupiny | **Implementováno:** opožděná událost smí navigovat jen dokud aktuální URL patří katalogu; živá regrese v navazujícím ověření |
| P2 | README uváděl dashboard ManageSpots, role Viewer/Editor a fyzicky obsazené místo | Chybné očekávání oprávnění a významu čísel | **Implementováno:** aktualizovaný postup a explicitní vysvětlení plánů; screenshoty nebyly přegenerovány |

## Zvolený návrh a workflow

Zachováno členění: **Plocha** pro denní provoz, **Místa / Návštěvy / Vozový park** pro konkrétní
agendy, **Dohled** pro lidská rozhodnutí a **Pravidla a ceny** pro méně časté nastavení. Nebyly
přidány nové sekce, grafy, senzory, platby, role, kvóty ani organizační vrstvy.

| Problém → změna | Přínos | Dotčené části | Riziko | Ověření |
| --- | --- | --- | --- | --- |
| Čtenář mohl měnit → kontrola aktuální policy při příkazu | Odebrané právo nebo zablokovaný účet nemění data | EffectivePermissions, spot/dashboard služby, UI | Jeden malý DB dotaz navíc; staré serverové API katalogu ponecháno pro kompatibilitu | Denial testy včetně změny ID a odebrání role |
| Zastaralý formulář → očekávaná rowversion z načtených dat | Novější práce zůstane zachovaná | DTO, služby, UI | Plán rezidenta také mění verzi místa, proto může vyvolat oprávněný konflikt | Dvě stránky, dva DB požadavky, zachování auditu |
| Oddělená kontrola dostupnosti → serializable rozhodnutí | Žádná částečná změna či překryv z race | Queue matcher, změna typu, existující admin move | Vyšší konkurence může vést k deadlocku; omezený retry nebo zpráva pro opakování | SQL lock test, race dvou kontextů, fault injection před commitem |
| Nejasný stav → viditelné načítání/chyba a explicitní dopad | Správce rozezná úspěch, nejistotu a skutečné prázdno | Dvě stávající Razor stránky a cs/en prostředky | Rozšíření stavového kódu, nutná vizuální QA | E2E sestavení; manuální scénáře níže |

Současný/cílový postup: hledání místa zůstává podle kódu a data, ale při přepnutí data se čeká na
nový výsledek. Přesun stále probíhá z kalendáře místa a aktualizuje tutéž rezervaci; potvrzení se
vztahuje na konkrétní načtenou verzi. Deaktivace nejdříve vysvětlí zachované plány a teprve poté
uloží stav. Kapacita přestala být automatický zápis při psaní: správce zadá hodnotu a uloží ji.
Konkrétní návod je v [README – Plocha parkoviště](../README.md#plocha-parkoviště-pro-správce).
Stejný postup je dostupný také v `/help`, jehož správcovské kapitoly respektují příslušná
oprávnění. Po příkazu dashboard znovu načte plochu i otevřený boční detail a již načtené
analytické údaje; chybová zpráva načítání nepřepisuje výsledek samotného příkazu.

## Neuzavřené body a rozhodnutí

- **P1, pravidlo k rozhodnutí:** `LotDashboardService.FreeSpotsForWindowAsync` výslovně obchází
  rezidenci, na rozdíl od `ReserveCoreAsync`. Audit zaznamená přesun, ale není explicitní souhlas
  s odebráním rezidentního dne ani zobrazení takového dopadu. Zůstalo stávající chování; nejde o
  potvrzený požadavek. Před další úpravou rozhodnout, zda má admin respektovat přidělené dny nebo
  zda má existovat výslovná autorizovaná výjimka. Nepřidávat skryté privilegium.
- **P1, rozsah osobních údajů k rozhodnutí:** role Analyst je komentována jako „numbers only“, ale
  `ViewAnalytics` otevírá jména a detail neshody včetně SPZ. Kód to povoluje; požadovanou hranici
  anonymní analytiky nelze z názvu role odvodit. Není opraveno plošným odebráním dat existujícím rolím.
- **P1, starší data:** oprava odchodu účtu chrání budoucí provedení cleanupu. Pokud už stará verze
  změnila minulou rezervaci na Cancelled a vrátila kredit, tato revize záznam automaticky
  neopravuje. Případnou obnovu rozhodnout až podle auditu/ledgeru a zálohy; produkční data nebyla čtena.
- **P1, provozní ověření:** celá SQL sada již prošla na skutečném enginu (viz navazující ověření).
  Zbývá test migrací ze zálohy existující instalace a vizuální kontrola celé aplikace.
- **P2:** členství/nastavení/fleet nejsou kompletně pokryty tokenem z UI a čerstvou DB autorizací
  příkazu. U založení návštěvy doplněna čerstvá autorizace a srozumitelný SQL konflikt
  (viz samostatné ověření návštěv níže). Navazující celek doplnil stejné záruky u rušení návštěv.
- **P2:** detail místa odvozuje „dnes“ z rezervací načtených pro parametr `from`; při otevření
  jiného období může dnešní souhrn postrádat skutečné dnešní plány. `bookedDates` navíc vylučuje
  jen Cancelled, nikoli Released. Nález z kódu; další samostatný celek sjednotit detail s vybraným
  dnem a pravidly stavů, doplnit SQL test. Nepovažovat denní dlaždici za kontrolu při uložení.
- **P2:** ukazatele analytiky používají dnešní kapacitu a krátkou lokální cache agregátů; nejde o
  historii kapacity ani autoritativní cache dostupnosti. Více instancí má nezávislou invalidaci.
  Nebyla přidána další cache. ListAdminPage načítá všechny kódy pro přirozené řazení, visitor list
  nemá stránkování, resident picker limituje 500 účtů. Nebyl naměřen produkční problém výkonu;
  indexy ani cache nebyly přidány naslepo.
- **P2:** úspěšná mutace a následná notifikace nejsou všude jeden odolný celek. Selhání zprávy po
  commitu může zobrazit chybu přesto, že data byla uložena; nejprve obnovit zobrazení před opakováním.
  Nový matcher neopakuje již commitnuté rozhodnutí kvůli chybě oznámení, ale atomický outbox pro
  všechny parkovací operace není v tomto rozsahu zaveden.
- **P2:** cleanup uvolněných dní používá UTC datum, jiné rezidentní operace site timezone;
  přechod přes místní půlnoc vyžaduje vlastní regresi. Interpretace uložených dnů zde nezměněna.
- **P2, rozsah UI obnovy:** chybové stavy byly doplněny pro seznam míst, jeho mutace a rezidentní
  kandidáty, dashboard, detail, přesunové cíle a analytiku. Živé náhledy hromadného vytvoření
  (`RefreshSeriesPlanAsync/RefreshListPlanAsync`) a další agendy vozidel, dohledu a auditu
  stále obsahují načítání bez obdobného zachycení selhání. Obecné tvrzení, že je opravena veškerá
  administrace při výpadku, by nebylo doložené. Načítání agendy návštěv je doplněno v navazujícím celku.
- **P2, dohledatelnost a klávesnice:** `NavMenu.razor` nabízí Dohled jen pro ReviewMismatches/
  ReviewCollusion, zatímco stránka dovoluje i ManageSpots/ManageReservations. U vlastní role
  s posledními oprávněními může chybět navigační vstup. `AdminDialog.razor` přesune fokus do
  dialogu a reaguje na Escape, ale nemá zachycení ani obnovení fokusu. Katalog nemá UI změny
  kódu/poznámky a mobilní karta nenabízí změnu typu; nejde o nově doplněné funkce.
- **P2, historický audit návštěv:** stará zrušení bez auditní události nelze zpětně přiřadit
  konkrétnímu správci z CreatedById (to je autor založení). Nové ruční i kalendářní rušení auditují
  konkrétní návštěvu; staré auditní údaje se nevymýšlejí ani zpětně nedoplňují.
- **P2, rozdíly dokumentace:** původní README sliboval dynamickou cenu a 30denní kupón, ale
  `IncentivePolicy.ComputeReservationCost` vrací pevný základ a `ReservationService.ApologyVoucherValidity`
  je 90 dní. Text README byl opraven podle kódu; tato revize neměnila ceny, platnost kompenzace ani
  nároky. Historická konfigurační pole a screenshoty zůstávají a nejsou důkaz aktuální funkce.
- **P3:** nejsou samostatné lokality/zóny, časové uzavírky ani opakované série rezervací; nejde
  automaticky o chybějící požadované funkce. Starší mapové testy a screenshoty vyžadují samostatnou
  aktualizaci; názvy souborů nejsou důkaz funkčnosti.

Nebyly nalezeny mockované produkční výsledky v upravených cestách; testovací Fake služby jsou jen
v testech. Doložené duplicitní pravidlo je dostupnost v Reserve/Queue/LotDashboard/Visitor,
a obnova kupónu v ReservationService a LotDashboardService. Historické enumy CheckedIn/NoShow
neznamenají, že existuje současné potvrzení příjezdu. Nevhodné chování ovládání je doloženo
u nechtěného autosave kapacity a chybových stavů; další tlačítka nelze bez živé UI kontroly označit
za experimentálně funkční ani nefunkční.

## Ověření a provoz

Výchozí stav: `dotnet test ...D3Parking.Application.Tests.csproj --no-restore` — **130 passed,
212 skipped, 0 failed**. SQL nebyl nahrazen in-memory providerem. Nové SQL scénáře používají
skutečné služby a SQL Server, včetně současných kontextů, stejného snapshotu, návratu celé změny
při chybě a auditu. Nové testy záměrně vyžadují vyhrazený SQL server; podrobnosti spuštění v
[TECHNICAL.md](TECHNICAL.md#ověření-revize-administrace).

Kontroly první revize před zprovozněním SQL Serveru:

| Kontrola | Výsledek |
| --- | --- |
| `dotnet test tests/D3Parking.Application.Tests/D3Parking.Application.Tests.csproj --no-restore` | **138 passed, 237 skipped, 0 failed**, celkem 375; zahrnuje 8 nových HTTP bezpečnostních testů |
| `dotnet build D3Soft.D3Parking.slnx -c Release --no-restore` | **Úspěch, 0 chyb, 0 varování**, včetně E2E projektu |
| `dotnet ef migrations has-pending-model-changes --no-build --project src/D3Parking.Infrastructure --startup-project src/D3Parking.Web` | **Žádné změny modelu od poslední migrace**, bez kontaktu s produkční databází |
| `git diff --check` | Úspěch |
| Nové SQL testy, rozšířené SQL testy dashboardu | Zkompilováno, **přeskočeno**, SQL Server chybí |
| Playwright `ParkingAdministrationTests` | Zkompilováno, **nespuštěno**; bez funkční lokální/testovací DB nebyla aplikace spouštěna ani vizuálně kontrolována |

Manuální/UI přejímka na testovací instalaci:
1. Analyst: otevřít detail, nevidět změnové akce; oprávněný správce je vidí. Odebrat jeho policy
   v druhé relaci a zkusit již otevřenou akci — zamítnutí bez změny dat.
2. Dvě relace: otevřít stejné místo/rezervaci. První uloží; druhá dostane konflikt, novější stav
   zůstane. Přesun na již zabraný cíl nezmění původní rezervaci, platbu ani historii.
3. Kapacita: napsat několik číslic, druhá relace vidí původní hodnotu až do stisku Uložit;
   nelze nastavit kapacitu menší než počet členů.
4. Deaktivace: zrušit potvrzení, nic se nezmění; potvrdit, nové rezervace jsou odmítnuté,
   stávající plány zůstávají a audit určí správce a místo.
5. Síť/DB selhání: měnit datum rychle, zpomalit odpovědi; stará odpověď nepřepíše nové datum,
   chyba zobrazí opakování, nikoli „vše volné“. Projít klávesnicí dialogy a obě jazykové varianty.
6. Odchod testovacího účtu: minulá a budoucí zaplacená rezervace; zachovat minulý stav a
   spotřebovaný rozpočet, zrušit pouze dosud neskončené plány podle stávajícího lifecycle.

Data a nasazení aplikace: žádná nová migrace, sloupec ani produktová závislost či konfigurační
položka. Použita existující rowversion. E2E projekt nově odkazuje na existující Infrastructure
pro opt-in přípravu syntetických dat; jeho lokální přepínač a provoz jsou popsány níže. Nové metody jsou přidané, původní signatury zůstávají; web používá checked varianty.
Původní binární verze umí stejné schéma, ale návrat vrací původní chyby. Publikovat všechny projekty
společně ze sestavovacího stroje; produkční server neprovádí build. Doplněný postup je v
[TECHNICAL.md – Nasazení](TECHNICAL.md#nasazení).


## Navazující ověření na běžícím SQL Serveru

Dne 19. 9. 2026 byla na požadavek uživatele spuštěna lokální instance SQL Server 2022 Developer
**16.0.4265.3**. Existující rootless Podman publikuje jen `127.0.0.1:14333`; aplikace používá
samostatnou databázi `D3Parking_LocalTest`. Připojení a logy zůstávají mimo Git. SMTP zachytává
lokální testovací pošta, žádné zprávy se neposílají skutečným příjemcům. Postup opětovného spuštění
je v [TECHNICAL.md](TECHNICAL.md#lokální-testovací-instance-sql-serveru-19-9-2026).

První úplný SQL běh: **363 prošlo, 12 selhalo, 0 přeskočeno**. Selhání byla rozebrána:

- Nový test blokování dostal očekávanou SQL chybu 1222, ale jiný obal EF výjimky. Test nyní dovolí
  obalovou výjimku, stále přísně vyžaduje `SqlException.Number == 1222` a ověřuje následnou ochranu
  nabídky před rezervací; invariant nebyl oslaben.
- `VoucherApprovalTests` a `OversightCaseTests` spoléhaly na zapnuté kredity/detekci koluze,
  ačkoli již výchozí HEAD má tyto funkce vypnuté. Fixtures nyní explicitně zapínají testovanou
  funkci. Nabídka v testu fronty nově respektuje existující zákaz časového okna přes půlnoc.
- `ResidentUsagePlanTests` sdílely aktivní plány mezi testy; globální sweep počítal i plán z
  předchozího testu. Každý scénář nyní začíná v čisté vyhrazené testovací databázi.

Po těchto opravách přípravy testů proběhla znovu **celá aplikační sada: 375 prošlo, 0 selhalo,
0 přeskočeno**. TRX je v soukromém runtime adresáři `results/sql-verified.trx`.
Produktová pravidla nebyla měněna kvůli očekávání testů.

Samostatně prošlo **18 testů** fronty, správy míst a zachování historie včetně deterministického
blokování konkurenčního insertu, dvou souběžných editací, přechodu typu vs. rezervace a rollbacku
před commitem. Nové databázové zámky jsou tedy nyní ověřené na skutečném enginu. Nejde o zátěžový
nebo vícehostový produkční test; nebyla provedena migrace kopie reálných historických dat.

V živém prohlížeči bylo ověřeno přihlášení, načtení plochy a detailu místa, čitelnost vysvětlení
plánované dostupnosti, potvrzení deaktivace a zrušení tohoto dialogu beze změny dat. Vizuálně byl
zkontrolován přehled a dialog v tmavém režimu. Vyhledávání členů role a detail skupiny oprávnění
byly rovněž ověřeny proti aplikaci. Nejde o kompletní vizuální QA všech obrazovek a šířek.


Nové `ParkingAdministrationTests` byly samostatně provedeny přes živou aplikaci: **3 prošly,
0 selhalo, 0 přeskočeno**. Dva nezávislé Blazor okruhy ověřily, že koncept kapacity se neukládá
při psaní a zastaralý zápis vrátí konflikt bez přepsání novější hodnoty. Další testy ověřily
zrušení i potvrzení deaktivace s aktuálním stavem seznamu a vysvětlení fyzické přítomnosti.
TRX: `results/e2e-admin-verified.trx`.


První úplný E2E běh na lokální aplikaci měl **42 úspěchů a 34 selhání ze 76 scénářů**.
Patnáct scénářů stále míří na odstraněný mapový editor; nebyly skrytě označeny jako přeskočené
ani obnoveny jako produktová funkce. Následující společné běhy používají výslovný filtr těchto
tří tříd, uvedený v [E2E README](../tests/D3Parking.E2E.Tests/README.md).

Testy byly upraveny podle skutečných komponent: čekání na dokončené filtrování a hydrataci,
aktuální taby a odkazy, odeslání číselné hodnoty přes `change`, kontrola nativního tlačítka
uvnitř Fluent wrapperu. Rezidentní scénáře si nyní připravují vlastní účet, dvě místa,
rezervaci, uvolnění a přesný kalendář v opt-in lokální testovací databázi. Po doběhu obnoví
měněná nastavení a odstraní vlastní záznamy. Doporučené místo je plnohodnotný výsledek hledání;
rezervační test ho nyní zahrnuje a stále vyžaduje výhradně vlastní testovací místo. Kalendář
má v pondělí dvě karty (rezidentní stav a další rezervaci), což potvrdil screenshot i rozměry;
rovnost výšek se proto kontroluje mezi odpovídajícími strukturami. CSS se kvůli testu neměnilo.

Zbývající dvě selhání detailů oprávnění byla naopak **reprodukovaná chyba aplikace**: navigace
přešla z katalogu do správného detailu a následně dvakrát zpět. `RolesAndGroups.OnTabChanged`
nyní ignoruje opožděné události po opuštění katalogu. Původní E2E scénáře ověřují skutečný
přechod kliknutím i obsah detailu. Diagnostické snímky a navigační záznam jsou v soukromém
runtime adresáři `diagnostics`; obsahují jen lokální testovací data.


### Konečný výsledek navazujícího ověření

| Kontrola | Výsledek | Důkaz v soukromém runtime adresáři |
| --- | --- | --- |
| Celá aplikační sada s dostupným SQL Serverem | **375 prošlo, 0 selhalo, 0 přeskočeno** | `results/sql-verified.trx` |
| Nová administrace ve skutečném prohlížeči | **3 prošly, 0 selhalo**; zahrnuty také ve společném běhu | `results/e2e-admin-verified.trx` |
| Reprodukce opravené navigace rolí/skupin po restartu webu | **2 prošly, 0 selhalo**; před opravou obě selhaly | `results/e2e-navigation-fixed.trx` |
| Společný E2E běh současných obrazovek s explicitním vyřazením 15 odstraněných mapových scénářů | **61 prošlo, 0 selhalo, 0 přeskočeno**, 2 min 41 s; včetně všech 9 rezidentních scénářů a jejich úklidu | `results/e2e-final.trx` |
| `dotnet build D3Soft.D3Parking.slnx -c Release --no-restore` po konečné změně | **Úspěch, 0 chyb, 0 varování** | `build-release.log` |
| `git diff --check` | **Úspěch** | pracovní strom |

Výsledek 61/61 je výslovně **filtrovaný běh**, nikoli zelená neomezená sada všech 76 E2E testů.
**P2: zbývá aktualizovat nebo odstranit 15 testů vyřazeného mapového editoru** po samostatné revizi
jejich účelu. Dříve uvedené rozhodovací body a omezení produkčního ověření tím nejsou uzavřené.
Lokální SQL a aktualizovaný web zůstaly po testování spuštěné na pozadí. Žádný commit, push,
produkční zásah ani změna nasazovacího systému nebyly provedeny.

## Návrh jednoduššího zakládání míst — navrženo, dosud nezapojeno do aplikace

Následující návrh vznikl na samostatný požadavek uživatele po dokončení předchozích testů.
Interaktivní ukázka používá výhradně vlastní ukázková data; nevytváří záznamy v SQL.
Zdroj návrhu je `output/proposals/parking-spot-creation.html`. Tato část není popisem již
implementovaného formuláře.

### Co zjednodušit

Současný dialog `ParkingSpots.razor` odděluje Jedno místo, Generátor řady a Vložit seznam.
Řada zobrazuje současně sedm polí včetně oddělovače a doplnění nulami. Navíc `_serPlan` začíná
prázdný a náhled se počítá až po změně vstupu; po pouhém otevření tedy není připraven ani
výchozí rozsah. Po úspěchu dialog zůstává otevřený nad obnoveným seznamem. Tyto kroky a
volby zbytečně zatěžují běžné založení.

Doporučený základ je jediný formulář **Označení / první označení + počet míst**, s výchozím
počtem 1. Typ zůstane viditelný a předvolený na Standardní; poznámka bude rozbalitelná.
Příklady: `A-12` × 1 → jedno místo, `A-01` × 20 → `A-01…A-20`, `U výtahu` × 1 → toto
přesné označení. Více míst se z označení odvozuje pouze při jednoznačném číselném konci;
písmeno na konci ani chybějící číslo se neodhaduje. Nuly, mezery a oddělovač zachovat podle
zadaného vzoru. Všechna výsledná označení musí před uložením projít stejnou validací jako
vlastní seznam. Stávající pokročilý generátor více řad zůstane vedlejší možností, nebude
odstraněn. Ukázka se soustředí na běžné zadání a vložení seznamu.

| Změna | Přínos a cílové chování | Dotčené části / ověření při realizaci |
| --- | --- | --- |
| Sloučit jedno místo a běžnou řadu | Není nutné nejprve zvolit režim; pro řadu stačí první kód a počet | Stávající dialog + malý parser vzoru nad současnými dávkovými operacemi; testy číselného konce, nul a limitů |
| Živý náhled včetně duplicit | Před potvrzením je jasné, co vznikne; tlačítko např. „Vytvořit 18 míst“, existující kódy se přeskočí beze změny | `PreviewBatchAsync`, pořadová čísla odpovědí; starší odpověď nesmí přepsat novější zadání |
| Jedno předvídatelné dokončení | Po úspěchu dialog zavřít a ukázat právě vytvořená místa se zprávou o výsledku | Návrat skutečně vytvořených ID z dávkové operace / výběr výsledků, ne jen skok na první kód; ověřit se stránkováním a aktivními filtry |
| Srozumitelné chyby | Chyba u pole; zadání se při konfliktu ani chybě sítě neztratí; při nejistém výsledku nejprve znovu načíst skutečný stav | UI busy/preview stav, přesné chybové výsledky služby; opakovaný submit, race dvou správců a síťové selhání |
| Vlastní seznam jako vedlejší cesta | Vložit jeden sloupec z tabulky; odlišit opakované řádky, existující kódy a neplatná označení | Zachovat současný textarea a dávkovou transakci; bez nové importní knihovny |

Nové místo zůstane podle současného kontraktu **aktivní a bez rezidenta**. Rozhraní tento
účinek uvede před potvrzením. Nejde o záruku dostupnosti pro libovolný termín; návštěvnický typ
patří do oddělené agendy. Rezidence se nastavuje až následně, nevznikne automatickým vytvořením.

### Nutné technické podmínky realizace

- Sjednotit validaci jednoho místa i dávky: označení 32 znaků, poznámka 512, platný typ,
  existující limit 500 různých označení na dávku. Limit posuzovat nad všemi normalizovanými
  kódy, nikoli jen těmi novými. Současná jednostranná kontrola UI může povolit dávku,
  kterou server následně odmítne.
- `CreateAsync` a `CreateBatchAsync` nyní převádějí všechny `DbUpdateException` na duplicitu
  či konflikt. Při realizaci rozlišit skutečné unikátní porušení, validaci a obecné selhání;
  příliš dlouhá poznámka nesmí být vysvětlena jako již existující místo. Nález je doložen
  kódem, v této návrhové práci nebyl reprodukován zápisem do DB.
- Zkontrolovat aktuální oprávnění aktéra i při vytvoření, v souladu s dříve doplněnými checked
  editacemi, a doplnit přiměřený audit. Stávající stránka má serverovou policy ManageSpots;
  návrh není tvrzením o nechráněném veřejném HTTP endpointu.
- Zachovat SQL unikátní index a atomické uložení celé nové části dávky. Náhled sám souběh
  neřeší. Při souběžném vytvoření obnovit náhled a nechat správce potvrdit změněný výsledek.
- Není potřeba změna databázového schématu, nová knihovna ani nový způsob nasazení.

Pro první implementaci doporučuji společný formulář, živý náhled, přesnou validaci a jasné
dokončení. Automatické rozpoznávání více složitých vzorů či doplňování „dalšího volného čísla“
není součástí návrhu: mohlo by vytvořit jiná označení, než správce zamýšlí.

Ověření této návrhové práce: prohlédnut současný živý dialog bez uložení dat; interaktivní
ukázka zkontrolována v prohlížeči při šířce 736 a 360 px v tmavém režimu. Vyzkoušen náhled
číselné řady s duplicitami, simulované dokončení, nečíselné označení pro více míst a vložený
seznam s opakovaným řádkem. Nejde o implementaci ani nové integrační testy aplikace.

## Návštěvy: datum nebo čas podle konfigurace

Navazující implementace reaguje na požadavek rezervovat návštěvě časové okno nebo celý den.
Zdroj pravidla: `ReservationWindowRules.MatchesMode` a jeho existující unit testy. Globální
`ReservationTimeMode` má dvě **výhradní** hodnoty: AllDay a TimeWindow. Neexistuje konfigurace
„návštěvy si vždy vyberou obojí“, zvláštní kalendář návštěv ani návštěvnické kredity/kvóty.
Takové nové pravidlo tato změna nezavádí.

| Nález před opravou | Důkaz a dopad | Priorita | Zvolené řešení |
| --- | --- | --- | --- |
| Návštěvy ignorují časový režim | `VisitorBookingService.CalendarErrorAsync` kontroloval pouze pokryté dny; `VisitorBookings.razor` vždy skládal čas od–do. Celodenní konfigurace dovolovala hodinovou návštěvu, časová konfigurace i celodenní/vícedenní přímý požadavek. Reprodukováno SQL testem. | P1 | Jeden `VisitorBookingWindowRules.Validate` pro UI, nabídku a příkaz; přebírá doménové MatchesMode a jednotný kalendář. |
| Uložení spoléhá na cachované nastavení a oprávnění stránky | Původní BookAsync četl GetPolicyAsync před transakcí a nekontroloval aktuálního aktéra. SQL reprodukce přijala nepovolený přímý příkaz. | P0 (oprávnění), P1 (konfigurace) | Ověření aktivního aktéra s ManageVisitors a čtení nastavení z DB uvnitř stejné Serializable transakce jako překryv a vložení. |
| Výsledek souběhu nebo oznámení mohl skončit výjimkou | SQL deadlock nebyl přeložen; chyba hostitelovy notifikace mohla navenek zneplatnit už commitnuté uložení. Původní náhodně načasovaný test souběhu sám chybu nereprodukoval. | P1/P2 | Existující omezený retry transakce; po vyčerpání srozumitelný konflikt. Notifikace až po commitu, mimo retry; její selhání nevrací neúspěch založení. |
| Formulář nerozlišuje neplatné okno od chybějících míst | Pro konec před začátkem zobrazoval „žádná volná místa“. Chybělo vysvětlení celého dne a datum v souhrnu dostupnosti. | P1 | Oddělené validace, načítání, chyba a prázdná nabídka; datum/čas i zóna jsou viditelné. Starší asynchronní výsledek nepřepíše novější výběr. |
| Chybějící délkové kontroly a audit založení | Limity byly jen ve formuláři; CreatedById nebyl událostí v existujícím auditu. | P2 | Server ověřuje 128/128/16 znaků po trim; v transakci zapisuje aktéra, GUID návštěvy/místa, termín a výsledek bez jména, firmy či SPZ. |

### Průchod a oprávnění návštěv

`Admin/VisitorBookings.razor` → interní služba přes Blazor Server circuit (samostatné veřejné
mutační HTTP API návštěv neexistuje) → `VisitorBookingService` → `VisitorBooking`/`ParkingSpot`
→ SQL Server. `D3ParkingDbContext.OnModelCreating` mapuje návštěvy s indexy `(SpotId, StartUtc)`
a `StartUtc`; žádný z nich se nevydává za unikátní ochranu proti obecným překryvům.

| Operace | Rozsah | Kontrola na serveru |
| --- | --- | --- |
| Otevření agendy, nabídka a seznam | Společný fond Visitor míst; budoucí/aktuální Booked návštěvy | Stránka vyžaduje `Parking.ManageVisitors`; čtení služby samo nepřijímá aktéra. |
| Založení | Jedno aktivní Visitor místo a platný termín | Stejná policy stránky plus nová čerstvá DB kontrola aktivního aktéra a oprávnění v BookCoreAsync. |
| Zrušení | Vybraná dosud neukončená Booked návštěva | Policy stránky plus čerstvá DB kontrola aktivního aktéra s ManageVisitors; změna a audit v jedné transakci. |
| Změna společného režimu/kalendáře | Konfigurace celého parkování | Existující agenda nastavení a `Parking.ManageIncentives`; pravidla oprávnění nebyla změněna. |

### Implementované chování

- **Celodenní režim:** správce vybere datum, místo a jméno; časová pole se nezobrazují. Interval
  je `SiteTime.Day` od místní půlnoci do další půlnoci. Při změně času může trvat 23 nebo 25 hodin.
- **Časový režim:** správce zvolí datum a od–do v jednom místním dni. `Do = 00:00` znamená
  následující půlnoc, jak dovoluje existující polouzavřený kontrakt. Celý den nebo pokračování
  po další půlnoci nový příkaz v tomto režimu nepřijme. Jarní neexistující čas formulář odmítne.
- Předstih, povolené všední/víkendové dny, svátky a rezervace na dnešek se řídí společnou
  konfigurací. Zaměstnanecké kredity, týdenní kvóty, no-show ani přidělení se návštěvám nezavádějí.
- Při otevření a změně termínu se čte čerstvá konfigurace i časová zóna. Příkaz kontroluje
  nastavení znovu v DB; změna režimu v otevřeném formuláři skončí chybou a obnovou formuláře,
  se zachováním vyplněných údajů. Sám nepřevede odeslanou hodinovou návštěvu na celý den.
- Souběh se řeší SQL Server transakcí se Serializable a existujícím retry; ochrana není zámkem
  jednoho procesu ani tvrzením, že unikátní index řeší překryvy. Navazující intervaly se nekříží.
- Po úspěchu se otevře pohled nadcházejících návštěv. Celodenní záznam nese text „celý den“ podle
  skutečného uloženého intervalu, nezávisle na nynějším režimu. Starší vícedenní záznam ukazuje
  také koncové datum a zůstává viditelný v dnešním přehledu, pokud do dneška zasahuje.
- Nedostupnost a načítací chyby se nezaměňují s nulovou kapacitou. Po neočekávané chybě uložení
  se obnoví přehled a formulář brání opakování, dokud jej obsluha nezavře a neověří výsledek.

### Dopady a zbývající rozhodnutí

Bez migrace, nových závislostí, nasazení nebo změn produkčních dat. Uložené UTC intervaly,
polouzavřené hranice i veřejná signatura BookAsync zůstávají. Staré binární verzi nové záznamy
schéma umožní přečíst, ale stará verze nové kontroly nevynucuje; nelze je garantovat při
souběžném provozu starých a nových instancí. Běžné publikování se sestavuje mimo produkční server.

Změna režimu **nezruší existující návštěvy**. `ParkingSettingsService.FindCalendarImpactAsync`
u nich dosud hodnotí pouze kalendářní dny; tento kontrakt zůstává. Změna kalendáře nadále používá
existující náhled dopadu a potvrzení rušení. Staré vícedenní či jinak režimu neodpovídající návštěvy
se automaticky nepřepisují, přesto stále blokují překryv. Požadavek na jejich převod nebo na
současnou volbu obou režimů pro návštěvy vyžaduje samostatné obchodní rozhodnutí (P2).

- **Vyřešeno navazujícím celkem:** ruční i kalendářní zrušení návštěvy mají audit a ruční příkaz
  čerstvou autorizaci a transakční kontrolu stavu. Obecná editace/přesun návštěv stále neexistují;
  jejich případné doplnění by vyžadovalo také token změny celého editovaného záznamu.
- **P2:** seznam není stránkovaný a nenabízí historii zrušených návštěv. Nabídka dovoluje jen
  aktivní místa typu Visitor; deaktivace sama neruší již uloženou návštěvu.
- **P2:** okamžitá změna globální časové zóny mezi posledním obnovením formuláře a uložením
  nemá zvláštní token. Veřejný příkaz pracuje s UTC okamžiky. Opakovaná podzimní hodina se nadále
  převádí zavedeným SiteTime.At; UI nenabízí výběr prvního/druhého výskytu. Jarní neexistující
  místní čas je odmítnut v UI; server přijímá jednoznačné UTC okamžiky.
- **P2:** notifikace při výpadku dopravy není zaručeně znovu doručena; přetrvává jen bezpečný
  záznam varování bez osobních údajů. Nevznikl obecný transakční outbox.

### Ověření návštěv

- Před opravou: 7 SQL reprodukcí, 6 očekávaných selhání; původní náhodný souběh prošel.
- Po opravě: celá aplikační sada **404/404**, 0 selhání, 0 přeskočení, konfigurace Release;
  z toho **29** testů VisitorBookingConfigurationTests proti skutečnému SQL Serveru.
  Vynucený souběh čeká po obou konfliktních SELECTech, kontroluje právě jeden booking i audit;
  další test pozastaví načtení policy a ověřuje blokování současného zápisu nastavení.
  Pokryty oba režimy, mezilehlá změna konfigurace, odvolané oprávnění, jiný/neaktivní typ místa,
  délky, navazující termíny, překryv/zrušení, DST23/25h, audit+rollback a chyba notifikace.
  Podvržené datum 9999-12-31 se odmítne před konstrukcí následující půlnoci.
- Současné obrazovky: **65/65 E2E**, 0 selhání, 0 přeskočení; opt-in příprava dat byla zapnutá.
  Výslovně vyloučeno 15 starých testů odstraněného editoru (`LotMapEditorTests`, `LotMapBoardTests`,
  `ReserveMapTests`). Nejde o úspěch nefiltrované sady. Po poslední hraniční validaci následoval
  ještě cílený běh návštěvnických E2E: **4/4 prošly**, včetně podvrženého data 9999-12-31.
- Solution Debug/Release a finální web Debug: 0 varování a chyb; `git diff --check` a kontrola
  XML obou jazykových resources bez duplicit prošly. Nová migrace není potřebná.
- Ručně v prohlížeči zkontrolováno finální rozložení časového formuláře v tmavém vzhledu na
  desktopu a šířce 390 px; popisky a inputy jsou ve společných buňkách, mobilní dialog se posouvá.
  E2E potvrzuje oba režimy, uchování rozepsaných údajů, SQL intervaly, prázdné placeholdery a
  `HostUserId = null`, pokud hostitel nebyl vybrán.

Privátní výsledky relace: `results/visitors-before.trx`, `application-visitors-release-final.trx`,
`e2e-visitors-complete.trx` a `visitors-ui-boundary-final.trx` v lokálním testovacím runtime popsaném
v TECHNICAL.md. E2E obnovuje nastavení a odstraní vlastní návštěvy/místa.

**Nevykonané scénáře:** v prohlížeči nebyl simulován výpadek databáze ani uměle opožděná odpověď
při rychlém přepínání data. Pro ruční ověření na izolované instanci otevřít formulář, dočasně
přerušit spojení s testovací DB a změnit termín: očekává se chyba s opakováním načtení a zakázané
uložení, nikoli nulová dostupnost. Po obnovení připojení musí fungovat opakování bez ztráty údajů.
U rychlého přepnutí dvou dat musí zůstat nabídka posledního vybraného termínu. Netvrdit, že tyto
konkrétní fault-injection scénáře proběhly jen proto, že jsou implementovány ochranné větve.


## Navazující oprava: bezpečné rušení návštěv

**Doložené chyby:** původní CancelAsync přijímal pouze ID návštěvy, bez identifikace a kontroly
aktéra. Reprodukční SQL test prokázal úspěšné rušení bez aktéra. Kód také dovoloval měnit Booked
záznam po konci návštěvy, neauditoval výsledek a UI rušilo ihned po kliknutí. Samostatná SQL
reprodukce ukázala chybějící audit konkrétní návštěvy při potvrzené změně kalendáře. Opravy
oprávnění mají prioritu P0, zachování historie a bezpečné workflow P1, doplnění auditu P1/P2.
Nejde o tvrzení, že existuje nechráněný veřejný HTTP endpoint; prokázána je mezera služby.

**Implementováno:**

- IVisitorBookingService.CancelCheckedAsync dostává ID aktéra z přihlášeného principalu.
  VisitorBookingService.CancelCoreAsync kontroluje aktivní účet a ManageVisitors ještě před
  čtením návštěvy, takže neoprávněný volající nerozliší existující a neexistující ID.
- Serializable transakce zahrnuje autorizaci, načtení aktuálního stavu, přechod Booked→Cancelled
  a audit. Existující omezený retry znovu čte data po deadlocku. Jedno ze dvou souběžných rušení
  uspěje, druhé vrátí AlreadyCancelled (při vyčerpání SQL retry ConcurrentChange), bez druhé události.
  Toto chrání současný jediný stavový přechod, není to obecné řešení budoucí editace bez rowversion.
- EndUtc <= nyní vrací Ended a historický záznam zůstává beze změny. Zrušení současné návštěvy
  je dovoleno. Změna kalendáře či deaktivace místa nebrání ručnímu zrušení platné návštěvy;
  rušení není nové založení a nemá znovu vynucovat pravidla pro vznik rezervace.
- Audit obsahuje aktéra, čas, ID rezervace/místa, původní interval a úspěšný výsledek; bez jména,
  firmy nebo SPZ návštěvy. V AccountAuditEvents se používá existující ReservationOverridden.
  Rušení neodstraňuje záznam návštěvy, nemanipuluje kredity a nevytváří náhradní rezervaci.
- ParkingSettingsService.ReconcileCalendarImpactAsync zapisuje stejnou událost pro každou
  rušenou návštěvu s důvodem calendar configuration change. Je ve stejné transakci jako globální
  SettingsChanged; nepotvrzený dopad ani rollback nezanechá audit úspěšného rušení.
- Hostitele se ruční příkaz pokusí upozornit až po commitu, mimo retry. Osiřelé místo neblokuje
  zrušení: použije se text bez kódu místa. Chyba upozornění nesimuluje neúspěšné zrušení.
- VisitorBookings.RequestCancellation ukáže potvrzení s hostem, místem, termínem a podmínkami
  další dostupnosti. Ponechat rezervaci/Zavřít nic nemění. Potvrzení používá checked příkaz;
  po úspěchu se obnoví seznam i nabídka. AlreadyCancelled a Ended zobrazí vysvětlení a odstraní
  zastaralý aktivní řádek obnovením seznamu, nikoli optimistickým mazáním.
- Při neznámém výsledku se dialog ponechá s chybou, zakáže další potvrzení a pokusí obnovit data.
  Po AccessDenied další čtení agendy nenásleduje. Načítací chyba nemění počty záložek na nulu.

**Kompatibilita:** bez migrace a nových závislostí. Legacy CancelAsync zachovává podpis, ale
bez aktéra nově bezpečně odmítá; volající musí přejít na checked variantu. Toto je úmyslné zpřísnění
bezpečnostního kontraktu, nikoli transparentně shodné chování. Bližší postup je v TECHNICAL.md.
Staré záznamy a význam časových intervalů se nemění, starý audit zrušení se nevymýšlí zpětně.

**Zbývá:** obecné čtení návštěv nadále spoléhá na oprávnění serverové stránky a její principal,
nikoli čerstvou DB kontrolu při každém reloadu (P2). Přehled historie a stránkování zůstávají P2.
Notifikace nemají trvalé opakování doručení při selhání; kalendářní rušení zatím neposílá samostatné
hostitelské upozornění (P2). Zábrana fokusu uvnitř sdíleného AdminDialog a jeho návrat nejsou v tomto
celku měněny. Žádný z těchto bodů se nepovažuje za implementovaný jen na základě návrhu.

### Ověření rušení

- Před opravou selhal SQL test legacy rušení bez aktéra (operace původně uspěla).
  Kalendářní reprodukce samostatně selhala na chybějícím návštěvním auditu.
- Po opravě **48/48 návštěvnických SQL scénářů** prošlo: 29 konfigurace/časů a 19 nových rušení;
  další **2/2 kalendářní SQL testy** ověřily potvrzení dopadu a rollback celé změny.
- Celá aplikační sada: **424/424**, 0 selhání, 0 přeskočení. Testy běžely proti skutečnému
  SQL Serveru, vlastní testovací databáze se po dokončení odstranily.
- Cílené návštěvnické E2E: **6/6**, 0 selhání, 0 přeskočení. Nové scénáře ověřují ponechání
  rezervace bez mutace, potvrzení rušení, zachování záznamu, jeden audit, opětovné rezervování
  stejného termínu a konflikt při rušení v jiném okně. Cleanup odstraní jen vlastní záznamy/audity.
- Solution Debug/Release a finální web: 0 chyb a varování; XML cs/en resources bez duplicit;
  `git diff --check` prošel.

Výsledky v privátním testovacím runtime/results: `visitor-cancellation-before.trx`,
`visitor-cancellation-after.trx`, `visitor-calendar-audit-before.trx`,
`visitor-calendar-audit-after.trx`, `application-cancellation-final.trx` a
`visitors-cancel-e2e.trx`. Finální průchod současných obrazovek **67/67 E2E prošel**, 0 selhání
ani přeskočení, výsledek `e2e-cancellation-final.trx`. Výslovně vyloučeno 15 historických testů
odstraněného editoru (`LotMapEditorTests`, `LotMapBoardTests`, `ReserveMapTests`); není to tvrzení
úspěchu nefiltrované sady.

Nebylo simulováno přerušení sítě přesně po commitu v prohlížeči; nejistý výsledek má ochrannou
UI větev. SQL testy skutečně vynucují selhání před commitem a selhání hostitelského oznámení
po commitu. Z toho neodvozovat, že byly experimentálně pokryty všechny druhy výpadků.


## Potvrzené pravidlo a oprava odchodu účtu

**Potvrzeno zadavatelem:** odchod účtu ruší i návštěvy, které založil pro jiné hostitele.
Nejde již o otevřený rozhodovací bod. Filtr zůstává `Booked && EndUtc > now &&
(HostUserId == userId || CreatedById == userId)`: zahrnuje budoucí i probíhající návštěvy,
nikoli skončené. Celodenní i časové rezervace používají stejný uložený interval; konfigurace
vzniku rezervací se při rušení znovu nevynucuje. Odchod zde znamená smazání přes
UserAdminService.DeleteAsync nebo deaktivaci přes EntraDirectoryService.SetActiveAsync.
Dočasné ruční AccountService.BlockAsync tento cleanup nespouští.

| Nález a priorita | Důkaz a dopad | Zvolené řešení a ověření |
| --- | --- | --- |
| P0: DeleteAsync neověřoval aktuální oprávnění aktéra | Kód před úpravou ověřoval pouze odlišný cílový účet a posledního administrátora; UI policy není ochrana služby | Čerstvý aktivní účet a Users.Delete před čtením cíle, ve stejné Serializable transakci. Přímé SQL integrační testy služby pokrývají neoprávněného/neaktivního/chybějícího aktéra i odebrané oprávnění. Nejde o důkaz veřejně nechráněného HTTP endpointu. |
| P1: zrušení při odchodu nemělo audit návštěvy | Před opravou oba SQL testy admin/system našly 0 místo 5 očekávaných událostí; chybové testy nemohly vyvolat selhání uložení neexistujícího auditu | Helper uloží změnu a jednotlivé ReservationOverridden společně. Opakování pracuje jen s Booked; selhání auditu vrací transakci. |
| P1: Entra ukládala blokaci a cleanup odděleně | SetActiveAsync před úpravou neměl vnější transakci; selhání mohlo zanechat částečný odchod (před opravou doloženo kódem) | Serializable transakce nad stejným kontextem jako Identity, včetně inactive retry a security stamp. Injektované selhání po zápisu auditu ověřuje návrat celého stavu. |
| P1: zastaralý hostitel šel uložit do nové návštěvy | Pět reprodukčních SQL testů před opravou prokázalo uložení pro chybějící nebo neaktivní účet | BookCoreAsync ve své Serializable transakci ověřuje existující aktivní hostitele. Null zůstává dovolen. Stejné zámky koordinují vytvoření a odchod hostitele. |
| P2: potvrzení smazání nevysvětlovalo rozsah návštěv | UserEdit zobrazoval pouze obecný počet „Zrušené návštěvy“ | Počet „Návštěvy ke zrušení“ a vysvětlení hostitel nebo zakladatel, včetně jiných hostitelů, probíhajících návštěv a zachování historie. |

**Implementováno:** EmployeeLifecycleCleanup.CleanOperationalAsync vybírá pro audit pouze ID
návštěvy/místa a interval, provede původní přechod Cancelled a vymaže HostUserId. Událost obsahuje
aktéra admin:<id> nebo system, čas, důvod employee departure a identifikátory; bez jména,
firmy a SPZ. Při samostatném volání helper vlastní Serializable transakci; oba produkční
volající drží vnější transakci zahrnující také účet. Bez nových rolí, výjimek a notifikací.

UserAdminService anonymizuje staré volné texty před cleanupem, aby nesmazal právě vytvořenou
korelaci zrušení. Starší politika anonymizace zůstává: pokud je účet nejprve deaktivován přes
Entra a smazán až později, starší auditní Detail se při smazání vynuluje, událost/čas/aktér
zůstávají. Není zaručena trvalá korelace každého staršího auditu přes tento pozdější krok (P2).

VisitorBookings.HostOptions nabízí pouze aktivní účty. Pokud hostitel odejde při otevřeném
formuláři, HostUnavailable vysvětlí neúspěch, odstraní zastaralou volbu a zachová ostatní údaje.
Uživatel zvolí jiného hostitele nebo znovu výslovně odešle bez něj. Nepřidává se automatická
náhradní rezervace. README a zabudovaná nápověda popisují obě cesty.

**Kompatibilita a provoz:** bez migrace, nových závislostí a změny konfigurace. Staré návštěvy
se nepřepisují zpětně. Záruky vyžadují aktualizaci všech instancí; stará binární verze používající
stejné schéma je nevynucuje. SQL běží v existujícím lokálním testovacím prostředí, produkce
nebyla měněna. Neproběhl commit ani push.

**Ověření tohoto celku:** cílená sada EmployeeLifecycleParkingHistoryTests,
VisitorBookingConfigurationTests, EntraDirectoryTests a LastAdministratorTests prošla **82/82**
na skutečném SQL Serveru. Celá aplikační sada prošla **446/446**, bez selhání a přeskočení.
Zahrnuje SQL souběh vytvoření a odchodu, rollback po skutečném SQL zápisu auditu, opakovanou
deaktivaci, skončené i právě probíhající návštěvy a přímou kontrolu oprávnění při smazání.
Test chyby celého Entra odchodu opakuje požadavek v novém DI scope jako další HTTP požadavek;
obnovení tracked stavu po výjimce ve stejném dlouho žijícím scope nebylo tímto testem ověřeno.
Výsledky: `lifecycle-visitors-after.trx`, `application-departure-final.trx` v privátním runtime/results.
Debug i Release build celé solution: 0 chyb a varování. Šest cs/en resource souborů má validní XML bez duplicit klíčů;
`git diff --check` prošel. Prohlížečová sada současných obrazovek prošla **68/68**, bez selhání
a přeskočení (`e2e-departure-final.trx`). Filtr výslovně vyloučil stejných 15 testů odstraněného
mapového editoru jako v předchozím celku. Nový scénář ověřil text dopadu v detailu účtu,
odchod vybraného hostitele při otevřeném formuláři, přeloženou chybu, nulový zápis při odmítnutí,
zachovaný koncept a následné výslovné úspěšné odeslání bez hostitele. Po sadě SQL kontrola
potvrdila 0 vlastních E2E návštěvnických míst, rezervací a účtů hostitele.
Reprodukce: lifecycle-visitors-before.trx (1 původní test prošel, 4 nové selhaly očekávaně)
a visitor-host-before.trx (5 zamítacích scénářů selhalo očekávaně). Selhání prvního vývojového
běhu kvůli příliš dlouhému syntetickému kódu místa bylo opraveno před těmito reprodukcemi.

**Navazující P1 opraveno v dalším celku níže:** ScimEndpoints.ReplaceUserAsync dříve ignoroval
neúspěšný AccountResult z SetActiveAsync a vracel HTTP 200 i při odmítnutí deaktivace.
**Zbývá P2:** samostatné upozornění jiného hostitele při odchodu, návštěvní historie v UI,
stránkování a výše uvedené starší limity čtení a doručení. Tyto části nejsou implementovány.

## SCIM: bezpečný odchod přes HTTP a jednotné pravidlo nastavení

**Potvrzeno zadavatelem:** „Odchod má vždy blokovat účet a zrušit návštěvy.“ Nastavení
BlockOnDeprovision tedy není volitelná výjimka. Zavádějící přepínač je odstraněn; staré pole
se zachovává pro kompatibilitu a efektivně je vždy true. Nejde o automatické rozšíření na
ruční dočasné blokování účtu. Ochrana posledního aktivního správce zůstává platná.

| Priorita a doložený nález | Dopad a reprodukce | Implementované řešení |
| --- | --- | --- |
| P1: PUT ignoroval odmítnutí deaktivace, POST u existujícího účtu deaktivaci vůbec nezavolal | Reálný HTTP+SQL test v obou případech vrátil 200 pro posledního správce místo 409; kontrolní PATCH/DELETE prošly | POST/PUT zpracují výslovný active přes SetActiveAsync a vrátí SCIM 409 při odmítnutí |
| P1: PUT ukládal profil před samostatnou transakcí odchodu | Kód SyncAsync→UpdateProfileAsync potvrzoval profil dříve; pouhá oprava HTTP odpovědi by částečnou změnu ponechala | Endpoint vlastní Serializable transakci nad stejným kontextem: profil, adopce, stav, oprávnění, parkování a audit. SetActiveAsync převezme pouze Serializable, jinou vnější izolaci odmítne před zápisem |
| P1: URL lokálního účtu + externalId cizí identity mohly změnit jiný účet | PUT volal SyncAsync bez kontroly výsledného UserId; doloženo kódem, následný HTTP test ověřuje nulovou změnu obou účtů | Před commitem musí result.UserId odpovídat cíli URL; jinak 409 a rollback. Legitimní adopce stejného potvrzeného místního účtu zůstává dovolená |
| P1: neurčitý výběr lokálního/external ID a neomezený provider | FindAsync používal OR bez priority; index dovoluje stejné oid pro různé providery | Nejprve lokální ID, pak Entra-only externalId. Mutace cizího providera i neúplného propojení PATCH/DELETE jsou odmítnuty |
| P1/P2: explicitní PUT active=true neobnovil účet | Kód zpracovával pouze false | Explicitní true obnoví stav přes stejnou hlídanou cestu; absence atributu existující stav nezmění. Role a zrušené návštěvy se neobnovují |
| P1/P2: UI nabízelo neúčinnou výjimku z odchodu | Čtyři settings SQL reprodukce před opravou ukázaly false v efektivní konfiguraci; endpointy přesto blokovaly účet | Podle potvrzeného pravidla EntraSettingsService normalizuje čtení a ukládání na true; UI místo přepínače vysvětluje dopad |
| P2: neplatný typ active mohl znamenat nechtěné true, primitivní PATCH operace končila výjimkou | Doloženo původním ReadIdentity/ReadActiveFromPatch; nové HTTP testy ověřují odmítnutí bez změn | Explicitní neplatná hodnota a neobjektové podporované vstupy vracejí SCIM 400; boolean/string bool jsou podporované, neznámé PATCH cesty zůstávají bez účinku |

**Zvolené řešení:** rozšíření existujících endpointů a služby, bez nové provisioning vrstvy.
PATCH a DELETE drží transakci už při výběru cíle, takže se vazba při operaci nemůže změnit.
Přihlášení dále používá původní SyncAsync a samo neodblokuje účet. Stávající bearer kontrola
zůstává před chráněnými operacemi; není zavedena cookie autentizace ani nový CSRF mechanismus.

**Pro správce:** v Nastavení → Entra ID je vysvětlen povinný dopad odchodu. Při konfliktu
posledního správce nejprve zajistěte další aktivní administrátorský účet a pak opakujte
provisioning. Úspěšný odchod se projeví blokovaným účtem, uvolněním rezervovaného termínu
rušených návštěv a auditem; záznamy návštěv se nemažou. Nejde o zjištění fyzické obsazenosti.

**Data a kompatibilita:** bez migrace a nových závislostí. Staré uložené false se při pouhém
čtení nepřepíše; efektivní hodnota je true a příští uložení nastavení ji normalizuje.
Konfigurační klíč EntraId__Scim__BlockOnDeprovision je kompatibilní historické pole bez možnosti
vypnout odchod. Oprava mění chybné HTTP 200 na 409 při odmítnutí, brání cizímu cíli a zpřísňuje
neplatné vstupy na 400. Nové záruky vyžadují aktualizaci všech instancí; starší binárky mají
stejné schéma, ale nezajišťují tyto opravy. Nasazení dál používá artefakt sestavený mimo produkci.

**Ověření:** před opravou `scim-red.trx` obsahuje dvě očekávaná selhání POST/PUT a dvě úspěšné
kontroly PATCH/DELETE. První společný běh `scim-after.trx` prošel 71/71, zahrnul 30 HTTP+SQL
případů, lifecycle a Entra služby/nastavení. Finální celá aplikační sada prošla **487/487**, bez
selhání a přeskočení (`application-scim-final.trx`). Obsahuje **36 SCIM HTTP+SQL případů**,
25 EntraDirectoryTests, 13 EntraSettingsTests a 5 lifecycle testů. Zahrnuje také odmítnutí
slabší vnější transakce ještě před zápisem, kolize lokálního/externího ID a neúplné propojení.
Release build celé solution prošel s 0 chybami a varováními. Cílený prohlížečový test
nastavení prošel **1/1** (`e2e-scim-settings-final.trx`): pravidlo je viditelné, přepínač chybí,
ostatní části konfigurace fungují. Celá prohlížečová sada 68/68 byla ověřena v předchozím
celku; v tomto běhu nebyla opakována. XML cs/en resources i `git diff --check` prošly.
Testy používají skutečný SQL Server, Kestrel a produkční route mapping/Identity/Entra službu;
jen konfigurace, hodiny a vynucené selhání jsou řízené testem. HTTP testy mají vlastní náhodný
testovací katalog, který při teardown odstraní. Injektované selhání po SQL zápisu auditu
vrátilo profil, session stamp, role, návštěvy i audit; následující požadavek uspěl.

**Neověřeno a zbývá:** komunikace s externím Entra tenantem ani jeho chování při opakování
HTTP 409 nebyly testovány. Nejde o implementaci celého SCIM standardu; stávající omezené
filtrování a neznámé PATCH cesty zůstávají. P2: trvalá korelace starších návštěvních auditů
při pozdějším smazání účtu, hostitelské upozornění při odchodu, historie/stránkování v UI.
Opakování po rollbacku ve stejném dlouho žijícím Blazor scope nebylo touto HTTP sadou ověřeno;
každý provisioning požadavek přirozeně používá nový scope.


## Audit UI administrace — 19. 9. 2026

Tento celek je **analýza, nikoli implementace oprav**. Prohlížečová kontrola proběhla na
lokální aplikaci se syntetickými daty: provozní přehled, návštěvy, parkovací místa
(počítač a šířka 390 px), nastavení. Kódová kontrola navíc zahrnula správu účtů,
audit a vozidla. Nejde o vizuální kontrolu každé administrátorské stránky.
Nebyly ukládány změny konfigurace ani vytvářeny či rušeny rezervace. Dočasný koncept
popisu webu byl použit pouze k ověření ztráty neuloženého formuláře.
P0 problém nebyl touto UI kontrolou potvrzen; nejde o potvrzení absence bezpečnostních chyb.

Odkazy níže jsou relativní ke složce `Source`; řádky odpovídají pracovnímu stromu při auditu.
„Prohlížeč“ znamená reprodukované chování, „kód“ doloženou implementaci bez dané runtime
reprodukce, „riziko“ scénář, jehož skutečný výskyt nebyl potvrzen.

| ID / priorita | Důkaz a skutečný dopad | Reprodukce / ověření | Navržená změna a následný test |
| --- | --- | --- | --- |
| UI-01 **P1** — ztráta konceptu nastavení | **Prohlížeč:** neuložený Popis webu po odchodu zmizí bez upozornění. **Kód:** `Settings.razor:659 LoadAsync` přepisuje modely všech záložek; `ApplyAsync:813` ho volá po uložení jediné záložky, tedy může zrušit koncepty ostatních záložek. | Nastavení → Obecné → změnit Popis webu → Návštěvy → zpět. Koncept chybí, potvrzovací dialog se neobjevil. Varianta uložení jiné záložky nebyla v prohlížeči vykonána. | Evidovat změny po záložkách, hlídat opuštění stránky, po uložení aktualizovat pouze uloženou záložku. Ověřit odchod, zrušení odchodu a uložení jiné záložky bez ztráty konceptu. |
| UI-02 **P1** — fokus uniká z modálního dialogu | **Prohlížeč:** při otevřeném dialogu návštěvy lze klávesnicí přejít na pozadí. `Components/Admin/AdminDialog.razor:5–49` pouze nastaví počáteční fokus a obsluhuje Escape; nemá zachycení Tab, neaktivní pozadí ani návrat fokusu. | Návštěvy → Nová rezervace pro návštěvu → Shift+Tab. Fokus je na tlačítku „Nadcházející návštěvy 0“ mimo stále otevřený dialog. | Opravit společný dialog: cyklus Tab/Shift+Tab, neaktivní pozadí, návrat na otevírací prvek. Ověřit i Escape a probíhající ukládání. |
| UI-03 **P1** — „Dnešní agenda“ neobsahuje celý dnešek | **Kód:** `VisitorBookingService.cs:27 ListUpcomingAsync` načítá jen Booked s EndUtc > now; `VisitorBookings.razor:295 TodayBookings` filtruje den až nad tímto seznamem. Správce nedohledá dřívější dnešní návštěvu ani zrušený záznam v této agendě. | Zdrojově doloženo; scénář návštěvy 9–10 hodin při kontrole v 15 hodin nebyl nově nasetován v UI. | Samostatný serverový dotaz pro vybraný den/období se stavem, ponechat kontrakt Upcoming. Přidat stránkovanou historii. Test dnešní ukončené návštěvy, přechodu přes půlnoc a zrušené rezervace. |
| UI-04 **P1** — neúplná obsluha selhání rizikových akcí | **Kód:** `UserEdit.razor:484 DeleteAsync` a `:504 OpenDeleteAsync` nastavují busy bez try/finally; neočekávaná výjimka nemá místní srozumitelnou chybu. `RunAsync:528`, `Settings.razor:813 ApplyAsync` a `UserCreate.razor:92 CreateAsync` nemají jednotnou ochranu rozpracovaného požadavku a neočekávaného selhání. | Injekce selhání v tomto auditu neproběhla. Nejde o tvrzení, že vznikly duplicitní účty nebo se skutečně poškodila data. | Důsledný busy guard, finally, zachování formuláře a místní chyba s bezpečným obnovením stavu. Testovat výjimku před zápisem i neurčitý výsledek po zápisu; nenabízet slepé opakování neověřené operace. |
| UI-05 **P2** — mobil neumí změnit typ místa | **Prohlížeč a kód:** `ParkingSpots.razor:202` má na počítači výběr typu, mobilní seznam `:258–292` pouze text typu a akce rezidentů/aktivace. Stejná agenda má podle zařízení rozdílné možnosti. | Při šířce 390 px chybí ovládání typu. Samotné vodorovné přetečení stránky se nepotvrdilo. | Společná akce „Upravit místo“ pro oba pohledy. Ověřit shodu funkcí při 390 px a na počítači, včetně konfliktní editace. |
| UI-06 **P2** — kód a poznámka místa nemají editační cestu | **Kód:** `ParkingSpots.razor:80,111,134` nabízí poznámku při založení, `:212,280` ji pouze vykresluje. Volání UpdateDetailsAsync v `:714` při změně typu zachovává původní kód a poznámku. Oprava překlepu není v této agendě dostupná. | Kontrola vstupů a volání služby; nová změna dat nebyla provedena. | Zahrnout kód a poznámku do stejného editačního dialogu jako typ. Ověřit validaci unikátního kódu, zachování rezervací a konflikt změny. |
| UI-07 **P2** — „Sdílený fond“ směšuje různý význam | **Prohlížeč:** karta vykazuje 68 míst, přestože aktivních je 58; není to údaj o použitelné kapacitě. **Kód:** `ParkingSpots.razor:226,276` označuje každé místo bez rezidenta jako sdílený fond bez ohledu na Visitor typ; `ParkingSpotService.cs:116` filtruje Shared jen přes OwnerId == null. | Návštěvnické místo nebylo pro tento scénář nově vytvořeno, jeho klasifikace je doložena kódem. Samotné zahrnutí neaktivních do inventáře není chyba výpočtu dostupnosti. | Pro inventář použít „Bez rezidenta“; skutečný sdílený zaměstnanecký fond výslovně oddělit od návštěv a uvést aktivitu. Ověřit standardní, návštěvnické a neaktivní místo. |
| UI-08 **P2** — nejednotný význam „Volné“ v přehledu | **Prohlížeč:** souhrn vysvětluje „Bez rezervace v přehledu“, ale dlaždice a filtry říkají „Volné“, sekce „dostupných“. `LotDashboard.razor:286,1017`, resource Lot_State_Free. Operátor může zaměnit přehled za potvrzenou možnost rezervace konkrétního termínu. | Ověřeno zobrazení; aplikace již obsahuje upozornění, že fyzickou přítomnost neověřuje. Není doložena chyba samotného výpočtu rezervací. | Sjednotit krátký popisek a časový kontext; finální dostupnost ověřovat v termínovém formuláři. Navíc opravit počet „Zobrazeno míst: 68“, když je vykresleno 60 a tlačítko říká „60 z 68“. |
| UI-09 **P2** — vyhledávání a filtry bez přístupného názvu | **Prohlížeč a kód:** searchbox návštěv a míst nemá přístupný název; filtry míst nemají jasný název nezávislý na vybrané hodnotě. `ParkingSpots.razor:150–162`, `VisitorBookings.razor:63`. Přepínač dnešní/nadcházející v `:66,69` neoznamuje vybraný stav přes aria-pressed. | Kontrola accessibility stromu; kompletní průchod čtečkou obrazovky neproběhl. | Přidat viditelné nebo přístupné názvy a sémantický stav přepínače. Test role/name a klávesnicového ovládání. |
| UI-10 **P2** — prázdné výsledky mají nesprávnou zprávu | **Prohlížeč:** v Dnešní agendě je „Žádné nadcházející návštěvy“. `VisitorBookings.razor:86` používá stejnou zprávu i pro nulový výsledek hledání. Není jasné, zda není agenda, nebo nic neodpovídá filtru. | Zobrazeno na prázdné dnešní agendě; vyhledávací větev je doložena kódem. | Rozlišit žádné dnešní návštěvy, žádné nadcházející a žádnou shodu; nabídnout zrušení filtru. Nezaměňovat s již samostatně ošetřenou chybou načtení. |
| UI-11 **P2** — výběr hostitele potichu ořízne seznam | **Kód:** `VisitorBookings.razor:345` volá UserAdmin.ListAsync(null); `UserAdminService.cs:28,36` vrací maximálně 500 účtů seřazených e-mailem. `HostOptions:534` teprve pak vybírá aktivní. Část oprávněných hostitelů tedy u většího adresáře nepůjde vybrat. | Limit doložen zdrojem, scénář více než 500 účtů nebyl nově vykonán. | Autorizované serverové vyhledávání aktivních hostitelů se stránkováním a jasným stavem výsledků. Test hostitele mimo první stránku a adresáře s blokovanými účty. |
| UI-12 **P2, riziko** — starší načtení může přepsat novější filtr | `UserList.razor:236 ReloadAsync` a `AuditLog.razor:176 ReloadAsync` přijímají výsledek bez pořadového čísla požadavku. Při překrytí načítání hrozí výsledky neodpovídající viditelnému filtru. | Souběh s řízeným zpožděním nebyl reprodukován; neprezentovat jako experimentálně potvrzenou chybu. | Přijmout pouze poslední požadavek, případně rušit předchozí. Test opačného pořadí dokončení dvou vyhledávání. |

### Doporučené uzavřené celky

1. **Bezpečné formuláře a dialogy:** UI-01, UI-02, UI-04. Malá společná oprava dialogu,
   ochrana konceptů nastavení a důsledná obsluha asynchronních akcí. Bez změn obchodních práv.
2. **Pravdivá agenda návštěv:** UI-03, UI-10, UI-11. Nejprve oddělit dnešní přehled od
   nadcházejících rezervací, následně hostitelské hledání a historii. Jde i o změnu čtecí služby,
   nikoli pouze přejmenování tabulky. Zachovat oprávnění a časovou zónu konfigurace.
3. **Jednotná obsluha míst:** UI-05 až UI-09. Použít existující editaci/službu a společné
   ovládání pro mobil i počítač; nerozmnožovat administraci ani pravidla dostupnosti.

Nejmenší vhodné řešení nevyžaduje nové sekce pro neexistující agendy, nový frontend,
architektonickou vrstvu nebo novou knihovnu. Potvrzené závady není potřeba řešit novými grafy.

**Stav:** výše uvedené opravy jsou pouze navrženy. V tomto analytickém celku byla změněna
jen tato dokumentace. Build a automatické testy nebyly opakovány, protože zdrojový kód se
neměnil; starší výsledky testů výše nejsou důkazem opravy těchto UI nálezů. Dostupnost vůči
produkčním datům, úplná čtečka obrazovky, selhání sítě/serveru a dlouhé souběžné vyhledávání
nebyly v tomto auditu ověřeny. Změny nemají dopad na schéma databáze, konfiguraci ani nasazení.


## Oprava UI: koncepty nastavení a společné dialogy — 19. 9. 2026

**Implementováno:** UI-01 a UI-02 v rozsahu níže; UI-04 pouze pro Nastavení webu.
Ostatní nálezy z předchozí tabulky zůstávají otevřené. Dřívější označení „pouze analýza“
se vztahuje k předchozímu analytickému celku, nikoli k této opravě.

- `Settings.razor`: změny se sledují po záložkách proti otisku načtených formulářů.
  Uložení obnoví jen model uložené záložky; ostatní koncepty včetně rozepsaného Entra
  formuláře zůstanou zachované. Textové vstupy aktualizují model během psaní.
  Do připojení ochrany je formulář neaktivní; prohlížeč označí úpravu ihned při vstupu,
  takže rychlý odchod nemusí čekat na serverový round trip.
  Probíhající uložení má serverový busy guard a neaktivní formulář, vždy se ukončí přes
  finally. Chyba služby zachová koncept; nejistý výsledek zápisu a selhání načtení po
  potvrzeném zápisu mají odlišná vysvětlení. Log obsahuje jen typ výjimky a klíč záložky.
- `NavigationLock` chrání opuštění dokumentu. Aplikace používá SSR router s jednotlivými
  interaktivními stránkami, proto samotný NavigationLock nestačil: `settingsDraftGuard`
  v `app.js` hlídá i odkazy enhanced navigation mimo komponentu a návrat historií pomocí
  dostupného Navigation API. Posluchače se odpojí po odstranění stránky. Nezavádí se
  globálně interaktivní router ani ukládání konceptů do prohlížeče.
- `AdminDialog.razor`, `app.js`, `app.css`: společný obal používá nativní dialog/showModal,
  takže pozadí je skutečně neaktivní. Tab/Shift+Tab se uzavírá uvnitř dialogu včetně
  Fluent shadow DOM. Escape respektuje existující Busy pravidlo. Po zavření se fokus
  vrací na původní tlačítko, případně až po jeho opětovném povolení; nekrade fokus,
  pokud už uživatel přešel do jiného pole. Vzhled a obsah formulářů se zachovávají.
- `AdminUiSafetyTests.cs` přidává pět regresních scénářů; `AdminTests.cs` ověřuje roli
  dialogu na novém nativním obalu namísto jeho vnitřní karty. Testy vlastního ukládání
  rezervací, konfliktů a generování míst nadále používají skutečnou aplikaci a SQL Server.

**Návod pro správce:** v Nastavení webu vyberte záložku, upravte hodnoty a použijte její
Uložit. Úspěch potvrzuje zpráva „Nastavení bylo uloženo.“ Rozpracované hodnoty ostatních
záložek zůstávají k samostatnému uložení. Při odchodu s konceptem zvolte zrušení odchodu,
chcete-li pokračovat; potvrzení odchodu koncept zahodí. Při neověřeném výsledku uložení
ponechte formulář otevřený a ověřte aktuální nastavení v nové kartě před opakováním.
V dialogu lze procházet ovládání Tab/Shift+Tab, zavřít jej Escape nebo tlačítkem Zavřít;
po zavření pokračuje klávesnicový fokus na tlačítku, které dialog otevřelo.

**Rozsah a omezení:** bez migrace, změny oprávnění, veřejných API nebo nových závislostí.
Nasazují se standardní sestavené artefakty včetně aktualizovaných statických souborů;
produkční nasazení nebylo provedeno. Nejde o ochranu proti souběžnému přepsání konfigurace
jiným správcem. Koncept se neobnovuje po pádu procesu nebo potvrzeném obnovení stránky.
Zpět/Vpřed v rámci enhanced navigation využívá Navigation API; v prohlížeči bez tohoto
API tato větev není zaručena (P2, zůstává k ověření/řešení podle podporovaných prohlížečů).
Neočekávané selhání služby a dvojí odeslání jsou ošetřeny kódem; tento celek neinjektoval
serverovou výjimku do nastavení. Oprava dalších účtových akcí z UI-04 stále zbývá.


**Ověření dokončeného celku:** původní tři reprodukční scénáře před opravou selhaly
(`ui-safety-before.trx`): únik fokusu, ztráta konceptu při uložení jiné záložky a odchod
bez potvrzení. Finální společný běh `ui-safety-confirmed.trx` prošel **34/34**, bez selhání
nebo přeskočení. Filtr zahrnuje AdminUiSafetyTests, AdminTests, VisitorBookingTests a
ParkingAdministrationTests; návštěvnický fixture měl zapnuté bezpečně omezené lokální
provisioning nad skutečným SQL Serverem. Nejde o opakování celé aplikační/testovací sady.

Při opravě testy odhalily i zpožděné povolení původního Fluent tlačítka a rychlý návrat
Zpět před serverovým zpracováním vstupu; obojí je zahrnuto v dokončeném řešení. Generátor
míst nyní v testu čeká na skutečné zavření dialogu před vyplňováním pole na neaktivním
pozadí. Mezilehlý běh měl opakované timeouty připojení stránek po souběžném sestavování
Release a byl přerušen; diagnostika ukázala HTTP 200, ale žádný Blazor websocket. Po novém
Debug sestavení a čistém startu se timeouty neopakovaly. Přesná příčina nebyla samostatně
izolována. Testovací helper nově vypíše pouze stav, cestu bez query a počty připojení/chyb,
aby další takové selhání nezůstalo bez kontextu.

Ruční prohlížečová kontrola potvrdila Shift+Tab uvnitř dialogu, návrat fokusu na
„Nová rezervace pro návštěvu“ a čitelné zobrazení dialogu na počítači i při šířce 390 px.
Testy běžely v Chromiu. CS/EN resources prošly XML kontrolou včetně unikátních klíčů,
JavaScript prošel kontrolou syntaxe bundled Node z Playwright a `git diff --check` prošel.

Finální Release build celé solution i Debug build webu prošly s **0 chybami a varováními**.
Po finálním sestavení a restartu lokální testovací aplikace se zopakovalo všech pět nových
regresních testů: **5/5** (`ui-safety-smoke.trx`). Lokální aplikace zůstává spuštěná,
produkce se neměnila. Nebyl proveden commit ani push.
