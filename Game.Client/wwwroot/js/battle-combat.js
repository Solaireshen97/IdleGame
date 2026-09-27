(() => {
    const sessions = new WeakMap();
    const tones = { fire: "#ffac69", water: "#80dbff", earth: "#e3c07c", wind: "#a5f2c6",
        light: "#fff0ad", dark: "#c6a0ff", neutral: "#ffe0a1", heal: "#8af1b7", hostile: "#ff8d82" };
    const reducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
    const number = value => value.toLocaleString("zh-CN");
    const spriteLoads = new WeakMap();
    const sprites = root => [...root.querySelectorAll(".fighter__portrait img, .battle-field__enemy-art img")];

    function decodeSprite(img) {
        const src = img.src;
        const cached = spriteLoads.get(img);
        if (cached?.src === src) return cached.ready;
        const ready = img.decode().then(() => img.naturalWidth > 0 && img.src === src, () => false);
        const entry = { src, ready };
        spriteLoads.set(img, entry);
        ready.then(loaded => { if (!loaded && spriteLoads.get(img) === entry) spriteLoads.delete(img); });
        return ready;
    }

    function preload(root) {
        if (!(root instanceof HTMLElement) || !root.isConnected) return;
        for (const img of sprites(root)) decodeSprite(img);
    }

    function waitForSprites(root, signal) {
        if (signal.aborted) return Promise.resolve(false);
        return new Promise(resolve => {
            const finish = loaded => {
                clearTimeout(timeout);
                signal.removeEventListener("abort", abort);
                resolve(loaded);
            };
            const abort = () => finish(false);
            // Failed or stalled downloads must not leave the scene in mid-attack.
            const timeout = setTimeout(() => finish(false), 8000);
            signal.addEventListener("abort", abort, { once: true });
            Promise.all(sprites(root).map(decodeSprite)).then(results => finish(results.every(Boolean)));
        });
    }

    function cancel(root) { sessions.get(root)?.abort(); }

    async function play(root, plan) {
        cancel(root);
        if (!(root instanceof HTMLElement) || !root.isConnected || document.hidden) return;
        const field = root.querySelector(".battle-field");
        const layer = root.querySelector(".combat-fx-layer");
        if (!field || !layer) return;
        const controller = new AbortController();
        const { signal } = controller;
        sessions.set(root, controller);
        const animations = new Set();
        const nodes = new Set();
        const timers = new Set();
        const units = new Map([...root.querySelectorAll("[data-combat-unit]")].map(unit =>
            [unit.dataset.combatUnit, { element: unit, hp: Number(unit.dataset.hp), max: Number(unit.dataset.maxHp) || 1 }]));
        const actionText = root.querySelector("[data-combat-action]");
        const hitsText = root.querySelector("[data-combat-hits]");
        const totalText = root.querySelector("[data-combat-total]");
        let hitCount = 0;
        let total = 0;
        let index = 0;
        const isReduced = reducedMotion();
        const numberDuration = 2200;
        const numberLanes = new Map();
        let lastNumberEnd = 0;

        const wait = ms => new Promise(resolve => {
            if (signal.aborted) return resolve();
            const done = () => { clearTimeout(id); timers.delete(id); signal.removeEventListener("abort", done); resolve(); };
            const id = setTimeout(done, ms);
            timers.add(id);
            signal.addEventListener("abort", done, { once: true });
        });
        const animate = (element, frames, options, remove = false) => {
            if (!element || signal.aborted) return;
            const animation = element.animate(frames, { fill: "none", easing: "cubic-bezier(.16,.7,.24,1)", ...options });
            animations.add(animation);
            animation.finished.then(() => {
                if (options.fill !== "forwards") animations.delete(animation);
                if (remove) { element.remove(); nodes.delete(element); }
            }, () => {});
            return animation;
        };
        const make = (className, point, color) => {
            const node = document.createElement("span");
            node.className = className;
            node.style.left = `${point.x}px`;
            node.style.top = `${point.y}px`;
            node.style.setProperty("--fx-color", color);
            layer.append(node);
            nodes.add(node);
            return node;
        };
        const portrait = unit => unit?.element.querySelector(".fighter__portrait, .battle-field__enemy-art");
        const pointOf = unit => {
            const box = (portrait(unit) ?? unit?.element ?? field).getBoundingClientRect();
            const bounds = field.getBoundingClientRect();
            return { x: box.left + box.width * .5 - bounds.left, y: box.top + box.height * .48 - bounds.top };
        };
        const updateHp = (unit, hp, max = unit.max, immediate = false) => {
            unit.hp = Math.max(0, Math.min(max, hp));
            unit.max = max;
            const progress = unit.element.querySelector("progress");
            if (progress) { progress.max = max; progress.value = unit.hp; }
            const trail = unit.element.querySelector(".combat-health__trail");
            if (trail) {
                trail.style.transition = immediate || isReduced ? "none" : "width 420ms ease-out 120ms";
                trail.style.width = `${unit.hp / max * 100}%`;
            }
        };
        const particles = (point, color, count, distance) => {
            for (let i = 0; i < count; i++) {
                const angle = (i / count * Math.PI * 2) + .3;
                const particle = make("combat-spark", point, color);
                animate(particle, [
                    { transform: `rotate(${angle}rad) translateX(5px) scaleX(1)`, opacity: 1 },
                    { transform: `rotate(${angle}rad) translateX(${distance * (.65 + i % 3 * .17)}px) scaleX(.15)`, opacity: 0 }
                ], { duration: 330 + i % 3 * 65 }, true);
            }
        };
        const impact = (event, point, color) => {
            const ring = make(`combat-impact combat-impact--${event.kind}`, point, color);
            animate(ring, [{ transform: "translate(-50%, -50%) scale(.2)", opacity: .95 },
                { transform: `translate(-50%, -50%) scale(${event.critical ? 1.65 : 1})`, opacity: 0 }], { duration: 360 }, true);
            if (event.kind === "damage" && ["slash", "dagger"].includes(event.style)) {
                const slash = make(`combat-cut${event.style === "dagger" || event.critical ? " combat-cut--double" : ""}`, point, color);
                const angle = index % 2 ? -35 : -145;
                animate(slash, [
                    { transform: `translate(-50%,-50%) rotate(${angle}deg) scale(.2, .6)`, opacity: 0 },
                    { transform: `translate(-50%,-50%) rotate(${angle + 8}deg) scale(1.1, 1)`, opacity: 1, offset: .22 },
                    { transform: `translate(-50%,-50%) rotate(${angle + 15}deg) scale(1.3, .2)`, opacity: 0 }
                ], { duration: 310 }, true);
            }
            particles(point, color, event.critical ? 10 : 6, event.critical ? 64 : 40);
        };
        const reserveNumber = async (event, point) => {
            // Party-wide attacks share lanes too, so different allies' damage
            // numbers cannot cover one another on the compact formation.
            const laneGroup = event.target === "enemy" ? "enemy" : "party";
            let pool = numberLanes.get(laneGroup);
            if (!pool) {
                const columns = event.target === "enemy" && field.clientWidth >= 240 ? 2 : 1;
                const rowHeight = field.clientWidth > 450 ? 32 : 26;
                const fieldTop = field.getBoundingClientRect().top;
                const healthBottom = Math.max(...[...units.values()].map(unit =>
                    unit.element.querySelector(".combat-health")?.getBoundingClientRect().bottom ?? fieldTop));
                const top = Math.max(18, healthBottom - fieldTop + 18);
                const rows = Math.max(1, Math.min(5, Math.floor((field.clientHeight - top - 18) / rowHeight) + 1));
                const areaLeft = event.target === "enemy" ? field.clientWidth * .3 : 12;
                const areaWidth = event.target === "enemy" ? field.clientWidth - areaLeft - 12 : field.clientWidth * .3 - 24;
                const width = Math.min(116, Math.max(32, areaWidth) / columns);
                const left = event.target === "enemy"
                    ? Math.max(areaLeft, Math.min(field.clientWidth - columns * width - 12, point.x - columns * width / 2))
                    : areaLeft;
                const bottom = Math.min(field.clientHeight - 16, Math.max(top + (rows - 1) * rowHeight, point.y + 48));
                pool = { cursor: 0, slots: Array.from({ length: rows * columns }, (_, i) => ({
                    x: left + (i % columns + .5) * width,
                    y: bottom - Math.floor(i / columns) * rowHeight,
                    width: width - 10, until: 0
                })) };
                numberLanes.set(laneGroup, pool);
            }
            // Never overwrite a live number. Short fields slow the next hit just
            // enough to free a lane instead of dropping hits or stacking text.
            while (!signal.aborted) {
                for (let i = 0; i < pool.slots.length; i++) {
                    const laneIndex = (pool.cursor + i) % pool.slots.length;
                    const lane = pool.slots[laneIndex];
                    if (lane.until > performance.now()) continue;
                    lane.until = Infinity;
                    pool.cursor = (laneIndex + 1) % pool.slots.length;
                    return lane;
                }
                await wait(Math.max(1, Math.min(...pool.slots.map(lane => lane.until)) - performance.now()));
            }
        };
        const popup = (event, lane, color) => {
            const node = make(`combat-number${event.critical ? " combat-number--critical" : ""}${event.target !== "enemy" && event.kind === "damage" ? " combat-number--incoming" : ""} combat-number--${event.kind}`,
                lane, color);
            if (event.kind === "damage" && event.elementModifierPercent) {
                const arrow = document.createElement("span");
                arrow.className = "combat-number__affinity";
                arrow.textContent = event.elementModifierPercent > 0 ? "↑" : "↓";
                node.append(arrow);
            }
            const amount = document.createElement("b");
            amount.textContent = event.amount > 0 ? `${event.kind === "heal" ? "+" : ""}${number(event.amount)}` : event.label;
            node.append(amount);
            if (event.critical) {
                const mark = document.createElement("span");
                mark.className = "combat-number__critical";
                mark.textContent = "!";
                node.append(mark);
            }
            const measuredWidth = node.getBoundingClientRect().width;
            const scale = Math.min(1, lane.width / Math.max(1, measuredWidth));
            lane.until = performance.now() + numberDuration;
            lastNumberEnd = Math.max(lastNumberEnd, lane.until);
            animate(node, [
                { transform: `translate(-50%, calc(-50% + 6px)) scale(${scale * .7})`, opacity: 0 },
                { transform: `translate(-50%, -50%) scale(${scale * 1.08})`, opacity: 1, offset: .07 },
                { transform: `translate(-50%, -50%) scale(${scale})`, opacity: 1, offset: .16 },
                { transform: `translate(-50%, calc(-50% - 3px)) scale(${scale})`, opacity: 1, offset: .82 },
                { transform: `translate(-50%, calc(-50% - 8px)) scale(${scale * .96})`, opacity: 0 }
            ], { duration: numberDuration, easing: "linear" }, true);
        };
        const applyHit = (event, target, lane, color) => {
            popup(event, lane, color);
            if (event.kind === "damage" || event.kind === "heal")
                updateHp(target, target.hp + (event.kind === "heal" ? event.amount : -event.amount));
            if (event.target === "enemy" && event.kind === "damage") {
                hitCount++;
                total += event.amount;
                if (hitsText) hitsText.textContent = `${hitCount} HIT`;
                if (totalText) totalText.textContent = `${number(total)} 伤害`;
            }
        };
        const launch = (event, source, target, duration, color) => {
            const from = pointOf(source);
            const to = pointOf(target);
            const actor = portrait(source);
            if (source && event.style !== "pulse") {
                const melee = event.style === "slash" || event.style === "dagger";
                const dx = melee ? (to.x - from.x) * .72 : (to.x > from.x ? 1 : -1) * 7;
                const dy = melee ? (to.y - from.y) * .5 - 5 : -4;
                source.element.classList.add("combat-unit--acting");
                animate(actor, [
                    { transform: "translate(0,0)", offset: 0 },
                    { transform: `translate(${-Math.sign(dx) * 6}px,2px)`, offset: .12 },
                    { transform: `translate(${dx}px,${dy}px)`, offset: .4 },
                    { transform: `translate(${dx}px,${dy}px)`, offset: .55 },
                    { transform: "translate(0,0)", offset: 1 }
                ], { duration: duration * .96 })?.finished.then(() => source.element.classList.remove("combat-unit--acting"), () => {});
            }
            if (source && ["arrow", "magic"].includes(event.style) && event.kind === "damage") {
                const projectile = make(`combat-projectile combat-projectile--${event.style}`, from, color);
                const angle = Math.atan2(to.y - from.y, to.x - from.x);
                animate(projectile, [
                    { transform: `translate(-50%,-50%) rotate(${angle}rad) scale(.5)`, opacity: 0 },
                    { transform: `translate(-50%,-50%) rotate(${angle}rad) scale(1)`, opacity: 1, offset: .12 },
                    { transform: `translate(calc(-50% + ${to.x - from.x}px),calc(-50% + ${to.y - from.y}px)) rotate(${angle}rad) scale(1)`, opacity: 1 }
                ], { duration: duration * .42, easing: "cubic-bezier(.4,0,.85,.5)" }, true);
                const cast = make("combat-cast", from, color);
                animate(cast, [{ transform: "translate(-50%,-50%) scale(.3)", opacity: .8 },
                    { transform: "translate(-50%,-50%) scale(1.2)", opacity: 0 }], { duration: duration * .65 }, true);
            }
        };
        const onVisibility = () => { if (document.hidden) controller.abort(); };
        const observer = new MutationObserver(() => { if (!root.isConnected) controller.abort(); });
        observer.observe(document.body, { childList: true, subtree: true });
        document.addEventListener("visibilitychange", onVisibility);
        root.classList.add("combat-preparing");
        root.dataset.combatPhase = "loading";
        if (actionText) actionText.textContent = "正在载入战斗画面…";
        if (hitsText) hitsText.textContent = "";
        if (totalText) totalText.textContent = "";

        try {
            const ready = await waitForSprites(root, signal);
            if (signal.aborted || !root.isConnected) return;
            if (!ready) return "assets-unavailable";
            root.classList.remove("combat-preparing");
            root.classList.add("combat-playing");
            root.dataset.combatPhase = "attacking";
            if (isReduced) {
                if (hitsText) hitsText.textContent = `${plan.hitCount} HIT`;
                if (totalText) totalText.textContent = `${number(plan.totalDamage)} 伤害`;
                if (actionText) actionText.textContent = plan.defeated ? "目标击破" : "回合完成";
                await wait(120);
                return;
            }
            // Preserve a legible minimum spacing even for long skill chains.
            const actionCount = plan.events.filter(event => !event.isFollowUp).length;
            const step = Math.max(180, Math.min(460, 1900 / Math.max(1, actionCount)));
            for (let eventIndex = 0; eventIndex < plan.events.length; eventIndex++) {
                const event = plan.events[eventIndex];
                if (signal.aborted || !root.isConnected) break;
                const source = units.get(event.source);
                const target = units.get(event.target);
                if (!target) continue;
                index++;
                const color = tones[event.tone] ?? tones.neutral;
                const point = pointOf(target);
                const lane = await reserveNumber(event, point);
                if (!lane || signal.aborted) break;
                if (actionText) actionText.textContent = `${source?.element.dataset.combatName ?? ""} · ${event.label}`.replace(/^ · /, "");
                root.dataset.combatTone = event.target === "enemy" ? "friendly" : event.kind === "damage" ? "hostile" : "heal";
                launch(event, source, target, step, color);
                await wait(step * .42);
                if (signal.aborted) break;
                impact(event, point, color);
                applyHit(event, target, lane, color);
                const sprite = portrait(target)?.querySelector("img");
                if (event.kind === "damage") {
                    const direction = event.target === "enemy" ? 1 : -1;
                    animate(sprite, [
                        { filter: "brightness(2.1) drop-shadow(0 0 7px #fff8)", transform: `translateX(${direction * (event.critical ? 9 : 5)}px)` },
                        { filter: "brightness(1.5)", transform: `translateX(${direction * (event.critical ? 9 : 5)}px)`, offset: .25 },
                        { filter: "brightness(1)", transform: "translateX(0)" }
                    ], { duration: Math.min(280, step * .75) });
                    if (event.critical) {
                        const flash = make("combat-flash", { x: 0, y: 0 }, color);
                        animate(flash, [{ opacity: .14 }, { opacity: 0 }], { duration: 240 }, true);
                    }
                }
                // A normal-attack echo belongs to this swing/projectile. Show its
                // own number during recovery without a second launch or impact.
                let followUpDelay = 0;
                while (!signal.aborted && plan.events[eventIndex + 1]?.isFollowUp) {
                    const echo = plan.events[++eventIndex];
                    const echoTarget = units.get(echo.target);
                    if (!echoTarget) continue;
                    const echoLane = await reserveNumber(echo, pointOf(echoTarget));
                    if (!echoLane || signal.aborted) break;
                    await wait(90);
                    if (signal.aborted) break;
                    applyHit(echo, echoTarget, echoLane, tones[echo.tone] ?? tones.neutral);
                    followUpDelay += 90;
                }
                await wait(Math.max(0, step * .58 - followUpDelay));
            }
            if (!signal.aborted && plan.defeated) {
                // Finish the last impact/recoil before beginning the disappearance.
                await wait(160);
                if (signal.aborted) return;
                root.dataset.combatPhase = "defeating";
                const enemy = units.get("enemy");
                const point = pointOf(enemy);
                particles(point, tones.light, 16, 100);
                animate(portrait(enemy), [{ filter: "brightness(1.8)", opacity: 1, transform: "translateY(0)" },
                    { filter: "brightness(1.2) grayscale(1)", opacity: 0, transform: "translateY(-8px) scale(.92)" }], { duration: 620, fill: "forwards" });
                await wait(620);
                await wait(Math.max(0, lastNumberEnd - performance.now()));
                if (signal.aborted) return;
                root.dataset.combatPhase = "finished";
                const banner = make("combat-finish", { x: field.clientWidth / 2, y: field.clientHeight * .4 }, tones.light);
                const kicker = document.createElement("small");
                kicker.textContent = plan.victory ? "VICTORY" : "BREAK";
                const title = document.createElement("strong");
                title.textContent = plan.victory ? "讨伐完成" : "目标击破";
                const caption = document.createElement("span");
                caption.textContent = `${plan.hitCount} 次命中 · ${number(plan.totalDamage)} 伤害`;
                banner.append(kicker, title, caption);
                animate(banner, [{ transform: "translate(-50%,-50%) scale(.88)", opacity: 0 },
                    { transform: "translate(-50%,-50%) scale(1)", opacity: 1 }], { duration: 230, fill: "forwards" });
                if (actionText) actionText.textContent = plan.victory ? "讨伐完成" : "目标击破";
                await wait(780);
            } else if (!signal.aborted) await wait(Math.max(0, lastNumberEnd - performance.now()));
        } finally {
            observer.disconnect();
            document.removeEventListener("visibilitychange", onVisibility);
            for (const animation of animations) animation.cancel();
            for (const timer of timers) clearTimeout(timer);
            for (const node of nodes) node.remove();
            for (const [key, unit] of units) {
                unit.element.classList.remove("combat-unit--acting");
                const final = plan.finalVitals[key];
                if (final && !signal.aborted) updateHp(unit, final.hp, final.maxHp, true);
            }
            if (sessions.get(root) === controller) {
                root.classList.remove("combat-playing", "combat-preparing");
                delete root.dataset.combatTone;
                delete root.dataset.combatPhase;
                sessions.delete(root);
            }
        }
    }

    window.battleCombat = { play, cancel, preload };
})();
