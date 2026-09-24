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
        }
    };
})();
