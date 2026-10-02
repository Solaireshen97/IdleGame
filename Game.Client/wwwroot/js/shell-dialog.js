const bindings = new Map();
let nextRegistration = 0;
const opened = new Set();
const openers = new WeakMap();
let previousOverflow;

function release(dialog) {
    if (!opened.delete(dialog) || opened.size) return;
    document.documentElement.style.overflow = previousOverflow;
}

export function connect(dialog, reference) {
    if (!(dialog instanceof HTMLDialogElement)) return 0;
    const existing = [...bindings].find(([, binding]) => binding.dialog === dialog);
    if (existing) return existing[0];
    const outside = event => {
        const rect = dialog.getBoundingClientRect();
        return event.target === dialog && (event.clientX < rect.left || event.clientX > rect.right ||
            event.clientY < rect.top || event.clientY > rect.bottom);
    };
    let pressedOutside = false;
    const down = event => { pressedOutside = outside(event); };
    const click = event => { if (pressedOutside && outside(event)) dialog.close(); pressedOutside = false; };
    const close = () => {
        release(dialog);
        const opener = openers.get(dialog);
        if (opener?.id && !opener.isConnected) document.getElementById(opener.id)?.focus({ preventScroll: true });
        openers.delete(dialog);
        reference.invokeMethodAsync('OnNativeClose').catch(() => {});
    };
    const keydown = event => {
        if (event.key !== 'Tab' && event.key !== 'Escape') return;
        // Nested dialogs own their keyboard interaction, including ancestor focus traps.
        event.stopPropagation();
        if (event.key === 'Escape') return;
        const controls = [...dialog.querySelectorAll('button, a[href], input, select, textarea, [tabindex]')]
            .filter(element => !element.disabled && element.tabIndex >= 0 && !element.closest('[inert]') && element.getClientRects().length);
        const first = controls[0], last = controls.at(-1);
        if (!first || (event.shiftKey ? document.activeElement === first : document.activeElement === last)) {
            event.preventDefault();
            (event.shiftKey ? last : first)?.focus({ preventScroll: true });
        }
    };
    dialog.addEventListener('pointerdown', down);
    dialog.addEventListener('click', click);
    dialog.addEventListener('close', close);
    dialog.addEventListener('keydown', keydown);
    const registration = ++nextRegistration;
    bindings.set(registration, { dialog, down, click, close, keydown });
    return registration;
}

export function setOpen(dialog, value) {
    if (!(dialog instanceof HTMLDialogElement) || !dialog.isConnected) return;
    if (value && !dialog.open) {
        if (!opened.size) previousOverflow = document.documentElement.style.overflow;
        opened.add(dialog);
        openers.set(dialog, document.activeElement);
        document.documentElement.style.overflow = 'hidden';
        dialog.showModal();
    } else if (!value && dialog.open) {
        dialog.close();
    }
}

export function disconnect(registration) {
    const handlers = bindings.get(registration);
    if (!handlers) return;
    const { dialog } = handlers;
    dialog.removeEventListener('pointerdown', handlers.down);
    dialog.removeEventListener('click', handlers.click);
    dialog.removeEventListener('close', handlers.close);
    dialog.removeEventListener('keydown', handlers.keydown);
    bindings.delete(registration);
    if (dialog.open) dialog.close();
    release(dialog);
    openers.delete(dialog);
}
