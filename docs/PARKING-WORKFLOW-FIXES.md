# Opravy parkovacího workflow: body 4–12

Stav pracovní kopie 19. 9. 2026. Navazuje na [audit](PARKING-WORKFLOW-REVIEW.md) a [ochranu započaté rezervace](RESERVATION-DAY-PROTECTION.md). Implementace používá lokální SQL Server a SDK 10.0.401. Produkční databáze nebyla upravena.

## Výsledné chování

| Bod | Změna |
|---|---|
| 4 | Běžná změna kalendáře, horizontu nebo denního/časového režimu neruší již uložené rezervace, frontu, předání, návštěvy ani rezidentní uvolnění. Dříve založená fronta a předání mohou dokončit svůj uložený interval. |
| 5 | Ruční „ponechat si den“ je trvalá výjimka. Uložení stejného plánu ji neodstraní. Změna plánu zobrazí konkrétní nová uvolnění, volné vracené dny a zachované rezervované/nabídnuté dny. |
| 6 | Převzetí fronty na `/` i `/parking` umí potvrzení uvolnění vlastního místa. Potvrzení náleží konkrétní nabídce, místu, intervalu a expiraci. |
| 7 | Spolurezident může veřejně uvolněný den druhého rezidenta najít a rezervovat na stejném fyzickém místě. Jde o běžné čerpání sdílené kapacity. |
| 8 | Přímá veřejná rezervace nepředběhne dřívějšího způsobilého čekajícího ani v mezeře před spuštěním matcheru. Fronta uchovává požadovaný typ místa a respektuje rozpočet, limit, blokace a vlastní nároky. |
| 9 | Před začátkem je storno, po začátku předčasné ukončení. Náhled uvádí refundaci, vrácení poukazu a dopad na limit. Zahájený den zůstává v týdenním limitu. |
| 10 | Nahlášená fyzická překážka dočasně blokuje místo. Náhrada má stejný typ. Hlášení i náhradu může použít rezident bez samostatné rezervace. Správce může blokaci výslovně ukončit. |
| 11 | Parkovací změna a její záznam do schránky/e-mailové fronty se ukládají atomicky. Výpadek externího doručení nezničí záznam a u nabídky nezpůsobí automatickou ztrátu pořadí. |
| 12 | Dostupnost zahrnuje implicitní rezidenty, rezervace, nabídky a blokace. Vrácení používá stejnou kontrolu v náhledu i při provedení. Hromadné uvolnění počítá skutečně dotčené dny. Stránka se obnovuje po 10 sekundách bez přepsání rozepsaného plánu. |

## Konfigurace a hranice pravidel

**Celý den.** Rezervace drží uložené místo od místní půlnoci do následující půlnoci. Přechod času znamená 23 nebo 25 hodin. Od začátku ji rezident ani správce nesmí přesunout nebo odebrat v žádné kombinaci rezidentské priority, lhůty a náhrad. Držitel ji smí sám dobrovolně ukončit; také může sám nahlásit fyzickou překážku a požádat o náhradu. Toto pravidlo nevyžaduje check-in.

**Kalendář.** Dny, svátky, horizont a rezervování na dnešek řídí nové žádosti. Uložený požadavek fronty/předání přežije změnu těchto pravidel i režimu. Při převzetí se znovu kontroluje aktuální kapacita, ochrana rezervací, rozpočet, týdenní limit a podmínky uvolnění vlastního místa; u předání i oprávnění příjemce. Změna konfigurace není uzavírka. Rezidentský přehled při zákazu nového rezervování dál ukazuje existující stav.

**Rezidentský plán.** Nový nebo změněný vzor se aplikuje při potvrzení náhledu. Pravidelná údržba potom doplňuje nově otevřenou část horizontu. Volná automatická uvolnění odporující novému vzoru se vrátí; ruční uvolnění a výjimky se zachovají. Rezervace či aktivní nabídka ponechají podkladové uvolnění platné. Pokud později zaniknou, lze volný den výslovně vzít zpět; změna vzoru sama neslibuje pozdější automatické vytlačení držitele.

**Alternativní místo.** `Deny`, `ConfirmRelease` a `AutoRelease` platí i pro převzetí fronty. Potvrzení a rezervace s případným uvolněním vlastních přidělených dnů jsou jedna transakce. Při ručním vrácení místa se dál hlídá překryv s vlastní alternativní rezervací.

**Fronta.** FIFO určuje přesný čas založení, při shodě ID. Přednost platí ve veřejném fondu pro způsobilého čekajícího, jeho typ a celý požadovaný interval. Vlastní přidělení rezidenta a soukromé předání zůstávají samostatnými cestami. Nezpůsobilý čekající neblokuje nabídku dalším. Změna provozní dostupnosti stáhne nabídku bez ztráty pořadí. Při platně doručené, nevyužité nabídce nadále platí přesun na konec fronty.

**Doručení nabídky.** `QueueOfferMinutes` je nejméně jedna minuta; skutečná expirace je menší z `nyní + nastavená lhůta` a konce parkování. Pokud je e-mail při expiraci nedoručený nebo byl odeslaný až po ní, nabídka uvolní kapacitu, ale zachová původní pořadí. Další automatická nabídka počká na obnovení doručení. Správce může neúspěšný e-mail opakovat stávající správou doručování. Pokud uživatel externí doručení vypnul, rozhoduje dostupná schránka a běžná expirace. Přijetí SMTP serverem není doklad přečtení e-mailu.

**Storno a rozpočet.** Administrační `ReleaseCutoff` se ukládá; podporovaný rozsah je 0–1439 minut před začátkem, shodně v UI a službě, v mezích současného SQL pole typu `time`. U denního režimu je začátek o půlnoci: například 360 minut znamená 18:00 předchozího dne, 0 minut půlnoc. Refundní lhůta se zaznamená do rezervace při vytvoření. Pozdější změna nastavení ji nepřepočítá. Bez rozpočtu dialog neukazuje zbytečné kreditní údaje.

**Historie a limit.** Zahájený den se do limitu započítává i po dobrovolném ukončení, včetně ukončení přesně v okamžiku začátku. Budoucí stornovaný den limit uvolní. Započatý není totéž co prokazatelně fyzicky využitý. Původní interval a okamžik uvolnění zůstávají uložené; historie navíc zobrazuje efektivní konec. Přesný výsledek se při potvrzení znovu vyhodnotí, takže nelze obejít lhůtu starým otevřeným dialogem. Dobrovolné ukončení je dostupné i pro starší rezervace ve stavu `CheckedIn`.

**Provozní blokace.** Hlášení s povinnou fotografií blokuje místo od nahlášení do původního konce postiženého intervalu. Na konci blokace automaticky přestává omezovat nabídku. Dřívější odblokování vyžaduje živé oprávnění `Parking.ManageSpots`, zaznamená správce do auditu a ponechá historii hlášení. Trvalá deaktivace je nezávislá: odblokování ji nepřepíše. Aktivní rezervace se při deaktivaci nepřesouvá; držitelé a implicitní rezidenti dostanou informaci o nedostupnosti.

**Oznámení.** Povinnou historii parkovacího příslibu ukládá schránka, osobní volby dál řídí volitelné externí doručení. Upozornění na rezervaci, nabídku, předání, vrácení/přesun rezidentem, administrativní přesun/storno, deaktivaci a automatické uvolnění vzniká spolu s příslušnou změnou. Následné živé doručení může selhat bez ztráty uloženého záznamu. SMTP stále používá existující odchozí frontu s opakováním.

## Migrace

Součástí změny jsou:

1. `20260919191636_PreserveParkingWorkflowPromises`: tabulka `ResidentDayHolds`, `QueueEntries.RequiredSpotType`, `OccupancyMismatches.ResolvedAtUtc` a `Reservations.RefundDeadlineUtc`.
2. `20260919192412_TrackQueueOfferDelivery`: vazba nabídky na stav doručení e-mailu `QueueEntries.OfferEmailDeliveryId`.

První migrace doplní existujícím rezervacím refundní lhůtu podle nastavení v okamžiku migrace. Původní individuální lhůtu stará databáze neuchovávala, a proto ji nelze zpětně rekonstruovat. Rezervace, intervaly, ceny a stavy migrace zachovává. Aktualizace dat je úmyslně rozpoznaná existujícím nasazovacím nástrojem jako migrace k revizi; zůstává jeho obvyklý postup zálohy a schválení přesného ID. Mimo izolované testovací databáze migrace spuštěné nebyly.

## Ověření

- Celá aplikační sada včetně doménových pravidel: **774 úspěšných testů, 0 neúspěšných, 2 přeskočené**. Zahrnuje skutečné transakce SQL Serveru, souběžné rezervování/frontu, ochranu zahájených rezervací, všechny migrační kroky a upgrade z předchozího schématu. [Výsledky](../artifacts/parking-remaining/results/application-verified.trx).
- Přeskočené jsou dva volitelné testy nasazení: jeden vyžaduje Windows SQL LocalDB, druhý předem publikovanou aplikaci v `D3PARKING_RELEASE_APP`. Zbytek sady běžel na zde dostupném SQL Serveru a SDK.
- Nové scénáře: [ParkingWorkflowRemainingTests.cs](../tests/D3Parking.Application.Tests/ParkingWorkflowRemainingTests.cs). Testují rollback po `SaveChanges` před commitem, přesné pořadí fronty, obnovu doručení, limity, reflexi konfigurace, blokace a implicitní nároky.
- Konfigurační a migrační testy: [ParkingSettingsCalendarChangeTests.cs](../tests/D3Parking.Application.Tests/ParkingSettingsCalendarChangeTests.cs), [MigrationDeploymentTests.cs](../tests/D3Parking.Application.Tests/MigrationDeploymentTests.cs).
- Prohlížeč Chromium: **10 úspěšných testů, 0 neúspěšných, 0 přeskočených**. [ParkingRemainingWorkflowTests.cs](../tests/D3Parking.E2E.Tests/ParkingRemainingWorkflowTests.cs), [StartedBookingProtectionTests.cs](../tests/D3Parking.E2E.Tests/StartedBookingProtectionTests.cs), [ResidentReclaimBookingTests.cs](../tests/D3Parking.E2E.Tests/ResidentReclaimBookingTests.cs), [NonResidentBookingTests.cs](../tests/D3Parking.E2E.Tests/NonResidentBookingTests.cs). Ověřené je potvrzení fronty na obou stránkách, spolurezidentní rezervace stejného místa, automatické obnovení bez ztráty rozepsaného plánu, odblokování správcem, ochrana celého dne i ukončení stavů `Reserved` a `CheckedIn`. [Výsledky](../artifacts/parking-remaining/results/browser-verified.trx).
- Při širším ověření byly opravené také dvě zastaralé testovací přípravy: test fotografického endpointu používal bajty bez obrazové signatury a test zálohy předpokládal společný souborový systém SQL Serveru a testovacího procesu. Produkční bezpečnostní ani nasazovací kód se kvůli tomu neměnil.

## Co tento rozsah nezavádí

Původní audit obsahoval i širší návrhy: plánované uzavírky areálu, kalendář mimořádných pracovních dnů, částečná rezidentní uvolnění v časovém režimu, plánované přepnutí časového pásma, noční režim nabídek a samostatný minimální užitečný interval nabídky. Tyto nové produktové funkce nebyly součástí očíslovaných oprav. Fotografie nadále dokládá hlášení překážky, ne automaticky prokázanou vinu jiného řidiče.
