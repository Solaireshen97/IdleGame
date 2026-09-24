(() => {
    const prefersReducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    window.battleUi = {
        scrollBelowSticky(target, stickyPanel) {
            if (!(target instanceof HTMLElement)) return;

            let clearance = 12;
            if (stickyPanel instanceof HTMLElement) {
                const style = window.getComputedStyle(stickyPanel);
                const stickyTop = Number.parseFloat(style.top);
                if (style.position === "sticky" && Number.isFinite(stickyTop)) {
                    clearance = stickyTop + stickyPanel.getBoundingClientRect().height + 12;
                }
            }

            const targetTop = window.scrollY + target.getBoundingClientRect().top - clearance;
            window.scrollTo({
                top: Math.max(0, targetTop),
                behavior: prefersReducedMotion() ? "auto" : "smooth"
            });
        },

        revealSelectedMobileCharacter(container) {
            if (!(container instanceof HTMLElement)) return;

            const selected = container.querySelector('[aria-selected="true"]');
            if (!(selected instanceof HTMLElement)) return;

            const containerRect = container.getBoundingClientRect();
            const selectedRect = selected.getBoundingClientRect();
            const padding = 8;
            const visibleStart = container.scrollLeft + padding;
            const visibleEnd = container.scrollLeft + container.clientWidth - padding;
            const selectedStart = container.scrollLeft + selectedRect.left - containerRect.left;
            const selectedEnd = selectedStart + selectedRect.width;

            let nextScroll = container.scrollLeft;
            if (selectedStart < visibleStart) {
                nextScroll = selectedStart - padding;
            } else if (selectedEnd > visibleEnd) {
                nextScroll = selectedEnd - container.clientWidth + padding;
            }

            const maxScroll = Math.max(0, container.scrollWidth - container.clientWidth);
            nextScroll = Math.max(0, Math.min(maxScroll, nextScroll));
            if (Math.abs(nextScroll - container.scrollLeft) < 1) return;

            container.scrollTo({
                left: nextScroll,
                behavior: prefersReducedMotion() ? "auto" : "smooth"
            });
        },

        revealQuickActionsOnShortViewport(target) {
            if (!(target instanceof HTMLElement)) return;

            const alignCommandAboveDock = (behavior = "auto") => {
                if (window.innerWidth > 900) return;

                const command = document.querySelector(".battle-command");
                const quickActions = document.querySelector(".battle-mobile-quick-actions");
                if (!(command instanceof HTMLElement) && !(quickActions instanceof HTMLElement)) return;

                const dock = document.querySelector(".mobile-dock");
                const dockTop = dock instanceof HTMLElement
                    ? dock.getBoundingClientRect().top
                    : window.innerHeight;
                // The quick-action rail sits a few pixels inside the command card. Leave
                // enough room for the card's bottom edge as well as the fixed dock.
                const safeBottom = dockTop - 18;
                const targetBottom = command instanceof HTMLElement
                    ? command.getBoundingClientRect().bottom
                    : quickActions.getBoundingClientRect().bottom;
                const requiredScroll = targetBottom - safeBottom;
                if (requiredScroll <= 0) return;

                const maxScroll = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
                const nextScroll = Math.min(maxScroll, window.scrollY + requiredScroll);
                if (nextScroll <= window.scrollY + 1) return;

                window.scrollTo({ top: nextScroll, behavior });
            };

            alignCommandAboveDock(prefersReducedMotion() ? "auto" : "smooth");

            if (!window.__battleUiViewportAlignment) {
                let frameId = 0;
                let delayedAlignmentId = 0;
                const onViewportResize = () => {
                    if (frameId) cancelAnimationFrame(frameId);
                    frameId = requestAnimationFrame(() => {
                        frameId = 0;
                        alignCommandAboveDock();
                    });

                    if (delayedAlignmentId) clearTimeout(delayedAlignmentId);
                    delayedAlignmentId = window.setTimeout(() => {
                        delayedAlignmentId = 0;
                        alignCommandAboveDock();
                    }, 180);
                };

                window.__battleUiViewportAlignment = onViewportResize;
                window.addEventListener("resize", onViewportResize, { passive: true });
                window.visualViewport?.addEventListener("resize", onViewportResize, { passive: true });
            }
        }
    };
})();
