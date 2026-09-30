const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const { chromium } = require('playwright');
const script = path.resolve(__dirname, '../../Game.Client/wwwroot/js/battle-combat.js');

async function scene(run) {
    const browser = await chromium.launch({ headless: true,
        channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined) });
    try {
        const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        const cssPath = path.resolve(__dirname, '../../Game.Client/obj/Debug/net8.0/scopedcss/Pages/Battle.razor.rz.scp.css');
        const css = fs.readFileSync(cssPath, 'utf8');
        const scope = css.match(/\[(b-[a-z0-9]+)\]/)[1];
        await page.setContent(`<style>${css}
            .battle-field { width:650px; height:350px; position:relative; }
            [data-combat-unit] { position:absolute; width:120px; height:180px; top:80px; }
            [data-combat-unit="1"] { left:10px; } [data-combat-unit="enemy"] { right:10px; }
            .fighter__portrait,.battle-field__enemy-art { position:relative; width:100px; height:100px; }
            </style><div id="root" ${scope}>
            <span data-combat-action></span><span data-combat-hits></span><span data-combat-total></span>
            <div class="battle-field" ${scope}>
                ${['1','enemy'].map(key => `<div ${scope} data-combat-unit="${key}" data-combat-name="同名" data-hp="${key === 'enemy' ? 500 : 95}" data-max-hp="${key === 'enemy' ? 500 : 100}">
                    <div class="combat-health" ${scope}><progress value="100" max="100"></progress></div>
                    <div class="${key === 'enemy' ? 'battle-field__enemy-art' : 'fighter__portrait'}" ${scope}>
                        <span data-combat-status-static ${scope}>最终状态由页面显示</span>
                        <span class="${key === 'enemy' ? 'enemy-effect-icons' : 'fighter__effect-icons'} combat-status-live" data-combat-status-live ${scope}></span>
                    </div></div>`).join('')}
                <div class="combat-fx-layer" ${scope}></div>
            </div></div>`);
        await page.addScriptTag({ path: script });
        await run(page);
        assert.deepEqual(errors, []);
    } finally { await browser.close(); }
}

const status = (code, stacks) => ({ code, name: '<img src=x onerror=alert(1)>', description: '来源快照', isPositive: true,
    stacks, durationText: '本次挑战', counterText: `剩余 ${stacks} 次`, glyph: '✦', boundTargetName: '同名怪物' });
const base = { round: 1, hitCount: 0, totalDamage: 0, defeated: false, victory: false,
    initialStatuses: { '1': [], enemy: [] }, finalVitals: { '1': { hp: 95, maxHp: 100 }, enemy: { hp: 500, maxHp: 500 } } };

// Exercise the actual playback file, generated scoped CSS, browser layout and DOM ownership.
test('plays transient Buff counts on their owner, removes enemy effects and restores the page DOM', async () => {
    await scene(async page => {
        await page.evaluate(() => {
            window.framesSeen = [];
            const root = document.querySelector('#root');
            window.observer = new MutationObserver(() => {
                if (!root.classList.contains('combat-playing')) return;
                const entry = [...root.querySelectorAll('[data-combat-unit]')].map(unit => ({
                    key: unit.dataset.combatUnit,
                    icons: [...unit.querySelectorAll('[data-combat-status-live] [data-status-code]')].map(icon => ({
                        code: icon.dataset.statusCode, count: icon.querySelector('small')?.textContent,
                        visible: getComputedStyle(icon).display, width: icon.getBoundingClientRect().width }))
                }));
                if (JSON.stringify(entry) !== JSON.stringify(window.framesSeen.at(-1))) window.framesSeen.push(entry);
            });
            window.observer.observe(root, { childList: true, subtree: true });
        });
        const plan = { ...base, initialStatuses: { '1': [], enemy: [status('enemy-buff', 1)] }, events: [
            { source: '1', target: '1', kind: 'buff', amount: 0, label: '获得次数', tone: 'light', style: 'pulse', status: status('charges', 3), statusChange: 'Added', countAfter: 3 },
            { source: '1', target: '1', kind: 'buff', amount: 0, label: '消耗次数', tone: 'light', style: 'pulse', status: status('charges', 2), statusChange: 'Consumed', countAfter: 2 },
            { source: '1', target: 'enemy', kind: 'dispel', amount: 0, label: '移除敌方效果', tone: 'light', style: 'pulse', status: status('enemy-buff', 1), statusChange: 'Removed', countAfter: 0 },
            { source: '', target: '1', kind: 'status', amount: 0, label: '次数到期', tone: 'neutral', style: 'pulse', status: status('charges', 2), statusChange: 'Expired', countAfter: 0 }
        ] };
        await page.evaluate(plan => battleCombat.play(document.querySelector('#root'), plan), plan);
        const frames = await page.evaluate(() => window.framesSeen);
        assert(frames.some(f => f[0].icons.some(i => i.code === 'charges' && i.count === '3' && i.width > 0)));
        assert(frames.some(f => f[0].icons.some(i => i.code === 'charges' && i.count === '2') && f[1].icons.length === 0));
        assert(frames.some(f => f.every(unit => unit.icons.length === 0)));
        assert.equal(await page.locator('[data-combat-status-live]').evaluateAll(bars => bars.every(b => b.children.length === 0)), true);
        assert.equal(await page.locator('#root').evaluate(root => root.classList.contains('combat-playing')), false);
        assert.equal(await page.locator('[data-combat-status-static]').first().evaluate(e => getComputedStyle(e).display !== 'none'), true);
        assert.equal(await page.locator('img').count(), 0);
    });
});

test('uses explicit skill flag and frozen HP, then synchronizes final vital values', async () => {
    await scene(async page => {
        const plan = { ...base, hitCount: 1, totalDamage: 500, finalVitals: { '1': { hp: 99, maxHp: 120 }, enemy: { hp: 0, maxHp: 500 } }, events: [
            { source: '1', target: 'enemy', amount: 500, hpAfter: 0, targetMaxHp: 500, kind: 'damage', style: 'magic', tone: 'fire', label: '普通攻击', isSkill: true, critical: true },
            { source: '1', target: '1', amount: 4, hpAfter: 99, targetMaxHp: 120, kind: 'heal', style: 'pulse', tone: 'heal', label: '生命恢复', isSkill: false }
        ] };
        await page.evaluate(plan => { window.playing = battleCombat.play(document.querySelector('#root'), plan); }, plan);
        await page.locator('.combat-effect--skill').waitFor();
        assert.equal(await page.locator('.combat-effect--skill strong').textContent(), '普通攻击');
        await page.evaluate(() => window.playing);
        assert.deepEqual(await page.locator('[data-combat-unit] progress').evaluateAll(bars => bars.map(b => ({ hp: b.value, max: b.max }))),
            [{ hp: 99, max: 120 }, { hp: 0, max: 500 }]);
        assert.equal(await page.locator('[data-combat-total]').textContent(), '500 伤害');
    });
});

test('cancelling playback clears temporary Buff icons and effects', async () => {
    await scene(async page => {
        const plan = { ...base, events: [{ source: '1', target: '1', kind: 'buff', amount: 0, label: '次数', tone: 'light', style: 'pulse', status: status('charges', 3), statusChange: 'Added', countAfter: 3 }] };
        await page.evaluate(plan => { window.playing = battleCombat.play(document.querySelector('#root'), plan); }, plan);
        await page.locator('[data-status-code="charges"]').waitFor();
        await page.evaluate(async () => { battleCombat.cancel(document.querySelector('#root')); await window.playing; });
        assert.equal(await page.locator('[data-status-code]').count(), 0);
        assert.equal(await page.locator('.combat-effect').count(), 0);
    });
});
