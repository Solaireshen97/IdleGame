window.formationUi = {
    revealSelection(strip) {
        if (!strip) return;
        const selected = strip.querySelector('[aria-pressed="true"]');
        if (!selected) { strip.scrollLeft = 0; return; }
        const rail = strip.getBoundingClientRect();
        const card = selected.getBoundingClientRect();
        // Align to a card's snap point, so scroll snapping cannot hide the selection again.
        if (card.left < rail.left || card.right > rail.right) strip.scrollLeft += card.left - rail.left;
    }
};
