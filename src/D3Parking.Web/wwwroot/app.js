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

// Odběr parkovacího kalendáře: po vytvoření soukromého feedu předá webcal URL rovnou
// systémové kalendářové aplikaci. Když zařízení protokol webcal nezná, komponenta stále
// nabídne běžný odkaz i zkopírování URL jako záložní cestu.
window.d3parkingCalendar = {
    openSubscription(url) {
        if (typeof url === 'string' && url.startsWith('webcal://')) {
            window.location.assign(url);
        }
    },
};

// Dialog callers use stable trigger ids so closing a modal returns keyboard users to the
// exact control that opened it. requestAnimationFrame waits until Blazor has removed the modal.
window.d3parkingFocus = {
    byId(id) {
        if (typeof id !== 'string') {
            return;
        }

        window.requestAnimationFrame(() => document.getElementById(id)?.focus());
    },
};

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


// Native modality also covers focusable controls inside Fluent shadow roots. Restore the
// opener when Blazor removes the conditional dialog, without a server-side Dispose interop.
let adminDialogTrigger;
document.addEventListener('click', event => {
    // Opening can first disable the initiating Fluent button while the server loads data.
    // Capture its actual shadow control before that render removes keyboard focus.
    const path = event.composedPath();
    adminDialogTrigger = path.find(node => node instanceof HTMLElement && node.matches('fluent-button'))
        ?? path.find(node => node instanceof HTMLElement && node.matches('button, a[href], [role="button"]'));
}, true);
window.adminDialog = {
    open(modal, content) {
        let opener = document.activeElement;
        while (opener?.shadowRoot?.activeElement) opener = opener.shadowRoot.activeElement;
        if (adminDialogTrigger?.isConnected && !modal.contains(adminDialogTrigger)) opener = adminDialogTrigger;
        modal.addEventListener('cancel', event => event.preventDefault());
        modal.addEventListener('keydown', event => {
            if (event.key !== 'Tab') return;
            const stops = [];
            const collect = root => {
                for (const element of root.children) {
                    if (element.hasAttribute('disabled') || element.hasAttribute('inert')
                        || element.matches(':disabled') || getComputedStyle(element).visibility === 'hidden') continue;
                    if (element.tabIndex >= 0 && element.getClientRects().length) stops.push(element);
                    if (element.shadowRoot) collect(element.shadowRoot);
                    collect(element);
                }
            };
            collect(content);
            let active = document.activeElement;
            while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement;
            const first = stops[0];
            const last = stops[stops.length - 1];
            // Native Tab still permits leaving for browser chrome at the boundaries.
            if (!first || active === content || (event.shiftKey ? active === first : active === last)) {
                event.preventDefault();
                (event.shiftKey ? last : first)?.focus();
                if (!first) content.focus();
            }
        });
        modal.showModal();
        content.focus();
        const observer = new MutationObserver(() => {
            if (modal.isConnected) return;
            observer.disconnect();
            modal.close();
            if (!opener?.isConnected || document.querySelector('dialog[open]')) return;
            // Fluent may re-enable its shadow button after the Blazor removal batch.
            const restoreObserver = new MutationObserver(() => requestAnimationFrame(restore));
            const expiry = setTimeout(() => restoreObserver.disconnect(), 5000);
            function restore() {
                const target = opener.shadowRoot?.querySelector('button, input, [tabindex="0"]') ?? opener;
                const current = document.activeElement;
                if (!opener.isConnected || document.querySelector('dialog[open]')
                    || (current !== document.body && current !== opener && !opener.contains(current))) {
                    restoreObserver.disconnect();
                    clearTimeout(expiry);
                } else if (!opener.hasAttribute('disabled') && !target.disabled) {
                    restoreObserver.disconnect();
                    clearTimeout(expiry);
                    target.focus();
                }
            }
            restoreObserver.observe(opener, { attributes: true, childList: true, subtree: true });
            if (opener.shadowRoot) restoreObserver.observe(opener.shadowRoot, { attributes: true, childList: true, subtree: true });
            requestAnimationFrame(restore);
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }
};


// Settings is an InteractiveServer island in an SSR router. NavigationLock handles
// document unloads but not enhanced-navigation links outside the island.
window.settingsDraftGuard = {
    update(root, dirty, busy, message) {
        if (!root._draftGuard) {
            const state = { dirty, busy, message };
            // Protect a keystroke immediately, before its InteractiveServer round trip.
            const input = event => {
                if (event.composedPath().some(node => node instanceof HTMLElement
                    && node.matches('input, textarea, select, fluent-switch, fluent-checkbox, fluent-select')))
                    state.dirty = true;
            };
            const unload = event => {
                if (root.isConnected && (state.dirty || state.busy)) {
                    event.preventDefault();
                    event.returnValue = '';
                }
            };
            root.addEventListener('input', input, true);
            root.addEventListener('change', input, true);
            window.addEventListener('beforeunload', unload);
            const confirm = () => !state.busy && (!state.dirty || window.confirm(state.message));
            const click = event => {
                const link = event.composedPath().find(node => node instanceof HTMLAnchorElement);
                if (!root.isConnected || event.defaultPrevented || event.button !== 0
                    || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey
                    || !link || link.hasAttribute('download') || (link.target && link.target !== '_self')) return;
                const url = new URL(link.href, location.href);
                if (url.origin !== location.origin || link.closest('[data-enhance-nav="false"]')) return;
                if (url.origin === location.origin && url.pathname === location.pathname
                    && url.search === location.search && url.hash) return;
                if (!confirm()) {
                    event.preventDefault();
                    event.stopImmediatePropagation();
                }
            };
            // Where supported, cancellation happens before enhanced back/forward navigation.
            const navigate = event => {
                if (root.isConnected && event.navigationType === 'traverse'
                    && event.destination.sameDocument && event.cancelable && !confirm()) event.preventDefault();
            };
            document.addEventListener('click', click, true);
            window.navigation?.addEventListener('navigate', navigate);
            const observer = new MutationObserver(() => {
                if (root.isConnected) return;
                document.removeEventListener('click', click, true);
                root.removeEventListener('input', input, true);
                root.removeEventListener('change', input, true);
                window.removeEventListener('beforeunload', unload);
                window.navigation?.removeEventListener('navigate', navigate);
                observer.disconnect();
            });
            observer.observe(document.body, { childList: true, subtree: true });
            root._draftGuard = state;
        }
        Object.assign(root._draftGuard, { dirty, busy, message });
    }
};
