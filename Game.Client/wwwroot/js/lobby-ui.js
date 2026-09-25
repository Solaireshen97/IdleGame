(() => {
    const observers = new WeakMap();
    window.lobbyUi = {
        observe(root, list, reference, view) {
            if (!(root instanceof HTMLElement) || !(list instanceof HTMLElement)) return;
            const previous = observers.get(root);
            if (previous?.list === list && previous.view === view) return;
            previous?.disconnect();
            let frame = 0, lastCapacity = 0, disposed = false;
            const observer = new ResizeObserver(() => {
                cancelAnimationFrame(frame);
                frame = requestAnimationFrame(() => {
                    if (disposed || !list.isConnected || !list.clientHeight) return;
                    const style = getComputedStyle(list);
                    const columns = parseInt(style.getPropertyValue('--lobby-columns'), 10) || 1;
                    const rowHeight = parseFloat(style.getPropertyValue('--lobby-row-height')) || 132;
                    const gap = parseFloat(style.rowGap) || 0;
                    const usable = list.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
                    const rows = Math.max(1, Math.min(6, Math.floor((usable + gap) / (rowHeight + gap))));
                    const capacity = rows * columns;
                    if (capacity === lastCapacity) return;
                    lastCapacity = capacity;
                    reference.invokeMethodAsync('UpdateLobbyCapacity', view, capacity).catch(() => { if (!disposed) lastCapacity = 0; });
                });
            });
            const disconnect = () => { disposed = true; observer.disconnect(); cancelAnimationFrame(frame); };
            observers.set(root, { list, view, disconnect });
            observer.observe(list);
        },
        disconnect(root) { observers.get(root)?.disconnect(); observers.delete(root); },
        focus(id) { document.getElementById(id)?.focus({ preventScroll: true }); },
        trapFocus(dialog) {
            if (!(dialog instanceof HTMLElement) || dialog.dataset.lobbyFocusTrap) return;
            dialog.dataset.lobbyFocusTrap = 'true';
            dialog.addEventListener('keydown', event => {
                if (event.key !== 'Tab') return;
                const controls = [...dialog.querySelectorAll('button, a[href], input, select, [tabindex="0"]')]
                    .filter(item => !item.disabled && item.getClientRects().length);
                const first = controls[0], last = controls.at(-1);
                if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus({ preventScroll: true }); }
                else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus({ preventScroll: true }); }
            });
        }
    };
})();
