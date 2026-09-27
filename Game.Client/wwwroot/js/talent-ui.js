(() => {
    const maps = new WeakMap();
    window.talentUi = {
        attach(map) {
            if (!(map instanceof HTMLElement) || maps.has(map)) return;
            let drag = null, suppressClick = false;
            const finish = event => {
                if (!drag || (event && event.pointerId !== drag.pointerId)) return;
                const { pointerId, moved } = drag;
                drag = null;
                suppressClick = moved && event?.type === 'pointerup';
                map.classList.remove('is-dragging');
                if (map.hasPointerCapture(pointerId)) map.releasePointerCapture(pointerId);
            };
            const down = event => {
                // Touch scrolling keeps the browser's native momentum and tap handling.
                if (event.pointerType !== 'mouse' || event.button !== 0 || !event.isPrimary) return;
                suppressClick = false;
                if (map.scrollHeight <= map.clientHeight) return;
                const bounds = map.getBoundingClientRect();
                if (event.clientX >= bounds.left + map.clientLeft + map.clientWidth) return;
                map.scrollTo({ top: map.scrollTop, behavior: 'instant' });
                drag = { pointerId: event.pointerId, startY: event.clientY, startTop: map.scrollTop, moved: false };
            };
            const move = event => {
                if (!drag || event.pointerId !== drag.pointerId) return;
                if ((event.buttons & 1) === 0) { finish(event); return; }
                const distance = event.clientY - drag.startY;
                if (!drag.moved && Math.abs(distance) < 6) return;
                if (!drag.moved) {
                    drag.moved = true;
                    map.setPointerCapture(event.pointerId);
                    map.classList.add('is-dragging');
                }
                map.scrollTop = drag.startTop - distance;
                event.preventDefault();
            };
            const click = event => {
                if (suppressClick && event.detail !== 0) {
                    event.preventDefault();
                    event.stopImmediatePropagation();
                }
                suppressClick = false;
            };
            const preventDrag = event => event.preventDefault();
            map.addEventListener('pointerdown', down);
            map.addEventListener('pointermove', move);
            map.addEventListener('pointercancel', finish);
            map.addEventListener('lostpointercapture', finish);
            map.addEventListener('click', click, true);
            map.addEventListener('dragstart', preventDrag);
            window.addEventListener('pointerup', finish);
            maps.set(map, () => {
                finish();
                map.removeEventListener('pointerdown', down);
                map.removeEventListener('pointermove', move);
                map.removeEventListener('pointercancel', finish);
                map.removeEventListener('lostpointercapture', finish);
                map.removeEventListener('click', click, true);
                map.removeEventListener('dragstart', preventDrag);
                window.removeEventListener('pointerup', finish);
            });
        },
        disconnect(map) { maps.get(map)?.(); maps.delete(map); },
        reveal(map, id) {
            const node = document.getElementById(id);
            if (!(map instanceof HTMLElement) || !node || !map.contains(node)) return;
            const viewport = map.getBoundingClientRect(), bounds = node.getBoundingClientRect();
            const top = viewport.top + map.clientTop + 12;
            const bottom = viewport.top + map.clientTop + map.clientHeight - 12;
            const distance = bounds.top < top ? bounds.top - top : bounds.bottom > bottom ? bounds.bottom - bottom : 0;
            if (distance) map.scrollTo({ top: map.scrollTop + distance, behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' });
        },
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
