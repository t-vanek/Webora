// Progresivní vylepšení auth formulářů (login / registrace). Stránky jsou static SSR, takže
// tu není žádný Blazor interop — jen delegované posluchače na documentu, které přežijí i
// enhanced navigation (skript se načte jednou, DOM se může libovolně měnit pod ním).

// Přepínač viditelnosti hesla: tlačítko s data-pw-toggle uvnitř .auth-field__box přepne typ
// sousedního inputu mezi password/text. Popisek pro čtečky nese data-label-show/hide.
document.addEventListener('click', (e) => {
    const btn = e.target.closest('[data-pw-toggle]');
    if (!btn) {
        return;
    }

    const input = btn.closest('.auth-field__box')?.querySelector('input');
    if (!input) {
        return;
    }

    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    btn.classList.toggle('is-on', show);
    btn.setAttribute('aria-pressed', String(show));
    const label = show ? btn.dataset.labelHide : btn.dataset.labelShow;
    if (label) {
        btn.setAttribute('aria-label', label);
    }
});

// Ukazatel síly hesla: input s data-strength plní sourozenecký .auth-strength (4 segmenty)
// hrubým skóre 0–4. Čistě orientační vodítko — skutečná pravidla vynucuje server.
document.addEventListener('input', (e) => {
    const input = e.target;
    if (!(input instanceof HTMLInputElement) || !input.hasAttribute('data-strength')) {
        return;
    }

    const meter = input.closest('.auth-field')?.querySelector('.auth-strength');
    if (!meter) {
        return;
    }

    const value = input.value;
    let score = 0;
    if (value.length >= 8) score++;
    if (value.length >= 12) score++;
    if (/[a-z]/.test(value) && /[A-Z]/.test(value)) score++;
    if (/\d/.test(value)) score++;
    if (/[^A-Za-z0-9]/.test(value)) score++;

    meter.dataset.score = value ? String(Math.max(1, Math.min(4, score))) : '0';
});

// Registrace service workeru (PWA): instalace na plochu a offline fallback stránka.
// Jen v zabezpečeném kontextu (https / localhost); opakovaná registrace je idempotentní.
if ('serviceWorker' in navigator && window.isSecureContext) {
    window.addEventListener('load', () => {
        navigator.serviceWorker.register('/service-worker.js').catch((err) => {
            console.warn('Registrace service workeru selhala:', err);
        });
    });
}

// Odhlášení ruší push subscription tohoto prohlížeče (formulář s data-push-unsubscribe):
// sdílený počítač nesmí dál zobrazovat notifikace odhlášeného uživatele. Odeslání formuláře
// se krátce pozdrží, aby prohlížeč odhlášku stihl dokončit; řádek na serveru se uklidí sám
// při příštím pokusu o doručení (push služba vrátí 404/410 a záznam se smaže).
document.addEventListener('submit', (e) => {
    const form = e.target;
    if (!(form instanceof HTMLFormElement)
        || !form.hasAttribute('data-push-unsubscribe')
        || form.dataset.pushUnsubscribed === '1'
        || !('serviceWorker' in navigator) || !('PushManager' in window)) {
        return;
    }

    e.preventDefault();
    const resubmit = () => {
        form.dataset.pushUnsubscribed = '1';
        form.requestSubmit();
    };

    const unsubscribe = (async () => {
        const registration = await navigator.serviceWorker.getRegistration();
        const subscription = await registration?.pushManager.getSubscription();
        await subscription?.unsubscribe();
    })().catch(() => undefined);

    // Odhláška je best-effort — přihlašovací UX má přednost, takže se čeká nejvýše chvilku.
    const timeout = new Promise((resolve) => setTimeout(resolve, 400));
    Promise.race([unsubscribe, timeout]).then(resubmit, resubmit);
});

// Nekonečné rolování: patka dlouhého seznamu se dá pozorovat a jakmile se přiblíží do výřezu,
// zavolá LoadMore() na Blazor komponentě. IntersectionObserver počítá průnik s výřezem, ale
// respektuje ořez všech rolujících předků — takže to funguje stejně uvnitř .lot-split__board
// (vlastní scrollport na širokém okně) jako když roluje celá stránka na mobilu.
//
// Pozorovaným prvkem je záměrně tlačítko, ne prázdný div: myší se další dávka načte sama,
// klávesnicí se na patku dá dotabovat a zmáčknout ji. Načítání bez klikatelné patky by bylo
// pro klávesnici slepá ulička.
window.d3parkingInfiniteScroll = {
    _observers: new Map(),

    observe(id, element, dotNetRef) {
        this.disconnect(id);
        if (!element || !('IntersectionObserver' in window)) {
            return;
        }

        // Předstih, aby další dávka doběhla ještě než patka doopravdy dojede do výřezu.
        const observer = new IntersectionObserver((entries) => {
            if (entries.some((entry) => entry.isIntersecting)) {
                dotNetRef.invokeMethodAsync('LoadMore').catch(() => undefined);
            }
        }, { rootMargin: '300px' });

        observer.observe(element);
        this._observers.set(id, observer);
    },

    disconnect(id) {
        const observer = this._observers.get(id);
        if (observer) {
            observer.disconnect();
            this._observers.delete(id);
        }
    },
};
