/*
    D3Parking — jednorázová oprava časů zadaných před přechodem na místní čas.

    PROČ
    Do opravy vyhodnocování místního času se uživatelem vybraný wall-clock ukládal tak, jako by
    šlo o UTC: výběr "13:00" skončil v databázi jako 13:00+00:00, přestože uživatel mínil 13:00
    místního času (tedy 11:00 UTC). Skript ty řádky reinterpretuje — číslice wall-clocku bere jako
    místní čas a dopočítá k nim správný offset.

    CO MĚNÍ
      1. Reservations.StartUtc / EndUtc           — okno rezervace vybrané uživatelem
      2. QueueEntries.StartUtc / EndUtc           — okno požadované ve frontě
      3. NotificationPreferences.MutedUntilUtc    — "ztlumit do zítřka" (místní půlnoc)
      4. Reservations.IsOffPeak                   — přepočet u dosud neuzavřených rezervací

    CO ZÁMĚRNĚ NEMĚNÍ
    Sloupce jako CreatedAtUtc, CheckedInAtUtc, ReleasedAtUtc, CompletedAtUtc, OccurredAtUtc nebo
    QueueBannedUntilUtc vznikly z reálného okamžiku (TimeProvider.GetUtcNow), takže vždycky byly
    správné. Posunout je by data naopak rozbilo.

    Nemění ani datumové sloupce (SpotReleases.Date, ParkingSpots.LastResidentReminderDate,
    LastAutoShareNoticeDate). Ty vznikly z UTC data, takže u akcí těsně kolem půlnoci mohou být
    o den vedle — ze samotného data už ale původní úmysl dopočítat nelze. Pokud na tom záleží,
    je nutná ruční revize.

    Body přiznané v minulosti se nepřepočítávají. Skript opravuje uložený čas, ne historii výplat.

    BEZPEČNOST
    Opravují se jen řádky s nulovým offsetem, takže záznamy vzniklé už po opravě zůstanou nedotčené
    a opakované spuštění nic neudělá. POZOR: tenhle rozlišovací znak funguje jen u zóny, která
    nikdy není na UTC+0. Pro Prahu (+01:00 / +02:00) platí; pro UTC nebo Londýn ne — tam by skript
    nešel použít bez jiného rozlišení.

    POUŽITÍ
    @TimeZone musí být WINDOWS jméno zóny, ne IANA — SQL Server IANA jména nepřijímá.
    Praha = 'Central European Standard Time' (odpovídá 'Europe/Prague' v nastavení webu).
    Seznam: SELECT name FROM sys.time_zone_info;

    Nejdřív spusť nanečisto (@Apply = 0), zkontroluj výpis, teprve pak @Apply = 1.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @TimeZone sysname = N'Central European Standard Time';
DECLARE @Apply    bit     = 0;   -- 0 = jen výpis (změny se vrátí zpět), 1 = provést

BEGIN TRANSACTION;

SELECT N'Rezervace k opravě'   AS Polozka, COUNT(*) AS Pocet FROM Reservations           WHERE DATEPART(tz, StartUtc) = 0
UNION ALL
SELECT N'Fronta k opravě',            COUNT(*) FROM QueueEntries            WHERE DATEPART(tz, StartUtc) = 0
UNION ALL
SELECT N'Ztlumení k opravě',          COUNT(*) FROM NotificationPreferences WHERE MutedUntilUtc IS NOT NULL AND DATEPART(tz, MutedUntilUtc) = 0;

-- Ukázka prvních deseti rezervací: co v databázi je a co z toho bude.
SELECT TOP 10
    Id,
    StartUtc                                                   AS Pred,
    CAST(StartUtc AS datetime2) AT TIME ZONE @TimeZone         AS Po
FROM Reservations
WHERE DATEPART(tz, StartUtc) = 0
ORDER BY StartUtc;

/* 1. + 2. + 3. — reinterpretace wall-clocku v místní zóně.
   CAST na datetime2 zahodí (nulový) offset a ponechá číslice; AT TIME ZONE je pak přečte
   jako místní čas a dopočítá offset platný pro daný okamžik, takže letní čas sedí. */

UPDATE Reservations SET StartUtc = CAST(StartUtc AS datetime2) AT TIME ZONE @TimeZone
WHERE DATEPART(tz, StartUtc) = 0;

UPDATE Reservations SET EndUtc = CAST(EndUtc AS datetime2) AT TIME ZONE @TimeZone
WHERE DATEPART(tz, EndUtc) = 0;

UPDATE QueueEntries SET StartUtc = CAST(StartUtc AS datetime2) AT TIME ZONE @TimeZone
WHERE DATEPART(tz, StartUtc) = 0;

UPDATE QueueEntries SET EndUtc = CAST(EndUtc AS datetime2) AT TIME ZONE @TimeZone
WHERE DATEPART(tz, EndUtc) = 0;

UPDATE NotificationPreferences SET MutedUntilUtc = CAST(MutedUntilUtc AS datetime2) AT TIME ZONE @TimeZone
WHERE MutedUntilUtc IS NOT NULL AND DATEPART(tz, MutedUntilUtc) = 0;

/* 4. Přepočet příznaku mimo špičku u rezervací, které ještě něco vyplatí. Špička je místní
   wall-clock rozsah, takže se porovnává čas dne v cílové zóně. */
UPDATE r
SET IsOffPeak = CASE
        WHEN CAST(r.StartUtc AT TIME ZONE @TimeZone AS time) <  s.PeakStart
          OR CAST(r.StartUtc AT TIME ZONE @TimeZone AS time) >= s.PeakEnd
        THEN 1 ELSE 0 END
FROM Reservations r
CROSS JOIN ParkingSettings s
WHERE r.Status IN (N'Reserved', N'CheckedIn');

SELECT N'Rezervace se zbylým nulovým offsetem' AS Kontrola, COUNT(*) AS Pocet
FROM Reservations WHERE DATEPART(tz, StartUtc) = 0;

IF @Apply = 1
BEGIN
    COMMIT TRANSACTION;
    PRINT N'Změny byly potvrzeny.';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT N'Zkušební běh — nic se neuložilo. Pro provedení nastav @Apply = 1.';
END
