# D3Soft.D3Parking

Firemní plánovač parkování v češtině a angličtině. Uživatelé rezervují místa, rezidenti sdílejí dny nebo je nabízejí kolegům, recepce eviduje návštěvy a správci řeší hlášení, vozový park a oprávnění.

Současný plánovač nevyžaduje potvrzování příjezdu ani odjezdu. Volitelný kreditový rozpočet používá **pevnou cenu rezervace**, nezávislou na obsazenosti. Ocenění jsou osobní a nemění oprávnění ani pořadí ve frontě. Starší schéma obsahuje i již neaktivní bodová pravidla.

| Potřebuji | Dokument |
|---|---|
| Instalovat, aktualizovat, restartovat nebo vrátit verzi | [Návod pro správce](Source/docs/ADMIN-GUIDE.md) |
| Vytvořit release a pochopit migrace/rollback | [Release a deployment](Source/docs/DEPLOYMENT.md) |
| Nastavit SQL, HTTPS, e-mail a tajemství | [Konfigurace](Source/docs/CONFIGURATION.md) |
| Architektura, funkce, endpointy, vývoj a testy | [Technická dokumentace](Source/docs/TECHNICAL.md) |
| Zjištění, opravy a zbývající rizika | [Audit](Source/docs/AUDIT.md) |

Řešení obsahuje Domain (pravidla), Application (rozhraní, DTO, handlery), Infrastructure (implementace služeb a SQL), Web (server), Web.Client (WASM zvoneček), Contracts (sdílené kontrakty) a dva testovací projekty. Používá .NET 10, Blazor a SQL Server.

`Source/build-release.ps1` je pro **sestavení ZIPu ze zdrojů** na počítači s Gitem a .NET SDK; bez parametrů se zeptá na verzi a nechá výsledek viditelný. `D3Parking-<verze>.ps1` je pro **instalaci a správu hotového ZIPu** na serveru a vyžaduje práva správce. Oba používají PowerShell 7.4+; postup při zavírajícím se okně builderu je v návodu k deploymentu.

## Vývoj na Windows

1. Nainstalujte SDK z `Source/global.json` a SQL Server Express LocalDB nebo vlastní testovací SQL Server.
2. Z kořene repozitáře přejděte do hlavní složky zdrojů a spusťte:

   ```powershell
   Set-Location Source
   dotnet restore D3Soft.D3Parking.slnx
   dotnet tool restore --tool-manifest dotnet-tools.json
   dotnet run --project src/D3Parking.Web
   ```

3. Otevřete adresu vypsanou aplikací. Vývojové údaje: `admin@d3parking.local` / `Admin123$`, pouze v `Development`. Toto prostředí automaticky aplikuje migrace.

Produkce dostává hotový `D3Parking-<verze>-win-x64.zip` a jeden samostatný [PowerShell průvodce](Source/deployment/D3Parking.ps1). Po spuštění nabídne instalaci, konfiguraci SQL/SMTP/HTTPS, aktualizaci, stav, restart a obnovu. Správce nepotřebuje ručně editovat JSON, Git ani .NET SDK. Podporovaný postup je jedna Windows služba s HTTPS a externím SQL Serverem; předpoklady a omezení popisuje návod pro správce.

Konfigurační soubory obsahují jednoduché české komentáře: význam položek, příklady hodnot a požadavky na SQL, poštu či certifikáty. Průvodce je doplňuje také do nastavení vytvořeného na serveru. [Přehled konfigurace](Source/docs/CONFIGURATION.md) vysvětluje, který soubor upravovat, co dodat od IT a jak změnu ověřit.

Licence: [MIT](LICENSE).
