(() => {
    const viewportSelector = "[data-drag-scroll]";
    let dragState = null;

    const getRail = viewport => viewport?.closest(".horizontal-rail");

    const updateRail = viewport => {
        const rail = getRail(viewport);
        if (!rail) return;

        const hasOverflow = viewport.scrollWidth > viewport.clientWidth + 2;
        const previous = rail.querySelector('[data-scroll-step="-1"]');
        const next = rail.querySelector('[data-scroll-step="1"]');
        rail.classList.toggle("horizontal-rail--overflowing", hasOverflow);
        if (previous) previous.disabled = !hasOverflow || viewport.scrollLeft <= 1;
        if (next) next.disabled = !hasOverflow || viewport.scrollLeft + viewport.clientWidth >= viewport.scrollWidth - 1;
    };

    const updateAll = () => document.querySelectorAll(viewportSelector).forEach(updateRail);

    document.addEventListener("click", event => {
        const control = event.target.closest("[data-scroll-step]");
        if (control) {
            const viewport = getRail(control)?.querySelector(viewportSelector);
            if (!viewport) return;
            const direction = Number(control.dataset.scrollStep) || 1;
            viewport.scrollBy({ left: direction * Math.max(180, viewport.clientWidth * .78), behavior: "smooth" });
            return;
        }

        const viewport = event.target.closest(viewportSelector);
        if (viewport?.dataset.dragSuppressClick === "true") {
            delete viewport.dataset.dragSuppressClick;
            event.preventDefault();
            event.stopPropagation();
        }
    }, true);

    document.addEventListener("pointerdown", event => {
        if (event.pointerType !== "mouse" || event.button !== 0 || event.target.closest("[data-scroll-step]")) return;
        const viewport = event.target.closest(viewportSelector);
        if (!viewport || viewport.scrollWidth <= viewport.clientWidth + 2) return;
        dragState = {
            viewport,
            pointerId: event.pointerId,
            startX: event.clientX,
            startScrollLeft: viewport.scrollLeft,
            moved: false
        };
        viewport.setPointerCapture?.(event.pointerId);
    });

    document.addEventListener("pointermove", event => {
        if (!dragState || dragState.pointerId !== event.pointerId) return;
        const distance = event.clientX - dragState.startX;
        if (!dragState.moved && Math.abs(distance) < 5) return;
        dragState.moved = true;
        dragState.viewport.classList.add("is-dragging");
        dragState.viewport.scrollLeft = dragState.startScrollLeft - distance;
        event.preventDefault();
    }, { passive: false });

    const finishDrag = event => {
        if (!dragState || dragState.pointerId !== event.pointerId) return;
        const { viewport, moved } = dragState;
        if (viewport.hasPointerCapture?.(event.pointerId)) viewport.releasePointerCapture(event.pointerId);
        viewport.classList.remove("is-dragging");
        if (moved) {
            viewport.dataset.dragSuppressClick = "true";
            window.setTimeout(() => delete viewport.dataset.dragSuppressClick, 0);
        }
        dragState = null;
        updateRail(viewport);
    };

    document.addEventListener("pointerup", finishDrag);
    document.addEventListener("pointercancel", finishDrag);
    document.addEventListener("dragstart", event => {
        if (event.target.closest(viewportSelector)) event.preventDefault();
    });

    document.addEventListener("wheel", event => {
        const viewport = event.target.closest(viewportSelector);
        if (!viewport || viewport.scrollWidth <= viewport.clientWidth + 2) return;
        const delta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY;
        if (!delta) return;
        const atStart = viewport.scrollLeft <= 1;
        const atEnd = viewport.scrollLeft + viewport.clientWidth >= viewport.scrollWidth - 1;
        if ((delta < 0 && atStart) || (delta > 0 && atEnd)) return;
        viewport.scrollLeft += delta;
        event.preventDefault();
    }, { passive: false });

    document.addEventListener("keydown", event => {
        const viewport = event.target.matches?.(viewportSelector) ? event.target : null;
        if (!viewport || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
        viewport.scrollBy({ left: event.key === "ArrowLeft" ? -180 : 180, behavior: "smooth" });
        event.preventDefault();
    });

    document.addEventListener("scroll", event => {
        if (event.target.matches?.(viewportSelector)) updateRail(event.target);
    }, true);

    new MutationObserver(records => {
        const containsNewRail = records.some(record => [...record.addedNodes].some(node =>
            node.nodeType === Node.ELEMENT_NODE
            && (node.matches?.(viewportSelector) || node.querySelector?.(viewportSelector))));
        if (containsNewRail) window.requestAnimationFrame(updateAll);
    }).observe(document.body, { childList: true, subtree: true });
    window.addEventListener("resize", updateAll);
    updateAll();
})();
