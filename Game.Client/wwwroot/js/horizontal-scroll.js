(() => {
    const viewportSelector = "[data-drag-scroll]";
    let dragState = null;
    let suppressClickUntil = 0;

    const getRail = viewport => viewport?.closest(".horizontal-rail");
    const closest = (target, selector) => target instanceof Element ? target.closest(selector) : null;
    const canScrollHorizontally = viewport => getComputedStyle(viewport).overflowX !== "hidden";
    const resolveViewport = target => {
        const viewport = closest(target, viewportSelector);
        if (viewport) return viewport;
        return closest(target, ".horizontal-rail")?.querySelector(viewportSelector) ?? null;
    };

    const updateRail = viewport => {
        const rail = getRail(viewport);
        if (!rail) return;

        const hasOverflow = canScrollHorizontally(viewport) && viewport.scrollWidth > viewport.clientWidth + 2;
        const previous = rail.querySelector('[data-scroll-step="-1"]');
        const next = rail.querySelector('[data-scroll-step="1"]');
        rail.classList.toggle("horizontal-rail--overflowing", hasOverflow);
        if (previous) previous.disabled = !hasOverflow || viewport.scrollLeft <= 1;
        if (next) next.disabled = !hasOverflow || viewport.scrollLeft + viewport.clientWidth >= viewport.scrollWidth - 1;
    };

    const updateAll = () => {
        document.querySelectorAll(viewportSelector).forEach(updateRail);
    };
    let updateScheduled = false;
    const scheduleUpdate = () => {
        if (updateScheduled) return;
        updateScheduled = true;
        window.requestAnimationFrame(() => {
            updateScheduled = false;
            updateAll();
        });
    };

    document.addEventListener("click", event => {
        if (performance.now() <= suppressClickUntil) {
            suppressClickUntil = 0;
            event.preventDefault();
            event.stopImmediatePropagation();
            return;
        }

        const viewport = resolveViewport(event.target);
        const control = closest(event.target, "[data-scroll-step]");
        if (control) {
            if (!viewport) return;
            const direction = Number(control.dataset.scrollStep) || 1;
            viewport.scrollBy({ left: direction * Math.max(180, viewport.clientWidth * .78), behavior: "smooth" });
        }
    }, true);

    document.addEventListener("mousedown", event => {
        if (event.button !== 0) return;
        const viewport = resolveViewport(event.target);
        if (!viewport || !canScrollHorizontally(viewport) || viewport.scrollWidth <= viewport.clientWidth + 2) return;

        const bounds = viewport.getBoundingClientRect();
        if (event.clientY >= bounds.bottom - 10) return;

        dragState = {
            viewport,
            startX: event.clientX,
            startScrollLeft: viewport.scrollLeft,
            moved: false
        };
    });

    window.addEventListener("mousemove", event => {
        if (!dragState) return;
        if ((event.buttons & 1) === 0) {
            finishDrag();
            return;
        }

        const distance = event.clientX - dragState.startX;
        if (!dragState.moved && Math.abs(distance) < 4) return;
        dragState.moved = true;
        dragState.viewport.classList.add("is-dragging");
        dragState.viewport.scrollLeft = dragState.startScrollLeft - distance;
        window.getSelection()?.removeAllRanges();
        event.preventDefault();
    }, { passive: false });

    function finishDrag() {
        if (!dragState) return;
        const { viewport, moved } = dragState;
        viewport.classList.remove("is-dragging");
        if (moved) {
            suppressClickUntil = performance.now() + 350;
        }
        dragState = null;
        updateRail(viewport);
    }

    window.addEventListener("mouseup", finishDrag);
    window.addEventListener("blur", finishDrag);
    document.addEventListener("dragstart", event => {
        if (resolveViewport(event.target)) event.preventDefault();
    });

    document.addEventListener("wheel", event => {
        const viewport = closest(event.target, viewportSelector);
        if (!viewport || !canScrollHorizontally(viewport) || viewport.scrollWidth <= viewport.clientWidth + 2) return;
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
        if (!viewport || !canScrollHorizontally(viewport) || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
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
        if (containsNewRail) scheduleUpdate();
    }).observe(document.body, { childList: true, subtree: true });
    window.addEventListener("resize", scheduleUpdate);
    updateAll();
})();
