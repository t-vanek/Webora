# Ochrana rezervace během dne a vztah ke konfiguraci

Rozbor a úprava bodu 3 z [auditu rezervací](PARKING-WORKFLOW-REVIEW.md), 19. 9. 2026.

**Rozhodnutí uživatele:** celodenní rezervace drží stejné místo celý daný den. Rezident ji během tohoto dne nemůže odebrat ani přesunout na náhradní místo. Držitel smí místo dobrovolně uvolnit. Další rezervace se posuzuje samostatně.

## Pevné pravidlo

Celodenní rezervace platí od 00:00 místního dne do 00:00 následujícího dne v časovém pásmu parkoviště. Od svého začátku drží stejné místo do uloženého konce, pokud ji sám držitel neukončí. Změna priority rezidenta, dostupná náhrada ani administrativní zásah tuto ochranu nevypínají. Jde o společné pravidlo, nikoli další přepínač konfigurace.

Rezervace na dnešek vytvořená například ve 13:00 stále ukládá celý místní den a je chráněná ihned po vytvoření. Nejde o 24 hodin od kliknutí. Při přechodu na letní nebo zimní čas může mít místní den 23 nebo 25 hodin.

V časovém režimu platí stejná ochrana od začátku do konce konkrétního intervalu. Rezervaci 09:00–17:00 lze před 09:00 řešit podle konfigurace; od 09:00 již drží stejné místo. Historický stav `CheckedIn` je chráněný i při dřívějším potvrzení příjezdu. Stav `Reserved` po začátku je chráněný také: aplikace nevyžaduje check-in.

Rozhodují **uložené `StartUtc` a `EndUtc`**, ne právě zvolený režim rezervování. Přepnutí z celodenního na časový režim proto existující celodenní rezervaci neodemkne. Změna časového pásma neposune uložený konec; zobrazení a pravidla nových rezervací už mohou používat jiné pásmo.

| Situace | Výsledek |
|---|---|
| Úterní celodenní rezervace, rezident se vrací v úterý v 11:00 | Odmítnutí vrácení. Místo zůstává držiteli i při volné náhradě. |
| Rezident chce ve stejnou chvíli vrátit středeční den | Středeční rezervace se vyhodnotí podle priority, lhůty, závaznosti a náhrady. |
| Hromadné vrácení zahrnuje chráněné úterý i středu | Celá operace se odmítne bez dílčích změn. Lze vybrat samotnou středu. |
| Držitel úterní rezervaci dobrovolně zruší nebo uvolní | Jeho nárok končí; místo lze znovu přidělit podle běžných pravidel. |
| Jiný držitel následně rezervuje tentýž den | Vznikne nový, ihned chráněný nárok. Původní držitel nemá automatické právo návratu. |
| Uvolněný den zatím nikdo nerezervoval | Rezident jej může vzít zpět, pokud mu nebrání vlastní alternativní rezervace. |
| Nastane konec rezervace | Původní rezervace zůstává historií. Další interval je samostatný nárok. |

## Co skutečně znamenají jednotlivé priority

Následující tabulka platí **výhradně před začátkem rezervace**, která není ve stavu `CheckedIn`. Od začátku rezervace všechny řádky končí zákazem přesunu či odebrání.

| Konfigurace | S dostupnou náhradou | Bez náhrady | Význam ochranné lhůty |
|---|---|---|---|
| `ConfirmedBookingProtected` — potvrzená rezervace má přednost | Rezident ji samoobslužně nepřesune. | Rezident ji samoobslužně nezruší. | Nemá vliv. |
| `AdvancePriority` — rezident jen před lhůtou | Přesun pouze před lhůtou. | Před lhůtou rozhoduje závaznost uvolnění a náhradní akce. | Od lhůty se odmítne i přesun s náhradou. |
| `ReplacementOnly` — pouze s náhradou | Přesun povolen. | Vrácení odmítnuto. | Nemá vliv. |
| `AdvanceOrReplacement` — hybrid | Přesun povolen před lhůtou i po ní. | Před lhůtou rozhoduje závaznost a náhradní akce; od lhůty odmítnutí. | Zavírá možnost odebrání bez náhrady. |
| `AbsolutePriority` — přednost rezidenta před začátkem | Přesun povolen. | Rozhoduje náhradní akce; ruční závaznost se ignoruje. | Nemá vliv. Ani tato priorita nepřekročí začátek rezervace. |

„Absolutní priorita“ tedy není absolutní oprávnění měnit již probíhající parkování. Hodnota enumu a uložená konfigurace zůstávají kompatibilní; název v administraci nyní výslovně říká „před začátkem rezervace“.

Předstartovní přesun zachovává identitu rezervace, interval, cenu a poukaz. Náhrada musí být aktivní, stejného typu a volná pro celý interval, včetně dočasných nabídek fronty. Přednost má trvale sdílené místo, potom uvolněné rezidentní místo. Toto je plánovací přesun před příjezdem; přepsání záznamu během parkování by samo fyzicky nepřestavilo auto.

### Závaznost ručního uvolnění

`ManualReleasesAreBinding=true` **neznamená zákaz všech změn**. V politice `AdvancePriority` a `AdvanceOrReplacement` brání odebrání bez náhrady, pokud den vznikl ručním uvolněním nebo přijatým adresným předáním. Přesun před začátkem na dostupnou náhradu dovoluje.

Automatické uvolnění podle plánu (`UsagePlan`) a při rezervaci alternativy (`AlternativeBooking`) není ruční závazek. V režimu `AbsolutePriority` se závaznost ignoruje. U `ConfirmedBookingProtected` a `ReplacementOnly` nic navíc nemění, protože odebrání bez náhrady už zakazuje samotná politika.

Právě zde byla původní chyba: zapnutá závaznost ani nalezená náhrada nechránily už zaparkované auto před přepsáním na jiné místo. Nová ochrana se vyhodnocuje dříve než tyto volby.

### Ochranná lhůta

Lhůta je další, dřívější hranice před začátkem. Pro středeční celodenní rezervaci:

- `PreviousDayAtTime`, 18:00: hranice je úterý 18:00 místního času.
- `HoursBeforeStart`, 24 hodin: při běžném dni je hranice úterý 00:00, protože středeční rezervace začíná o půlnoci. Počet hodin je skutečná délka času; při změně letního času se místní hodina může lišit.
- Přesně v okamžiku lhůty již platí větev „po lhůtě“. Pevná ochrana nejpozději od středy 00:00 platí vždy.

V hybridním režimu „ochrana od úterý 18:00“ stále dovoluje přesun s náhradou do začátku rezervace. Pokud má být od potvrzení neměnné i konkrétní místo, odpovídá tomu `ConfirmedBookingProtected`.

### Akce při chybějící náhradě

`ResidentNoReplacementAction` se použije pouze tehdy, když předchozí pravidla vůbec dovolují odebrání bez náhrady. Nastavení této akce samo žádné právo na odebrání nevytváří.

| Volba | Chování před začátkem, pokud politika dovoluje odebrání |
|---|---|
| `Deny` | Vrácení se odmítne. |
| `ManagerOnly` | Samoobslužné vrácení se odmítne a odkáže na správce. |
| `CancelAndQueue` | Rezervace se zruší, vrátí se kredity/poukaz a držitel se vrátí do fronty podle stávajících pravidel. |
| `CancelAndNotify` | Rezervace se zruší, vrátí se kredity/poukaz a držitel dostane oznámení. |

Žádná z těchto akcí se na chráněnou rezervaci nespustí. Správce také nemá výjimku pro její přesun nebo zrušení.

## Související konfigurace a další stavy

| Oblast | Dopad nynější opravy |
|---|---|
| Celodenní vs. časový režim | Stávající volba zůstává zachovaná. Ochrana se odvozuje z uloženého intervalu a přežije změnu režimu. |
| Zákaz rezervací na dnešek, pracovní dny, svátky, kratší horizont | Po následné opravě bodu 4 zůstávají zachované i budoucí rezervace, fronta, předání a podkladová uvolnění. Nová pravidla platí pro nové žádosti. |
| Automatické uvolnění vs. potvrzení při rezervaci alternativy | Určuje vznik uvolnění vlastního dne, nikoli oprávnění odebrat jinému člověku chráněnou rezervaci. |
| Rezident má současně rezervovanou alternativu | Platí samostatná ochrana z bodu 2: nejprve musí ukončit kolidující vlastní rezervaci. Ani volný vlastní den nelze vrátit tak, aby držel dvě místa. |
| Nabídka fronty | Ještě není rezervací; rezident ji může odvolat podle dosavadních pravidel. Po převzetí vznikne rezervace a uplatní se ochrana intervalu. |
| Přijaté adresné předání | Vytvořená rezervace má tutéž ochranu. Čekající žádost či nabídka není totožná s rezervací. |
| Správce místa | U započaté rezervace nenabízí přesun ani zrušení. Ochranu kontroluje i služba při přímém nebo opožděném požadavku. |
| Odebrání účtu zaměstnance | Přístupy se odeberou a budoucí rezervace se zruší. Započatá rezervace zůstane do konce, protože zrušení účtu samo neodstraní auto z místa. |
| Držitel hlásí fyzicky obsazené místo | Zůstává dostupná jeho vlastní akce zrušení a případného získání náhrady. Jde o rozhodnutí držitele vzdát se původního místa. |
| Storno, předčasné uvolnění a refundace | Zůstávají dostupné držiteli. Následná oprava bodu 9 doplnila náhled refundace, uloženou refundní lhůtu a zachování zahájeného dne v týdenním limitu. |
| Návštěvy spravované recepcí | Samostatná entita `VisitorBooking` má vlastní proces. Tato oprava řeší zaměstnanecké rezervace rezidentů a nerezidentů, nikoli rušení návštěv recepcí. |

Nouzová fyzická uzavírka není skrytá výjimka tohoto pravidla. Případný budoucí proces musí výslovně řešit držitele a skutečné přestavení vozidla. Běžné tlačítko správce ani změna kalendáře tuto situaci nesimulují.

## Hodnocení a doporučení

Pro požadavek „daný den už s místem nelze hnout“ není nutné zavádět další konfiguraci. Společná ochrana od začátku stačí a předchází kombinacím, které by slib držiteli oslabily. Globální nastavení uložené aplikace se touto opravou nemění.

Pokud má potvrzená rezervace garantovat **konkrétní místo už od potvrzení**, doporučuji `ConfirmedBookingProtected`. Pokud firma potřebuje předem vyvažovat návrat rezidentů, může zachovat hybrid, závazné ruční uvolnění a hranici předchozí den v 18:00. V takovém případě musí být uživateli jasné, že před půlnocí může dostat jiné místo. Volba `Deny` nebo `ManagerOnly` při chybějící náhradě poskytuje předvídatelnější výsledek než automatické rušení a vracení do fronty.

Výchozí hodnoty ve zdrojovém kódu jsou hybrid, závazné ruční uvolnění, předchozí den v 18:00 a `CancelAndQueue`; nejde o výpis aktuálně uložené produkční konfigurace. Nápověda byla upravena tak, aby nepoužívala „kdykoli“ a neslibovala přesun po začátku.

Další vhodná úprava administrace je skrýt neúčinné volby podle priority: lhůta má význam jen pro `AdvancePriority` a hybrid, závaznost pouze tam, kde může zabránit odebrání bez náhrady. Náhled konkrétního příkladu před uložením by měl uvést, do kdy lze změnit místo a zda může rezervace úplně zaniknout. Toto zjednodušení formuláře není součástí nynější opravy.

Následná [oprava bodů 4–12](PARKING-WORKFLOW-FIXES.md) oddělila pravidla nových žádostí od uložených rezervací a fronty a přidala záznam refundní lhůty. Nezavádí obecné plánování účinnosti konfigurace ani verzování všech podmínek.

## Provedení a ověření

Společné pravidlo je v [ReservationWindowRules.cs](../src/D3Parking.Domain/Parking/ReservationWindowRules.cs). Přesun chrání i samotná doména [Reservation.cs](../src/D3Parking.Domain/Parking/Reservation.cs). Kontroly používají vrácení rezidentovi, správa parkoviště a změny nastavení; úklid při odebrání zaměstnance zachovává započaté rezervace. Zamítnutá operace nemění místo, stav, interval, kredity, frontu ani revizi kalendáře.

- [StartedReservationProtectionTests.cs](../tests/D3Parking.Application.Tests/StartedReservationProtectionTests.cs): 160 kombinací pěti priorit, čtyř náhradních akcí, dostupné/nedostupné náhrady a čtyř variant zdroje/závaznosti uvolnění; dále hranice místní půlnoci v 23/24/25hodinovém dni, doménový zákaz přesunu, dobrovolné ukončení držitelem, změna režimu, rezervace vytvořená během dne a atomické hromadné vrácení s následným samostatným řešením zítřka.
- Rozšířené testy správy parkoviště, změn kalendáře a odebrání zaměstnance ověřují ochranu i mimo rezidentní samoobsluhu. Dosavadní testy budoucích přesunů, priorit, konfliktů a souběhu kontrolují zachování těchto cest.
- [StartedBookingProtectionTests.cs](../tests/D3Parking.E2E.Tests/StartedBookingProtectionTests.cs): skutečný Chromium ověřil vysvětlení ochrany, nepřístupné vrácení dne a dobrovolné uvolnění držitelem. Znovu prošel také test vrácení při vlastní alternativě.

**Výsledek: 265 úspěšných aplikačních/doménových testů a 2 úspěšné testy v Chromium, bez selhání či přeskočení.** Výsledky cílené sady jsou v [application.trx](../artifacts/parking-day-protection/results/application.trx) a [browser.trx](../artifacts/parking-day-protection/results/browser.trx). Testy používají SDK 10.0.401, lokální SQL Server a oddělené databáze; prohlížeč vlastní testovací host. Nejde o spuštění celé testovací sady. Samotná oprava bodu 3 nevyžadovala migraci; navazující opravy bodů 4–12 dvě migrace obsahují.
