# Prohlížečové testy D3Parking

Playwright pro .NET ovládá skutečnou aplikaci v Chromium. Sada pokrývá přihlášení a autorizaci, odhlášení včetně CSRF, osobní ocenění, plánování s kontrolou zvoleného času, administraci, orientační mapu a mobilní rozložení.

## Spuštění na Windows

Potřebujete SDK z global.json, nativní SQL Server nebo LocalDB a oprávnění zakládat/mazat testovací databáze. Prohlížeč se při prvním spuštění stáhne automaticky; další běhy používají instalovaný Chromium.

```powershell
dotnet test tests/D3Parking.E2E.Tests -c Release --artifacts-path artifacts/e2e
```

WebAppFixture automaticky spustí Development host na volném loopback portu, vytvoří vlastní databázi D3Parking_E2E_<GUID>, počká na /health/ready a přihlásí testovacího správce. Po běhu ukončí svůj proces, odstraní pouze vlastní DB a dočasný soubor přihlášení. Existující instanci na localhost automaticky nepřebírá. Vývojové přihlašovací údaje jsou v appsettings.Development.json a Pages.cs; do release nevstupují.

Jiný nativní testovací SQL server lze zadat proměnnou prostředí (název DB fixture nahradí vlastním GUID):

```powershell
$env:ConnectionStrings__SqlServer = 'Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True'
dotnet test tests/D3Parking.E2E.Tests -c Release --artifacts-path artifacts/e2e
```

Výslovné BASE_URL použije již běžící **vyhrazenou testovací** instanci s očekávaným testovacím účtem a daty. Pokud neodpoví readiness, test skončí; nezakládá náhradní host. Testy mění účty, nastavení, místa a rezervace, proto BASE_URL nesmí mířit na produkci. Fixture tuto externí DB nemaže ani neosévá.

```powershell
$env:BASE_URL = 'https://vyhrazene-testy.example.cz'
dotnet test tests/D3Parking.E2E.Tests -c Release --artifacts-path artifacts/e2e
Remove-Item Env:BASE_URL
```

## Obsah

- WebAppFixture.cs: izolovaný host, DB a přihlášený kontext.
- Pages.cs: společné interakce a připojení InteractiveServer circuitu.
- AuthTests.cs / AccountTests.cs: autentizace, autorizace, profil a CSRF odhlášení.
- DashboardTests.cs / ParkingTests.cs: přehled, osobní ocenění, kapacita a rezervace.
- AdminTests.cs / AdminDetailTests.cs: role/skupiny, uživatelé, místa, vozidla, návštěvy, pravidla, dohled.
- OrientationMapTests.cs: nahrání aktuální PNG mapy, náhled a chráněný obrazový endpoint.
- ResponsiveTests.cs: úzký viewport, navigace a horizontální přetékání.

Odstraněný editor map se netestuje. Výchozí plánování má nulovou pevnou cenu, proto výchozí UI nezobrazuje starý kreditový ani reputační model. SQL testy placeného režimu mají cenu výslovně nastavenou ve fixture.

Čekání na websocket samo o sobě nezaručuje dokončení všech asynchronních inicializací stránky. Testy navazují na viditelný výsledek akce; opakování je přípustné pouze pro idempotentní volbu pohledu, nikoli pro neověřené duplicitní rezervace.
