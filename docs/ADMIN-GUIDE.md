# Návod pro správce

Tento návod předpokládá Windows x64. Ukázky používají `C:\D3Parking`, službu `D3Parking` a veřejnou adresu `https://parking.company.cz:8443`. Nahraďte doménu skutečnou adresou své organizace. Instalační cesta může být jiná, pak přidejte `-InstallPath` ke každému příkazu.

## První server — jednorázově

1. Od IT si zajistěte podporovaný Windows Server x64, **PowerShell 7.4 nebo novější**, DNS pro veřejnou adresu a HTTPS certifikát PFX s privátním klíčem/heslem a odpovídající doménou v SAN. Nestačí vývojový certifikát. .NET SDK ani IIS nepotřebujete.
2. Správce databáze připraví SQL Server, prázdnou databázi D3Parking a dva SQL účty: aplikace (čtení a zápis) a deployment (db_owner této DB). Dostanete dvě připojovací hodnoty. SQL certifikát musí být na serveru důvěryhodný. DBA vytvoří zálohovací adresář **na SQL Serveru**, povolí zápis jeho službě a ověří kapacitu. Vyžádejte si také SMTP relay, povoleného odesílatele a způsob ověření.
3. Uložte důvěryhodný release ZIP a `.sha256` do `C:\Releases`. Ověřte součet proti hodnotě od vydavatele:

   ```powershell
   Get-FileHash C:\Releases\D3Parking-1.2.3-win-x64.zip -Algorithm SHA256
   ```

   Teprve pak rozbalte ZIP například do `C:\Releases\D3Parking-1.2.3-tools`. Budete používat jeho složku `deployment` a dokumenty `docs`.
4. Otevřete **PowerShell 7 jako správce** (Start → PowerShell 7 → pravé tlačítko → Spustit jako správce), nikoli starý Windows PowerShell 5.1. Spusťte:

   ```powershell
   cd C:\Releases\D3Parking-1.2.3-tools\deployment
   .\initialize.ps1 -InstallPath C:\D3Parking -PublicUrl https://parking.company.cz:8443
   ```

   Vytvoří se adresáře, ochranný certifikát a zastavená služba. Aplikace zatím neběží. Cesta ani služba nesmí předem existovat; tím se chrání stávající instalace. Po úspěchu zkopírujte celou složku `deployment` do `C:\D3Parking\Deployment` pro další obsluhu.
5. Otevřete `C:\D3Parking\config\appsettings.json`, `config\deployment.json`, `secrets\secrets.json` a `secrets\deployment.json` v textovém editoru spuštěném jako správce. Nahraďte všechny hodnoty `REPLACE_...`. JSON musí zachovat uvozovky/čárky; zpětné lomítko v cestě je `\\`. Neopisujte tajemství do e-mailu nebo do příkazové řádky. Popis klíčů je v CONFIGURATION.md. Pro první účet zvolte vlastní administrátorský e-mail a silné unikátní heslo.
6. Nainstalujte HTTPS PFX; příkaz si bezpečně vyžádá heslo a nastaví i práva služby:

   ```powershell
   cd C:\D3Parking\Deployment
   .\set-https-certificate.ps1 -CertificatePath C:\Releases\parking.company.cz.pfx
   ```

   Případný kořenový/intermediate certifikát organizace musí IT instalovat do důvěryhodného úložiště počítače. Heslo ochranného `protection.pfx` generuje initializer; neměňte je.
7. Nechte IT povolit příchozí TCP port veřejného HTTPS (v příkladu 8443) z požadované sítě a odchozí SQL/SMTP. Health port 5081 se do sítě neotevírá. Ověřte DNS z tohoto serveru i z uživatelského počítače. Konfigurace neotevírá firewall automaticky.
8. Spusťte kontrolu a nasazení:

   ```powershell
   .\deploy.ps1 -ReleasePath C:\Releases\D3Parking-1.2.3-win-x64.zip -CheckOnly
   .\deploy.ps1 -ReleasePath C:\Releases\D3Parking-1.2.3-win-x64.zip
   ```

9. Počkejte na `Deployment completed successfully.` Otevřete veřejnou adresu v prohlížeči, přihlaste se vlastním administrátorem, nastavte časovou zónu parkoviště v Nastavení webu a ověřte e-mail. Odstraňte AdminEmail/AdminPassword z IdentitySeed v secrets.json; účet zůstane v DB. Zálohujte config, secrets a data/keys mimo server a s DBA nacvičte obnovu SQL backupu.

Při přechodu ze staré instalace nejde o prázdnou databázi. Nejdříve viz poznámka o klíčence v CONFIGURATION.md a schvalování historických migrací v DEPLOYMENT.md. Nekopírujte vývojový appsettings do produkce.

## Jak zjistím, že aplikace běží a jakou má verzi?

V prohlížeči otevřete `https://parking.company.cz:8443/version`. Uvidíte `version`, `commit`, `environment`, `runtime`. Na `/health/ready` očekávejte HTTP 200 a `status: ready`. HTTP 503 znamená, že SQL/schéma není připravené. `/health/live` pouze potvrzuje běžící webový proces.

Na serveru:

```powershell
Get-Service D3Parking
Invoke-RestMethod http://127.0.0.1:5081/version
Invoke-RestMethod http://127.0.0.1:5081/health/ready
```

Samotný stav Running není důkaz funkčnosti. Zkontrolujte také otevření stránky a přihlášení.

## Jak nasadím další verzi?

1. Získejte nový ZIP a jeho ověřený checksum. Uložte je do `C:\Releases`.
2. V PowerShellu 7 jako správce přejděte do `C:\D3Parking\Deployment`.
3. Spusťte ` .\deploy.ps1 -ReleasePath C:\Releases\D3Parking-1.2.4-win-x64.zip -CheckOnly`.
4. Je-li požadována revize migrací, pošlete DBA `database/migrations.sql` z daného release a seznam ID z výstupu. Po schválení vložte přesná ID do ApprovedMigrations v config/deployment.json a kontrolu opakujte. Pokud se mění nástroje, nahraďte celou složku Deployment ověřenou verzí z nového balíčku, když žádné nasazení neběží.
5. Stejný příkaz spusťte bez `-CheckOnly`. Během zálohy/aktualizace nastane odstávka. Úspěch znamená až `Deployment completed successfully.` Konfiguraci do release nekopírujte.

## Restart

```powershell
Restart-Service D3Parking
Invoke-RestMethod http://127.0.0.1:5081/health/ready
```

Pokud služba právě startuje, několik sekund počkejte a readiness zopakujte. Změna shared config vyžaduje restart. HTTPS certifikát lze obnovit pomocí set-https-certificate.ps1; nejdříve si připravte kontrolu a odstávku. Neměňte tím certifikát Data Protection.

## Rollback

```powershell
cd C:\D3Parking\Deployment
.\deploy.ps1 -Rollback -CheckOnly
.\deploy.ps1 -Rollback
```

Nástroj použije předchozí release, pořídí backup a zkontroluje shodu jeho schématu se skutečnou DB. Pokud ji odmítne, binárky ručně nepřepínejte. Obnova DB může ztratit zápisy vzniklé od backupu, proto ji rozhoduje DBA s vlastníkem dat.

## Když deployment selže

Přečtěte `[FAILED]` a cestu za `Details:`. Pokud selhal preflight, aplikace a databáze se nezměnily. Pokud začala migrace, služba může zůstat zastavená. Diagnostika je v `logs\deploy-*.log`, přidružených `.preflight.json`/`.upgrade.json`, `logs\application-*.log` a `state\in-progress.json`. Žurnál sám nemažte.

| Zpráva/problém | Význam | Postup |
|---|---|---|
| Release checksum mismatch | ZIP neodpovídá vydavateli | Znovu stáhněte ZIP i ověřený součet; nespouštějte jeho skripty. |
| Missing configuration / REPLACE hodnoty | Není dokončena konfigurace | Opravte shared JSON podle CONFIGURATION.md. |
| Preflight failed at database | SQL připojení nebo oprávnění selhalo | DBA ověří server, DB, oba účty, síť a důvěru SQL certifikátu; v reportu je typ chyby a SQL číslo. |
| Preflight failed at smtp | Relay/TLS/ověření není dostupné | Opravte SMTP nastavení nebo síť, pak opakujte kontrolu. |
| HTTPS certificate… | PFX, heslo, SAN, platnost nebo důvěra CA | Dodejte správný certifikát/řetězec; nevypínejte ověřování TLS. |
| Service … lacks Read/Modify | Služba nevidí soubor nebo nemůže zapisovat | Pro HTTPS použijte set-https-certificate.ps1. Ostatní ACL porovnejte s initializerem, neudělujte Everyone. |
| Review database/migrations.sql | Připravená migrace vyžaduje revizi | DBA schválí konkrétní ID; doplňte ApprovedMigrations. |
| Database migration history differs | Cizí/novější schéma nebo návrat přes změnu DB | S DBA vyberte správný release nebo obnovte recovery point. |
| Health check failed | Proces neběží správně, SQL není připravené nebo odpověděla jiná verze | Čtěte application log; ověřte veřejné DNS/HTTPS, port a DB. |
| An interrupted deployment needs recovery | Zůstala neuzavřená operace | Postup níže; neopakujte naslepo update. |
| Target release already exists | Verzi nelze přepisovat | Vydejte novou verzi nebo obnovte existující přes recover; nepřepisujte DLL. |

### Obnova přerušeného nasazení / databáze

1. Zastavte `D3Parking` a ověřte, že žádný další deployment neběží. Zachovejte všechny logy a `state/in-progress.json`.
2. DBA zjistí skutečné schéma a případné částečné migrace. Cesta k backupu je v upgrade reportu/žurnálu. Podle situace dokončí bezpečnou opravu, nebo obnoví celou DB z vhodného backupu. Nástroje aplikace nikdy nespouštějí SQL restore ani Down automaticky.
3. Pokud se obnovovaly také konfigurační soubory/klíče, vraťte odpovídající kompletní config, secrets, ochranný certifikát a data/keys. Chraňte jejich ACL. Ověřte platnost HTTPS.
4. Vyberte ponechaný release odpovídající obnovené DB a spusťte:

   ```powershell
   .\recover.ps1 -Version 1.2.3 -CheckOnly
   .\recover.ps1 -Version 1.2.3
   ```

5. Recover odmítne jinou historii migrací, nic nemigruje a žurnál uzavře až po zdravém lokálním i veřejném HTTPS startu. Předchozí verzi po takové obnově záměrně nezaznamenává. Ověřte přihlášení a rezervaci, zaznamenejte DBA restore a případnou ztrátu novějších zápisů.

Při neúplném selhání samotného initialize (ještě žádný release/DB změna) ponechte službu zastavenou a po kontrole s IT odstraňte pouze nově vytvořenou prázdnou instalaci a její placeholder službu; nikdy existující ostrou instalaci. Pak proveďte inicializaci znovu.

## Co ručně neměnit

Needitujte binárky, release.json, soubory ve verzovaném release ani stav installation.json. Nepřepisujte klíčenku nebo ochranný PFX, nemažte aktuální/předchozí release, nespouštějte vývojové helpery/SQL skripty na produkci. Starší nepoužívané release a backupy archivujte podle dohodnuté retence až po ověření možností obnovy.

