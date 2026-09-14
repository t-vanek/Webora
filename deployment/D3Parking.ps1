#requires -Version 7.4
<#
.SYNOPSIS
D3Parking: samostatný český průvodce instalací a správou Windows služby.
.DESCRIPTION
Jediný provozní skript. Vyžaduje Windows x64, PowerShell 7.4+, release ZIP a připravené SQL/SMTP/HTTPS.
Bez parametrů otevře menu. -Action Help vypíše příklady. Tajemství se nezadávají v argumentech.
#>
[CmdletBinding()]
param(
    [ValidateSet('Wizard','Install','Configure','Update','Check','Status','Restart','Rollback','Recover','Certificate','Diagnostics','RestoreConfiguration','Help')]
    [string]$Action = 'Wizard',
    [string]$InstallPath = 'C:\D3Parking',
    [string]$ReleasePath,
    [string]$ExpectedSha256,
    [string]$Version,
    [switch]$CheckOnly,
    [switch]$Yes
)

# === Integrita, cesty, procesy a Windows služba ===
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ChildPath([string]$Parent, [string]$Path) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes its parent: $Path" }
    $full
}

function Assert-NoReparsePoint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links/junctions are not allowed: $($item.FullName)" }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
}

function Write-JsonAtomic([object]$Value, [string]$Path) {
    $temp = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = $Value | ConvertTo-Json -Depth 30
    $kind = switch -Regex ($Path) {
        '[\\/]config[\\/]appsettings\.json$' { 'Shared' }
        '[\\/]config[\\/]deployment\.json$' { 'Policy' }
        '[\\/]secrets[\\/]secrets\.json$' { 'Secrets' }
        '[\\/]secrets[\\/]deployment\.json$' { 'Maintenance' }
    }
    if ($kind) { $json = Add-ConfigurationComments $json $kind }
    Set-Content -LiteralPath $temp -Value $json -Encoding utf8NoBOM
    if (Test-Path -LiteralPath $Path) { Set-Acl -LiteralPath $temp -AclObject (Get-Acl -LiteralPath $Path) }
    [IO.File]::Move($temp, $Path, $true)
}

function Get-ConfigurationComments([string]$Kind) {
    $comments = @{
        Deployment = 'Prostředí této instalace. Nastavuje průvodce společně s Windows službou.'
        'Deployment:Environment' = 'Production = ostrý provoz; Staging = zkušební server. Musí být stejné v config/deployment.json. Development patří jen vývojáři.'
        ConnectionStrings = 'Údaje, podle kterých aplikace najde databázi. Na serveru patří do secrets, protože obsahují heslo.'
        'ConnectionStrings:SqlServer' = @('Připojení aplikace k SQL. Server = adresa SQL; Database = název existující databáze; User ID a Password = účet od správce DB.', 'Pro ostrý provoz: samostatný účet se SELECT/INSERT/UPDATE/DELETE, Encrypt=True a TrustServerCertificate=False. SQL musí mít důvěryhodný certifikát. Hodnotu bezpečně sestaví průvodce.')
        Account = 'Adresy používané například v potvrzovacích a obnovovacích e-mailech.'
        'Account:BaseUrl' = 'Adresa, kterou lidé otevřou v prohlížeči, např. https://parking.firma.cz:8443. Potřebuje DNS a HTTPS certifikát. Bez /login, dalších cest nebo parametrů.'
        AllowedHosts = 'Povolená jména serveru, např. parking.firma.cz;127.0.0.1. Bez https://, portu a cesty; oddělujte středníkem. Hvězdička povoluje vše a v produkci je odmítnuta.'
        Kestrel = 'Vestavěný webový server aplikace. IIS není pro tento způsob instalace potřeba.'
        'Kestrel:Endpoints' = 'Dvě místa, kde aplikace čeká na spojení: Public pro lidi a Health pro místní kontrolu.'
        'Kestrel:Endpoints:Public' = 'Veřejný vstup do aplikace. IT musí povolit jeho TCP port v síti a firewallu.'
        'Kestrel:Endpoints:Public:Url' = 'Např. https://0.0.0.0:8443: 0.0.0.0 znamená všechny místní IPv4 adresy, 8443 je port. Není to adresa do prohlížeče. Port musí odpovídat Account:BaseUrl.'
        'Kestrel:Endpoints:Public:Certificate' = 'HTTPS certifikát: umožňuje šifrované spojení a prokazuje jméno tohoto webu.'
        'Kestrel:Endpoints:Public:Certificate:Path' = 'Úplná cesta k souboru PFX s privátním klíčem. Dodejte jej od IT; musí platit, obsahovat doménu v SAN a mít důvěryhodný řetězec. Průvodce nastaví právo služby ke čtení.'
        'Kestrel:Endpoints:Public:Certificate:Password' = 'Heslo k HTTPS PFX od IT. Není to heslo do aplikace. Patří jen do secrets.json; pokud PFX heslo nemá, zůstává prázdné.'
        'Kestrel:Endpoints:Health' = 'Místní kontrola, zda proces běží a databáze je připravená. Není určena uživatelům.'
        'Kestrel:Endpoints:Health:Url' = 'Např. http://127.0.0.1:5081. 127.0.0.1 znamená pouze tento počítač. Port musí být volný, jiný než veřejný a stejný jako HealthUrl v deployment.json. Do sítě jej neotevírejte.'
        DataProtection = 'Ochrana přihlašovacích údajů aplikace, uložených integračních tajemství a čekajících e-mailů. Není to HTTPS.'
        'DataProtection:Certificate' = 'Ochranný certifikát šifruje klíčenku data/keys. Společně s databází potřebujete zálohovat klíčenku, tento PFX i jeho heslo.'
        'DataProtection:Certificate:Path' = 'Úplná cesta k protection.pfx. Průvodce vytvoří RSA certifikát na 5 let. Při aktualizaci ho zachovejte; pro výměnu musí IT zajistit čtení starých klíčů.'
        'DataProtection:Certificate:Password' = 'Náhodné heslo vytvořené průvodcem k ochrannému PFX. Nepřepisujte ho heslem HTTPS certifikátu. Bez správného hesla a PFX nelze přečíst chráněná data.'
        IdentitySeed = 'Jednorázové založení prvního správce. Změnou těchto hodnot neměníte heslo již existujícího účtu.'
        'IdentitySeed:AdminEmail' = 'Vlastní e-mail prvního správce. Po úspěšném přihlášení odstraňte v průvodci e-mail i heslo; účet zůstane v DB.'
        'IdentitySeed:AdminPassword' = 'Vlastní jedinečné heslo: v produkci nejméně 12 znaků, velké i malé písmeno, číslice a jiný znak. Uložte jen do secrets.json, nikdy do veřejné konfigurace.'
        'IdentitySeed:AdminDisplayName' = 'Jméno zobrazené u nově zakládaného správce, např. Správce parkování. Není to přihlašovací jméno.'
        Smtp = 'Odesílání e-mailů přes poštovní server. Údaje dá správce pošty. Bez dostupného SMTP zprávy zůstávají ve frontě a mohou později selhat.'
        'Smtp:Host' = 'Jméno SMTP serveru, např. smtp.firma.cz, bez https:// a bez portu. localhost znamená poštu na stejném počítači; sama se tím žádná poštovní služba nevytvoří.'
        'Smtp:Port' = 'Číslo vstupu do SMTP serveru (1 až 65535). Často 587 pro StartTls, 465 pro SslOnConnect nebo 25 pro firemní relay. Správnou dvojici port + Security určí správce pošty.'
        'Smtp:TimeoutSeconds' = 'Jak dlouho čekat na jednu SMTP síťovou operaci, v sekundách. Povoleno 5 až 300; 30 je běžná výchozí hodnota. Není to interval opakování zpráv.'
        'Smtp:Security' = @('StartTls = spojení se povinně přepne na šifrované; SslOnConnect = šifruje se od začátku; None = bez šifrování.', 'Auto vybírá režim podle serveru/portu a nezaručuje povinné TLS. Produkce s Basic nebo OAuth2 vyžaduje StartTls či SslOnConnect; průvodce Auto nenabízí.')
        'Smtp:Authentication' = 'None = relay nevyžaduje přihlášení (musí povolit tento server); Basic = UserName + Password; OAuth2 = token získaný pomocí nastavení OAuth2 a schránka UserName.'
        'Smtp:UserName' = 'Pro Basic přihlašovací jméno; pro OAuth2 schránka, za kterou se odesílá. Dodá správce pošty. Patří do secrets.json.'
        'Smtp:Password' = 'Heslo SMTP účtu pro Basic. Pro OAuth2 se používá ClientSecret, nikoli toto heslo. Zadávejte skrytě v průvodci, ne do příkazové řádky.'
        'Smtp:SenderEmail' = 'E-mail, který příjemce uvidí jako odesílatele, např. parking@firma.cz. SMTP server musí účtu nebo relay dovolit odesílat právě z této adresy.'
        'Smtp:SenderName' = 'Čitelný název odesílatele v poště, např. Firemní parkování. Nemění přihlášení ani oprávnění poštovní schránky.'
        'Smtp:OAuth2' = 'Používá se jen při Authentication = OAuth2. Aplikaci a oprávnění pro automatické odesílání musí předem připravit správce pošty/identity.'
        'Smtp:OAuth2:TokenEndpoint' = 'HTTPS adresa, kde poskytovatel vydává přístupové tokeny. Dodá správce identity. Nejde o SMTP server ani adresu přihlášení uživatele.'
        'Smtp:OAuth2:ClientId' = 'Identifikátor registrované aplikace od správce identity. Není to e-mail ani heslo. Registrace musí podporovat client_credentials a mít potřebná oprávnění.'
        'Smtp:OAuth2:ClientSecret' = 'Tajný klíč registrované aplikace. Skutečnou hodnotu ukládejte pouze do secrets.json. Sledujte jeho platnost; po expiraci se token nezíská.'
        'Smtp:OAuth2:Scope' = 'Rozsah oprávnění, o který aplikace žádá. Přesný text dodá poskytovatel/správce identity; nevymýšlejte ho. Prázdná hodnota parametr scope neposílá.'
        Geocoding = 'Vyhledání zeměpisných souřadnic podle adresy. Potřebuje dostupný server této služby.'
        'Geocoding:NominatimBaseUrl' = 'Základní adresa Nominatim serveru, bez /search. Z tohoto serveru aplikace získává souřadnice. Provozovatel musí použití povolit; pro firemní provoz domluvte vhodnou službu a její limity.'
        'Geocoding:UserAgent' = 'Představení aplikace geokódovací službě, např. D3Parking/1.0 (kontakt: parking@firma.cz). Použijte skutečný kontakt a formát platné HTTP User-Agent hlavičky.'
        Distance = 'Jak se počítá vzdálenost mezi dvěma body. Nemění cenu rezervace na dynamickou.'
        'Distance:Provider' = 'Haversine = vzdálenost vzdušnou čarou, funguje bez internetu. Osrm = vzdálenost po silnici, potřebuje dostupný OSRM server; při chybě použije vzdušnou vzdálenost.'
        'Distance:OsrmBaseUrl' = 'Základní URL směrovací služby OSRM. Použije se jen pro Provider = Osrm. Veřejná ukázková služba není zárukou dostupnosti pro firmu; provoz domluvte s IT.'
        ForwardedHeaders = 'Důvěra k serverům stojícím před aplikací (proxy). Při přímém přístupu na Kestrel ponechte výchozí hodnoty.'
        'ForwardedHeaders:TrustAllProxies' = 'false = nevěřit libovolnému prostředníkovi. Ponechte false; true je v produkci odmítnuté, protože cizí požadavek by mohl tvrdit falešnou adresu nebo protokol.'
        'ForwardedHeaders:KnownProxies' = 'Seznam konkrétních IP důvěryhodných proxy, např. ["10.20.0.10"]. Nejsou to IP uživatelů. [] nepřidává další proxy k výchozí místní důvěře.'
        'ForwardedHeaders:KnownNetworks' = 'Sítě důvěryhodných proxy v CIDR zápisu, např. ["10.20.0.0/24"]. Rozsah určí IT; všechny jeho adresy dostanou důvěru. [] nepřidává další sítě.'
        Serilog = 'Záznamy o chodu serveru. Pomáhají zjistit, proč něco nefunguje. V instalaci se navíc automaticky zapisuje do logs mimo release.'
        'Serilog:MinimumLevel' = 'Nejnižší závažnost, která se zapíše. Podrobnější úroveň znamená více řádků a větší logy.'
        'Serilog:MinimumLevel:Default' = 'Information = běžné provozní zprávy; Warning = upozornění a chyby; Error = chyby. Debug/Verbose používejte jen dočasně při hledání problému.'
        'Serilog:MinimumLevel:Override' = 'Výjimky pro konkrétní části systému; omezují velké množství technických zpráv.'
        'Serilog:MinimumLevel:Override:Microsoft.AspNetCore' = 'Warning zapisuje upozornění/chyby webového frameworku. Information přidá více zpráv o požadavcích.'
        'Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore' = 'Warning omezuje zprávy databázové knihovny. Information může výrazně zvětšit log kvůli SQL operacím.'
        'Serilog:WriteTo' = 'Kam log posílat. Tento seznam přidává konzoli; souborový log instalace zapíná také kód aplikace.'
        'Serilog:WriteTo:0:Name' = 'Console = vypisovat do konzole procesu. U Windows služby není běžné okno vidět; čtěte soubory v logs.'
        'Serilog:WriteTo:0:Args' = 'Doplňující nastavení tohoto výstupu logu. Běžně není nutné měnit.'
        'Serilog:WriteTo:0:Args:outputTemplate' = 'Vzhled řádku: Timestamp je čas, Level závažnost, Message zpráva, NewLine nový řádek a Exception chyba. Značky ve složených závorkách ponechte.'
        'Serilog:Enrich' = 'FromLogContext připojuje kontext operace, pokud jej aplikace poskytne. Ponechte pro snazší hledání souvisejících událostí.'
        Logging = 'Obecné úrovně .NET logování. Hlavní serverové logování zde řídí Serilog; klient v prohlížeči používá sekci Logging.'
        'Logging:LogLevel' = 'Filtr zpráv podle závažnosti. Více podrobností může znamenat více provozních údajů v logu.'
        'Logging:LogLevel:Default' = 'Information = běžné zprávy a chyby; Warning = jen upozornění a chyby; Error = jen chyby. Pro obvyklý provoz ponechte Information.'
        'Logging:LogLevel:Microsoft.AspNetCore' = 'Warning omezuje technické zprávy webového frameworku. U klienta se filtr uplatní pouze, pokud zprávy s touto kategorií vznikají.'
        WebPush = 'Volitelné oznámení prohlížeče. Potřebuje vlastní dvojici VAPID klíčů, dostupnou push službu, HTTPS a souhlas uživatele. Bez klíčů je vypnuté.'
        'WebPush:Subject' = 'Kontakt provozovatele pro push službu, obvykle mailto:parking@firma.cz. Není to adresa příjemce oznámení.'
        'WebPush:PublicKey' = 'Veřejná část páru VAPID. Smí ji dostat prohlížeč; musí patřit ke stejnému PrivateKey.'
        'WebPush:PrivateKey' = 'Soukromá část páru VAPID. Produkční hodnotu dejte pouze do serverových secrets, nikdy do wwwroot. Při změně páru může být nutné obnovit odběry oznámení.'
    }
    if ($Kind -eq 'Development') {
        $comments['ConnectionStrings:SqlServer'] = 'Jen vývoj na tomto PC: LocalDB musí být nainstalovaná. Database určuje jméno vývojové DB; Trusted_Connection používá Windows účet. TrustServerCertificate=True přeskočí ověření certifikátu a do produkce nepatří.'
        $comments['Account:BaseUrl'] = 'Vývojová adresa pro odkazy, např. http://localhost:5163. Musí odpovídat portu, na kterém jste aplikaci spustili. localhost funguje jen na tomto počítači.'
        $comments['IdentitySeed:AdminEmail'] = 'Známý vývojový přihlašovací účet. Pouze pro místní Development, nikdy pro ostrý server.'
        $comments['IdentitySeed:AdminPassword'] = 'Veřejně známé testovací heslo z repozitáře, nikoli tajemství. Pouze pro Development. V ostré instalaci zadejte vlastní silné heslo průvodcem.'
        $comments['WebPush:PrivateKey'] = 'Veřejně známý vývojový VAPID klíč. Hodí se jen k místnímu testu; pro produkci vytvořte vlastní pár a soukromou část dejte do secrets.json.'
    }
    if ($Kind -eq 'Policy') {
        $comments = @{
            ServiceName = 'Jméno Windows služby vytvořené průvodcem, např. D3Parking. Není to doména. Ruční přejmenování zde nepřejmenuje službu ani její účet.'
            Environment = 'Production = ostrá instalace; Staging = testovací. Musí se shodovat s aplikací a argumenty služby. Pro každé prostředí použijte vlastní instalaci a DB.'
            PublicUrl = 'Stejná veřejná HTTPS adresa jako Account:BaseUrl, např. https://parking.firma.cz:8443. Kontrola ji musí otevřít i ze serveru; potřebuje DNS, port a důvěryhodný certifikát.'
            HealthUrl = 'Stejná adresa jako Kestrel:Endpoints:Health:Url, např. http://127.0.0.1:5081. Jen místní kontrola; neotvírat do sítě.'
            SqlBackupDirectory = 'Úplná cesta NA SQL SERVERU, např. D:\SqlBackups\D3Parking. DBA vytvoří adresář, dá službě SQL právo zápisu a zajistí místo. Není to složka ZIPu na webovém serveru.'
            ApprovedMigrations = 'Seznam přesných ID migrací schválených po kontrole SQL s DBA. [] znamená žádné výslovné schválení. Průvodce si vyžádá chybějící ID; nepište sem hvězdičku ani libovolné názvy.'
        }
    }
    if ($Kind -eq 'Maintenance') {
        $comments['ConnectionStrings'] = 'Pouze pro správce a deployment; běžná aplikace tento soubor nesmí číst.'
        $comments['ConnectionStrings:SqlServer'] = 'SQL účet pro zálohy a migrace, odlišný od účtu aplikace. Server a Database musí být stejné jako v secrets.json; potřebuje db_owner této DB, Encrypt=True a TrustServerCertificate=False. Účet připraví DBA; nepoužívejte sa.'
    }
    return $comments
}

function Add-ConfigurationComments([string]$Json, [ValidateSet('Default','Development','Client','Shared','Secrets','Policy','Maintenance')][string]$Kind) {
    $comments = Get-ConfigurationComments $Kind
    $document = [Text.Json.JsonDocument]::Parse($Json)
    $stream = [IO.MemoryStream]::new()
    $options = [Text.Json.JsonWriterOptions]::new()
    $options.Indented = $true
    $options.Encoder = [Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    $writer = [Text.Json.Utf8JsonWriter]::new($stream, $options)
    function Write-CommentedElement([Text.Json.JsonElement]$Element, [string]$Prefix) {
        switch ($Element.ValueKind) {
            'Object' {
                $writer.WriteStartObject()
                foreach ($property in $Element.EnumerateObject()) {
                    $key = if ($Prefix) { "$($Prefix):$($property.Name)" } else { $property.Name }
                    if ($comments.ContainsKey($key)) { foreach ($comment in $comments[$key]) { $writer.WriteCommentValue([string]$comment) } }
                    $writer.WritePropertyName($property.Name)
                    Write-CommentedElement $property.Value $key
                }
                $writer.WriteEndObject()
            }
            'Array' {
                $writer.WriteStartArray(); $index=0
                foreach ($item in $Element.EnumerateArray()) { Write-CommentedElement $item "$($Prefix):$index"; $index++ }
                $writer.WriteEndArray()
            }
            default { $Element.WriteTo($writer) }
        }
    }
    try {
        $writer.WriteCommentValue('D3Parking: komentáře vysvětlují nastavení a aplikace je ignoruje. Měňte hodnoty za dvojtečkou, ne názvy položek. Podrobný návod: docs/CONFIGURATION.md a docs/ADMIN-GUIDE.md.')
        switch ($Kind) {
            'Default' { $writer.WriteCommentValue('Výchozí nastavení serveru v release. V produkci měňte config/appsettings.json a secrets/secrets.json mimo releases; tyto sdílené soubory mají přednost. Výchozí localhost, .local a * nejsou hotové produkční nastavení.') }
            'Development' { $writer.WriteCommentValue('POUZE MÍSTNÍ VÝVOJ. Načítá se v Development a přepisuje základní nastavení. Známé testovací účty a klíče nepřenášejte na ostrý server. V Development se automaticky aplikují migrace databáze.') }
            'Client' { $writer.WriteCommentValue('VEŘEJNÝ soubor prohlížeče: každý návštěvník si jej může stáhnout. Patří sem jen klientské nastavení; nikdy SQL, hesla, SMTP tajemství ani privátní klíče. Server se nastavuje v D3Parking.Web nebo ve sdílené konfiguraci instalace.') }
            'Shared' { $writer.WriteCommentValue('Sdílené nastavení této instalace. Hesla patří do secrets/secrets.json, které má vyšší přednost. Průvodce po uložení znovu doplní vestavěné komentáře; vlastní poznámky si uchovejte zvlášť. Pro změnu hodnot použijte Nastavení, potom Řízený restart.') }
            { $_ -in @('Secrets','Maintenance') } { $writer.WriteCommentValue('NEVEŘEJNÝ soubor: obsahuje hesla v podobě potřebné aplikací/nasazením. Chrání jej oprávnění Windows, nikoli tyto komentáře. Nepatří do Gitu, e-mailu ani wwwroot. Chraňte také jeho zálohy.') }
            'Policy' { $writer.WriteCommentValue('Pravidla pro správce nasazení. Používejte stejný instalační profil v průvodci. Soubor musí odpovídat Windows službě a config/appsettings.json.') }
        }
        if ($Kind -in @('Default','Shared')) {
            $writer.WriteCommentValue('Entra ID nastavujte v aplikaci v Nastavení → Entra ID. Sekce EntraId zde záměrně chybí: zadaný konfigurační klíč má přednost před DB a uzamkne odpovídající pole v administraci. Přihlašovací ClientSecret a SCIM BearerToken patří do secrets, pokud je IT musí vynutit konfigurací. Uložená tajemství vyžadují trvalou klíčenku data/keys.')
        }
        Write-CommentedElement $document.RootElement ''
        $writer.Flush()
        # Utf8JsonWriter places separators after comments. Move them back to the value
        # and render short // lines, so an administrator sees ordinary, readable JSONC.
        $lines = [Text.Encoding]::UTF8.GetString($stream.ToArray()).Replace("`r`n", "`n").Split("`n")
        for ($i=0; $i -lt $lines.Length; $i++) {
            if ($lines[$i] -match '^\s*/\*.*\*/,$') {
                $previous = $i-1
                while ($previous -ge 0 -and $lines[$previous] -match '^\s*/\*') { $previous-- }
                if ($previous -lt 0) { throw 'Invalid generated configuration comment position.' }
                $lines[$previous] += ','
                $lines[$i] = $lines[$i].TrimEnd(',')
            }
        }
        $formatted = [Collections.Generic.List[string]]::new()
        foreach ($line in $lines) {
            if ($line -match '^(\s*)/\*(.*)\*/$') {
                $indent=$Matches[1]; $text=$Matches[2]; $wrapped=''
                foreach ($word in $text.Split(' ')) {
                    if ($wrapped -and ($indent.Length+$wrapped.Length+$word.Length+4) -gt 110) { $formatted.Add("$indent// $wrapped"); $wrapped='' }
                    $wrapped = if ($wrapped) { "$wrapped $word" } else { $word }
                }
                $formatted.Add("$indent// $wrapped")
            } else { $formatted.Add($line) }
        }
        return $formatted -join "`n"
    } finally { $writer.Dispose(); $stream.Dispose(); $document.Dispose() }
}

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }

function Assert-Administrator {
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'Deployment requires Windows x64.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell 7 as Administrator, then run the command again.'
    }
}

function Assert-ServiceAccess([string]$Path, [string]$ServiceName, [Security.AccessControl.FileSystemRights]$Rights) {
    Assert-NoReparsePoint $Path
    $sid = ([Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName")).Translate([Security.Principal.SecurityIdentifier]).Value
    [long]$granted = 0
    foreach ($rule in (Get-Acl -LiteralPath $Path).Access) {
        if ($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $sid) {
            if ($rule.AccessControlType -eq 'Deny') { throw "Service access denied: $Path" }
            $granted = $granted -bor [long]$rule.FileSystemRights
        }
    }
    if (($granted -band [long]$Rights) -ne [long]$Rights) { throw "Service $ServiceName lacks $Rights on $Path. Follow the ACL instructions in ADMIN-GUIDE.md." }
}

function Expand-VerifiedRelease([string]$Archive, [string]$Destination, [string]$ExpectedSha256) {
    if ($ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Missing or invalid SHA-256 checksum.' }
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Release checksum mismatch. Application was not modified.' }
    if (Test-Path -LiteralPath $Destination) { throw 'Release extraction directory already exists.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$size = 0
        if ($zip.Entries.Count -gt 20000) { throw 'Release has too many entries.' }
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or $name -match '(^|/)\.\.?(/|$)' -or $name -match '[. ](/|$)') {
                throw 'Release contains an unsafe path.'
            }
            if (-not $seen.Add($name)) { throw 'Release contains duplicate paths.' }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Release contains a symbolic link.' }
            $null = Assert-ChildPath $Destination (Join-Path $Destination $name)
            $size += $entry.Length
            if ($size -gt 4GB) { throw 'Release exceeds the 4 GiB unpacked limit.' }
        }
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination)))
        if ($drive.AvailableFreeSpace -lt ($size + 512MB)) { throw 'Insufficient disk space for release extraction.' }
        $null = New-Item -ItemType Directory -Path $Destination
        foreach ($entry in $zip.Entries) {
            $target = Assert-ChildPath $Destination (Join-Path $Destination $entry.FullName)
            if ($entry.FullName.EndsWith('/')) { $null = New-Item -ItemType Directory -Force -Path $target; continue }
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $target)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $zip.Dispose() }
    Test-ReleaseDirectory $Destination
}

function Test-ReleaseDirectory([string]$Path) {
    Assert-NoReparsePoint $Path
    $manifest = Read-Json (Join-Path $Path 'release.json')
    if ($manifest.format -ne 1 -or $manifest.runtime -ne 'win-x64' -or $manifest.version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Unsupported release metadata.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if (-not $seen.Add($file.path)) { throw 'Duplicate manifest entry.' }
        $target = Assert-ChildPath $Path (Join-Path $Path $file.path)
        Assert-NoReparsePoint $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Release file checksum mismatch: $($file.path)" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($Path, $file.FullName).Replace('\', '/')
        if ($relative -ne 'release.json' -and -not $seen.Contains($relative)) { throw "Unexpected release file: $relative" }
    }
    foreach ($required in @('app/D3Parking.Web.exe', 'app/D3Parking.Web.dll', 'app/D3Parking.Web.runtimeconfig.json', 'app/coreclr.dll', 'database/migrations.sql')) {
        if (-not $seen.Contains($required)) { throw "Release is incomplete: $required" }
    }
    $manifest
}

function Invoke-ReleaseCommand([string]$AppPath, [string]$Root, [string]$Environment, [string]$Command, [string]$ReportPath, [string]$ExpectedSchema = '') {
    if (Test-Path -LiteralPath $ReportPath) { throw 'Maintenance report already exists; use a new report path.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $AppPath 'D3Parking.Web.exe'))
    $start.WorkingDirectory = $AppPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($value in @('--contentRoot', $AppPath, '--environment', $Environment, '--Deployment:InstallPath', $Root,
        '--deployment-command', $Command, '--deployment-report', $ReportPath, '--deployment-expected-schema', $ExpectedSchema)) { $start.ArgumentList.Add($value) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        # Drain both streams concurrently; a full stderr pipe must not deadlock maintenance.
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(15000)) {
            Write-Host "[PROBÍHÁ] Kontrola / údržba: $Command, uplynulo $([int]$elapsed.Elapsed.TotalSeconds) s. Vyčkejte na výsledek."
            if ($elapsed.Elapsed.TotalMilliseconds -ge 2500000) { $process.Kill($true); throw 'Maintenance timed out; inspect database before recovery.' }
        }
        $null = $output.GetAwaiter().GetResult()
        $null = $errors.GetAwaiter().GetResult()
        if (-not (Test-Path -LiteralPath $ReportPath)) { throw "Maintenance process failed before its report (exit $($process.ExitCode)). Check shared JSON files and certificates." }
        $result = Read-Json $ReportPath
        if ($process.ExitCode -ne 0 -and $result.success) { throw 'Maintenance exited unsuccessfully despite its report. Inspect the database before recovery.' }
        return $result
    } finally { $process.Dispose() }
}

function Get-ServiceCommand([string]$AppPath, [string]$Root, [string]$Environment) {
    foreach ($value in @($AppPath, $Root, $Environment)) { if ($value.Contains('"')) { throw 'Quotes are not allowed in installation paths.' } }
    '"{0}" --contentRoot "{1}" --Deployment:InstallPath "{2}" --environment "{3}"' -f (Join-Path $AppPath 'D3Parking.Web.exe'), $AppPath, $Root, $Environment
}

function Set-ReleaseService([string]$Name, [string]$BinaryPath) {
    & sc.exe config $Name 'binPath=' $BinaryPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not change Windows service release path.' }
}

function Wait-ReleaseHealthy([string]$Url, [object]$Manifest, [string]$Environment) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try {
            $response = Invoke-RestMethod -Uri "$($Url.TrimEnd('/'))/health/ready" -TimeoutSec 6 -MaximumRedirection 0 -NoProxy
            if ($response.status -eq 'ready' -and $response.release.version -eq $Manifest.version -and $response.release.commit -eq $Manifest.commit -and $response.release.environment -eq $Environment) { return }
        } catch { }
        Start-Sleep -Milliseconds 1000
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Health check failed or a different release answered. Inspect application logs.'
}

function Initialize-Installation {
param([string]$InstallPath, [uri]$PublicUrl, [ValidateSet('Production','Staging')][string]$Environment = 'Production', [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,60}$')][string]$ServiceName = 'D3Parking', [ValidateRange(1024,65535)][int]$HealthPort = 5081)
Assert-Administrator
if (-not [IO.Path]::IsPathFullyQualified($InstallPath)) { throw 'InstallPath must be absolute.' }
$root = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
if (Test-Path -LiteralPath $root) { throw 'InstallPath already exists. Initialization never overwrites an installation.' }
$parent = Split-Path $root
while (-not (Test-Path -LiteralPath $parent)) {
    $parent = Split-Path $parent
    if (-not $parent) { throw 'D3PARKING: Instalační disk není dostupný. Zvolte existující místní disk.' }
}
Assert-NoReparsePoint $parent
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw 'Service name is already in use.' }
if ($PublicUrl.Scheme -ne 'https' -or $PublicUrl.IsLoopback -or $PublicUrl.AbsolutePath -ne '/' -or $PublicUrl.UserInfo -or $PublicUrl.Query -or $PublicUrl.Port -eq $HealthPort) {
    throw 'PublicUrl must be the public HTTPS origin, with a different port from HealthPort.'
}
$null = New-Item -ItemType Directory -Force -Path $root
# Protect the root BEFORE writing secrets. Localized Windows group names are avoided via SIDs.
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $root -AclObject $acl
foreach ($dir in @('releases','config','secrets','logs','backups','data/keys','state')) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $root $dir) }

# A virtual service account has no password to distribute. Its path is replaced after preflight.
$placeholder = Get-ServiceCommand (Join-Path $root 'releases/not-installed/app') $root $Environment
& sc.exe create $ServiceName 'binPath=' $placeholder 'start=' 'demand' 'obj=' "NT SERVICE\$ServiceName" 'DisplayName=' "D3Parking ($Environment)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service creation failed. Initialization is incomplete at $root; no application was started." }
& sc.exe sidtype $ServiceName unrestricted | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not enable the service SID.' }
$serviceSid = ([Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName")).Translate([Security.Principal.SecurityIdentifier])
# Root traversal only. The service cannot read deployment credentials or change binaries/config.
$rootAcl = Get-Acl -LiteralPath $root
$rootAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'ReadAndExecute', 'None', 'None', 'Allow'))
Set-Acl -LiteralPath $root -AclObject $rootAcl
foreach ($dir in @('releases','config')) {
    $path = Join-Path $root $dir
    $folderAcl = Get-Acl -LiteralPath $path
    $folderAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $folderAcl
}
foreach ($dir in @('logs','data')) {
    $path = Join-Path $root $dir
    $folderAcl = Get-Acl -LiteralPath $path
    $folderAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $folderAcl
}
$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=D3Parking Data Protection', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(5))
    try { [IO.File]::WriteAllBytes((Join-Path $root 'secrets/protection.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)) }
    finally { $certificate.Dispose() }
} finally { $rsa.Dispose() }
$settings = [ordered]@{
    Deployment = @{ Environment = $Environment }
    Account = @{ BaseUrl = $PublicUrl.GetLeftPart([UriPartial]::Authority) }
    AllowedHosts = "$($PublicUrl.Host);127.0.0.1"
    Kestrel = @{ Endpoints = @{
        Public = @{ Url = "https://0.0.0.0:$($PublicUrl.Port)"; Certificate = @{ Path = (Join-Path $root 'secrets/site.pfx') } }
        Health = @{ Url = "http://127.0.0.1:$HealthPort" }
    } }
    DataProtection = @{ Certificate = @{ Path = (Join-Path $root 'secrets/protection.pfx') } }
    Smtp = @{ Host = 'REPLACE_WITH_SMTP_RELAY'; Port = 587; Security = 'StartTls'; Authentication = 'Basic'; SenderEmail = 'REPLACE_WITH_SENDER'; SenderName = 'D3Parking'; TimeoutSeconds = 30 }
}
Write-JsonAtomic $settings (Join-Path $root 'config/appsettings.json')
Write-JsonAtomic ([ordered]@{
    ServiceName = $ServiceName; Environment = $Environment; HealthUrl = "http://127.0.0.1:$HealthPort";
    PublicUrl = $PublicUrl.GetLeftPart([UriPartial]::Authority); SqlBackupDirectory = 'REPLACE_WITH_ABSOLUTE_PATH_ON_SQL_SERVER'; ApprovedMigrations = @()
}) (Join-Path $root 'config/deployment.json')
Write-JsonAtomic ([ordered]@{
    ConnectionStrings = @{ SqlServer = 'Server=REPLACE_SQL_SERVER;Database=D3Parking;User ID=REPLACE_APP_USER;Password=REPLACE_PASSWORD;Encrypt=True;TrustServerCertificate=False' }
    IdentitySeed = @{ AdminEmail = 'REPLACE_WITH_ADMIN_EMAIL'; AdminPassword = 'REPLACE_WITH_UNIQUE_PASSWORD' }
    Smtp = @{ UserName = 'REPLACE_WITH_SMTP_USER'; Password = 'REPLACE_WITH_SMTP_PASSWORD' }
    Kestrel = @{ Endpoints = @{ Public = @{ Certificate = @{ Password = 'REPLACE_WITH_PFX_PASSWORD' } } } }
    DataProtection = @{ Certificate = @{ Password = $password } }
}) (Join-Path $root 'secrets/secrets.json')
Write-JsonAtomic (@{ ConnectionStrings = @{ SqlServer = 'Server=REPLACE_SQL_SERVER;Database=D3Parking;User ID=REPLACE_DEPLOY_USER;Password=REPLACE_PASSWORD;Encrypt=True;TrustServerCertificate=False' } }) (Join-Path $root 'secrets/deployment.json')
# Application-readable secret material lives separately from the administrator-only DB credential.
foreach ($name in @('secrets.json','protection.pfx')) {
    $path = Join-Path $root "secrets/$name"
    $fileAcl = Get-Acl -LiteralPath $path
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'Read', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $fileAcl
}
$secretAcl = Get-Acl -LiteralPath (Join-Path $root 'secrets')
$secretAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'ReadAndExecute', 'None', 'None', 'Allow'))
Set-Acl -LiteralPath (Join-Path $root 'secrets') -AclObject $secretAcl
Write-Host "[OK] Připraven adresář $root, ochranný certifikát a zastavená služba $ServiceName."
Write-Host '[DALŠÍ KROK] Průvodce nyní vyžádá SQL, SMTP, správce a HTTPS certifikát.'

}
function Invoke-Deployment {
param([string]$ReleasePath, [string]$ExpectedSha256, [string]$InstallPath, [switch]$Rollback, [switch]$CheckOnly, [switch]$Yes)
Assert-Administrator
$root = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
Assert-NoReparsePoint $root
$policy = Read-Json (Join-Path $root 'config/deployment.json')
if ($policy.Environment -notin @('Production','Staging') -or $policy.ServiceName -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,60}$') { throw 'Invalid deployment environment/service name.' }
$healthUri = [uri]$policy.HealthUrl
if ($healthUri.Scheme -ne 'http' -or $healthUri.Host -ne '127.0.0.1' -or $healthUri.AbsolutePath -ne '/') { throw 'HealthUrl must use http://127.0.0.1:<port>.' }
$publicUri = [uri]$policy.PublicUrl
if ($publicUri.Scheme -ne 'https' -or $publicUri.IsLoopback -or $publicUri.AbsolutePath -ne '/') { throw 'PublicUrl must be the public HTTPS origin.' }
foreach ($dir in @('state','logs','releases','config','secrets','data/keys','backups')) { Assert-NoReparsePoint (Join-Path $root $dir) }
$statePath = Join-Path $root 'state/installation.json'
$journalPath = Join-Path $root 'state/in-progress.json'
$log = Join-Path $root "logs/deploy-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([Guid]::NewGuid().ToString('N')).log"
$lock = $null
$stage = $null
$stopped = $false
$upgradeStarted = $false
$changed = $false
$old = $null
$oldManifest = $null
$completed = $false
function Step([string]$Text) { Write-Host $Text; Add-Content -LiteralPath $log -Value "$([DateTime]::UtcNow.ToString('o')) $Text" }
try {
    $lock = [IO.File]::Open((Join-Path $root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Assert-ConfigurationReady $root
    if (Test-Path -LiteralPath $journalPath) { throw 'An interrupted deployment needs recovery. Read state/in-progress.json and ADMIN-GUIDE.md before proceeding.' }
    $state = if (Test-Path -LiteralPath $statePath) { Read-Json $statePath } else { $null }
    if ($state) {
        $old = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$($state.current)")
        $oldManifest = Test-ReleaseDirectory $old
    }
    $service = Get-CimInstance Win32_Service -Filter "Name='$($policy.ServiceName)'"
    if (-not $service -or $service.StartName -ne "NT SERVICE\$($policy.ServiceName)") { throw 'Expected dedicated Windows service/account is missing. Use Action Install.' }
    if ($service.State -notin @('Running','Stopped')) { throw 'Service is changing state. Wait and rerun preflight.' }
    if ($old -and $service.PathName -ne (Get-ServiceCommand (Join-Path $old 'app') $root $policy.Environment)) { throw 'Service path does not match installation state. Investigate before deployment.' }
    if ($old -and $service.State -ne 'Running' -and -not $Rollback) { throw 'Current service is stopped. Use Action Recover or Action Rollback after investigating the outage.' }
    if (-not $old -and $service.State -ne 'Stopped') { throw 'First installation expects a stopped placeholder service.' }
    if ($Rollback) {
        if (-not $state -or -not $state.previous) { throw 'No previous release is recorded.' }
        $target = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$($state.previous)")
        $manifest = Test-ReleaseDirectory $target
    } else {
        $archive = (Resolve-Path -LiteralPath $ReleasePath).Path
        if (-not $ExpectedSha256) { $ExpectedSha256 = (Get-Content -LiteralPath "$archive.sha256" -Raw).Trim() }
        $stage = Join-Path ([IO.Path]::GetTempPath()) "d3parking-stage-$([Guid]::NewGuid().ToString('N'))"
        $manifest = Expand-VerifiedRelease $archive $stage $ExpectedSha256
        $target = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$($manifest.version)")
        if (Test-Path -LiteralPath $target) { throw 'Target release already exists. Never overwrite a versioned release; choose another version or use -Rollback.' }
    }
    if ($manifest.dirty) { throw 'Production/Staging deployment refuses a dirty working-tree artifact. Build from a reviewed Git commit.' }
    Step "Nasazení D3Parking | Prostředí: $($policy.Environment) | Aktuální: $(if ($state) { $state.current } else { 'nenainstalováno' }) | Cílová: $($manifest.version)"
    Step '[OK] Integrita vydání a identita Windows služby'
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))
    $source = if ($stage) { $stage } else { $target }
    $size = (Get-ChildItem -LiteralPath $source -Recurse -File | Measure-Object Length -Sum).Sum
    if ($drive.AvailableFreeSpace -lt ($size * 2 + 1GB)) { throw 'Insufficient free disk space. Free space without deleting current or previous releases.' }
    Step '[OK] Volné místo na disku'
    $shared = Read-Json (Join-Path $root 'config/appsettings.json')
    if ($shared.Deployment.Environment -ne $policy.Environment -or $shared.Kestrel.Endpoints.Health.Url.TrimEnd('/') -ne $policy.HealthUrl.TrimEnd('/') -or $shared.Account.BaseUrl.TrimEnd('/') -ne $policy.PublicUrl.TrimEnd('/')) {
        throw 'Application and deployment environment/URLs must match.'
    }
    foreach ($path in @((Join-Path $root 'config/appsettings.json'), (Join-Path $root 'secrets/secrets.json'), $shared.Kestrel.Endpoints.Public.Certificate.Path, $shared.DataProtection.Certificate.Path)) {
        Assert-ServiceAccess $path $policy.ServiceName ([Security.AccessControl.FileSystemRights]::Read)
    }
    foreach ($dir in @('logs','data/keys')) { Assert-ServiceAccess (Join-Path $root $dir) $policy.ServiceName ([Security.AccessControl.FileSystemRights]::Modify) }
    Step '[OK] Oprávnění služby ke konfiguraci, certifikátům, logům a klíčům'
    # Exercise administrator access to the actual state and release directories before stopping.
    foreach ($dir in @('state','releases','backups')) {
        $probe = Join-Path $root "$dir/.write-test-$([Guid]::NewGuid().ToString('N'))"
        [IO.File]::WriteAllText($probe, '')
        Remove-Item -LiteralPath $probe
    }
    $preflightPath = "$log.preflight.json"
    $check = Invoke-ReleaseCommand (Join-Path $source 'app') $root $policy.Environment 'preflight' $preflightPath
    if (-not $check.success) { throw "Preflight failed at $($check.phase): $($check.reason)" }
    Step "[OK] Konfigurace, certifikáty, SQL oprávnění a SMTP | Čekající migrace: $($check.schema.pending.Count)"
    Add-Content -LiteralPath $log -Value ($check | ConvertTo-Json -Depth 20)
    if ($check.unapprovedMigrations.Count -gt 0) { throw "Review database/migrations.sql and approve these exact IDs in config/deployment.json: $($check.unapprovedMigrations -join ', ')" }
    if ($Rollback -and $check.schema.pending.Count -gt 0) { throw 'Rollback target schema does not match the database. Database restoration requires DBA recovery.' }
    if ($check.release.version -ne $manifest.version -or $check.release.commit -ne $manifest.commit -or $check.schema.target -ne $manifest.schema) { throw 'Executable identity/schema differs from release manifest.' }
    if ($old -and -not $Rollback) { Wait-ReleaseHealthy $policy.HealthUrl $oldManifest $policy.Environment; Step '[OK] Aktuální aplikace je připravená' }
    elseif (-not $old) {
        foreach ($port in @($healthUri.Port, $publicUri.Port)) {
            if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port is already occupied." }
        }
    }
    if ($CheckOnly) { Step '[OK] Kontrola dokončena. Služba, aplikace ani databáze se nezměnily.'; $completed = $true; return }
    Show-DeploymentPlan $policy $state $manifest $check
    if (-not (Confirm-Operation 'Zastavit aplikaci, zálohovat databázi a nasadit uvedenou verzi?' -Yes:$Yes)) { return }
    Write-JsonAtomic @{ previous = if ($state) { $state.current } else { $null }; target = $manifest.version; phase = 'prepared'; log = $log } $journalPath
    # Copy after all critical preflight checks; never overwrite current/previous releases.
    if ($stage) {
        $null = New-Item -ItemType Directory -Path $target
        Copy-Item -Path (Join-Path $stage '*') -Destination $target -Recurse
        $null = Test-ReleaseDirectory $target
        Assert-ServiceAccess (Join-Path $target 'app/D3Parking.Web.exe') $policy.ServiceName ([Security.AccessControl.FileSystemRights]::ReadAndExecute)
    }
    $stopped = $true
    # A server reboot during migration must not automatically start old binaries.
    & sc.exe config $policy.ServiceName 'start=' 'demand' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not suspend automatic service startup.' }
    if ($old) { Stop-Service -Name $policy.ServiceName; (Get-Service $policy.ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
    Step '[OK] Aplikace zastavena; automatický start je po dobu údržby vypnutý'
    Write-JsonAtomic @{ previous = if ($state) { $state.current } else { $null }; target = $manifest.version; phase = 'database upgrade'; log = $log } $journalPath
    $upgradeStarted = $true
    $expectedSchema = if ($check.schema.applied.Count) { $check.schema.applied[-1] } else { 'none' }
    $upgrade = Invoke-ReleaseCommand (Join-Path $target 'app') $root $policy.Environment 'upgrade' "$log.upgrade.json" $expectedSchema
    $changed = $upgrade.databaseMayHaveChanged
    $upgradeStarted = $false
    if (-not $upgrade.success) { throw "Upgrade failed at $($upgrade.phase): $($upgrade.reason)" }
    Step "[OK] Ověřená SQL záloha: $($upgrade.backup)"
    Step '[OK] Databázové schéma připraveno'
    Write-JsonAtomic @{ previous = if ($state) { $state.current } else { $null }; target = $manifest.version; phase = 'starting target'; backup = $upgrade.backup; schemaChanged = $changed; log = $log } $journalPath
    Set-ReleaseService $policy.ServiceName (Get-ServiceCommand (Join-Path $target 'app') $root $policy.Environment)
    Start-Service -Name $policy.ServiceName
    Wait-ReleaseHealthy $policy.HealthUrl $manifest $policy.Environment
    Wait-ReleaseHealthy $policy.PublicUrl $manifest $policy.Environment
    Step '[OK] Lokální i veřejné HTTPS, databáze a identita vydání'
    & sc.exe config $policy.ServiceName 'start=' 'delayed-auto' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set automatic service startup.' }
    Write-JsonAtomic @{ current = $manifest.version; previous = if ($state) { $state.current } else { $null }; schema = $manifest.schema; backup = $upgrade.backup; deployedAt = [DateTime]::UtcNow.ToString('o') } $statePath
    $completed = $true
    Remove-Item -LiteralPath $journalPath
    Step 'Nasazení bylo úspěšně dokončeno.'
} catch {
    Step ("[CHYBA] " + (Get-FriendlyError $_))
    if ($completed) {
        Step 'Nové vydání prošlo kontrolami a stav instalace byl uložen. Aplikace běží. Před dalším nasazením vyřešte zbývající chybu zápisu žurnálu či logu.'
    } elseif ($stopped -and $old -and -not $changed -and -not $upgradeStarted) {
        try {
            Stop-Service -Name $policy.ServiceName -ErrorAction SilentlyContinue
            (Get-Service $policy.ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
            Set-ReleaseService $policy.ServiceName (Get-ServiceCommand (Join-Path $old 'app') $root $policy.Environment)
            Start-Service -Name $policy.ServiceName
            Wait-ReleaseHealthy $policy.HealthUrl $oldManifest $policy.Environment
            Wait-ReleaseHealthy $policy.PublicUrl $oldManifest $policy.Environment
            & sc.exe config $policy.ServiceName 'start=' 'delayed-auto' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not restore automatic startup.' }
            Remove-Item -LiteralPath $journalPath -ErrorAction SilentlyContinue
            Step 'Předchozí aplikace obnovena; databázové schéma se nezměnilo.'
        } catch { Step 'Automatický návrat aplikace selhal. Pokračujte obnovou s IT podle ADMIN-GUIDE.md.' }
    } elseif ($stopped) {
        Stop-Service -Name $policy.ServiceName -ErrorAction SilentlyContinue
        Step 'Aplikace je zastavená. Databáze se mohla změnit. DBA musí ověřit schéma před spuštěním odpovídající verze.'
    } else {
        if (Test-Path -LiteralPath $journalPath) { Step 'Příprava byla přerušena; zachovejte state/in-progress.json pro obnovu.' }
        Step 'Aplikace a databáze se nezměnily.'
    }
    Write-Host "Podrobnosti: $log"
    throw
} finally {
    if ($lock) { $lock.Dispose() }
    # Staging is a unique direct child of the OS temp directory; never remove a computed production path.
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $safeStage = Assert-ChildPath ([IO.Path]::GetTempPath()) $stage
        Assert-NoReparsePoint $safeStage
        Remove-Item -LiteralPath $safeStage -Recurse -Force
    }
}

}
function Invoke-Recovery {
param([string]$Version, [string]$InstallPath, [switch]$CheckOnly, [switch]$Yes)
Assert-Administrator
$root = [IO.Path]::GetFullPath($InstallPath)
Assert-NoReparsePoint $root
$policy = Read-Json (Join-Path $root 'config/deployment.json')
if ($policy.Environment -notin @('Production','Staging') -or $policy.ServiceName -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,60}$') { throw 'Invalid deployment environment/service name.' }
$service = Get-CimInstance Win32_Service -Filter "Name='$($policy.ServiceName)'"
if (-not $service -or $service.StartName -ne "NT SERVICE\$($policy.ServiceName)") { throw 'Recovery requires the initialized service identity.' }
$target = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$Version")
$manifest = Test-ReleaseDirectory $target
if ($manifest.dirty) { throw 'Recovery refuses a dirty artifact.' }
$lock = [IO.File]::Open((Join-Path $root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$report = Join-Path $root "logs/recover-$([Guid]::NewGuid().ToString('N')).json"
try {
    Assert-ConfigurationReady $root
    if ((Get-Service $policy.ServiceName).Status -ne 'Stopped') { throw 'Recovery requires a stopped service. Use Action Rollback for a running installation.' }
    $check = Invoke-ReleaseCommand (Join-Path $target 'app') $root $policy.Environment preflight $report
    if (-not $check.success) { throw "Recovery preflight failed: $($check.reason). Podrobnosti: $report" }
    if ($check.schema.pending.Count -ne 0) { throw 'Database schema does not exactly match the selected release. Have the DBA restore the correct recovery point first.' }
    if ($check.release.version -ne $manifest.version -or $check.release.commit -ne $manifest.commit -or $check.schema.target -ne $manifest.schema) { throw 'Release identity mismatch.' }
    Write-Host "[OK] Verze $($manifest.version) odpovídá skutečnému schématu DB. Žádná migrace se nespustí."
    Write-Host "Prostředí: $($policy.Environment) | Služba: $($policy.ServiceName) | Adresa: $($policy.PublicUrl)"
    Assert-ConfigurationReady $root
    if ($CheckOnly) { return }
    if (-not (Confirm-Operation 'Spustit zvolený release nad databází ověřenou po obnově?' -Yes:$Yes)) { return }
    Write-JsonAtomic @{ target = $Version; phase = 'recovery start'; log = $report } (Join-Path $root 'state/in-progress.json')
    & sc.exe config $policy.ServiceName 'start=' 'demand' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not suspend automatic service startup.' }
    Set-ReleaseService $policy.ServiceName (Get-ServiceCommand (Join-Path $target 'app') $root $policy.Environment)
    Start-Service $policy.ServiceName
    try {
        Wait-ReleaseHealthy $policy.HealthUrl $manifest $policy.Environment
        Wait-ReleaseHealthy $policy.PublicUrl $manifest $policy.Environment
    } catch { Stop-Service $policy.ServiceName; throw }
    & sc.exe config $policy.ServiceName 'start=' 'delayed-auto' | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-Service $policy.ServiceName; throw 'Could not restore automatic startup.' }
    try {
        Write-JsonAtomic @{ current = $Version; previous = $null; schema = $manifest.schema; backup = 'See recovery log / DBA restore record'; deployedAt = [DateTime]::UtcNow.ToString('o') } (Join-Path $root 'state/installation.json')
    } catch { Stop-Service $policy.ServiceName; throw }
    Remove-Item -LiteralPath (Join-Path $root 'state/in-progress.json')
    Write-Host "[OK] Obnova byla dokončena. Protokol: $report"
} finally { $lock.Dispose() }

}

# === Rozhraní průvodce a bezpečné zprávy ===
function Get-FriendlyError($Failure) {
    $message = if ($Failure -is [Management.Automation.ErrorRecord]) { $Failure.Exception.Message } else { [string]$Failure }
    if ($message.StartsWith('D3PARKING: ')) { return $message.Substring(11) }
    $reasons = [ordered]@{
        'Administrator|requires Windows' = 'Spusťte PowerShell 7 jako správce na Windows x64. Služba vyžaduje tato oprávnění.'
        'checksum|integrity|incomplete|Unexpected release|Duplicate manifest|Unsupported release' = 'Balíček není úplný nebo nesouhlasí kontrolní součty. Získejte ZIP a SHA-256 z důvěryhodného zdroje.'
        'unsafe path|duplicate paths|symbolic link|Links/junctions|escapes its parent|too many entries|unpacked limit' = 'Balíček nebo instalační cesta obsahuje nepovolené cesty či odkazy. Použijte běžný místní adresář a ověřený release.'
        'dirty' = 'Tento ZIP vznikl z necommitovaných změn. Použijte čistý release sestavený z prověřeného Git commitu.'
        'interrupted deployment|in-progress|changing state' = 'Předchozí operace není uzavřena nebo se služba přepíná. Zvolte Stav a diagnostika, potom Obnova; žurnál nemažte ručně.'
        'already exists|already in use' = 'Cesta, služba nebo verze už existuje. Existující instalaci aktualizujte; stejné vydání nepřepisujte.'
        'Insufficient|disk space' = 'Na disku není dost místa. Archivujte nepoužívané soubory; zachovejte aktuální a předchozí vydání.'
        'Service.*lacks|Service access denied|service/account|service identity|initialized service identity|service path|Service path' = 'Služba nebo její oprávnění neodpovídají instalaci. Ověřte účet NT SERVICE a oprávnění v diagnostice.'
        'Preflight failed at smtp' = 'SMTP kontrola selhala. V průvodci ověřte relay, port, TLS a přihlašovací údaje; potom kontrolu opakujte.'
        'Preflight failed at database|migration history differs|schema does not|Database changed|Review database|approve|Rollback target schema' = 'Databáze neodpovídá vydání, chybí oprávnění nebo revize migrací. V předběžném reportu najdete fázi a ID migrací; pokračujte s DBA.'
        'certificate|PFX|Preflight failed at configuration' = 'Ověřte společnou konfiguraci, certifikát, heslo, platnost, SAN a důvěryhodný řetězec. Použijte volbu Nastavení nebo HTTPS certifikát.'
        'Health check failed' = 'Aplikace neodpověděla jako správná zdravá verze. Ověřte stav služby, SQL, veřejné DNS a HTTPS v diagnostice.'
        'No previous release' = 'Instalace zatím nemá zaznamenané předchozí vydání.'
        'stopped|stopped service|placeholder service' = 'Stav služby neodpovídá operaci. Zvolte Stav; po přerušeném nasazení použijte Obnova, nikoli běžný update.'
        'Maintenance|Upgrade failed' = 'Údržba selhala nebo neposkytla spolehlivý výsledek. Přečtěte uložený report a žurnál; před obnovou ověřte skutečný stav databáze.'
        'environment|URLs|HealthUrl|PublicUrl|absolute|Quotes' = 'Nesouhlasí prostředí, adresa nebo instalační cesta. Opravte je pomocí průvodce Nastavení.'
    }
    foreach ($pattern in $reasons.Keys) { if ($message -match $pattern) { return $reasons[$pattern] } }
    # JSON/parser/provider exceptions can include secret values. Never print their raw messages.
    return 'Operace se nepodařila. Ověřte dostupnost souborů, oprávnění a formát nastavení. Zvolte diagnostiku; tajemství ani syrové chyby parseru se nevypisují.'
}

function Read-Value([string]$Label, [string]$Default = '', [scriptblock]$Validate = { param($v) -not [string]::IsNullOrWhiteSpace($v) }, [string]$Hint = 'Hodnota není platná.') {
    while ($true) {
        $suffix = if ($Default) { " [$Default]" } else { '' }
        $value = (Read-Host "$Label$suffix").Trim().Trim('"')
        if (-not $value) { $value = $Default }
        if (& $Validate $value) { return $value }
        Write-Host "[OPRAVTE] $Hint" -ForegroundColor Yellow
    }
}

function Read-Secret([string]$Label, [string]$Existing = '', [switch]$AllowEmpty) {
    if ($Existing -match '^REPLACE_') { $Existing = '' }
    while ($true) {
        $hint = if ($Existing) { ' (Enter ponechá stávající hodnotu)' } else { '' }
        $secure = Read-Host "$Label$hint" -AsSecureString
        try { $value = [Net.NetworkCredential]::new('', $secure).Password } finally { $secure.Dispose() }
        if ($value) { return $value }
        if ($Existing) { return $Existing }
        if ($AllowEmpty) { return '' }
        Write-Host 'Hodnota je povinná; při psaní se nezobrazuje.' -ForegroundColor Yellow
    }
}

function Confirm-Operation([string]$Description, [switch]$Yes) {
    Write-Host "`n$Description" -ForegroundColor Yellow
    if ($Yes) { Write-Host 'Potvrzeno parametrem -Yes.'; return $true }
    return (Read-Host 'Pro pokračování napište ANO; Enter operaci zruší') -ceq 'ANO'
}

function Read-Map([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
function Get-Setting($Map, [string]$Path, $Default = '') {
    $value = $Map
    foreach ($part in $Path.Split(':')) {
        if ($value -isnot [Collections.IDictionary] -or -not $value.Contains($part)) { return $Default }
        $value = $value[$part]
    }
    if ($null -eq $value -or ([string]$value) -match '^REPLACE_') { return $Default }
    return $value
}
function Set-Setting($Map, [string]$Path, $Value) {
    $parts = $Path.Split(':'); $node = $Map
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        if (-not $node.Contains($parts[$i]) -or $node[$parts[$i]] -isnot [Collections.IDictionary]) { $node[$parts[$i]] = @{} }
        $node = $node[$parts[$i]]
    }
    $node[$parts[-1]] = $Value
}

function Remove-Setting($Map, [string]$Path) {
    $parts = $Path.Split(':'); $node = $Map
    for ($i=0; $i -lt $parts.Length-1; $i++) {
        if ($node -isnot [Collections.IDictionary] -or -not $node.Contains($parts[$i])) { return }
        $node = $node[$parts[$i]]
    }
    if ($node -is [Collections.IDictionary]) { $null = $node.Remove($parts[-1]) }
}

function Normalize-ManagedSettings($App, $Secret) {
    # Preserve effective values while removing duplicate managed keys from the higher-priority file.
    foreach ($key in @('Deployment:Environment','Account:BaseUrl','AllowedHosts','Kestrel:Endpoints:Public:Url','Kestrel:Endpoints:Health:Url','Kestrel:Endpoints:Public:Certificate:Path','DataProtection:Certificate:Path',
        'Smtp:Host','Smtp:Port','Smtp:SenderEmail','Smtp:Authentication','Smtp:Security','Smtp:OAuth2:TokenEndpoint','Smtp:OAuth2:ClientId','Smtp:OAuth2:Scope')) {
        $value = Get-Setting $Secret $key $null
        if ($null -ne $value) { Set-Setting $App $key $value }
        Remove-Setting $Secret $key
    }
    foreach ($key in @('ConnectionStrings:SqlServer','IdentitySeed:AdminEmail','IdentitySeed:AdminPassword','Smtp:UserName','Smtp:Password','Smtp:OAuth2:ClientSecret','Kestrel:Endpoints:Public:Certificate:Password','DataProtection:Certificate:Password')) {
        $value = Get-Setting $Secret $key (Get-Setting $App $key $null)
        if ($null -ne $value) { Set-Setting $Secret $key $value }
        Remove-Setting $App $key
    }
}

function Assert-ConfigurationReady([string]$Root) {
    if (Test-Path -LiteralPath (Join-Path $Root 'state/config-in-progress.json')) {
        throw 'D3PARKING: Zápis nastavení byl přerušen. Nejdříve zvolte Obnovit přerušený zápis nastavení.'
    }
}

function Get-Installation([string]$Root) {
    if (-not [IO.Path]::IsPathFullyQualified($Root)) { throw 'D3PARKING: Instalační cesta musí být absolutní.' }
    Assert-NoReparsePoint $Root
    $policy = Read-Json (Join-Path $Root 'config/deployment.json')
    if ($policy.ServiceName -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,60}$' -or $policy.Environment -notin @('Production','Staging')) { throw 'D3PARKING: Neplatný název služby nebo prostředí v instalačním profilu.' }
    foreach ($endpoint in @($policy.PublicUrl, $policy.HealthUrl)) {
        $uri = [uri]$endpoint
        if (-not $uri.IsAbsoluteUri -or $uri.AbsolutePath -ne '/' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'D3PARKING: Adresy instalace musí být samotné originy bez cesty, hesla nebo parametrů.' }
    }
    if (([uri]$policy.PublicUrl).Scheme -ne 'https' -or ([uri]$policy.PublicUrl).IsLoopback -or ([uri]$policy.HealthUrl).Host -ne '127.0.0.1' -or ([uri]$policy.HealthUrl).Scheme -ne 'http') { throw 'D3PARKING: Veřejná adresa musí být HTTPS; health musí být HTTP na 127.0.0.1.' }
    $stateFile = Join-Path $Root 'state/installation.json'
    $state = if (Test-Path -LiteralPath $stateFile) { Read-Json $stateFile } else { $null }
    $release = if ($state) { Assert-ChildPath (Join-Path $Root 'releases') (Join-Path $Root "releases/$($state.current)") } else { $null }
    return @{ Policy = $policy; State = $state; Release = $release }
}

function Show-DeploymentPlan($Policy, $State, $Manifest, $Check) {
    Write-Host "`nPLÁN NASAZENÍ" -ForegroundColor Cyan
    Write-Host "Prostředí: $($Policy.Environment) | Služba: $($Policy.ServiceName)"
    Write-Host "Verze: $(if ($State) { $State.current } else { 'nová instalace' }) → $($Manifest.version)"
    Write-Host "Adresa: $($Policy.PublicUrl)"
    Write-Host "Nové migrace: $($Check.schema.pending.Count) | Neodsouhlasené: $($Check.unapprovedMigrations.Count)"
    Write-Host "SQL záloha: $($Policy.SqlBackupDirectory) (cesta na SQL serveru; zápis se ověří samotnou zálohou)"
    Write-Host 'Odstávka: ano, po dobu zálohy, migrací a ověření startu. Délku předem negarantujeme.'
    Write-Host $(if ($Check.schema.pending.Count) { 'Návrat před změnu schématu: vyžaduje rozhodnutí DBA a obnovu odpovídající databáze.' } else { 'Návrat binárek: možný při shodném schématu a dostupném předchozím vydání.' })
}

function Get-ReleaseInput([string]$Path, [string]$Hash) {
    if (-not $Path) { $Path = Read-Value 'Cesta k release ZIPu' '' { param($v) (Test-Path -LiteralPath $v -PathType Leaf) -and $v.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) } 'Vyberte existující soubor .zip.' }
    $Path = (Resolve-Path -LiteralPath $Path).Path
    if (-not $Hash -and (Test-Path -LiteralPath "$Path.sha256")) { $Hash = (Get-Content -LiteralPath "$Path.sha256" -Raw).Trim() }
    if (-not $Hash) { $Hash = Read-Value 'Ověřený SHA-256 od vydavatele' '' { param($v) $v -match '^[0-9a-fA-F]{64}$' } 'SHA-256 má 64 hexadecimálních znaků.' }
    Write-Host "Balíček: $Path"
    Write-Host 'SHA-256 chrání integritu. Původ skriptu i součtu musí být důvěryhodný.'
    return @{ Path = $Path; Hash = $Hash }
}

function Open-Release([string]$Path, [string]$Hash) {
    $stage = Join-Path ([IO.Path]::GetTempPath()) "d3parking-wizard-$([Guid]::NewGuid().ToString('N'))"
    try {
        $manifest = Expand-VerifiedRelease $Path $stage $Hash
        if ($manifest.dirty) { throw 'Release is dirty.' }
        Write-Host "[OK] Ověřeny všechny soubory vydání $($manifest.version)."
        return @{ Directory = $stage; Manifest = $manifest }
    } catch { if (Test-Path -LiteralPath $stage) { Remove-TemporaryRelease $stage }; throw }
}
function Remove-TemporaryRelease([string]$Path) {
    $safe = Assert-ChildPath ([IO.Path]::GetTempPath()) $Path
    if ([IO.Path]::GetFileName($safe) -notmatch '^d3parking-wizard-[a-f0-9]{32}$') { throw 'D3PARKING: Odmítnuto odstranění neznámé pracovní složky.' }
    Assert-NoReparsePoint $safe
    Remove-Item -LiteralPath $safe -Recurse -Force
}

# === Nastavení: průvodce, ověření kandidáta a obnovitelný zápis ===
function New-ProtectedDirectory([string]$Path) {
    $null = New-Item -ItemType Directory -Path $Path
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-32-544','S-1-5-18')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function New-SqlConnection([string]$Server, [string]$Database, [string]$User, [string]$Password) {
    # Framework builder quotes semicolons and quotes inside passwords; never concatenate SQL credentials.
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    foreach ($entry in @{Server=$Server;Database=$Database;'User ID'=$User;Password=$Password;Encrypt='True';TrustServerCertificate='False'}.GetEnumerator()) { $builder.Add($entry.Key, $entry.Value) }
    return $builder.psbase.ConnectionString
}
function Read-SqlConnection([string]$Connection) {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    if ($Connection) { $builder.psbase.ConnectionString = $Connection }
    $result = @{}
    $aliases = @{ Server=@('Server','Data Source','Address'); Database=@('Database','Initial Catalog'); User=@('User ID','UID'); Password=@('Password','PWD') }
    foreach ($name in $aliases.Keys) {
        $result[$name] = ''
        foreach ($alias in $aliases[$name]) { if ($builder.ContainsKey($alias)) { $result[$name] = [string]$builder[$alias]; break } }
        if ($result[$name] -match '^REPLACE_') { $result[$name] = '' }
    }
    return $result
}

function Restore-ConfigurationFiles([string]$Root) {
    $journalPath = Join-Path $Root 'state/config-in-progress.json'
    $journal = Read-Json $journalPath
    $backup = Assert-ChildPath (Join-Path $Root 'backups') $journal.backup
    Assert-NoReparsePoint $backup
    $allowed = @('config/appsettings.json','config/deployment.json','secrets/secrets.json','secrets/deployment.json')
    if (@($journal.files).Count -ne 4 -or @($journal.files.relative | Select-Object -Unique).Count -ne 4) { throw 'D3PARKING: Neúplný žurnál nastavení. Zachovejte soubory a požádejte IT o obnovu.' }
    foreach ($file in $journal.files) {
        if ($file.relative -notin $allowed) { throw 'D3PARKING: Neznámý soubor v žurnálu nastavení.' }
        $source = Assert-ChildPath $backup (Join-Path $backup $file.relative.Replace('/','.'))
        Assert-NoReparsePoint $source
        if ((Get-FileHash -LiteralPath $source).Hash -ne $file.sha256) { throw 'D3PARKING: Záloha nastavení je poškozená. Automatická obnova byla odmítnuta.' }
    }
    foreach ($file in $journal.files) {
        Write-JsonAtomic (Read-Map (Join-Path $backup $file.relative.Replace('/','.'))) (Join-Path $Root $file.relative)
    }
    if ($journal.PSObject.Properties['service']) { Set-ConfigurationStartup $journal.service.name $journal.service.startup }
    Remove-Item -LiteralPath $journalPath
    Write-Host '[OK] Původní nastavení bylo obnoveno. Databáze se neměnila.'
}

function Set-ConfigurationStartup([string]$Name, [string]$Startup) {
    if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,60}$' -or $Startup -notin @('auto','delayed-auto','demand','disabled')) { throw 'Invalid service startup journal.' }
    $service = Get-CimInstance Win32_Service -Filter "Name='$Name'"
    if (-not $service -or $service.StartName -ne "NT SERVICE\$Name") { throw 'Service identity differs from configuration journal.' }
    & sc.exe config $Name 'start=' $Startup | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set service startup during configuration transaction.' }
}

function Save-Configuration([string]$Root, $Documents, [string]$ServiceName = '') {
    Assert-ConfigurationReady $Root
    $backup = Join-Path $Root "backups/settings-$([Guid]::NewGuid().ToString('N'))"
    New-ProtectedDirectory $backup
    $files = @()
    foreach ($relative in @('config/appsettings.json','config/deployment.json','secrets/secrets.json','secrets/deployment.json')) {
        $source = Join-Path $Root $relative
        Assert-NoReparsePoint $source
        $copy = Join-Path $backup $relative.Replace('/','.')
        Copy-Item -LiteralPath $source -Destination $copy
        $files += @{ relative = $relative; sha256 = (Get-FileHash -LiteralPath $copy).Hash }
    }
    $journal = Join-Path $Root 'state/config-in-progress.json'
    $transaction = @{ backup=$backup; files=$files; phase='writing configuration' }
    if ($ServiceName) {
        $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        if (-not $service -or $service.StartName -ne "NT SERVICE\$ServiceName") { throw 'Service identity differs from configuration.' }
        $startup = switch ($service.StartMode) { 'Auto' { if ($service.DelayedAutoStart) { 'delayed-auto' } else { 'auto' } }; 'Manual' { 'demand' }; 'Disabled' { 'disabled' }; default { throw 'Invalid service startup mode.' } }
        $transaction.service = @{name=$ServiceName;startup=$startup}
    }
    Write-JsonAtomic $transaction $journal
    try {
        # Keep the running process untouched, but prevent reboot from loading half-written files.
        if ($ServiceName) { Set-ConfigurationStartup $ServiceName demand }
        foreach ($file in $files) { Write-JsonAtomic $Documents[$file.relative] (Join-Path $Root $file.relative) }
        if ($ServiceName) { Set-ConfigurationStartup $ServiceName $transaction.service.startup }
        Remove-Item -LiteralPath $journal
    } catch {
        try { Restore-ConfigurationFiles $Root } catch { Write-Host '[POZOR] Obnova nastavení se nedokončila. Použijte volbu Obnovit přerušený zápis nastavení.' -ForegroundColor Yellow }
        throw 'D3PARKING: Nastavení se nepodařilo celé uložit. Zkontrolujte stav obnovy před restartem služby.'
    }
    Write-Host "[OK] Nastavení uloženo. Chráněná záloha předchozího stavu: $backup"
}

function Install-HttpsCertificate([string]$Root, [string]$ServiceName, [string]$Source) {
    $target = Join-Path $Root "secrets/site-$([Guid]::NewGuid().ToString('N')).pfx"
    Copy-Item -LiteralPath $Source -Destination $target
    $sid = ([Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName")).Translate([Security.Principal.SecurityIdentifier])
    $acl = Get-Acl -LiteralPath $target
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'Read', 'Allow'))
    Set-Acl -LiteralPath $target -AclObject $acl
    return $target
}

function Invoke-ConfigurationWizard([string]$Root, [string]$AppPath, [switch]$CertificateOnly, [switch]$Yes) {
    $installation = Get-Installation $Root
    $lock = [IO.File]::Open((Join-Path $Root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $candidate = $null
    try {
        Assert-ConfigurationReady $Root
        if (Test-Path -LiteralPath (Join-Path $Root 'state/in-progress.json')) { throw 'An interrupted deployment needs recovery.' }
        $documents = @{}
        foreach ($relative in @('config/appsettings.json','config/deployment.json','secrets/secrets.json','secrets/deployment.json')) { $documents[$relative] = Read-Map (Join-Path $Root $relative) }
        $app = $documents['config/appsettings.json']; $policy = $documents['config/deployment.json']
        $secret = $documents['secrets/secrets.json']; $maintenance = $documents['secrets/deployment.json']
        Normalize-ManagedSettings $app $secret
        Write-Host "`nNASTAVENÍ | $($policy.Environment) | $Root" -ForegroundColor Cyan
        Write-Host 'Hesla se nezobrazují. Ukládají se do souborů omezených ACL; aplikace potřebuje přístup ke svým tajemstvím.'
        if (-not $CertificateOnly) {
            $public = Read-Value 'Veřejná HTTPS adresa' $policy.PublicUrl { param($v) $u=$null; [uri]::TryCreate($v,[UriKind]::Absolute,[ref]$u) -and $u.Scheme -eq 'https' -and -not $u.IsLoopback -and $u.AbsolutePath -eq '/' -and -not $u.Query -and -not $u.Fragment -and -not $u.UserInfo } 'Například https://parking.firma.cz:8443, bez podadresáře.'
            $healthPort = [int](Read-Value 'Místní health port' ([string]([uri]$policy.HealthUrl).Port) { param($v) $n=0; [int]::TryParse($v,[ref]$n) -and $n -ge 1024 -and $n -le 65535 } 'Port musí být 1024–65535.')
            if (([uri]$public).Port -eq $healthPort) { throw 'D3PARKING: Veřejný a health port musí být rozdílné.' }
            $policy.PublicUrl = ([uri]$public).GetLeftPart([UriPartial]::Authority)
            $policy.HealthUrl = "http://127.0.0.1:$healthPort"
            Set-Setting $app 'Account:BaseUrl' $policy.PublicUrl
            Set-Setting $app 'AllowedHosts' "$(([uri]$public).Host);127.0.0.1"
            Set-Setting $app 'Kestrel:Endpoints:Public:Url' "https://0.0.0.0:$(([uri]$public).Port)"
            Set-Setting $app 'Kestrel:Endpoints:Health:Url' $policy.HealthUrl

            Write-Host "`nSQL SERVER — existující samostatná databáze a dva účty od DBA."
            $runtimeSql = Read-SqlConnection (Get-Setting $secret 'ConnectionStrings:SqlServer')
            $deploySql = Read-SqlConnection (Get-Setting $maintenance 'ConnectionStrings:SqlServer')
            $server = Read-Value 'SQL server (případně server,port)' $runtimeSql.Server
            $database = Read-Value 'Databáze' $(if ($runtimeSql.Database) { $runtimeSql.Database } else { 'D3Parking' }) { param($v) $v -and $v -notin @('master','model','msdb','tempdb') } 'Použijte samostatnou aplikační DB.'
            $runtimeUser = Read-Value 'SQL účet aplikace (čtení a zápis)' $runtimeSql.User
            $runtimePassword = Read-Secret 'Heslo SQL účtu aplikace' $runtimeSql.Password
            $deployUser = Read-Value 'SQL účet nasazení (db_owner této DB)' $deploySql.User
            if ($runtimeUser -eq $deployUser) { throw 'D3PARKING: Pro aplikaci a nasazení použijte dva odlišné SQL účty.' }
            $deployPassword = Read-Secret 'Heslo SQL účtu nasazení' $deploySql.Password
            Set-Setting $secret 'ConnectionStrings:SqlServer' (New-SqlConnection $server $database $runtimeUser $runtimePassword)
            Set-Setting $maintenance 'ConnectionStrings:SqlServer' (New-SqlConnection $server $database $deployUser $deployPassword)
            $policy.SqlBackupDirectory = Read-Value 'Adresář záloh NA SQL SERVERU (připravený DBA)' (Get-Setting $policy 'SqlBackupDirectory') { param($v) [IO.Path]::IsPathFullyQualified($v) } 'Absolutní cesta; zapisuje do ní služba SQL Serveru.'

            Write-Host "`nSMTP — ověříme spojení a přihlášení; kontrola neodesílá e-mail."
            Set-Setting $app 'Smtp:Host' (Read-Value 'SMTP relay' (Get-Setting $app 'Smtp:Host'))
            Set-Setting $app 'Smtp:Port' ([int](Read-Value 'SMTP port' ([string](Get-Setting $app 'Smtp:Port' 587)) { param($v) $n=0; [int]::TryParse($v,[ref]$n) -and $n -gt 0 -and $n -le 65535 } 'Port musí být 1–65535.'))
            Set-Setting $app 'Smtp:SenderEmail' (Read-Value 'Adresa odesílatele' (Get-Setting $app 'Smtp:SenderEmail') { param($v) $m=$null; [Net.Mail.MailAddress]::TryCreate($v,[ref]$m) } 'Zadejte platnou e-mailovou adresu.')
            $auth = Read-Value 'Ověření SMTP: None / Basic / OAuth2' (Get-Setting $app 'Smtp:Authentication' 'Basic') { param($v) $v -cin @('None','Basic','OAuth2') } 'Vyberte None, Basic nebo OAuth2.'
            $security = Read-Value 'Zabezpečení SMTP: StartTls / SslOnConnect / None' (Get-Setting $app 'Smtp:Security' 'StartTls') { param($v) $v -cin @('StartTls','SslOnConnect','None') } 'Vyberte jednu z uvedených možností.'
            if ($auth -ne 'None' -and $security -eq 'None') { throw 'D3PARKING: Přihlašovací údaje SMTP lze použít pouze se StartTls nebo SslOnConnect.' }
            Set-Setting $app 'Smtp:Authentication' $auth; Set-Setting $app 'Smtp:Security' $security
            if ($auth -ne 'None') { Set-Setting $secret 'Smtp:UserName' (Read-Value 'SMTP uživatel / schránka' (Get-Setting $secret 'Smtp:UserName')) }
            if ($auth -eq 'Basic') { Set-Setting $secret 'Smtp:Password' (Read-Secret 'Heslo SMTP' (Get-Setting $secret 'Smtp:Password')) }
            if ($auth -eq 'OAuth2') {
                Set-Setting $app 'Smtp:OAuth2:TokenEndpoint' (Read-Value 'OAuth2 token endpoint (HTTPS)' (Get-Setting $app 'Smtp:OAuth2:TokenEndpoint') { param($v) $u=$null; [uri]::TryCreate($v,[UriKind]::Absolute,[ref]$u) -and $u.Scheme -eq 'https' -and -not $u.UserInfo } 'Zadejte HTTPS adresu token endpointu.')
                Set-Setting $app 'Smtp:OAuth2:ClientId' (Read-Value 'OAuth2 Client ID' (Get-Setting $app 'Smtp:OAuth2:ClientId'))
                Set-Setting $secret 'Smtp:OAuth2:ClientSecret' (Read-Secret 'OAuth2 Client secret' (Get-Setting $secret 'Smtp:OAuth2:ClientSecret'))
                Set-Setting $app 'Smtp:OAuth2:Scope' (Read-Value 'OAuth2 Scope (může být prázdný)' (Get-Setting $app 'Smtp:OAuth2:Scope') { $true })
            }
            if (-not $installation.State) {
                Set-Setting $secret 'IdentitySeed:AdminEmail' (Read-Value 'E-mail prvního správce' (Get-Setting $secret 'IdentitySeed:AdminEmail') { param($v) $m=$null; [Net.Mail.MailAddress]::TryCreate($v,[ref]$m) } 'Zadejte platný e-mail.')
                Set-Setting $secret 'IdentitySeed:AdminPassword' (Read-Secret 'Heslo prvního správce (12+, velká/malá písmena, číslice, symbol)' (Get-Setting $secret 'IdentitySeed:AdminPassword'))
            } elseif (Get-Setting $secret 'IdentitySeed:AdminEmail') {
                $removeSeed = Read-Value 'Odstranit uložené údaje prvního správce? Účet v DB zůstane. Ano / Ne' 'Ano' { param($v) $v -in @('Ano','Ne') }
                if ($removeSeed -eq 'Ano') {
                    Set-Setting $secret 'IdentitySeed:AdminEmail' ''
                    Set-Setting $secret 'IdentitySeed:AdminPassword' ''
                }
            }
        }

        $oldCertificate = Get-Setting $app 'Kestrel:Endpoints:Public:Certificate:Path'
        $certificatePath = Read-Value 'HTTPS certifikát PFX (Enter ponechá aktuální)' $(if ($oldCertificate -and (Test-Path -LiteralPath $oldCertificate -PathType Leaf)) { $oldCertificate } else { '' }) { param($v) $v -and (Test-Path -LiteralPath $v -PathType Leaf) } 'Vyberte existující PFX s privátním klíčem a odpovídajícím SAN.'
        $certificatePassword = Read-Secret 'Heslo HTTPS PFX (u nového PFX může být prázdné)' $(if ($certificatePath -eq $oldCertificate) { Get-Setting $secret 'Kestrel:Endpoints:Public:Certificate:Password' } else { '' }) -AllowEmpty
        Set-Setting $app 'Kestrel:Endpoints:Public:Certificate:Path' ([IO.Path]::GetFullPath($certificatePath))
        Set-Setting $secret 'Kestrel:Endpoints:Public:Certificate:Password' $certificatePassword

        $candidate = Join-Path $Root "backups/candidate-$([Guid]::NewGuid().ToString('N'))"
        New-ProtectedDirectory $candidate
        $null = New-Item -ItemType Directory -Path (Join-Path $candidate 'config'), (Join-Path $candidate 'secrets')
        foreach ($relative in $documents.Keys) { Write-JsonAtomic $documents[$relative] (Join-Path $candidate $relative) }
        Write-Host '[KONTROLA] Nové nastavení je dočasné. Ověřuji certifikáty, SQL a SMTP; běžící instalace zatím používá původní soubory.'
        $reportPath = Join-Path $candidate 'preflight.json'
        $check = Invoke-ReleaseCommand $AppPath $candidate $policy.Environment preflight $reportPath
        if (-not $check.success) {
            $failureReport = Join-Path $Root "logs/config-check-$([Guid]::NewGuid().ToString('N')).json"
            Copy-Item -LiteralPath $reportPath -Destination $failureReport
            Write-Host "Kontrola selhala ve fázi: $($check.phase). Sanitizovaný report: $failureReport"
            throw "Preflight failed at $($check.phase)"
        }
        Write-Host "[OK] Konfigurace ověřena. Prostředí $($policy.Environment), adresa $($policy.PublicUrl), nové migrace: $($check.schema.pending.Count)."
        $reviewSql = Read-SqlConnection (Get-Setting $secret 'ConnectionStrings:SqlServer')
        Write-Host "SQL: $($reviewSql.Server) / $($reviewSql.Database) | účet aplikace: $($reviewSql.User)"
        Write-Host "SMTP: $(Get-Setting $app 'Smtp:Host'):$(Get-Setting $app 'Smtp:Port') | $(Get-Setting $app 'Smtp:Security') / $(Get-Setting $app 'Smtp:Authentication')"
        Write-Host "HTTPS certifikát: $certificatePath | cílový hostname: $(([uri]$policy.PublicUrl).Host)"
        Write-Host 'Uložení změní konfiguraci. Databázi nemigruje, službu nezastaví ani nerestartuje. Změny se projeví při dalším startu.'
        Write-Host 'Během zápisu dočasně nastaví ruční start služby, aby restart serveru nenačetl neúplné soubory; potom vrátí původní režim.'
        if (-not (Confirm-Operation 'Uložit ověřené nastavení a zálohovat původní soubory?' -Yes:$Yes)) { return $false }
        $targetPfx = Install-HttpsCertificate $Root $policy.ServiceName $certificatePath
        Set-Setting $app 'Kestrel:Endpoints:Public:Certificate:Path' $targetPfx
        Save-Configuration $Root $documents $policy.ServiceName
        Write-Host '[DALŠÍ KROK] Proveďte instalaci / aktualizaci nebo řízený restart. DNS, firewall a dostupnost veřejné adresy ověří závěrečná kontrola.'
        return $true
    } finally {
        $lock.Dispose()
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            $safe = Assert-ChildPath (Join-Path $Root 'backups') $candidate
            Assert-NoReparsePoint $safe
            Remove-Item -LiteralPath $safe -Recurse -Force
        }
        $documents=$null; $secret=$null; $maintenance=$null; $runtimePassword=$null; $deployPassword=$null; $certificatePassword=$null
    }
}

# === Stav, diagnostika a řízené operace ===
function Get-HealthResult([string]$Url, $State, [string]$Environment) {
    try {
        $response = Invoke-RestMethod -Uri "$($Url.TrimEnd('/'))/health/ready" -TimeoutSec 6 -MaximumRedirection 0 -NoProxy
        $matches = $State -and $response.release.version -eq $State.current -and $response.release.environment -eq $Environment
        return @{ ready=($response.status -eq 'ready' -and [bool]$matches); expectedVersion=[bool]$matches; version=[string]$response.release.version; environment=[string]$response.release.environment }
    } catch { return @{ ready=$false; expectedVersion=$false; reason='Nedostupné nebo neplatná odpověď; ověřte službu, SQL, DNS a TLS.' } }
}

function Show-InstallationStatus([string]$Root, [switch]$Diagnostics) {
    $installation = Get-Installation $Root
    $policy=$installation.Policy; $state=$installation.State
    $service = Get-Service -Name $policy.ServiceName -ErrorAction SilentlyContinue
    $disk = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Root))
    $report = [ordered]@{
        checkedAt=[DateTime]::UtcNow.ToString('o'); environment=$policy.Environment; serviceName=$policy.ServiceName
        serviceStatus=$(if ($service) { [string]$service.Status } else { 'Nenalezena' })
        current=$(if ($state) { $state.current } else { $null }); previous=$(if ($state) { $state.previous } else { $null })
        freeDiskGiB=[Math]::Round($disk.AvailableFreeSpace/1GB,1)
        interruptedDeployment=(Test-Path -LiteralPath (Join-Path $Root 'state/in-progress.json'))
        interruptedConfiguration=(Test-Path -LiteralPath (Join-Path $Root 'state/config-in-progress.json'))
        localHealth=(Get-HealthResult $policy.HealthUrl $state $policy.Environment)
        publicHealth=(Get-HealthResult $policy.PublicUrl $state $policy.Environment)
    }
    Write-Host "`nSTAV INSTALACE | $Root" -ForegroundColor Cyan
    Write-Host "Prostředí: $($report.environment) | Služba: $($report.serviceName) — $($report.serviceStatus)"
    Write-Host "Aktuální verze: $($report.current) | Předchozí: $($report.previous) | Volné místo: $($report.freeDiskGiB) GiB"
    Write-Host "Lokální readiness: $($report.localHealth.ready) | Veřejné HTTPS readiness: $($report.publicHealth.ready)"
    Write-Host "Neuzavřené nasazení: $($report.interruptedDeployment) | Neuzavřený zápis nastavení: $($report.interruptedConfiguration)"
    Write-Host "Logy: $(Join-Path $Root 'logs') | Nastavení: $(Join-Path $Root 'config')"
    if ($Diagnostics) {
        try {
            $app = Read-Map (Join-Path $Root 'config/appsettings.json')
            $secrets = Read-Map (Join-Path $Root 'secrets/secrets.json')
            foreach ($section in @('Kestrel:Endpoints:Public:Certificate','DataProtection:Certificate')) {
                $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Get-Setting $app "$($section):Path"), (Get-Setting $secrets "$($section):Password"), [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
                try {
                    $report[$section] = @{ expires=$cert.NotAfter.ToUniversalTime().ToString('o'); daysRemaining=[Math]::Floor(($cert.NotAfter.ToUniversalTime()-[DateTime]::UtcNow).TotalDays) }
                    Write-Host "Certifikát $section platí do $($cert.NotAfter.ToString('yyyy-MM-dd')); zbývá $($report[$section].daysRemaining) dní."
                    if ($report[$section].daysRemaining -le 30) { Write-Host '[POZOR] Blíží se konec platnosti. HTTPS obnovte průvodcem; obnovu ochranného PFX a klíčenky řešte s IT.' -ForegroundColor Yellow }
                } finally { $cert.Dispose() }
            }
        } catch { $report.certificateCheck='Certifikát nelze načíst; ověřte cestu, heslo a oprávnění.' }
        if ($installation.Release -and -not $report.interruptedConfiguration -and -not $report.interruptedDeployment) {
            try {
                $manifest = Test-ReleaseDirectory $installation.Release
                $report.releaseIntegrity='OK'
                $preflightPath = Join-Path $Root "logs/diagnostic-preflight-$([Guid]::NewGuid().ToString('N')).json"
                $check = Invoke-ReleaseCommand (Join-Path $installation.Release 'app') $Root $policy.Environment preflight $preflightPath
                # Whitelist report fields. Never export raw config, secrets or application logs.
                $report.preflight = @{ success=[bool]$check.success; phase=$(if ($check.success) { 'SQL, SMTP, certifikáty a schéma ověřeny' } else { [string]$check.phase }) }
                Write-Host "Předběžná kontrola: $($report.preflight.success) — $($report.preflight.phase)"
            } catch { $report.preflight = @{ success=$false; phase=(Get-FriendlyError $_) } }
        } else { $report.preflight = @{ success=$false; phase='Nevykonáno: chybí dokončená instalace nebo je přerušen zápis nastavení.' } }
        $path = Join-Path $Root "logs/diagnostics-$([Guid]::NewGuid().ToString('N')).json"
        Write-JsonAtomic $report $path
        Write-Host "[OK] Diagnostický report: $path"
        Write-Host 'Report obsahuje pouze vybrané provozní údaje. Neobsahuje hesla, připojovací řetězce, obsah e-mailů ani syrové aplikační logy.'
    }
}

function Invoke-ControlledRestart([string]$Root, [switch]$Yes) {
    $installation = Get-Installation $Root
    if (-not $installation.Release) { throw 'D3PARKING: Není nainstalované žádné vydání. Zvolte Instalace.' }
    $lock = [IO.File]::Open((Join-Path $Root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-ConfigurationReady $Root
        if (Test-Path -LiteralPath (Join-Path $Root 'state/in-progress.json')) { throw 'An interrupted deployment needs recovery.' }
        $manifest = Test-ReleaseDirectory $installation.Release
        $policy = $installation.Policy
        $service = Get-CimInstance Win32_Service -Filter "Name='$($policy.ServiceName)'"
        if (-not $service -or $service.StartName -ne "NT SERVICE\$($policy.ServiceName)" -or $service.PathName -ne (Get-ServiceCommand (Join-Path $installation.Release 'app') $Root $policy.Environment)) { throw 'Service identity or service path differs from installation state.' }
        $report = Join-Path $Root "logs/restart-$([Guid]::NewGuid().ToString('N')).json"
        $check = Invoke-ReleaseCommand (Join-Path $installation.Release 'app') $Root $policy.Environment preflight $report
        if (-not $check.success -or $check.schema.pending.Count) { throw 'D3PARKING: Kontrola před restartem neprošla. Opravte konfiguraci nebo proveďte standardní update s migrací.' }
        if (-not (Confirm-Operation "Restartovat službu $($policy.ServiceName)? Dojde ke krátké odstávce a načtení uloženého nastavení." -Yes:$Yes)) { return }
        Restart-Service -Name $policy.ServiceName
        Wait-ReleaseHealthy $policy.HealthUrl $manifest $policy.Environment
        Wait-ReleaseHealthy $policy.PublicUrl $manifest $policy.Environment
        Write-Host '[OK] Restart dokončen; lokální i veřejná readiness odpovídá instalované verzi.'
    } finally { $lock.Dispose() }
}

function Invoke-UpdateWizard([string]$Root, [string]$Path, [string]$Hash, [switch]$CheckOnly, [switch]$Yes) {
    $inputRelease = Get-ReleaseInput $Path $Hash
    $opened = Open-Release $inputRelease.Path $inputRelease.Hash
    try {
        $installation = Get-Installation $Root
        $reportPath = Join-Path $Root "logs/plan-$([Guid]::NewGuid().ToString('N')).json"
        $planLock = [IO.File]::Open((Join-Path $Root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            Assert-ConfigurationReady $Root
            if (Test-Path -LiteralPath (Join-Path $Root 'state/in-progress.json')) { throw 'An interrupted deployment needs recovery.' }
            $check = Invoke-ReleaseCommand (Join-Path $opened.Directory 'app') $Root $installation.Policy.Environment preflight $reportPath
        } finally { $planLock.Dispose() }
        if (-not $check.success) { Write-Host "Protokol: $reportPath"; throw "Preflight failed at $($check.phase)" }
        Show-DeploymentPlan $installation.Policy $installation.State $opened.Manifest $check
        if ($check.unapprovedMigrations.Count) {
            Write-Host "[REVIZE DBA] SQL skript: $(Join-Path $opened.Directory 'database/migrations.sql')"
            Write-Host "Vyžadují schválení: $($check.unapprovedMigrations -join ', ')"
            if ($CheckOnly -or $Yes) { throw 'D3PARKING: Kontrola zjistila migrace vyžadující revizi DBA. Aktualizaci spusťte interaktivně bez -Yes po jejich kontrole.' }
            Write-Host 'Skript je dostupný do ukončení této operace. Po review DBA zadejte přesná ID; Enter nic neschválí a skončí.'
            $typed = Read-Host 'Schválená ID oddělená čárkou'
            $approved = @($typed.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() } | Select-Object -Unique)
            if ($approved.Count -ne $check.unapprovedMigrations.Count -or @($approved | Where-Object { $_ -notin $check.unapprovedMigrations }).Count) { throw 'D3PARKING: Nebyly výslovně schváleny všechny požadované migrace. Aplikace ani databáze se nezměnily.' }
            $lock = [IO.File]::Open((Join-Path $Root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                Assert-ConfigurationReady $Root
                if (Test-Path -LiteralPath (Join-Path $Root 'state/in-progress.json')) { throw 'An interrupted deployment needs recovery.' }
                $policy = Read-Map (Join-Path $Root 'config/deployment.json')
                $policy.ApprovedMigrations = @(@($policy.ApprovedMigrations) + $approved | Select-Object -Unique)
                Write-JsonAtomic $policy (Join-Path $Root 'config/deployment.json')
            } finally { $lock.Dispose() }
        }
    } finally { Remove-TemporaryRelease $opened.Directory }
    # Full checks run again under the deployment lock, immediately before any service/DB changes.
    Invoke-Deployment -InstallPath $Root -ReleasePath $inputRelease.Path -ExpectedSha256 $inputRelease.Hash -CheckOnly:$CheckOnly -Yes:$Yes
}

function Invoke-InstallWizard([string]$Root, [string]$Path, [string]$Hash, [switch]$Yes) {
    $inputRelease = Get-ReleaseInput $Path $Hash
    $opened = Open-Release $inputRelease.Path $inputRelease.Hash
    try {
        if (-not (Test-Path -LiteralPath $Root)) {
            Write-Host "`nPRVNÍ INSTALACE — SQL databázi a účty připraví DBA. Připravte také SMTP a HTTPS PFX."
            $url = Read-Value 'Veřejná HTTPS adresa' '' { param($v) $u=$null; [uri]::TryCreate($v,[UriKind]::Absolute,[ref]$u) -and $u.Scheme -eq 'https' -and -not $u.IsLoopback -and $u.AbsolutePath -eq '/' -and -not $u.UserInfo -and -not $u.Query -and -not $u.Fragment } 'Například https://parking.firma.cz:8443.'
            $environment = Read-Value 'Prostředí: Production / Staging' 'Production' { param($v) $v -cin @('Production','Staging') }
            $name = Read-Value 'Název Windows služby' 'D3Parking' { param($v) $v -match '^[A-Za-z][A-Za-z0-9_-]{0,60}$' }
            $port = [int](Read-Value 'Místní health port' '5081' { param($v) $n=0; [int]::TryParse($v,[ref]$n) -and $n -ge 1024 -and $n -le 65535 })
            if (-not (Confirm-Operation "Vytvořit chráněnou instalaci v $Root a zastavenou službu $name ($environment)? SQL ani firewall se v tomto kroku nemění." -Yes:$Yes)) { return }
            Initialize-Installation $Root ([uri]$url) $environment $name $port
        } else {
            $installation = Get-Installation $Root
            if ($installation.State) { throw 'D3PARKING: Aplikace je již nainstalovaná. Zvolte Aktualizace.' }
            Write-Host '[POKRAČOVÁNÍ] Načítám dříve připravenou instalaci. Existující klíčenka zůstává zachovaná.'
        }
        if (-not (Invoke-ConfigurationWizard $Root (Join-Path $opened.Directory 'app') -Yes:$Yes)) { return }
        Write-Host '[PŘED NASAZENÍM] IT musí zajistit DNS a příchozí veřejný HTTPS port. Health port se do sítě neotevírá.'
    } finally { Remove-TemporaryRelease $opened.Directory }
    Invoke-UpdateWizard $Root $inputRelease.Path $inputRelease.Hash -Yes:$Yes
}

function Show-WizardHelp {
    Write-Host @'
D3Parking — jeden provozní PowerShell skript

Spusťte PowerShell 7.4+ jako správce na Windows x64.
  .\D3Parking.ps1                              český průvodce
  .\D3Parking.ps1 -InstallPath D:\Apps\Parking   jiná instalační cesta
  .\D3Parking.ps1 -Action Update -ReleasePath C:\Releases\release.zip -CheckOnly
  .\D3Parking.ps1 -Action Update -ReleasePath C:\Releases\release.zip
  .\D3Parking.ps1 -Action Status
  .\D3Parking.ps1 -Action Diagnostics
  .\D3Parking.ps1 -Action Rollback -CheckOnly
  .\D3Parking.ps1 -Action Recover -Version 0.1.0 -CheckOnly

-Yes potvrdí plán změn pro automatizovaný Update/Restart/Rollback/Recover.
Neschvaluje rizikové migrace a neobchází integritu ani kontrolu schématu.
Hesla se zadávají skrytě v průvodci; nejsou parametry příkazové řádky.
SQL server, databáze, účty, důvěryhodné certifikáty a síť musí být připravené IT.
Skript neobnovuje SQL databázi automaticky a neinstaluje software z internetu.
'@
}

function Invoke-WizardAction([string]$Selected, [string]$Root, [string]$Path, [string]$Hash, [string]$TargetVersion, [switch]$CheckOnly, [switch]$Yes) {
    if ($CheckOnly -and $Selected -notin @('Update','Check','Rollback','Recover','Status','Diagnostics')) { throw 'D3PARKING: Tato operace nepodporuje -CheckOnly. Nic nebylo změněno; pro čtení stavu použijte Check nebo Diagnostics.' }
    switch ($Selected) {
        'Install' { if ($CheckOnly) { throw 'D3PARKING: Instalace sbírá nastavení interaktivně. Pro kontrolu připravené instalace použijte Action Check.' }; Invoke-InstallWizard $Root $Path $Hash -Yes:$Yes }
        'Update' { Invoke-UpdateWizard $Root $Path $Hash -CheckOnly:$CheckOnly -Yes:$Yes }
        'Configure' {
            $installation = Get-Installation $Root
            if (-not $installation.Release) { Invoke-InstallWizard $Root $Path $Hash -Yes:$Yes; return }
            $null = Test-ReleaseDirectory $installation.Release
            $null = Invoke-ConfigurationWizard $Root (Join-Path $installation.Release 'app') -Yes:$Yes
        }
        'Certificate' {
            $installation = Get-Installation $Root
            if (-not $installation.Release) { throw 'D3PARKING: První certifikát nastavte volbou Instalace.' }
            $null = Test-ReleaseDirectory $installation.Release
            $null = Invoke-ConfigurationWizard $Root (Join-Path $installation.Release 'app') -CertificateOnly -Yes:$Yes
        }
        'Check' { if ($Path) { Invoke-UpdateWizard $Root $Path $Hash -CheckOnly } else { Show-InstallationStatus $Root -Diagnostics } }
        'Status' { Show-InstallationStatus $Root }
        'Diagnostics' { Show-InstallationStatus $Root -Diagnostics }
        'Restart' { Invoke-ControlledRestart $Root -Yes:$Yes }
        'Rollback' { Invoke-Deployment -InstallPath $Root -Rollback -CheckOnly:$CheckOnly -Yes:$Yes }
        'Recover' {
            if (-not $TargetVersion) { $TargetVersion = Read-Value 'Verze odpovídající obnovené databázi (po ověření DBA)' '' { param($v) $v -match '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' } }
            Invoke-Recovery -Version $TargetVersion -InstallPath $Root -CheckOnly:$CheckOnly -Yes:$Yes
        }
        'RestoreConfiguration' {
            Assert-NoReparsePoint $Root
            $lock = [IO.File]::Open((Join-Path $Root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                if (-not (Test-Path -LiteralPath (Join-Path $Root 'state/config-in-progress.json'))) { Write-Host 'Žádný přerušený zápis nastavení nebyl nalezen.'; return }
                if (Confirm-Operation 'Obnovit čtyři konfigurační soubory ze zálohy přerušené operace? Služba se nerestartuje.' -Yes:$Yes) { Restore-ConfigurationFiles $Root }
            } finally { $lock.Dispose() }
        }
    }
}

function Start-D3ParkingWizard {
    if ($Action -eq 'Help') { Show-WizardHelp; return }
    if ($Action -eq 'Wizard' -and ($CheckOnly -or $Yes)) { throw 'D3PARKING: -CheckOnly a -Yes vyžadují konkrétní -Action. Interaktivní menu spusťte bez těchto přepínačů.' }
    Assert-Administrator
    if (-not [IO.Path]::IsPathFullyQualified($InstallPath)) { throw 'D3PARKING: Instalační cesta musí být absolutní.' }
    if ($Action -ne 'Wizard') {
        Invoke-WizardAction $Action $InstallPath $ReleasePath $ExpectedSha256 $Version -CheckOnly:$CheckOnly -Yes:$Yes
        return
    }
    $choices = @{ '1'='Install'; '2'='Update'; '3'='Configure'; '4'='Status'; '5'='Diagnostics'; '6'='Restart'; '7'='Certificate'; '8'='Rollback'; '9'='Recover'; '10'='RestoreConfiguration'; '11'='Check' }
    while ($true) {
        Write-Host "`nD3PARKING — SPRÁVA INSTALACE | $InstallPath" -ForegroundColor Cyan
        Write-Host '1 Instalace / dokončení instalace   2 Aktualizace ze ZIPu'
        Write-Host '3 Nastavení SQL, SMTP a adres       4 Stav služby a verze'
        Write-Host '5 Diagnostický report               6 Řízený restart'
        Write-Host '7 HTTPS certifikát                  8 Návrat předchozí verze'
        Write-Host '9 Obnova po přerušeném nasazení     10 Obnova přerušeného zápisu nastavení'
        Write-Host '11 Kontrola release bez nasazení    12 Změnit instalační cestu'
        Write-Host '0 Konec'
        $choice = Read-Host 'Vyberte číslo'
        if ($choice -eq '0') { return }
        if ($choice -eq '12') { $script:InstallPath = Read-Value 'Instalační cesta' $InstallPath { param($v) [IO.Path]::IsPathFullyQualified($v) }; continue }
        if (-not $choices.ContainsKey($choice)) { Write-Host 'Zadejte číslo z nabídky.'; continue }
        try {
            if ($choice -eq '11') { $inputRelease=Get-ReleaseInput '' ''; Invoke-UpdateWizard $InstallPath $inputRelease.Path $inputRelease.Hash -CheckOnly }
            else { Invoke-WizardAction $choices[$choice] $InstallPath $ReleasePath $ExpectedSha256 $Version }
        } catch { Write-Host ("[CHYBA] " + (Get-FriendlyError $_)) -ForegroundColor Red }
    }
}

# Dot-sourcing exposes functions for automated tests; copying this single file needs no sidecars.
if ($MyInvocation.InvocationName -ne '.') {
    try { Start-D3ParkingWizard }
    catch { Write-Host ("[CHYBA] " + (Get-FriendlyError $_)) -ForegroundColor Red; exit 1 }
}
