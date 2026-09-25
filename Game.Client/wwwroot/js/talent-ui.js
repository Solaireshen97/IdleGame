(() => {
    const observers = new WeakMap();
    window.talentUi = {
        observe(map, reference) {
            if (!(map instanceof HTMLElement) || observers.has(map)) return;
            let frame = 0, last = 0, disposed = false;
            const observer = new ResizeObserver(() => {
                cancelAnimationFrame(frame);
                frame = requestAnimationFrame(() => {
                    if (disposed || !map.isConnected || !map.clientHeight) return;
                    const capacity = Math.max(1, Math.min(10, Math.floor(map.clientHeight / 64)));
                    if (capacity === last) return;
                    last = capacity;
                    reference.invokeMethodAsync('UpdateTierCapacity', capacity).catch(() => {});
                });
            });
            observers.set(map, () => { disposed = true; cancelAnimationFrame(frame); observer.disconnect(); });
            observer.observe(map);
        },
        disconnect(map) { observers.get(map)?.(); observers.delete(map); },
        focus(id) { document.getElementById(id)?.focus({ preventScroll: true }); },
        openDialog(dialog) {
            if (!(dialog instanceof HTMLElement)) return;
            dialog.focus({ preventScroll: true });
            if (dialog.dataset.talentTrap) return;
            dialog.dataset.talentTrap = 'true';
            dialog.addEventListener('keydown', event => {
                if (event.key !== 'Tab') return;
                const controls = [...dialog.querySelectorAll('button, a[href], input, select, [tabindex="0"]')]
                    .filter(item => !item.disabled && item.getClientRects().length);
                const first = controls[0], last = controls.at(-1);
                if (!first) { event.preventDefault(); return; }
                if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog)) {
                    event.preventDefault(); last.focus({ preventScroll: true });
                } else if (!event.shiftKey && (document.activeElement === last || document.activeElement === dialog)) {
                    event.preventDefault(); first.focus({ preventScroll: true });
                }
            });
        }
    };
})();
