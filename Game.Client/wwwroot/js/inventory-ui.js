window.inventoryUi = (() => {
    const clients = new Map();
    let nextRegistration = 1;
    const focusHistory = [];
    let trapped = null;
    const visible = () => !document.hidden;
    const notify = () => { if (visible()) for (const client of clients.values()) client.invokeMethodAsync('OnVisibleAsync').catch(() => {}); };
    const focusables = element => Array.from(element.querySelectorAll('button:not(:disabled),a[href],input:not(:disabled),select:not(:disabled),[tabindex="0"]')).filter(node => node.getClientRects().length);
    const trap = event => {
        if (!trapped?.isConnected || event.key !== 'Tab') return;
        const nodes = focusables(trapped);
        if (!nodes.length) { event.preventDefault(); trapped.focus(); return; }
        const first = nodes[0], last = nodes[nodes.length - 1];
        if (event.shiftKey && (document.activeElement === first || !trapped.contains(document.activeElement))) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && (document.activeElement === last || !trapped.contains(document.activeElement))) { event.preventDefault(); first.focus(); }
    };
    document.addEventListener('keydown', trap);
    return {
        isVisible: visible,
        revealList(heading) {
            if (!heading?.isConnected) return;
            const headerBottom = document.querySelector('.top-row')?.getBoundingClientRect().bottom || 0;
            const top = window.scrollY + heading.getBoundingClientRect().top - Math.max(0, headerBottom) - 12;
            window.scrollTo({ top: Math.max(0, top), behavior: 'instant' });
            heading.focus({ preventScroll: true });
        },
        revealCategory(strip) {
            if (!strip) return;
            const selected = strip.querySelector('[aria-pressed="true"]');
            if (!selected) return;
            const rail = strip.getBoundingClientRect(), item = selected.getBoundingClientRect();
            if (item.left < rail.left) strip.scrollLeft -= rail.left - item.left;
            else if (item.right > rail.right) strip.scrollLeft += item.right - rail.right;
        },
        scrollTo(id) { const element = document.getElementById(id); if (element) { element.scrollIntoView({ block: 'center', behavior: 'auto' }); element.focus({ preventScroll: true }); } },
        // Interop recreates the JS wrapper for a .NET reference on each call.
        // Use our own registration ID so disposal removes the original wrapper.
        connect(client) { if (!clients.size) { document.addEventListener('visibilitychange', notify); window.addEventListener('focus', notify); } const id = nextRegistration++; clients.set(id, client); return id; },
        disconnect(id) { clients.delete(id); trapped = null; focusHistory.length = 0; if (!clients.size) { document.removeEventListener('visibilitychange', notify); window.removeEventListener('focus', notify); } },
        focusDialog(element, modal, openerKey) {
            if (!element) return;
            const focus = document.activeElement;
            const scope = trapped || (modal ? focus?.closest?.('.inventory-detail') || document.querySelector('.inventory-detail') : null);
            const inventoryKey = openerKey || focus?.closest?.('[data-inventory-key]')?.getAttribute('data-inventory-key');
            focusHistory.push({ focus, trap: trapped, scope, inventoryKey });
            const narrow = window.matchMedia('(max-width:760px)').matches;
            trapped = modal || narrow ? element : null;
            if (narrow && !modal) { element.setAttribute('role', 'dialog'); element.setAttribute('aria-modal', 'true'); }
            (focusables(element)[0] || element).focus({ preventScroll: true });
        },
        refocusDialog(element) { if (!element?.isConnected) return; trapped = element; (focusables(element)[0] || element).focus({ preventScroll: true }); },
        restoreFocus() {
            trapped = null;
            while (focusHistory.length) {
                const previous = focusHistory.pop();
                const previousTrap = previous.trap?.isConnected ? previous.trap : null;
                const previousScope = previous.scope?.isConnected ? previous.scope : null;
                const previousFocus = previous.focus;
                const currentOpener = previous.inventoryKey ? document.querySelector(`.inventory-item[data-inventory-key="${CSS.escape(previous.inventoryKey)}"]:not(:disabled)`) : null;
                const inventoryFallback = previous.inventoryKey ? document.querySelector('.inventory-list-heading button:not(:disabled)') : null;
                const canFocus = previousFocus?.isConnected && previousFocus !== document.body && previousFocus !== document.documentElement
                    && previousFocus.matches('button:not(:disabled),a[href],input:not(:disabled),select:not(:disabled),[tabindex]') && previousFocus.getClientRects().length;
                const target = previousTrap ? canFocus && previousTrap.contains(previousFocus) ? previousFocus : focusables(previousTrap)[0] || previousTrap
                    : canFocus ? previousFocus : previousScope ? focusables(previousScope)[0] || previousScope : currentOpener || inventoryFallback;
                if (target) { trapped = previousTrap; target.focus({ preventScroll: true }); break; }
            }
        }
    };
})();
