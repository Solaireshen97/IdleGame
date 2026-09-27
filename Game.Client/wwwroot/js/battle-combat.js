(() => {
    const sessions = new WeakMap();
    const tones = { fire: "#ffac69", water: "#80dbff", earth: "#e3c07c", wind: "#a5f2c6",
        light: "#fff0ad", dark: "#c6a0ff", neutral: "#ffe0a1", heal: "#8af1b7", hostile: "#ff8d82" };
    const reducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
    const number = value => value.toLocaleString("zh-CN");
    const basicLabels = new Set(["普通攻击", "二连击", "追击", "普攻追击", "追加伤害", "持续伤害", "敌方反击", "回合伤害"]);
    const isSkill = event => event.kind === "damage" && !basicLabels.has(event.label);
    const spriteLoads = new WeakMap();
    const sprites = root => [...root.querySelectorAll(".fighter__portrait img, .battle-field__enemy-art img, .battle-field__backdrop img")];

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
        const effectCards = [];
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
        const vector = (className, point, color, paths) => {
            const node = make(className, point, color);
            const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
            svg.setAttribute("viewBox", "0 0 160 100");
            svg.setAttribute("aria-hidden", "true");
            for (const d of paths) {
                const path = document.createElementNS(svg.namespaceURI, "path");
                path.setAttribute("d", d);
                svg.append(path);
            }
            node.append(svg);
            return node;
        };
        const pointOf = unit => {
            const art = portrait(unit);
            const img = art?.querySelector("img");
            const box = (img ?? art ?? unit?.element ?? field).getBoundingClientRect();
            const bounds = field.getBoundingClientRect();
            let top = box.top;
            let height = box.height;
            // Narrow party slots leave empty space above a contained sprite.
            // Anchor hits to the rendered image, rather than that empty box.
            if (img?.naturalWidth && getComputedStyle(img).objectFit === "contain") {
                const scale = Math.min(box.width / img.naturalWidth, box.height / img.naturalHeight);
                height = img.naturalHeight * scale;
                const position = getComputedStyle(img).objectPosition.split(" ")[1];
                top += (box.height - height) * (parseFloat(position) / 100 || 0);
            }
            return { x: box.left + box.width * .5 - bounds.left, y: top + height * .48 - bounds.top };
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
        const particles = (point, color, count, distance, shape = "spark") => {
            for (let i = 0; i < count; i++) {
                const angle = (i / count * Math.PI * 2) + .3;
                const particle = make(`combat-spark combat-spark--${shape}`, point, color);
                animate(particle, [
                    { transform: `rotate(${angle}rad) translateX(5px) scaleX(1)`, opacity: 1 },
                    { transform: `rotate(${angle}rad) translateX(${distance * (.65 + i % 3 * .17)}px) scaleX(.15)`, opacity: 0 }
                ], { duration: 330 + i % 3 * 65 }, true);
            }
        };
        const impact = (event, point, color) => {
            const targetArt = portrait(units.get(event.target));
            const size = (event.target === "enemy" ? 1 : Math.min(1, Math.max(.6, (targetArt?.clientWidth ?? 80) / 80)))
                * (isSkill(event) ? 1.12 : 1);
            if (event.kind === "damage") {
                const burst = make(`combat-hit-burst${event.critical ? " combat-hit-burst--critical" : ""}`, point, color);
                animate(burst, [
                    { transform: "translate(-50%, -50%) scale(.65)", opacity: .95 },
                    { transform: "translate(-50%, -50%) scale(1.2)", opacity: .75, offset: .16 },
                    { transform: "translate(-50%, -50%) scale(1.55)", opacity: 0 }
                ], { duration: event.critical ? 320 : 260 }, true);
            }
            const ring = make(`combat-impact combat-impact--${event.kind}`, point, color);
            animate(ring, [{ transform: "translate(-50%, -50%) scale(.2)", opacity: .95 },
                { transform: `translate(-50%, -50%) scale(${event.critical ? 1.65 : 1})`, opacity: 0 }], { duration: 360 }, true);
            if (event.kind === "damage") {
                if (["slash", "dagger"].includes(event.style)) {
                    const dagger = event.style === "dagger";
                    for (let i = 0; i < (dagger || event.critical ? 2 : 1); i++) {
                        const slash = vector(`combat-cut${dagger ? " combat-cut--dagger" : ""}`, point, color, [
                            "M10 83 C36 18 104 -3 152 29 C99 12 44 39 10 83Z",
                            "M12 81 C45 20 105 7 151 29", "M32 87 C59 52 98 33 136 31"
                        ]);
                        const angle = (index % 2 ? -24 : -155) + i * (dagger ? 105 : 80);
                        animate(slash, [
                            { transform: `translate(-58%,-44%) rotate(${angle - 18}deg) scale(${size * .35},${size * .65})`, opacity: .35 },
                            { transform: `translate(-50%,-50%) rotate(${angle}deg) scale(${size})`, opacity: 1, offset: .18 },
                            { transform: `translate(-46%,-54%) rotate(${angle + 12}deg) scale(${size * 1.08})`, opacity: .8, offset: .5 },
                            { transform: `translate(-42%,-58%) rotate(${angle + 18}deg) scale(${size * 1.16},${size * .7})`, opacity: 0 }
                        ], { duration: dagger ? 340 : 430, delay: i * 55 }, true);
                    }
                } else if (event.style === "arrow") {
                    const from = pointOf(units.get(event.source));
                    const angle = Math.atan2(point.y - from.y, point.x - from.x);
                    const pierce = vector("combat-pierce", point, color, [
                        "M10 50 H150 M135 40 L150 50 L135 60", "M35 31 H111 M42 69 H126"
                    ]);
                    animate(pierce, [
                        { transform: `translate(-65%,-50%) rotate(${angle}rad) scale(${size * .35},${size})`, opacity: 1 },
                        { transform: `translate(-45%,-50%) rotate(${angle}rad) scale(${size})`, opacity: .95, offset: .25 },
                        { transform: `translate(-30%,-50%) rotate(${angle}rad) scale(${size * 1.2},${size * .6})`, opacity: 0 }
                    ], { duration: 390 }, true);
                } else if (event.style === "magic") {
                    const spell = make("combat-spell", point, color);
                    animate(spell, [
                        { transform: `translate(-50%,-50%) rotate(-35deg) scale(${size * .25})`, opacity: 1 },
                        { transform: `translate(-50%,-50%) rotate(15deg) scale(${size})`, opacity: .95, offset: .3 },
                        { transform: `translate(-50%,-50%) rotate(65deg) scale(${size * 1.3})`, opacity: 0 }
                    ], { duration: 500 }, true);
                } else if (event.style === "holy") {
                    const light = make("combat-holy", point, color);
                    animate(light, [
                        { transform: `translate(-50%,-50%) scale(${size * .5},${size * .25})`, opacity: .8 },
                        { transform: `translate(-50%,-50%) scale(${size})`, opacity: 1, offset: .2 },
                        { transform: `translate(-50%,-65%) scale(${size * .8},${size * 1.25})`, opacity: 0 }
                    ], { duration: 500 }, true);
                }
            } else if (["heal", "cleanse"].includes(event.kind)) {
                const column = make("combat-heal-column", point, color);
                animate(column, [
                    { transform: "translate(-50%,-35%) scale(.7,.3)", opacity: 0 },
                    { transform: "translate(-50%,-50%) scale(1)", opacity: .85, offset: .25 },
                    { transform: "translate(-50%,-65%) scale(1.1)", opacity: 0 }
                ], { duration: 650 }, true);
                for (let i = 0; i < 4; i++) {
                    const mote = make("combat-heal-mote", { x: point.x + (i - 1.5) * 14, y: point.y + 20 }, color);
                    animate(mote, [
                        { transform: "translate(-50%,0) scale(.5)", opacity: 0 },
                        { transform: "translate(-50%,-10px) scale(1)", opacity: 1, offset: .2 },
                        { transform: `translate(-50%,${-45 - i % 2 * 15}px) scale(.5)`, opacity: 0 }
                    ], { duration: 600, delay: i * 45 }, true);
                }
            } else if (event.kind === "guard") {
                const shield = vector("combat-shield", point, color, [
                    "M80 8 L118 23 L114 56 Q110 78 80 93 Q50 78 46 56 L42 23Z", "M80 23 V74 M62 45 H98"
                ]);
                animate(shield, [
                    { transform: `translate(-50%,-50%) scale(${size * .4})`, opacity: 0 },
                    { transform: `translate(-50%,-50%) scale(${size})`, opacity: .95, offset: .25 },
                    { transform: `translate(-50%,-55%) scale(${size * 1.1})`, opacity: 0 }
                ], { duration: 650 }, true);
            }
            particles(point, color, event.critical ? 10 : 6, event.critical ? 64 : 40,
                event.style === "magic" ? "crystal" : event.style === "holy" ? "star" : "spark");
        };
        const retireNumber = lane => {
            const node = lane.node;
            if (node) {
                const { transform, opacity } = getComputedStyle(node);
                lane.animation?.cancel();
                animations.delete(lane.animation);
                // Make room during the next attack's windup, before its impact.
                animate(node, [
                    { transform, opacity },
                    { transform: `${transform} translateY(-6px)`, opacity: 0 }
                ], { duration: 70, easing: "linear" }, true);
            }
            lane.node = null;
            lane.animation = null;
            lane.until = 0;
            lane.displayWidth = null;
        };
        const reserveNumber = (event, point, count = 1) => {
            let pool = numberLanes.get(event.target);
            if (!pool) {
                const rowHeight = field.clientWidth > 450 ? 46 : 39;
                const fieldTop = field.getBoundingClientRect().top;
                const target = units.get(event.target);
                const healthBottom = target?.element.querySelector(".combat-health")?.getBoundingClientRect().bottom ?? fieldTop;
                const top = Math.min(field.clientHeight - 28, Math.max(28, healthBottom - fieldTop + 28));
                const bottom = Math.max(top, field.clientHeight - 24);
                const center = Math.max(top, Math.min(bottom, point.y));
                const targetWidth = event.target === "enemy" ? 180
                    : Math.min(140, Math.max(64, (portrait(target)?.getBoundingClientRect().width ?? 80) + 20));
                const width = Math.max(32, Math.min(targetWidth,
                    (Math.min(point.x, field.clientWidth - point.x) - 10) * 2));
                // First hit sits on the target's body. Subsequent hits stay on
                // its vertical axis; never spread into the middle of the field.
                let positions = [0, -1, 1].map(offset => center + offset * rowHeight)
                    .filter(y => y >= top && y <= bottom);
                // Keep a swing and its echo together even when the compact
                // field only has room for two rows around the body.
                if (positions.length < 2 && bottom > top) {
                    const gap = Math.min(rowHeight, bottom - top);
                    const upper = Math.max(top, Math.min(bottom - gap, center));
                    const lower = Math.min(bottom, Math.max(top + gap, center));
                    positions = center - upper <= lower - center ? [upper, upper + gap] : [lower, lower - gap];
                }
                pool = { cursor: 0, slots: positions.map(y => ({ x: point.x, y, width, until: 0 })) };
                numberLanes.set(event.target, pool);
            }
            const now = performance.now();
            const candidates = pool.slots.map((_, i) => {
                const laneIndex = (pool.cursor + i) % pool.slots.length;
                const lane = pool.slots[laneIndex];
                const overlaps = [...numberLanes.values()].flatMap(other => other === pool ? [] : other.slots.filter(active =>
                    active.until > now && Math.abs(active.x - lane.x) < ((active.displayWidth ?? active.width) + lane.width) / 2
                    && Math.abs(active.y - lane.y) < (field.clientWidth > 450 ? 46 : 39)));
                return { lane, laneIndex, overlaps, readyAt: Math.max(lane.until, ...overlaps.map(active => active.until)) };
            });
            // Prefer free local rows, then replace the oldest numbers. Text
            // lifetime must never add a pause between characters' attacks.
            candidates.sort((a, b) => Math.max(now, a.readyAt) - Math.max(now, b.readyAt));
            const selected = candidates.slice(0, Math.min(count, pool.slots.length));
            for (const slot of selected) {
                retireNumber(slot.lane);
                for (const overlap of slot.overlaps) retireNumber(overlap);
            }
            for (const slot of selected) slot.lane.until = Infinity;
            pool.cursor = (selected[selected.length - 1].laneIndex + 1) % pool.slots.length;
            return selected.map(slot => slot.lane);
        };
        const popup = (event, lane, color) => {
            const node = make(`combat-number${event.critical ? " combat-number--critical" : ""}${event.isFollowUp ? " combat-number--echo" : ""}${event.target !== "enemy" && event.kind === "damage" ? " combat-number--incoming" : ""} combat-number--${event.kind}`,
                lane, color);
            if (event.isFollowUp || event.kind === "heal") {
                const tag = document.createElement("small");
                tag.className = "combat-number__tag";
                tag.textContent = event.isFollowUp ? "追击" : "恢复";
                node.append(tag);
            }
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
            const scale = Math.min(1, lane.width / Math.max(1, measuredWidth) / 1.15);
            lane.displayWidth = measuredWidth * scale * 1.15;
            lane.until = performance.now() + numberDuration;
            lastNumberEnd = Math.max(lastNumberEnd, lane.until);
            lane.node = node;
            lane.animation = animate(node, [
                { transform: `translate(-50%, calc(-50% + 3px)) scale(${scale * 1.15})`, opacity: 1 },
                { transform: `translate(-50%, -50%) scale(${scale * 1.04})`, opacity: 1, offset: .045 },
                { transform: `translate(-50%, -50%) scale(${scale})`, opacity: 1, offset: .1 },
                { transform: `translate(-50%, calc(-50% - 3px)) scale(${scale})`, opacity: 1, offset: .82 },
                { transform: `translate(-50%, calc(-50% - 8px)) scale(${scale * .96})`, opacity: 0 }
            ], { duration: numberDuration, easing: "linear" }, true);
            lane.animation?.finished.then(() => {
                if (lane.node !== node) return;
                lane.node = null;
                lane.animation = null;
                lane.until = 0;
                lane.displayWidth = null;
            }, () => {});
        };
        const showEffect = (label, kind, color, anchor) => {
            const bounds = field.getBoundingClientRect();
            const point = pointOf(anchor);
            const healthBottom = anchor?.element.querySelector(".combat-health")?.getBoundingClientRect().bottom ?? bounds.top;
            const card = make(`combat-effect combat-effect--${kind}`,
                point, color);
            const icon = document.createElement("i");
            icon.textContent = ({ buff: "↑", debuff: "↓", guard: "◇", interrupt: "!", cleanse: "✚", dispel: "✧", cooldown: "↻" })[kind] ?? "✦";
            const text = document.createElement("strong");
            text.textContent = label;
            card.append(icon, text);
            const size = card.getBoundingClientRect();
            const scale = Math.min(1, (field.clientWidth - 24) / Math.max(1, size.width));
            const halfWidth = size.width * scale * .53;
            const halfHeight = size.height * scale * .53;
            // Skill names belong to the caster's formation position, even when
            // a melee sprite lunges. State changes belong to their recipient.
            const x = Math.max(halfWidth + 8, Math.min(field.clientWidth - halfWidth - 8, point.x));
            const top = healthBottom - bounds.top + halfHeight + 8;
            const y = Math.min(field.clientHeight - halfHeight - 14, Math.max(top, point.y - 38));
            card.style.left = `${x}px`;
            card.style.top = `${y}px`;
            card.style.setProperty("--effect-anchor-offset", `${(point.x - x) / scale}px`);
            // Keep previous callouts at their own units. Only replace a card
            // that would cover this one instead of relocating it to another unit.
            for (const old of [...effectCards]) {
                const oldBox = old.getBoundingClientRect();
                const oldX = oldBox.left + oldBox.width / 2 - bounds.left;
                const oldY = oldBox.top + oldBox.height / 2 - bounds.top;
                if (Math.abs(oldX - x) >= (oldBox.width / 2 + halfWidth + 6)
                    || Math.abs(oldY - y) >= (oldBox.height / 2 + halfHeight + 6)) continue;
                old.remove();
                nodes.delete(old);
                effectCards.splice(effectCards.indexOf(old), 1);
            }
            effectCards.push(card);
            const duration = 1400;
            lastNumberEnd = Math.max(lastNumberEnd, performance.now() + duration);
            animate(card, [
                { transform: `translate(-50%, -50%) scale(${scale * 1.06})`, opacity: 1 },
                { transform: `translate(-50%, -50%) scale(${scale})`, opacity: 1, offset: .08 },
                { transform: `translate(-50%, calc(-50% - 3px)) scale(${scale})`, opacity: 1, offset: .75 },
                { transform: `translate(-50%, calc(-50% - 10px)) scale(${scale})`, opacity: 0 }
            ], { duration, easing: "linear" }, true)?.finished.then(() => {
                const cardIndex = effectCards.indexOf(card);
                if (cardIndex >= 0) effectCards.splice(cardIndex, 1);
            }, () => {});
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
            if (source && ["arrow", "magic", "holy"].includes(event.style) && event.kind === "damage") {
                const projectile = make(`combat-projectile combat-projectile--${event.style}`, from, color);
                const angle = Math.atan2(to.y - from.y, to.x - from.x);
                animate(projectile, [
                    { transform: `translate(-50%,-50%) rotate(${angle}rad) scale(.5)`, opacity: 0 },
                    { transform: `translate(-50%,-50%) rotate(${angle}rad) scale(1)`, opacity: 1, offset: .12 },
                    { transform: `translate(calc(-50% + ${to.x - from.x}px),calc(-50% + ${to.y - from.y}px)) rotate(${angle}rad) scale(1)`, opacity: 1 }
                ], { duration: duration * .42, easing: "cubic-bezier(.4,0,.85,.5)" }, true);
                const cast = make(`combat-cast combat-cast--${event.style}`, from, color);
                animate(cast, [
                    { transform: `translate(-50%,-50%) rotate(${angle}rad) scale(.3)`, opacity: .9 },
                    { transform: `translate(-50%,-50%) rotate(${angle}rad) scale(1)`, opacity: .85, offset: .3 },
                    { transform: `translate(-50%,-50%) rotate(${angle + .7}rad) scale(1.3)`, opacity: 0 }
                ], { duration: Math.max(260, duration * .8) }, true);
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
                if (event.amount <= 0) {
                    if (actionText) actionText.textContent = event.label;
                    showEffect(event.label, event.kind, color, target);
                    impact(event, point, color);
                    await wait(300);
                    continue;
                }
                // Reserve the main hit and echo together without waiting for
                // previous numbers to finish fading.
                const pairedEcho = plan.events[eventIndex + 1]?.isFollowUp;
                const lanes = reserveNumber(event, point, pairedEcho ? 2 : 1);
                const lane = lanes?.[0];
                if (!lane || signal.aborted) break;
                if (actionText) actionText.textContent = `${source?.element.dataset.combatName ?? ""} · ${event.label}`.replace(/^ · /, "");
                root.dataset.combatTone = event.target === "enemy" ? "friendly" : event.kind === "damage" ? "hostile" : "heal";
                if (isSkill(event))
                    showEffect(event.label, "skill", color, source ?? target);
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
                let followUpIndex = 1;
                while (!signal.aborted && plan.events[eventIndex + 1]?.isFollowUp) {
                    const echo = plan.events[++eventIndex];
                    const echoTarget = units.get(echo.target);
                    if (!echoTarget) continue;
                    const echoLane = (echo.target === event.target ? lanes[followUpIndex++] : null)
                        ?? reserveNumber(echo, pointOf(echoTarget))[0];
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
