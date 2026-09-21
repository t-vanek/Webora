# Uvolňování rezervací

V administraci nastavení parkování je výběr **Kdy lze uvolnit rezervaci**.
Platí pro vlastní rezervace rezidentů i nerezidentů a pro uvolňování přidělených
rezidentních dnů.

- Pouze do předchozího dne: hranicí je půlnoc na začátku dne rezervace.
- I v den rezervace, před začátkem: lze zrušit dnešní rezervaci, dokud nezačne.
- I během rezervace: lze uvolnit také zbývající část probíhající rezervace.

Volitelná dodatečná uzávěrka určuje čas předchozího dne (například 18:00) nebo
minimální předstih v minutách (například 120). Platí dřívější hranice režimu
a uzávěrky. Uvolnění musí být provedeno před touto hranicí, přesně v čase
uzávěrky je již odmítnuto. Pro možnost odjet předčasně použijte třetí režim bez
dodatečné uzávěrky.

Celodenní rezervace a rezidentní dny začínají o půlnoci. Režim „před začátkem“
proto neumožní uvolnění dnešního rezidentního dne. U časové rezervace například
od 10:00 to možné je. U rezervace se zobrazuje konkrétní termín nebo informace
o možnosti ukončení během parkování. Náhled znovu načte aktuální pravidla.

Hranicí je půlnoc v časovém pásmu parkoviště. Povolení vytvářet nové rezervace na
dnešek je samostatné nastavení. Vrácení kreditů a poukázek nadále závisí na
pravidlech včasného zrušení; povolené uvolnění automaticky neznamená nárok na refundaci.

Server ověřuje pravidlo při potvrzení i při náhledu. Rezidentní rozsah začínající
nepovoleným dnem odmítne celý, automatický plán takový den přeskočí. Rezervace
alternativního místa nesmí zákaz obejít automatickým uvolněním dnešního vlastního místa.
Již existující rezervace a dříve provedená uvolnění změna nastavení neruší.

Migrace `AddSameDayReleaseRule` a `AddReleaseModesAndDeadlines` zachovávají
dosavadní chování: původní zapnutý přepínač znamená „i během rezervace“, vypnutý
„do předchozího dne“. Dodatečná uzávěrka je vypnutá. Nezvolený nový režim přebírá
původní přepínač; po uložení v administraci platí explicitní režim.
V produkci je nutné migrace aplikovat před spuštěním nové verze aplikace.
