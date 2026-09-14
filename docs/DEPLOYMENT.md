# Release a deployment

## Podporovaný cíl

Windows x64 se službou `D3Parking` (název lze změnit při inicializaci), PowerShell 7.4+, samostatný SQL Server. HTTPS obsluhuje Kestrel. Release obsahuje .NET runtime; produkce neinstaluje SDK, nepoužívá Git, neprovádí restore/build/run. SQL Server a důvěryhodné certifikáty jsou jednorázové externí předpoklady. Linux/systemd ani IIS nemají v této změně instalační nástroj; samotný .NET kód na ně nelze zaměňovat za ověřený deployment.

## Vytvoření release na build počítači

1. Získejte zdroje běžným Gitem z firemního repozitáře. Hostitel Gitu není předepsaný.
2. Nainstalujte SDK přesně z `global.json`. Proveďte testy dle TECHNICAL.md, včetně SQL testů. Commitněte prověřené změny včetně lock souborů.
3. V kořeni repozitáře otevřete PowerShell 7:

   ```powershell
   .\build-release.ps1 -Version 1.2.3
   ```

4. Výstup je `artifacts/releases/D3Parking-1.2.3-win-x64.zip` , stejnojmenný `.zip.sha256`, samostatný `D3Parking-1.2.3.ps1` a jeho `.sha256`. Log publish je v oznámeném `artifacts/release-build-<id>/publish.log`.
5. Předejte jeden provozní skript, ZIP, kontrolní součty a tento návod správci. Checksum doručte důvěryhodným kanálem; součet sám není digitální podpis vydavatele. Verzi nikdy nepřepisujte.

`-AllowDirty` slouží pouze k místnímu ověření rozpracované změny: metadata označí `.dirty` a `dirty=true`; deployment do Staging/Production jej odmítne. Skript neprovádí commit ani neoznačuje testy za splněné. Před ostrým vydáním je nutný čistý Git commit.

Balíček obsahuje `D3Parking.ps1`, `app/`, provozní `docs/`, `database/migrations.sql`, LICENSE a `release.json`. Manifest má verzi, commit, runtime, seznam migrací, cílové schéma a SHA-256 každého souboru. `/version` čte identitu ze sestavené assembly, ne z volně editovatelné runtime konfigurace.

`appsettings` a sdílená konfigurace zapisovaná průvodcem obsahují české komentáře `//`. Aplikace a PowerShell je podporují. Manifest, žurnály a reporty zůstávají striktní JSON. Komentáře ve sdíleném nastavení vzniknou při instalaci nebo příštím uložení konfigurace; samotný update release existující shared soubory nepřepisuje. Podrobnosti čtení a priority hodnot jsou v CONFIGURATION.md.

SDK a NuGet závislosti jsou připnuté; publish používá locked restore, deterministické CI sestavení a oddělený pracovní adresář. ZIP má stabilní pořadí a čas položek z Git commitu; `buildTimestamp` je úmyslně čas zdrojového commitu, nikoli čas zabalení. Byte-for-byte reprodukovatelnost mezi různými stroji je nutné ověřit porovnáním výsledných SHA; metadata ji sama nedokazují. Lokální config, PFX, klíče, logy a zdrojové soubory do balíčku nepatří; builder při nálezu takového publish obsahu selže.

## Aktualizace

```powershell
cd C:\Releases
.\D3Parking.ps1 -Action Update -ReleasePath C:\Releases\D3Parking-1.2.3-win-x64.zip -InstallPath C:\D3Parking -CheckOnly
.\D3Parking.ps1 -Action Update -ReleasePath C:\Releases\D3Parking-1.2.3-win-x64.zip -InstallPath C:\D3Parking
```

Příklad předpokládá samostatný skript z důvěryhodného release přejmenovaný na `D3Parking.ps1`, uložený mimo `releases`. Bez parametrů otevře české menu. První instalaci popisuje ADMIN-GUIDE.md. Jediný nutný parametr běžné aktualizace je ReleasePath; InstallPath má výchozí `C:\D3Parking`.

Před změnou aplikace se kontroluje checksum ZIPu, bezpečné cesty a jednotlivé soubory, metadata, čistý build, Windows služba/identita/cesta/stav, prostředí, disk, ACL, shared JSON, certifikáty, runtime a deployment SQL oprávnění, shoda schématu a SMTP. Archiv se rozbaluje do dočasného adresáře. CheckOnly může vytvořit dočasné soubory a diagnostický log, ale nepřepíná službu, nepíše do DB ani nemění release konfiguraci.

Po úspěšné kontrole se zapíše žurnál, zkopíruje nový verzovaný adresář, zastaví služba, pořídí a ověří SQL backup, aplikují výslovně schválené migrace a změní cesta služby. Po startu se zkontroluje lokální readiness **i veřejné HTTPS** a přesná verze/commit/environment. Teprve pak se nastaví automatický start, atomicky uloží aktivní/předchozí verze a smaže žurnál. Symlinky ani přepis jediné kopie DLL se nepoužívají.

Krátká odstávka je záměrná. Nejde o rolling deployment a není podporovaný souběh více produkčních instancí. Dlouhá migrace/backup odstávku prodlouží. Po dobu údržby je služba v ručním režimu startu; automatický start se obnoví po ověřeném nasazení nebo bezpečném návratu původní aplikace. Restart serveru během migrace tak automaticky nespustí nekompatibilní starší vydání.

## Migrace a obnova

Historie obsahuje nevratné datové změny: například `RemoveLotMapEditorModel` maže tabulky a `ConvertReservationsToPlanner` mění uložené stavy a pravidla. EF Down není všeobecná obnova dat.

Preflight vypíše počet pending migrací. Raw SQL, změny/mazání sloupců a ostatní nečistě přidávající operace označí k revizi. Správce s DBA prohlédne `database/migrations.sql`; schválené **konkrétní identifikátory** zadá v průvodci, který je uloží do ApprovedMigrations v config/deployment.json. Bez nich upgrade existující DB nepokračuje. U skutečně prázdné DB je dovoleno vytvořit celý historický řetězec; DB s tabulkami bez migrační historie se odmítne.

Backup se provádí před každým upgrade/rollbackem, i když schema není změněné (start synchronizuje built-in oprávnění). SQL používá COPY_ONLY a CHECKSUM plus RESTORE VERIFYONLY. Nasazení nikdy automaticky nevytváří chybějící DB ani nespouští Down. Neúspěšný backup zastaví další postup, stará aplikace se může vrátit, pokud migrace ještě nezačala.

```powershell
.\D3Parking.ps1 -Action Rollback -InstallPath C:\D3Parking
```

Rollback volí zaznamenanou předchozí verzi, kontroluje její soubory a vyžaduje **přesnou shodu skutečné migrační historie**. Při změně schématu se zastaví před přepnutím. Není zde přepínač „ignoruj schéma“.

Selže-li start nového vydání beze změny schématu, nástroj zkusí vrátit původní službu a zkontroluje její readiness. Pokud migrace mohla změnit DB nebo proces skončil bez výsledného reportu, nechá aplikaci zastavenou a zachová žurnál. Postup DBA obnovy a `D3Parking.ps1 -Action Recover` je v ADMIN-GUIDE.md.

## Provozní hranice ověření

Readiness ověřuje dostupnost DB, kompletní migrační historii a čtení vybraných skutečných modelových sloupců. Nezaručuje úspěch všech business operací ani funkčnost e-mailové schránky, Entra, push a geokódování. SMTP preflight se připojí a autentizuje, ale neposílá zprávu. Release proto nejdříve nasaďte na Staging, proveďte přihlášení/rezervaci/e-mail a případné volitelné integrace. Omezení konkrétního auditního běhu uvádí AUDIT.md.
