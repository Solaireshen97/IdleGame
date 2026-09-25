(() => {
    const inventories = new WeakMap();
    window.weaponBoardUi = {
        focusTile(id) {
            const tile = document.getElementById(id);
            if (tile instanceof HTMLElement) tile.focus({ preventScroll: true });
        },
        observeInventory(board, list, reference) {
            if (!(board instanceof HTMLElement) || !(list instanceof HTMLElement)) return;
            const previous = inventories.get(board);
            if (previous?.list === list) return;
            previous?.disconnect();
            let frame = 0;
            let lastCapacity = 0;
            let disposed = false;
            const measure = () => {
                if (disposed || !list.isConnected || list.clientHeight === 0) return;
                const style = getComputedStyle(list);
                const columns = Number.parseInt(style.getPropertyValue('--inventory-columns'), 10) || 3;
                const rowHeight = Number.parseFloat(style.getPropertyValue('--inventory-row-height')) || 116;
                const gap = Number.parseFloat(style.rowGap) || 0;
                const usable = list.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
                const rows = Math.max(1, Math.min(4, Math.floor((usable + gap) / (rowHeight + gap))));
                const capacity = columns * rows;
                if (lastCapacity === capacity) return;
                lastCapacity = capacity;
                reference.invokeMethodAsync('UpdateInventoryCapacity', capacity).catch(() => {
                    if (!disposed) lastCapacity = 0;
                });
            };
            const observer = new ResizeObserver(() => {
                cancelAnimationFrame(frame);
                frame = requestAnimationFrame(measure);
            });
            const disconnect = () => { disposed = true; observer.disconnect(); cancelAnimationFrame(frame); };
            inventories.set(board, { list, disconnect });
            observer.observe(list);
        },
        disconnectInventory(board) {
            inventories.get(board)?.disconnect();
            inventories.delete(board);
        },
        trapDialogFocus(dialog) {
            if (!(dialog instanceof HTMLElement) || dialog.dataset.focusTrap) return;
            dialog.dataset.focusTrap = 'true';
            dialog.addEventListener('keydown', event => {
                if (event.key !== 'Tab') return;
                const items = [...dialog.querySelectorAll('button, a[href], input, select, [tabindex="0"]')]
                    .filter(item => !item.disabled && item.getClientRects().length);
                const first = items[0], last = items.at(-1);
                if (event.shiftKey && document.activeElement === first) {
                    event.preventDefault(); last?.focus({ preventScroll: true });
                } else if (!event.shiftKey && document.activeElement === last) {
                    event.preventDefault(); first?.focus({ preventScroll: true });
                }
            });
        }
    };
})();
