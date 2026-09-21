# Audit rezervací, rezidentního přidělení a uvolňování míst

Datum: 19. 9. 2026. Rozsah: aktuální pracovní kopie, doménová pravidla, služby, Razor obrazovky, konfigurace a existující testy. Nerezident zde znamená zaměstnance bez přiděleného místa; návštěvník recepce je samostatný režim.

Původní audit kombinoval kontrolu implementace s cílenými testy proti lokálnímu SQL Serveru. Prošlo 84 existujících testů a osm diagnostických reprodukcí potvrdilo níže popsané problémové chování služeb. Diagnostické testy zachycují stav před opravami; jejich úspěch neznamená, že byly vady opravené. Obrazovky byly v této fázi posouzeny z kódu. Produkční provoz ani produkční hodnoty nastavení nebyly ověřovány.

**Následné opravy na žádost uživatele:** body 1–12 jsou opravené v pracovní kopii. Níže zůstávají původní reprodukce a u každého bodu je uvedené provedené řešení. Bod 3 má samostatný [rozbor konfigurace a ochrany celého dne](RESERVATION-DAY-PROTECTION.md); body 4–12 mají [přehled implementace, konfigurace, migrací a ověření](PARKING-WORKFLOW-FIXES.md). Existující rozpracované změny uživatele zůstaly zachované.

**Oprava původní informace o prostředí:** požadované SDK 10.0.401 je nainstalované v `~/.dotnet` a lokální SQL Server běží na `127.0.0.1:14333`. První pokus použil systémový `dotnet` s jiným SDK a nenastavené připojení testovacího procesu; nešlo o chybějící instalace. Po použití správného SDK a připojení všechny zde uvedené testy proběhly. Databáze pro ověření byly oddělené od aplikačních dat.

## Závěr

Základ systému je použitelný: chrání souběžné rezervování, rozlišuje rezidentní přidělení a rezervaci, má frontu, náhrady, refundace a adresné předání. Největší slabinou jsou přechody mezi těmito funkcemi. Uživatel nemá vždy spolehlivou odpověď na tři otázky: **Mám kde parkovat? Do kdy platí tento příslib? Co přesně způsobí moje akce?**

Původní prioritou bylo odstranit zablokování nerezidentního formuláře, zabránit souběhu vlastního přidělení s alternativní rezervací a chránit již začaté parkování. Tyto tři vady jsou opravené. Následná oprava doplnila zachování budoucích příslibů při změně konfigurace, trvalé výjimky plánu, přednost fronty, rozlišené ukončování, provozní blokace a transakční záznamy oznámení.

## Co už je pokryté a stojí za zachování

- Vytvoření rezervace a rozhodování fronty používá serializable transakce; existují testy souběhu, překryvů a dvojích refundací.
- Vlastní přidělení rezidenta nespotřebovává týdenní limit sdílené kapacity. Přidělení lze rozdělit mezi více rezidentů.
- Rezervace alternativního místa a automatické uvolnění vlastního dne se zapisují atomicky.
- Návrat rezidentního místa umí přednostně hledat náhradu; přesun zachovává identitu rezervace, cenu a kalendářovou revizi.
- Kalendář používá místní čas areálu; celodenní intervaly zohledňují změnu délky dne při přechodu času.
- Návštěvnická místa jsou oddělená od zaměstnaneckého fondu. Přijetí adresného předání znovu ověřuje oprávnění příjemce.
- Automatický plán má značku zpracovaného horizontu, takže běžný další průchod sám neopakuje ručně odvolané uvolnění.
- Odměny neovlivňují pořadí fronty ani přístup k místům. Aplikace je plánovač bez povinného potvrzování příjezdu a odjezdu.

Absence check-inu sama o sobě není chyba. Znamená ale, že systém nesmí zaměňovat rezervaci za prokázanou fyzickou přítomnost ani za prokázané nedostavení.

## Pohled obou uživatelů

| Situace | Současné chování | Doporučená zkušenost |
|---|---|---|
| Nerezident vybírá termín | Datum a čas, seznam míst, pevná cena při zapnutém rozpočtu. | Výběr termínu dostupný vždy; před potvrzením vidět cenu, zbývající limit a ochranu rezervace. |
| Nerezident nenajde místo | Fronta jen při nedostupnosti libovolného místa ve fondu. | Rozlišit „nic vhodného“, „nesplňuji pravidla“ a „plno“; fronta má respektovat požadavky na místo. |
| Nerezident získá nabídku | Nabídka s expirací, nutné převzetí. | Vidět termín, místo, zbývající čas a důsledky převzetí přímo na kartě; údaje se samy aktualizují. |
| Rezident má přidělený den | Může mít právo parkovat bez samostatné rezervace. | Jednoznačné „Máš přiděleno A-12“; přidělení zahrnout do kapacity, konfliktů a řešení incidentů. |
| Rezident nepotřebuje den | Uvolní celé datum nebo rozsah; automatizace podle dnů týdne. | Náhled konkrétních dotčených dnů, výjimek a času účinnosti. |
| Rezident chce místo zpět | Výsledek závisí na zdroji uvolnění, hostovi, lhůtě, náhradě a konfiguraci. | Před akcí konkrétní výsledek: volné vrácení / výměna / žádost správci / zamítnutí s důvodem. |
| Rezident rezervuje jiné místo | Uvolnění vlastního dne podle nastavené politiky. | Atomická změna „A-12 uvolníš, B-08 získáš“, platná i přes frontu a předání. |
| Kdokoli odjíždí dřív | `Uvolnit` i `Zrušit` ukončí celou rezervaci. | Před začátkem „Zrušit rezervaci“, po začátku „Ukončit dříve“; zachovat proběhlou část. |

## Zjištění podle priority

### 1. P1 — Nerezident může ztratit možnost vybrat jiný den — opraveno

**Scénář:** dnes je nepovolená sobota, svátek nebo je vypnuto rezervování na dnešek. `_day` se při otevření nastaví na dnešek. `SelectedDateAllowsNewReservation` je false, takže se nevykreslí `SharedSearchArea`, která obsahuje také jediný výběr data nerezidenta. Týdenní plánovač se zobrazuje jen rezidentům. Nerezident se proto z této obrazovky nedostane ani k povolenému pondělí. Stejná past vznikne po výběru zakázaného data.

**Řešení:** výběr data a času vykreslovat vždy. Zakázat pouze akci hledání/rezervování a vysvětlit důvod. Nabídnout „Nejbližší povolený den“. Stejný základ kalendáře používat pro oba typy uživatelů.

**Provedeno:** výběr data a času nerezidentovi zůstává dostupný i při zakázaném dnešku, víkendu či svátku a nezmizí ani po výběru zakázaného data. Stávající vysvětlení a zákaz hledání zůstávají účinné do výběru povoleného termínu. Ověřeno v prohlížeči pro celodenní i časový režim. Tlačítko pro automatický výběr nejbližšího dne a společný týdenní plánovač nejsou součástí této opravy.

Zdroj: [Reserve.razor](../D3Parking.Web/Components/Parking/Reserve.razor), ř. 94, 460, 1695 a 1834.

### 2. P1 — Rezident může opět blokovat dvě místa — opraveno

**Scénář:** rezident rezervuje B-08; systém správně uvolní jeho A-12. Dokud si A-12 nikdo nevezme, rezident použije „vzít zpět“. `ReclaimCoreAsync` smaže uvolnění, ale nekontroluje jeho stále existující rezervaci B-08. Má tedy rezervaci B-08 a současně vlastní přidělení A-12. UI vrácení takového volného dne umožňuje.

**Řešení:** jednotně vynucovat nejvýše jeden současný nárok na parkovací kapacitu uživatele, včetně přidělení bez rezervace. Nabídnout atomickou výměnu: „Zrušit B-08 a vrátit A-12“. Pokud je původní místo obsazené, použít pravidla ochrany rezervace. Kontrola musí být společná pro rezervaci, vrácení, změnu členství i administrativní přesun.

**Provedeno:** `ReclaimCoreAsync` ve stejné serializable transakci před jakoukoli změnou ověřuje živé rezervace rezidenta na jiných místech. Překryv s reálně vracenými dny odmítne s konkrétním vysvětlením. Kontrola zahrnuje hromadné vrácení a přijaté adresné předání, včetně části předání přesahující požadovaný rozsah. Zrušené, uvolněné, dokončené a již skončené rezervace neblokují vrácení; rezervace téhož fyzického místa není druhým nárokem. Dialog rezidenta při známém překryvu ukáže důvod a vypne akci vrácení. Pro návrat se používá již existující atomická cesta zrušení alternativy a vrácení volného automaticky uvolněného vlastního dne. Samostatná výměna zahrnující obsazené vlastní místo ani změny přidělování rezidentů nejsou součástí této opravy.

Zdroj: [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 360–408; [ResidentSpotService.cs](../D3Parking.Infrastructure/Parking/ResidentSpotService.cs), ř. 353–559.

### 3. P1 — Přesun rezervace během dne nepřesune auto — opraveno

**Scénář:** kolega parkuje na A-12 od 8:00 do 17:00. V 11:00 si rezident místo vyžádá zpět. Při dostupné náhradě služba okamžitě přepíše rezervaci na B-08 a vrátí A-12 rezidentovi. Kontroluje konec rezervace, nikoli zákaz automatického přesunu po začátku. V režimu bez check-inu zůstává i probíhající parkování ve stavu `Reserved`.

Nastavení `ManualReleasesAreBinding` tomu nezabrání: omezuje vytlačení bez náhrady, nikoli přesun. Ani ochrana historického `CheckedIn` není dostatečná, protože kontrola tohoto stavu je až ve větvi bez náhrady.

**Přijaté řešení a provedení:** celodenní rezervace drží stejné místo od místní půlnoci do následující půlnoci, časová od začátku do konce intervalu. Rezident ani správce ji během této doby nemohou přesunout či odebrat, a to při žádné prioritě ani dostupnosti náhrad. Ochrana vychází z uloženého intervalu a přežije změnu režimu či kalendáře. Držitel ji podle výslovného rozhodnutí uživatele smí dobrovolně zrušit nebo uvolnit. Budoucí rezervace se dál řídí konfigurací. Zákaz se kontroluje ve službách i při doménovém přesunu; UI vysvětluje důvod. Podrobná matice priorit, lhůt, závaznosti a náhradních akcí je v [samostatném rozboru](RESERVATION-DAY-PROTECTION.md).

Zdroj: [ResidentSpotService.cs](../D3Parking.Infrastructure/Parking/ResidentSpotService.cs), ř. 415–491; [Reservation.cs](../D3Parking.Domain/Parking/Reservation.cs), metoda `MoveTo`.

### 4. P1 — Pravidla pro vytvoření rezervace se používají i k rušení existujících — opraveno

**Scénář:** správce vypne rezervace na dnešek. Nápověda výslovně slibuje, že existující dnešní rezervace zůstanou platné. Změna ale spustí `FindCalendarImpactAsync`, který na všechny dosud neskončené rezervace aplikuje pravidlo pro nové rezervování. Po potvrzení dopadů se dnešní rezervace zruší, včetně již započatých. Podobně zkrácení horizontu může rušit dříve oprávněně vytvořené budoucí plány.

Fronta používá stejnou kontrolu při údržbě: čekání založené dopředu se v den parkování stane nepřípustným, pokud `SameDayReservationsAllowed=false`. To není totéž jako zákaz nových dnešních žádostí.

Samotná změna konfigurace má náhled a potvrzení, což je správně. Ve službě ale následně chybí oznámení dotčeným držitelům rezervací; audit změny není náhradou za informaci řidiči.

**Řešení:** oddělit oprávnění vytvořit nový požadavek, platnost existujícího příslibu a skutečnou uzavírku. Běžné změny účinné od určeného data mají zachovat existující rezervace. Nouzové rušení má být samostatná akce s přesnými dopady, důvodem, refundací, oznámením a řešením již začatých intervalů. U fronty výslovně určit, zda dřívější žádost smí být potvrzena i v den parkování.

**Provedeno:** Běžné uložení konfigurace zachovává rezervace, frontu, soukromá předání, návštěvy i uvolnění. Dříve založená fronta a předání mohou dokončit svůj uložený interval i po změně kalendáře či režimu. Nové žádosti se řídí aktuálními pravidly. Převzetí dál ověřuje živou kapacitu a ostatní podmínky.

Zdroj: [ParkingSettingsService.cs](../D3Parking.Infrastructure/Parking/ParkingSettingsService.cs), ř. 399–529; [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 1441–1455; [SharedResource.resx](../D3Parking.Web/Resources/SharedResource.resx), klíč `Parking_Settings_SameDayReservationsTooltip`.

### 5. P2 — Pravidelný plán a ruční výjimky nejsou samostatně modelované — opraveno

**Scénář A:** automaticky uvolněné úterý rezident změní na den, kdy bude jezdit do práce. Uložení jen přepíše plán a vynuluje značku zpracování. Následná údržba vytváří nová uvolnění, ale nebere zpět stará uvolnění kvůli změně osobního plánu. Totéž platí při vypnutí automatického uvolňování. Výsledek odporuje očekávání, že vybrané dny zůstávají přidělené rezidentovi.

**Scénář B:** ruční vrácení dne je chráněné značkou zpracovaného horizontu, nikoli uloženou výjimkou. Opětovné uložení plánu, i beze změny hodnot, tuto značku smaže. Pokud den zůstává mimo pravidelný plán, může být znovu automaticky uvolněn. Značky resetují i některé změny globálního kalendáře.

**Řešení:** oddělit opakující se vzor a výjimku pro konkrétní datum (`Ponechat`, `Uvolnit`, `Předat`). Při změně vzoru ukázat rozdíl: nové uvolnění, zpět získané volné dny, chráněné rezervace. Výjimky zachovat, dokud je uživatel výslovně nezruší. Uložení stejných hodnot má být bez vedlejšího účinku. Nepřebírat již rezervované dny automaticky.

**Provedeno:** Ruční vrácení zapisuje trvalou výjimku ResidentDayHold. Nová ruční nabídka téhož dne ji výslovně odstraní. Změna pravidelného plánu má náhled konkrétních dat, ihned doplní nová uvolnění a vrací pouze volná automatická uvolnění. Manuální uvolnění, rezervace a aktivní nabídky fronty zachovává. Stejné hodnoty nemění zpracovaný horizont.

Zdroj: [ResidentSpotService.cs](../D3Parking.Infrastructure/Parking/ResidentSpotService.cs), ř. 693–855; [ParkingSpotResident.cs](../D3Parking.Domain/Parking/ParkingSpotResident.cs), `SetUsagePlan`; [ParkingSettingsService.cs](../D3Parking.Infrastructure/Parking/ParkingSettingsService.cs), ř. 203–216.

### 6. P2 — Potvrzení uvolnění vlastního místa nefunguje přes frontu — opraveno

**Scénář:** rezident s drženým vlastním místem získá nabídku jiného místa. Při politice `ConfirmRelease` převzetí vždy posílá `confirmResidentRelease:false`. Služba vrátí požadavek na potvrzení, ale `ClaimQueueAsync` zobrazí jen chybu. Potvrzovací dialog má pouze přímá rezervace. Stejný parametr chybí i při převzetí z úvodní stránky.

**Řešení:** společný náhled rezervace pro přímou rezervaci, frontu a předání. Náhled obsahuje přesnou změnu vlastního místa; potvrzení je svázané s konkrétní nabídkou, intervalem a verzí pravidel. Dočasnou nabídkou uživatel nesmí přijít o pořadí jen proto, že chybí potřebné potvrzovací UI.

**Provedeno:** Parkování i úvodní stránka používají společnou akci převzetí. Požadavek ConfirmRelease zobrazí potvrzení konkrétní nabídky a intervalu; potvrzené převzetí atomicky uvolní vlastní přidělený den. Změna identity nabídky potvrzení zneplatní.

Zdroj: [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 393–396 a 1401–1402; [Reserve.razor](../D3Parking.Web/Components/Parking/Reserve.razor), ř. 1969–1995 a 2343–2346; [Home.razor](../D3Parking.Web/Components/Pages/Home.razor), ř. 160.

### 7. P2 — Sdílený rezident nemůže využít uvolnění spolurezidenta přes běžné hledání — opraveno

**Scénář:** A a B mají stejné fyzické místo. Na úterý je přidělené B, který jej uvolní. Služba by rezervaci A povolila jako čerpání sdílené kapacity. UI však vždy odstraní fyzické `_ownedSpot.SpotId` ze seznamu výsledků, bez ohledu na denní přidělení. A nemůže vzít den zpět, protože uvolnění patří B. Pokud je to jediné volné místo, fronta současně odmítne zařazení, protože backend vidí dostupnou kapacitu.

**Řešení:** rozhodovat podle nároku v konkrétním termínu, nikoli podle samotného členství na místě. Vlastní přidělený den nabízí vrácení; veřejně uvolněný den jiného rezidenta nabízí běžnou rezervaci a počítá se do limitu.

**Provedeno:** Seznam dostupných míst skrývá vlastní fyzické místo pouze při vlastním přidělení pro vybraný den. Veřejně uvolněný den spolurezidenta lze běžně rezervovat; spotřebovává týdenní limit sdílené kapacity.

Zdroj: [Reserve.razor](../D3Parking.Web/Components/Parking/Reserve.razor), ř. 1145–1153 a 1550–1558; [ResidentSpotService.cs](../D3Parking.Infrastructure/Parking/ResidentSpotService.cs), ř. 136–145; [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 1319–1323.

### 8. P2 — Fronta chrání nabídku, ale negarantuje přednost při novém uvolnění — opraveno

Přímá rezervace respektuje již existující nabídky, nikoli samotné čekající. Ruční uvolnění rezidentního dne vrátí úspěch bez zpracování fronty. Automatické uvolnění při rezervaci alternativy také nevyvolá okamžitý matching. Místo mezitím může přímo získat nově příchozí, zatímco čekající čekají na údržbu. U zrušení běžné rezervace se fronta sice spustí hned, ale až po commitu, takže i zde existuje mezera.

**Řešení:** stanovit, zda má FIFO garantovat přednost. Pokud ano, nově uvolněnou kapacitu buď přidělit prvnímu způsobilému čekajícímu ve stejné transakci, nebo dočasně skrýt přímým rezervacím do zpracování fronty. Samotné rychlejší spouštění údržby mezeru neodstraní.

**Provedeno:** Přímá veřejná rezervace uvnitř stejné serializable transakce kontroluje dřívější způsobilé čekající pro daný typ a celý interval. Uvolnění a alternativní rezervace vyvolávají zpracování fronty. Matcher přeskakuje nezpůsobilé čekající a respektuje vybraný typ. Neplatná provozní nabídka se stáhne bez ztráty pořadí.

Zdroj: [ResidentSpotService.cs](../D3Parking.Infrastructure/Parking/ResidentSpotService.cs), ř. 235–250; [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 431–456, 758–783 a 1472–1549; [ParkingMaintenanceService.cs](../D3Parking.Web/Parking/ParkingMaintenanceService.cs), ř. 54–56.

### 9. P2 — „Uvolnit“ a „Zrušit“ mají téměř stejný význam; ztrácí se již proběhlá část — opraveno

Obě tlačítka jsou nabízená současně před začátkem i během rezervace. Obě akce odstraní celou rezervaci z živé dostupnosti a používají stejnou podmínku refundace. Týdenní limit úplně ignoruje stavy `Released` a `Cancelled`.

**Scénář:** uživatel parkuje v pondělí téměř celý den, před koncem klikne na uvolnění a zopakuje to v úterý. Oba dny přestanou zabírat týdenní limit a lze rezervovat další. Pokud má limit znamenat nejvýše dva dny čerpání za týden, neplní tento účel. Pokud má omezovat jen neukončené plány, musí to být takto pojmenováno.

V celodenním režimu má rezervace začátek o půlnoci. I s nulovým `ReleaseCutoff` ranní zrušení nevrací kredity. UI tento finanční důsledek před akcí neukazuje. Administrační uložení navíc nastavuje cutoff na nulu; ovládací prvek je zakomentovaný.

**Řešení:** před začátkem storno, po začátku předčasné ukončení s uloženým efektivním koncem. Zahájený den zůstane započtený do kvóty, pokud tak zní firemní pravidlo; bez check-inu jej nazývat zahájeným, nikoli prokazatelně využitým. Zobrazit přesnou refundaci a její lhůtu. Pro celodenní rezervace definovat samostatný čas storna, pokud se má lišit od půlnoci.

**Provedeno:** Před začátkem se nabízí storno, po začátku předčasné ukončení, vždy s náhledem refundace. Zahájený a dobrovolně ukončený den dál spotřebovává týdenní limit. Historie zachovává původní interval i efektivní konec. Nové rezervace ukládají refundní lhůtu; administrace ji skutečně ukládá a validuje v rozsahu 0–1439 minut před začátkem.

Zdroj: [Reserve.razor](../D3Parking.Web/Components/Parking/Reserve.razor), ř. 1285–1288; [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 694–699 a 724–847; [ParkingSettings.razor](../D3Parking.Web/Components/Admin/ParkingSettings.razor), ř. 508–524 a 1197–1203.

### 10. P2 — Provozní problém místa není součástí jeho dostupnosti — opraveno

Po nahlášení fyzicky obsazeného místa se rezervace zruší, ale místo zůstane úmyslně dostupné dalším. Hrozí řetězec dalších řidičů poslaných ke stejnému blokujícímu autu. Náhradní místo v tomto toku navíc není filtrováno podle typu původního místa.

Deaktivace ponechá existující rezervace a upozorní jejich držitele. Rezident, který používá pouze implicitní přidělení bez rezervace, v seznamu příjemců není. `OwnedSpotDto` nepřenáší aktivitu místa, takže rezidentní přehled může dál tvrdit, že den drží. Specializovaný tok „Nemohu zaparkovat“ vyžaduje rezervaci, kterou tento rezident nemusí mít.

**Řešení:** odlišit zákaz nových rezervací od fyzické nedostupnosti. Přidat dočasné blokování s důvodem, intervalem a přezkumem; jedno hlášení nemusí znamenat trvalou odstávku. Incident musí jít založit i proti platnému rezidentnímu přidělení. Oprava má zahrnout existující plány i rezidenty. Náhrada musí splnit požadavky vozidla a řidiče.

**Provedeno:** Hlášení fyzické překážky místo blokuje do uloženého konce intervalu nebo výslovného uvolnění správcem s oprávněním ManageSpots. Blokaci respektuje hledání, fronta, přímé rezervace i náhrady. Automatická náhrada je stejného typu. Incident může nahlásit i implicitně přidělený rezident; při náhradě neponechá dva nároky. Deaktivace je viditelná a oznámená i rezidentům bez rezervace.

Zdroj: [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 890–904 a 945–979; [ParkingSpotService.cs](../D3Parking.Infrastructure/Parking/ParkingSpotService.cs), ř. 501–516; [OwnedSpotDto.cs](../D3Parking.Application/Parking/OwnedSpotDto.cs).

### 11. P2 — Potvrzení změny a doručení informace nejsou jeden spolehlivý proces — opraveno

Rezervace i vrácení místa commitují stav a až potom samostatně zapisují oznámení. Výpadek mezi kroky může zanechat provedenou změnu bez informace. U nabídky z fronty se může začít odpočítávat čas, aniž uživatel dostane zprávu. Existující e-mailová fronta chrání již zapsané oznámení, nikoli mezeru mezi změnou parkování a jeho vytvořením.

Přesun a zrušení při vrácení rezidentovi mají navíc různou úroveň oznámení; přesun a návrat do fronty jsou `Warning`, kterou lze potlačit osobním nastavením kategorie. Veřejně slíbené místo by nemělo zmizet jen s volitelnou informací.

**Řešení:** ukládat událost změny společně s parkovací transakcí; idempotentní doručování ji zpracuje později. Pro změnu potvrzeného místa definovat povinný záznam pro uživatele a jasná pravidla externích kanálů. Výpadek doručování nabídky nemá automaticky znamenat ztrátu pořadí.

**Provedeno:** Záznam do schránky a případný e-mail do trvalé odchozí fronty vznikají ve stejné transakci jako nabídka, rezervace, předání nebo vynucená změna. Schránka uchová změnu parkování i při vypnuté kategorii; osobní preference dál řídí volitelné externí doručení. Expirace nabídky nepřesáhne konec intervalu. Při nedoručeném e-mailu se kapacita uvolní dalším, původní pořadí zůstane zachované a další nabídka počká na obnovení doručení.

Zdroj: [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 571–586 a 1565–1591; [ResidentSpotService.cs](../D3Parking.Infrastructure/Parking/ResidentSpotService.cs), ř. 558–590; [NotificationService.cs](../D3Parking.Infrastructure/Notifications/NotificationService.cs), ř. 116–173.

### 12. P2 — Zobrazení dostupnosti a akcí nevychází z úplného stavu — opraveno

- „Obsazenost“ v cenové nabídce počítá rezervace, ale ne nevydaná rezidentní přidělení ani dočasné nabídky fronty. Parkoviště s deseti rezidentními místy bez rezervací může ukazovat 0 % a současně nenabídnout žádné místo.
- `CanReclaim` znamená převážně „uvolnil tento uživatel“, nikoli „současná pravidla opravdu dovolují vrácení“. UI proto nabízí akci, která teprve po kliknutí narazí na ochranu rezervace.
- Počet dnů v náhledu hromadného uvolnění je délka intervalu, zatímco služba přeskakuje svátky, nepřidělené, již uvolněné a další nezpůsobilé dny. „Návrat“ následující den také nemusí odpovídat rotačnímu přidělení.
- Desetisekundový refresh načte data rezervací/fronty pouze při změně politiky nebo kalendářního dne. Běžná rezervace kolegy, nová nabídka či změna osobního plánu proto samy nezaktualizují otevřenou stránku.

**Řešení:** jednotný výpočet dostupnosti a povolených akcí pro uživatele, místo a interval. Oddělit „rezervováno“, „přiděleno rezidentům“, „dočasně nabídnuto“, „mimo provoz“ a „volné pro tebe“. Vrácení a hromadné změny mají mít náhled dopadů ze stejné logiky jako provedení. Změny dat mají invalidovat příslušný kalendář; rozhodující kontrola zůstává na serveru.

**Provedeno:** Hledání a cenový kontext sdílejí výpočet kapacity včetně rezidentů, nabídek a blokací. CanReclaim používá tentýž rozhodovací kód jako provedení a vrací důvod zamítnutí. Hromadné uvolnění ukazuje skutečná dotčená data a další přidělený den. Otevřené parkování obnovuje data po 10 sekundách i bez změny politiky, při zachování rozepsaného plánu. Zákaz nových rezervací neskrývá existující rezidentský stav.

Zdroj: [ReservationService.cs](../D3Parking.Infrastructure/Parking/ReservationService.cs), ř. 655–673; [ResidentSpotService.cs](../D3Parking.Infrastructure/Parking/ResidentSpotService.cs), ř. 136–145 a 332–344; [Reserve.razor](../D3Parking.Web/Components/Parking/Reserve.razor), ř. 1067–1101, 1402–1411 a 2033.

## Původní návrhy dalších stavů a pravidel

Tabulka níže je původní širší návrh. Opravy bodů 1–12 nejsou implementací všech volitelných budoucích funkcí v tabulce; přesné hranice jsou uvedené v navazujícím přehledu oprav.

Nedoporučuji jeden velký enum kombinující všechny významy. Oddělit právo k místu, stav rezervace a provozní dostupnost. Časový stav „budoucí / právě běží / skončila“ lze odvodit; není to informace o fyzické přítomnosti.

| Stav / událost | Navržené pravidlo |
|---|---|
| Přiděleno rezidentovi bez rezervace | Platný nárok zahrnutý do konfliktů, kapacity a řešení incidentu. |
| Rezident v tento den nemá přidělení | Stejná pravidla sdílené rezervace jako nerezident; může využít uvolnění spolurezidenta. |
| Ruční výjimka „tento den potřebuji“ | Přetrvá uložení opakujícího se plánu i běžnou změnu horizontu. |
| Částečně uvolněný den | V časovém režimu rozhodnout, zda podporovat např. 12:00–17:00; dnešní `SpotRelease` reprezentuje jen celý den. |
| Fronta čeká / má nabídku | Oddělit od potvrzené rezervace; přesná expirace a důvod odebrání. |
| Nevyzvednutá nabídka | Posun dozadu je možná politika, ale zavést limit opakovaných nabídek a pravidla nočního doručování. |
| Nabídka těsně před koncem parkování | Expiraci omezit koncem intervalu; pod minimální užitečnou délkou nenabízet. |
| Čekající bez potřebného rozpočtu nebo oprávnění | Před nabídkou ověřit způsobilost; místo neblokovat nepřevzitelnou nabídkou. |
| Přímé předání vs. veřejná fronta | Výslovně rozhodnout, zda smí předání obcházet veřejnou frontu; nyní jde o oddělenou soukromou cestu. |
| Nevyřízená žádost o předání | Rozhodnout, kdy jde jen o žádost a kdy o blokaci. Nyní i čekající žádost brání souběžnému uvolnění daného termínu. |
| Potvrzená, ale odvolatelná rezervace | Viditelně uvést podmínky a okamžik ochrany; nepoužívat bez dalšího stejný příslib jako u garantované rezervace. |
| Započatá rezervace | Implementováno: stejné místo až do konce; může jej dobrovolně uvolnit držitel. Správce ani rezident ochranu nepřepíší. |
| Předčasné ukončení | Zachovat původní interval i skutečný okamžik ukončení plánu; uvolnit jen zbytek, případně s předávací rezervou. |
| Nepotvrzená fyzická překážka | Dočasný blok nebo zvýrazněné omezení s přezkumem; ne automatická trvalá odstávka. |
| Plánovaná odstávka / uzavřená část areálu | Časově omezený stav s náhledem dotčených nároků, refundací a náhradami. |
| Změna rezidentů | Ukázat nová rotační přidělení; chránit platné rezervace a individuální výjimky. |
| Změna pravidel / časového pásma | Verze, účinnost a náhled; odlišit nová pravidla od změny již slíbených podmínek. |
| Výpadek / opakovaný klik / opožděná odpověď | Idempotentní akce s dohledatelným výsledkem; po obnovení načíst autoritativní stav. |
| Nedostavení / překročení konce | Bez dat o přítomnosti pouze podezření či hlášení, nikoli automaticky prokázané porušení. |
| Speciální požadavky na místo | Potřeba nabíjení, velikost vozidla, motocykl či oprávnění k vyhrazené kapacitě jsou součástí rezervace, fronty i náhrad. |

Minimální invarianty: jeden fyzický prostor nemá dva překrývající se přísliby; uživatel nemá dva překrývající se nároky včetně rezidentního přidělení; veřejné uvolnění nemůže přepsat platnou soukromou nabídku; změna obrazovky není potvrzení změny pravidel; změna DB sama nepotvrzuje přestavení auta.

## Doporučená konfigurace

Následující je návrh výchozího profilu pro běžné firemní parkování, nikoli výpis produkčních hodnot. Uvedené nové možnosti vyžadují doplnění implementace.

| Oblast | Doporučení | Důvod / podmínka |
|---|---|---|
| Režim | Pro denní kancelářské parkování celý den; časová okna jen při skutečné potřebě střídání. | Dnešní rezidentní uvolnění je celodenní; časový režim potřebuje jasná pravidla předání části dne. |
| Horizont rezervací | 14 dnů, jasně zobrazit poslední datum včetně. | Srozumitelný společný rámec. Současný výpočet zahrnuje dnešek a den D+14. |
| Horizont plánu rezidenta | 14 dnů, standardně stejný jako rezervace. | Kratší horizont znevýhodňuje nerezidenty, protože část později sdílené kapacity zatím nevidí. |
| Dny a svátky | Po–Pá, český kalendář, svátky vypnuté; přidat konkrétní mimořádné otevřené/zavřené dny. | Samotný týdenní vzor neřeší firemní uzavírky ani pracovní sobotu. |
| Rezervace na dnešek | Povolit, pokud není konkrétní provozní důvod pro zákaz. | Uvolněná kapacita se dá využít ještě dnes. Zákaz se má týkat nových žádostí, ne zpětně rušit nároky. |
| Týdenní limit | Např. 2 různé dny sdílené kapacity; upřesnit započtení zahájených dnů. | Vlastní přidělení se nepočítá. Čekání doporučuji omezovat samostatným limitem a při převzetí znovu ověřit kvótu. |
| Potvrzená rezervace | Doporučené `ConfirmedBookingProtected`. | Nejjednodušší příslib pro řidiče. Ochrana započatého intervalu je nyní společná všem politikám; rozdíly zůstávají před začátkem. |
| Hybridní priorita | Jen jako vědomá firemní volba; ochrana např. předchozí den v 18:00. | Před ochranou musí uživatel vidět odvolatelnost. „Bez náhrady“ doporučuji řešit přes správce. |
| Ruční uvolnění | Závazné, jakmile je využije jiný držitel. | Podporuje důvěru ve sdílení. V režimu plné ochrany je přepínač nadbytečný a má být skrytý. |
| Alternativní místo | `ConfirmRelease`, až po sjednocení přímé rezervace a fronty. | Uživatel vidí, že dává k dispozici vlastní den. Současná cesta přes frontu toto neumí. |
| Nabídka fronty | Výchozí 30 minut; přidat horní limit a omezení koncem rezervovaného intervalu. | Validátor dnes kontroluje jen minimum 1 minuta. Pravidla mají respektovat zbývající čas a doručitelnost. |
| Automatická údržba | Zpracování změn událostmi, periodická údržba jako záloha. | Kratší interval sám nezajistí spravedlivé přidělení ani atomické změny. |
| Kredity | Výchozí vypnuto (`BaseReservationCost=0`), dokud není jasný účel vedle týdenního limitu. | Dvě souběžná omezení komplikují vysvětlení zamítnutí. Při zapnutí ukázat cenu, obnovu i storno před potvrzením. |
| Vrácení kreditů | Výslovná storno lhůta, zachovaná pro již vytvořenou rezervaci. | Dnešní logika používá aktuální pravidla; celodenní začátek o půlnoci může překvapit. |
| Změny konfigurace | Uložit s účinností a přehledem dopadů; běžně zachovat potvrzené rezervace. | Jinak administrace může měnit už slíbené podmínky a skrýt provozní problém. |

Konfiguraci rozdělit na kalendář, rozdělování kapacity, ochranu rezervací, provoz a volitelný rozpočet. Nabídnout několik přednastavených profilů a pokročilé volby skrýt podle skutečné relevance. Kombinace pěti reclaim politik, závaznosti uvolnění, dvou lhůt a čtyř fallbacků je pro správce bez simulace konkrétního příkladu obtížně předvídatelná.

Oddělit aktivní nastavení od historických bodových polí. Například nápověda osobního plánu stále slibuje odměnu za předstih, přestože vytváření uvolnění přidává nula bodů. Skryté prvky a staré názvy nesmějí sloužit jako specifikace skutečných pravidel.

## Doporučené provedení

1. **Opravit nedostupné a nebezpečné cesty:** nerezidentní datum, vrácení při existující alternativě, přesun po začátku, oddělení nových a existujících rezervací.
2. **Sjednotit rozhodování:** jedna služba vyhodnotí nárok, kalendář, provozní omezení, kvótu, vhodnost místa, frontu a dopad na vlastní přidělení. UI dostává důvod i povolené akce. Při potvrzení server vše znovu ověří v transakci.
3. **Doplnit trvalé výjimky a náhledy:** změna plánu, hromadné uvolnění, návrat místa a administrativní změna mají konkrétní seznam dopadů. Zachovat přehlednou historii zdroje a důvodu.
4. **Spolehlivě distribuovat změny:** událost ve stejné transakci, následné doručování a aktualizace otevřených kalendářů. Fronta musí dostat příležitost dříve než přímý nový zájemce, pokud má platit FIFO.
5. **Zjednodušit pojmy a konfiguraci:** jedna akce pro storno před začátkem, jiná pro předčasný konec; vyřešit refundaci, typy míst a dočasné provozní bloky.

Není nutné přepsat celý projekt. Jde především o sjednocení podmínek a rozhraní mezi již existujícími službami.

## Ověření před nasazením

Existující testy ověřují řadu izolovaných pravidel. Prioritní doplnění představují jejich kombinace a obě persony:

| Test | Požadovaný výsledek |
|---|---|
| Nerezident otevře stránku o víkendu / svátku / se zakázaným dneškem. | Stále může vybrat povolený budoucí den. |
| Nerezident vybere zakázaný den a chce jej opravit. | Výběr data nezmizí. |
| Rezident má alternativu a vrací vlastní místo. | Nejvýše jeden nárok; případná výměna atomická. |
| Vrácení při probíhající rezervaci kolegy. | Žádný automatický přesun bez potvrzení nebo zásahu správce. |
| Vypnutí rezervací na dnešek s platnou dnešní rezervací. | Rezervace zůstane, podle deklarovaného pravidla. |
| Zkrácení horizontu s již potvrzeným vzdálenějším termínem. | Zachování nebo výslovně schválená migrace s oznámením. |
| Dříve založená fronta přejde přes půlnoc do dne parkování. | Nezmizí jen kvůli zákazu nové dnešní žádosti, pokud je zamýšleno pozdější převzetí. |
| Změna úterý na pracovní den / vypnutí automatiky. | Náhled a konzistentní změna již materializovaných volných uvolnění. |
| Ruční vrácení a následné uložení stejného týdenního plánu. | Ruční výjimka zůstane zachovaná. |
| Dva rezidenti, den B veřejně uvolněný, rezervuje A. | UI i služba umožní stejnou operaci se správnou kvótou. |
| Fronta + `ConfirmRelease`, na /parking i na úvodní stránce. | Potvrzení funguje a vytváří rezervaci i uvolnění atomicky. |
| Čekající vs. přímý zájemce při ručním či automatickém uvolnění. | Výsledek odpovídá deklarované prioritě fronty. |
| Předčasné ukončení dvou téměř dokončených dnů. | Neobnoví spotřebovanou týdenní kvótu, pokud omezuje zahájené dny. |
| Celodenní rezervace zrušená ráno při zapnutém rozpočtu. | Náhled refundace přesně odpovídá výsledku. |
| Neaktivní místo s rezidentem bez rezervace. | Žádný zavádějící příslib; informace a cesta k řešení. |
| Hlášená překážka a další hledající uživatel. | Dostane bezpečnou dostupnost podle pravidel dočasného bloku. |
| Speciální místo, fronta a náhradní přidělení. | Zachovají potřebné vlastnosti a oprávnění. |
| Selhání oznámení těsně po potvrzení změny. | Výsledek změny je dohledatelný a oznámení se neztratí. |
| Otevřené dvě relace a nabídka přidělená údržbou. | Obě relace dostanou čerstvý stav; nevznikne dvojí potvrzení. |
| Změna času, lokální půlnoc a přesná hranice ochranné lhůty. | Jednoznačné intervaly; neexistující nebo dvojznačný čas je vysvětlen. |

Výše uvedená tabulka je návrh přejímacích scénářů pro opravy, nikoli tvrzení o jejich úspěšném provedení. Skutečně provedená ověření následují níže.

## Provedené ověření původního auditu před opravami

Ověřeno 19. 9. 2026 pomocí SDK 10.0.401 a lokálního SQL Serveru 16.0.4265.3 (`d3parking-test-sql`, port 14333). Testy používají vlastní databáze a po dokončení je odstraňují.

**Existující testy: 84 úspěšných, 0 neúspěšných, 0 přeskočených.** Cílený výběr pokryl rezidentní prioritu a plán, politiky vrácení, rezervační okna, validaci a změny nastavení kalendáře, souběžné rezervování, konzistenci fronty, ceny, více rezidentů a doménu předání místa. Výsledek je v [existing-tests.trx](../artifacts/parking-workflow-audit/results/existing-tests.trx). Nejde o spuštění celé testovací sady.

**Diagnostické reprodukce: 8 z 8 potvrdilo současné problémové chování.** Každý scénář běžel nad samostatnou SQL databází. Volány byly skutečné aplikační služby a persistence; čas, nastavení a doručování oznámení byly řízené testovacími náhradami. Dopad změny konfigurace ověřoval skutečný `ParkingSettingsService`.

| Zjištění | Reprodukovaný výsledek |
|---|---|
| 2 — Dva nároky rezidenta | Po rezervaci alternativy a vrácení vlastního dne zůstala alternativní rezervace aktivní a vlastní den se vrátil do stavu `Held`. |
| 3 — Přesun po začátku | Vrácení místa rezidentovi přesunulo již započatou rezervaci kolegy na jiné místo bez potvrzení přestavení. |
| 4 — Změna pravidel | Vypnutí nových rezervací na dnešek zařadilo probíhající rezervaci do dopadů a po potvrzení správcem ji zrušilo. |
| 4 — Dřívější žádost ve frontě | Údržba zrušila včera založenou žádost na dnešek při vypnutých nových rezervacích na dnešek. |
| 5 — Změna pravidelného plánu | Přepnutí plánu na potřebu místa každý den ponechalo dříve automaticky uvolněný den veřejný. |
| 5 — Ruční výjimka | Uložení stejných hodnot plánu a následná údržba znovu uvolnily ručně vrácený den. |
| 6 — Potvrzení přes frontu | Převzetí nabídky při `ConfirmRelease` skončilo požadavkem na potvrzení, které rozhraní této akce nepřijímá; rezervace nevznikla. |
| 9 — Týdenní limit | Po předčasném ukončení dvou již započatých dnů šlo při limitu dvou dnů vytvořit rezervaci na třetí den. |

Zdroj diagnostik je v [ParkingAuditReproductions.cs](../artifacts/parking-workflow-audit/repro/ParkingAuditReproductions.cs), výsledky v [repro-tests.trx](../artifacts/parking-workflow-audit/results/repro-tests.trx). Jde o lokální pomocné artefakty v adresáři ignorovaném Gitem; nebyly přidány do běžné testovací sady. Ostatní zjištění nadále vycházejí z kontroly zdrojového kódu. Zejména nebyly dynamicky ověřeny obrazovky, fyzické parkování ani doručování oznámení.

## Ověření oprav bodů 1 a 2

- Původních 84 cílených testů prošlo i po opravě: [výsledky](../artifacts/parking-workflow-fixes/results/application-after.trx).
- [ResidentReclaimConflictTests.cs](../tests/D3Parking.Application.Tests/ResidentReclaimConflictTests.cs): všech 14 regresních případů prošlo proti lokálnímu SQL. Pokrývají oba režimy rezervací, ochranu celého hromadného vrácení, živé i historické stavy, konec rezervace, skutečně vracené dny, adresné předání přes více dnů, rezervaci stejného fyzického místa a souběh vrácení s rezervací alternativy. [Výsledky](../artifacts/parking-workflow-fixes/results/reclaim-after.trx).
- [NonResidentBookingTests.cs](../tests/D3Parking.E2E.Tests/NonResidentBookingTests.cs): oba testy v Chromium prošly. Nerezident může přejít ze zakázaného dneška, víkendu a svátku na povolený den a získat výsledky hledání v celodenním i časovém režimu. [Výsledky](../artifacts/parking-workflow-fixes/results/browser-after.trx).
- [ResidentReclaimBookingTests.cs](../tests/D3Parking.E2E.Tests/ResidentReclaimBookingTests.cs): test v Chromium ověřil vysvětlení konfliktu a vypnutou akci vrácení v rezidentním dialogu. Alternativní rezervace i uvolnění vlastního dne zůstaly zachované. [Výsledky](../artifacts/parking-workflow-fixes/results/resident-browser-after.trx).

Celkem prošlo 98 různých aplikačních/doménových testů a tři testy v prohlížeči; žádný nebyl přeskočen. Ověření používá oddělené testovací databáze a webový host. Oprava nevyžaduje databázovou migraci. Původní diagnostika dvojího nároku z auditu už po opravě nemá projít; nahrazuje ji regresní test požadující odmítnutí konfliktu.

## Ověření opravy bodu 3

Prošlo **265 cílených aplikačních/doménových testů a 2 testy v Chromium**, bez selhání či přeskočení. Výběr zahrnuje 160 kombinací priorit, náhradních akcí, dostupnosti náhrady a zdroje/závaznosti uvolnění; hranice celého místního dne včetně změny letního času; dobrovolné ukončení držitelem; ochranu při zásahu správce, změně konfigurace a odebrání zaměstnance; atomické hromadné vrácení a zachování budoucích přesunů. Ověřené byly rovněž dosavadní konflikty, souběh a kalendářové revize.

Podrobnosti, rozsah zbývajících problémů a odkazy na výsledky jsou v [rozboru ochrany celého dne](RESERVATION-DAY-PROTECTION.md). Původní diagnostiky bodu 3 a rušení již započaté rezervace z bodu 4 nyní nahrazují testy požadující zachování rezervace.
