# D3Parking / Webora

Firemní plánovač parkování v češtině a angličtině. Uživatelé rezervují místa, rezidenti sdílejí dny nebo je nabízejí kolegům, recepce eviduje návštěvy a správci řeší hlášení, vozový park a oprávnění.

Současný plánovač nevyžaduje potvrzování příjezdu ani odjezdu. Volitelný kreditový rozpočet používá **pevnou cenu rezervace**, nezávislou na obsazenosti. Ocenění jsou osobní a nemění oprávnění ani pořadí ve frontě. Starší schéma obsahuje i již neaktivní bodová pravidla.

| Potřebuji | Dokument |
|---|---|
| Instalovat, aktualizovat, restartovat nebo vrátit verzi | [Návod pro správce](docs/ADMIN-GUIDE.md) |
| Vytvořit release a pochopit migrace/rollback | [Release a deployment](docs/DEPLOYMENT.md) |
| Nastavit SQL, HTTPS, e-mail a tajemství | [Konfigurace](docs/CONFIGURATION.md) |
| Architektura, funkce, endpointy, vývoj a testy | [Technická dokumentace](docs/TECHNICAL.md) |
| Zjištění, opravy a zbývající rizika | [Audit](docs/AUDIT.md) |

Řešení obsahuje Domain (pravidla), Application (rozhraní, DTO, handlery), Infrastructure (implementace služeb a SQL), Web (server), Web.Client (WASM zvoneček), Contracts (sdílené kontrakty) a dva testovací projekty. Používá .NET 10, Blazor a SQL Server.

## Vývoj na Windows

1. Nainstalujte SDK z `global.json` a SQL Server Express LocalDB nebo vlastní testovací SQL Server.
2. V kořeni repozitáře spusťte:

   ```powershell
   dotnet restore D3Parking.slnx
   dotnet tool restore --tool-manifest dotnet-tools.json
   dotnet run --project src/D3Parking.Web
   ```

3. Otevřete adresu vypsanou aplikací. Vývojové údaje: `admin@d3parking.local` / `Admin123$`, pouze v `Development`. Toto prostředí automaticky aplikuje migrace.

Produkce dostává hotový `D3Parking-<verze>-win-x64.zip`; nepotřebuje Git ani .NET SDK. Podporovaný postup je jedna Windows služba s HTTPS a externím SQL Serverem. Kontejnery ani GitHub nejsou součástí release/deployment procesu.

Licence: [MIT](LICENSE).
