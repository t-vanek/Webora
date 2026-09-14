# Návod pro správce — jeden PowerShell průvodce

Pro běžnou správu stačí **D3Parking.ps1**, hotový release ZIP a jeho SHA-256. Skript funguje samostatně, bez dalších `.ps1`, Gitu nebo .NET SDK. Release obsahuje vlastní runtime. Používá Windows službu, Kestrel HTTPS a externí SQL Server.

## Spuštění

Vydavatel předá `D3Parking-<verze>.ps1`, `D3Parking-<verze>-win-x64.zip` a jejich `.sha256`. Skript lze pro pohodlnější obsluhu přejmenovat na `D3Parking.ps1`; stejný soubor je také v kořeni ZIPu. Uchovávejte jej například v `C:\Releases`, mimo verzované adresáře aplikace. Původ skriptu i kontrolních součtů musí být důvěryhodný; SHA-256 není podpis vydavatele. Pokud Windows u staženého důvěryhodného skriptu požaduje odblokování, postupujte podle firemní politiky. Nevypínejte ji globálně.

Otevřete **PowerShell 7.4+ jako správce** na Windows x64 (nikoli Windows PowerShell 5.1):

```powershell
cd C:\Releases
.\D3Parking.ps1
```

Pro jinou instalaci použijte `.\D3Parking.ps1 -InstallPath D:\Apps\Parking`. Cestu lze změnit i v menu. Volba `-Action Help` vypíše stručnou nápovědu bez administrátorských práv.

## Co si připravit pro první instalaci

- Veřejnou HTTPS adresu, funkční DNS a PFX s privátním klíčem, heslem a odpovídající doménou v SAN. IT zajistí důvěru CA/intermediate certifikátů v úložišti počítače a přístup ke kontrole odvolání certifikátu.
- Existující samostatnou SQL databázi a dva různé SQL účty: aplikace s právy čtení/zápisu a nasazení s `db_owner` této databáze. SQL certifikát musí být důvěryhodný; průvodce používá ověřené šifrované připojení.
- Zálohovací adresář **na SQL Serveru**, do něhož může zapisovat služba SQL Serveru; připraví a kapacitu ověří DBA.
- SMTP relay, port, odesílatele a způsob přihlášení (None, Basic nebo OAuth2). U OAuth2 také token endpoint, Client ID, Client secret a případný scope.
- E-mail a vlastní silné heslo prvního správce.
- Síťová pravidla od IT: příchozí veřejný HTTPS port a odchozí SQL/SMTP. Health port se do sítě neotevírá; poslouchá jen na `127.0.0.1`.

Skript nevytváří SQL server ani databázi, neinstaluje certifikační autoritu, neotevírá firewall a nemění DNS. Tyto kroky závisejí na firemní infrastruktuře; jejich požadavky průvodce vypíše.

### Co přesně si vyžádat od IT

Následující příklad je zadání pro IT, ne hotové údaje vaší firmy. Hesla si nechte předat chráněným kanálem a do této tabulky je nezapisujte.

| Údaj | Příklad a vysvětlení | Kdo jej dodá |
|---|---|---|
| Veřejná adresa | `https://parking.firma.cz:8443`; stejnou adresu použijete v prohlížeči. | Správce DNS/sítě |
| HTTPS PFX a jeho heslo | Certifikát pro `parking.firma.cz` s privátním klíčem; SAN je seznam jmen webů, pro které platí. | Správce certifikátů |
| SQL server a DB | `sql01.firma.cz,1433` a `D3Parking`; nezadávejte sem URL s `https://`. | DBA = správce databází |
| Dva SQL účty | Např. `parking_app` pro aplikaci a `parking_deploy` pro zálohy/migrace; každý má vlastní heslo a odlišná práva. | DBA |
| SQL zálohovací složka | Např. `D:\SqlBackups\D3Parking` **na SQL serveru**. Oprávnění zápisu potřebuje služba SQL Serveru. | DBA |
| SMTP | Např. `smtp.firma.cz`, port 587, StartTls, Basic; správce musí potvrdit skutečnou kombinaci a povolit odesílatele. | Správce pošty |
| Odesílatel a přihlášení SMTP | Např. `parking@firma.cz`; adresa odesílatele a přihlašovací jméno nemusí být stejné. | Správce pošty |
| Síťová dostupnost | Veřejný HTTPS port k aplikaci; odchozí SQL a SMTP, případně OAuth2 a kontrola certifikátů. | Správce sítě |

Před spuštěním průvodce si také zvolte instalační cestu, prostředí Production/Staging a vlastní e-mail/heslo prvního správce. PFX pro **Data Protection** nemusíte shánět: ten vytvoří průvodce. Podrobný slovník hodnot a ukázku propojení adres najdete v [CONFIGURATION.md](CONFIGURATION.md).

## První instalace krok za krokem

1. Zvolte **1 — Instalace**, vyberte ZIP a potvrďte SHA-256. Součet nabídne ze souboru `.zip.sha256`; lze jej zadat ručně. Kontrolují se i všechny soubory uvnitř balíčku.
2. Zadejte adresu, prostředí Production/Staging, jméno služby a místní health port. Průvodce před vytvořením adresářů a zastavené služby ukáže souhrn. Potvrzení je přesné `ANO`; Enter ruší daný krok.
3. Vyplňte jednotlivé hodnoty SQL, SMTP, prvního správce a HTTPS PFX. Hesla se zadávají skrytě. Nemusíte ručně editovat JSON ani escapovat SQL hesla.
4. Průvodce ověří novou konfiguraci v odděleném chráněném adresáři. Kontroluje SQL účty a schéma, SMTP spojení/přihlášení a certifikáty. Kontrola neodesílá e-mail. Teprve po úspěchu a potvrzení uloží nastavení a chráněnou zálohu původních souborů.
5. Zobrazí plán nasazení: současnou a cílovou verzi, prostředí, migrace, SQL zálohu a předpoklad odstávky. Po potvrzení zastaví aplikaci, provede a ověří zálohu DB, aplikuje migrace a spustí službu. Úspěch oznámí až po lokální **i veřejné HTTPS** kontrole správné verze/commitu/prostředí.
6. Otevřete aplikaci, přihlaste se, ověřte rezervaci a skutečné doručení e-mailu. V Nastavení webu nastavte časovou zónu parkoviště. Potom volbou **3 — Nastavení** odstraňte uložené údaje prvního správce; účet v databázi zůstane zachovaný. Zašifrované zálohy nebo ACL chráněné zálohy nastavení mohou staré údaje nadále obsahovat, proto je chraňte podle firemní retence.

Zrušení po přípravě adresářů ponechá zastavenou službu a její konfiguraci. Pokud příprava úspěšně vytvořila všechny soubory, volba Instalace na stejné cestě nabídne dokončení. Při selhání samotné přípravy ještě před jejich vytvořením zachovejte zastavený stav a nechte IT posoudit neúplný adresář/službu; průvodce je automaticky nemaže.

Při přechodu ze starší instalace s existující DB nejdříve řešte zachování klíčenky podle CONFIGURATION.md a revizi migrací podle DEPLOYMENT.md.

## Kde jsou vysvětlivky a jak změnit nastavení

Na nainstalovaném serveru otevřete jako správce `C:\D3Parking\config\appsettings.json` (nebo svou instalační cestu). Nad položkami jsou české řádky `//`: co položka dělá, jakou hodnotu očekává a odkud ji získat. Neveřejné údaje jsou popsané v `secrets\secrets.json`; tento soubor nikomu neposílejte. Stejnou nápovědu mají oba soubory `deployment.json`.

Soubory ve `src` jsou určeny vývojáři. Soubory uvnitř `releases` jsou součástí ověřeného balíčku a neupravují se. Při nejasnosti nejprve použijte tabulku „Který soubor otevřít“ v CONFIGURATION.md.

Pro běžnou změnu postupujte takto:

1. Spusťte aktuální skript se správným `-InstallPath` a zvolte **3 — Nastavení**, pro samotný HTTPS PFX volbu **7**.
2. Zkontrolujte nabízené hodnoty a odpovězte na otázky. Prázdné zadání ponechá nabízenou hodnotu; u existujících hesel je ponechání označeno v otázce. Nepoužívejte příklady z návodu místo skutečných údajů od IT.
3. Vyčkejte na kontrolu SQL, SMTP a certifikátů. Při chybě původní soubory zůstávají zachované. Opravte příčinu uvedenou ve zprávě; nezkoušejte obcházet kontrolu TLS.
4. Přečtěte souhrn a uložení potvrďte `ANO`. Průvodce pořídí chráněnou zálohu a zapíše hodnoty i vestavěné komentáře. Po neúplném zápisu použijte volbu **10 — Obnova zápisu nastavení**.
5. Zvolte **6 — Řízený restart** a potvrďte krátkou odstávku. Teprve restart načte nové hodnoty. Potom ověřte veřejnou stránku, přihlášení a podle změny skutečný e-mail.

Existující instalace získá nové vestavěné komentáře při uložení nastavení novým průvodcem. Samotná výměna skriptu soubory instalace nepřepisuje. Vlastní ruční komentáře se při uložení nepřenášejí; provozní poznámky udržujte zvlášť. Úpravy mimo nabídku průvodce (např. podrobnost logování) jsou pokročilá správa: přečtěte komentáře, chraňte zálohu souboru a před restartem použijte **5 — Diagnostika**. Diagnostika nemusí odhalit chybu každé volitelné integrace.

## Další správa ze stejného menu

| Volba | Co provede |
|---|---|
| 2 Aktualizace | Ověření ZIPu, revize migrací, plán, SQL backup, přepnutí a kontrola nové verze. |
| 3 Nastavení | SQL, SMTP, adresy a HTTPS; ověření kandidáta a záloha před uložením. Nastavení se načte při příštím startu služby. |
| 4 Stav | Stav služby, aktuální/předchozí verze, místo na disku, lokální a veřejná připravenost, neuzavřené operace. |
| 5 Diagnostika | Stav, platnost HTTPS/ochranného PFX, integrita vydání, preflight a JSON s vybranými údaji bez hesel a syrových logů. |
| 6 Řízený restart | Ověření konfigurace a identity služby, potvrzení odstávky, restart a obě readiness kontroly. |
| 7 HTTPS certifikát | Ověření nového PFX a celé konfigurace, bezpečné uložení a práva služby. Potom použijte řízený restart. Ochranný PFX klíčenky se nemění. |
| 8 Návrat předchozí verze | Binární rollback se zálohou a přesnou kontrolou shody databázového schématu. |
| 9 Obnova nasazení | Spuštění konkrétní ponechané verze po ověření skutečného stavu DB s DBA. Vyžaduje zastavenou službu. |
| 10 Obnova zápisu nastavení | Vrátí čtyři soubory z ověřené zálohy přerušené operace. Nemění DB ani nerestartuje službu. |
| 11 Kontrola release | Plné předběžné kontroly vybraného ZIPu bez zastavení služby, migrace či kopírování do releases. |

Migrace vyžadující review se automaticky neschválí. Průvodce ukáže cestu k SQL skriptu a přesná ID. Po revizi s DBA je správce zadá; jiný seznam nebo Enter aktualizaci ukončí. Schválení se ukládá do instalačního profilu. Kontrola `-CheckOnly` a automatické `-Yes` při chybějícím schválení skončí chybou.

## Přímé příkazy

```powershell
.\D3Parking.ps1 -Action Update -ReleasePath C:\Releases\D3Parking-1.2.4-win-x64.zip -CheckOnly
.\D3Parking.ps1 -Action Update -ReleasePath C:\Releases\D3Parking-1.2.4-win-x64.zip
.\D3Parking.ps1 -Action Configure
.\D3Parking.ps1 -Action Restart
.\D3Parking.ps1 -Action Diagnostics
.\D3Parking.ps1 -Action Rollback -CheckOnly
.\D3Parking.ps1 -Action Rollback
```

`-CheckOnly` nemění službu, databázi ani uloženou konfiguraci; může vytvořit dočasné soubory, zámek a log. Pro operace Nastavení/Certifikát/Restart/Obnova nastavení není podporovaný a je odmítnut před změnami. `-Yes` přeskočí potvrzení plánu u předem připravených automatizovaných operací, nikoli bezpečnostní kontroly nebo revizi migrací. Pro automatizaci dodávejte ZIP/hash či verzi explicitně; instalační/nastavovací formulář zůstává interaktivní. Hesla nikdy nevkládejte do argumentů.

## Když něco selže

Čtěte českou zprávu `[CHYBA]`, doporučený další krok a cestu k protokolu. Předběžná kontrola služby ani DB nemění. Zápis konfigurace má vlastní transakční žurnál; neúplný zápis blokuje další deployment/restart do obnovy. Při zápisu dočasně vypne automatický start služby a po úspěchu/obnově vrátí původní režim. Běžící proces při tom nezastavuje. Stejně tak je automatický start vypnutý po dobu migrace, aby restart serveru nespustil starší aplikaci nad rozpracovanou DB. Další souběžnou změnu stejné instalace blokuje zámek. Neuzavřený žurnál může znamenat i právě probíhající operaci — nejprve ověřte, že jiný správce stále nepracuje.

Protokoly jsou v `C:\D3Parking\logs`; stav v `state\installation.json`, žurnál deploymentu v `state\in-progress.json`, žurnál nastavení v `state\config-in-progress.json`. Zálohy nastavení v `backups` jsou dostupné administrátorům/SYSTEM a **obsahují tajemství**. Export diagnostiky záměrně obsahuje jen vybrané údaje; aplikační logy ani celé konfigurační zálohy neposílejte bez posouzení jejich obsahu.

Při neúspěšném startu beze změny schématu se nástroj pokusí vrátit původní aplikaci. Pokud se DB mohla změnit nebo není znám výsledek migrace, aplikaci zastaví a zachová žurnál. Starší binárky nad změněným schématem nespustí.

### Obnova aplikace / databáze

1. S IT zastavte službu a ověřte, že jiný deployment neběží. Zachovejte logy a žurnál.
2. DBA zjistí skutečné schéma/částečné migrace. Podle situace bezpečně dokončí opravu nebo obnoví odpovídající zálohu. SQL restore a EF Down skript nikdy neprovádí automaticky; obnovou lze ztratit novější zápisy.
3. Při obnově celého serveru vraťte také odpovídající config, secrets, ochranný PFX a `data/keys` včetně ACL.
4. Vyberte dostupný release odpovídající DB:

   ```powershell
   .\D3Parking.ps1 -Action Recover -Version 1.2.3 -CheckOnly
   .\D3Parking.ps1 -Action Recover -Version 1.2.3
   ```

5. Obnova nic nemigruje; žurnál uzavře až po zdravém startu. Ověřte přihlášení, rezervaci a e-mail. Zaznamenejte DBA restore a případnou ztrátu novějších dat.

Zálohování mimo server, retenční pravidla a pravidelný nácvik obnovy zůstávají součástí provozu IT. Automatický skript je nenahrazuje.

## Účtové e-maily po aktualizaci MAIL-001

Migrace `20260914112604_AddDurableEmailOutbox` přidává pouze tabulku EmailDeliveries a indexy; stávající uživatele ani notifikační zprávy nemění. Proběhne standardním deploymentem se zálohou. Binární rollback před tuto migraci vyžaduje obvyklou DBA obnovu odpovídajícího schématu.

Požadavek na e-mail nyní potvrdí jeho uložení do SQL. Worker se probouzí nejpozději po 15 sekundách a při chybě opakuje odeslání. Sledujte v application logu `Email delivery ... permanently failed` a `Email outbox dispatch failed`. DBA může bez čtení citlivého obsahu zjistit stav:

```sql
SELECT Status, COUNT(*) AS Messages, MIN(CreatedAtUtc) AS OldestCreatedUtc
FROM dbo.EmailDeliveries GROUP BY Status;
SELECT TOP (50) Id, Status, Attempts, CreatedAtUtc, NextAttemptUtc, LastError
FROM dbo.EmailDeliveries WHERE Status <> 'Sent' ORDER BY CreatedAtUtc;
```

Při Failed opravte SMTP/SQL/klíčenku a požádejte o nový potvrzovací nebo obnovovací e-mail. Staré zprávy se nejpozději po 24 hodinách uzavřou; jejich token mohl vypršet dříve. Po restartu může při nejistém potvrzení SMTP přijít stejná zpráva dvakrát. Obnova fronty vyžaduje vedle DB také odpovídající Data Protection keys a PFX. Stará paměťová fronta z předchozí verze se zpětně rekonstruovat nedá.

## Co ručně neměnit

Needitujte binárky, release.json, soubory ve verzovaném release ani stav installation.json. Nepřepisujte klíčenku nebo ochranný PFX, nemažte aktuální/předchozí release, nespouštějte vývojové helpery/SQL skripty na produkci. Starší nepoužívané release a backupy archivujte podle dohodnuté retence až po ověření možností obnovy.
