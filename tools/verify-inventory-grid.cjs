// Published-client mobile grid check. Every game API is a local read-only fixture.
// node tools/verify-inventory-grid.cjs <published-client/wwwroot>
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { createRequire } = require('node:module');
const { chromium } = createRequire(path.resolve(__dirname, '../Game.Server.Tests/Client/package.json'))('playwright');
const root = path.resolve(process.argv[2]);
assert(fs.existsSync(path.join(root, '_framework/blazor.webassembly.js')));
const output = path.resolve('artifacts/inventory-grid-review-20261002');
fs.mkdirSync(output, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const file = path.resolve(root, '.' + decodeURIComponent(url.pathname));
  if (!file.startsWith(root + path.sep)) return res.writeHead(404).end();
  const resolved = fs.existsSync(file) && fs.statSync(file).isFile() ? file : path.extname(url.pathname) ? null : path.join(root, 'index.html');
  if (!resolved) return res.writeHead(404).end();
  res.writeHead(200, { 'Content-Type': types[path.extname(resolved)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
  fs.createReadStream(resolved).pipe(res);
});
const character = { characterId: 1, name: 'Sollayre', professionCode: 'swordsman', professionName: '骑士', level: 5, hp: 1259, maxHp: 1320, attack: 900, experience: 94, experienceToNextLevel: 150 };
const weapons = [['ember-blade', '余烬长剑'], ['apprentice-wand', '学徒魔杖'], ['tide-saber', '潮汐弯刀'], ['gale-bow', '疾风短弓'], ['stone-hammer', '磐岩战锤'], ['dusk-dagger', '暮影匕首']].map(([code, name], i) => ({
  key: `weapon:${i + 1}`, assetKind: 'weapon', instanceId: i + 1, code, name, category: 'weapons', version: 1, quantity: 1,
  description: '冒险途中获得的武器。', isDefinitionKnown: true, itemLevel: 10 + i, attack: 120 + i * 12, maxHp: 48 + i * 5,
  qualityRank: i % 4, element: i % 6, isEquipped: i === 0, equippedSlotIndex: i === 0 ? 1 : null, isLocked: i === 0,
  formationReferences: i === 0 ? [{ id: 1, name: '火焰突击' }, { id: 2, name: '日常冒险' }] : [],
  actions: [{ action: 'dismantle', allowed: i > 0, reasonCodes: i === 0 ? ['WeaponEquipped'] : [] }]
}));
const materials = [['peacebloom', '宁神花'], ['silverleaf', '银叶草'], ['earthroot', '地根草'], ['briarthorn', '石南草'], ['seed-peacebloom', '宁神花种子'], ['seed-silverleaf', '银叶草种子'], ['seed-earthroot', '地根草种子'], ['seed-briarthorn', '石南草种子']].map(([code, name], i) => ({
  key: `stack:${code}`, assetKind: 'stack', code, name, category: 'planting', quantity: i === 7 ? 2147483647 : 12 + i * 16, tier: 1, isDefinitionKnown: true,
  description: '用于种植与炼金的材料。'
}));
const supply = { ...materials[0], key: 'stack:minor-healing-potion', code: 'minor-healing-potion', name: '初级治疗药剂', category: 'supplies', quantity: 99 };
const unknown = { key: 'stack:unknown', assetKind: 'stack', code: 'unknown', name: '待识别的古老材料', category: 'other', quantity: 5000, isDefinitionKnown: false };
const entries = [...weapons, ...materials, supply, unknown];
const categories = ['weapons', 'souls', 'supplies', 'planting', 'upgrade', 'exchange', 'other'];

async function scene(browser, baseUrl, viewport) {
  const context = await browser.newContext({ viewport, isMobile: viewport.width < 800, hasTouch: viewport.width < 800 });
  const unexpected = [], errors = [];
  let holdCategory, releaseCategory, failCategory;
  try {
    await context.addInitScript(() => localStorage.setItem('authToken', 'local-inventory-grid-fixture'));
    await context.route('**/*', async route => {
      const request = route.request(), url = new URL(request.url());
      const json = body => route.fulfill({ json: body });
      if (/\/appsettings(?:\.Production)?\.json$/.test(url.pathname)) return json({ ApiBaseUrl: baseUrl + '/' });
      if (!url.pathname.startsWith('/api/')) return route.continue();
      if (request.method() !== 'GET') { unexpected.push(`${request.method()} ${url.pathname}`); return route.fulfill({ status: 405 }); }
      switch (url.pathname) {
        case '/api/user/me': return json({ userId: 1, userName: 'fixture', activeCharacterId: 1, characterCount: 1 });
        case '/api/user/character': return json(character);
        case '/api/user/characters': return json([character]);
        case '/api/user/characters/1/inventory': {
          const category = url.searchParams.get('category');
          assert(categories.includes(category), 'The warehouse must request a specific category.');
          if (category === holdCategory) await new Promise(resolve => { releaseCategory = resolve; });
          if (category === failCategory) return route.fulfill({ status: 503, json: { message: '测试读取失败' } });
          const items = entries.filter(item => item.category === category && (!url.searchParams.get('search') || item.name.includes(url.searchParams.get('search'))));
          return json({ characterId: 1, characterName: character.name, gold: 490, weaponCount: weapons.length, ownedStackKinds: materials.length + 2, equippedCount: 1, protectedCount: 1,
            categories: categories.map(category => ({ category, rowCount: entries.filter(item => item.category === category).length })),
            filteredRowCount: items.length, page: 1, pageSize: 40, items });
        }
        case '/api/user/characters/1/inventory/details': {
          const entry = entries.find(item => item.key === url.searchParams.get('key'));
          assert(entry); return json({ characterId: 1, entry });
        }
        default: unexpected.push(url.pathname); return route.fulfill({ status: 404 });
      }
    });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    page.on('pageerror', error => errors.push(error.message));
    const grid = page.locator('.inventory-items');
    const tab = name => page.locator('.inventory-categories').getByRole('button', { name: new RegExp(`^${name}`) });
    const noOverflow = async () => assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${viewport.width}: horizontal overflow`);
    const checkImages = async () => {
      await page.locator('.inventory-items img').evaluateAll(images => Promise.all(images.map(img => img.decode())));
      assert(await page.locator('.inventory-items img').evaluateAll(images => images.every(img => img.naturalWidth > 0 && !img.hidden)));
    };
    const capture = async label => { await page.evaluate(() => scrollTo({ top: 0, behavior: 'instant' })); await page.screenshot({ path: path.join(output, `${viewport.width}-${label}.png`) }); };
    await page.goto(`${baseUrl}/inventory`);
    await grid.waitFor({ timeout: 60000 });
    assert.equal(await tab('全部').count(), 0);
    assert.equal(await tab('武器').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('.inventory-item').count(), weapons.length);
    const searchRect = await page.getByRole('searchbox', { name: '搜索物品' }).boundingBox();
    const filterToggle = page.locator('.inventory-filter-options summary');
    const filterRect = await filterToggle.boundingBox();
    assert(Math.abs(searchRect.y - filterRect.y) <= 2, 'Search and filters must share one row.');
    assert(searchRect.x + searchRect.width <= filterRect.x + 1, 'Search must not overlap the filter button.');
    await filterToggle.click();
    await page.locator('.inventory-filter-fields select').first().waitFor({ state: 'visible' });
    await noOverflow();
    await filterToggle.click();
    await page.locator('.inventory-filter-fields select').first().waitFor({ state: 'hidden' });
    if (viewport.width < 800) assert.equal((await grid.evaluate(el => getComputedStyle(el).gridTemplateColumns)).split(' ').length, 3);
    assert((await page.locator('.inventory-item').first().innerText()).includes('120'));
    assert((await page.locator('.inventory-item').first().innerText()).includes('编2'));
    assert.equal((await filterToggle.locator('strong').innerText()).trim(), '筛选 / 排序');
    await noOverflow(); await checkImages(); await capture('weapons');
    await page.locator('.inventory-item').first().click();
    await page.locator('.inventory-detail').waitFor();
    await noOverflow();
    await page.getByRole('button', { name: '关闭物品详情' }).click();
    await page.waitForFunction(() => document.activeElement?.classList.contains('inventory-item'));
    await page.getByRole('button', { name: '批量整理', exact: true }).click();
    assert(await page.locator('.inventory-row input').first().isDisabled());
    await page.locator('.inventory-item').nth(1).click();
    assert(await page.locator('.inventory-row input').nth(1).isChecked());
    assert.equal(await page.locator('.inventory-detail').count(), 0);
    await page.locator('.inventory-item').nth(1).click();
    assert.equal(await page.locator('.inventory-row input').nth(1).isChecked(), false);
    await page.getByRole('button', { name: '退出批量', exact: true }).click();

    // Hold a category response and then fail it: old weapons must never masquerade as supplies.
    holdCategory = failCategory = 'supplies';
    await tab('补给').click();
    await page.waitForFunction(() => document.querySelector('.inventory-list h2')?.textContent === '补给');
    assert.equal(await page.locator('.inventory-item').count(), 0);
    while (!releaseCategory) await page.waitForTimeout(20);
    holdCategory = undefined; releaseCategory();
    await page.getByRole('button', { name: '重新同步', exact: true }).waitFor();
    assert.equal(await page.locator('.inventory-item').count(), 0);
    failCategory = undefined;
    await page.getByRole('button', { name: '重新同步', exact: true }).click();
    await page.locator('.inventory-tile-name').filter({ hasText: '初级治疗药剂' }).waitFor();
    await tab('种植材料').click();
    await page.waitForFunction(() => document.querySelectorAll('.inventory-item').length === 8);
    if (viewport.width < 800) assert.equal((await grid.evaluate(el => getComputedStyle(el).gridTemplateColumns)).split(' ').length, 4);
    assert.equal(await page.locator('.inventory-tile-stats').count(), 0);
    await noOverflow(); await checkImages(); await capture('materials');
    await page.locator('.inventory-item').first().click(); await page.locator('.inventory-detail-stock').waitFor();
    await noOverflow(); await page.getByRole('button', { name: '关闭物品详情' }).click();
    await tab('其他').click(); await page.locator('.inventory-tile-name').filter({ hasText: unknown.name }).waitFor();
    assert.equal(await page.locator('.inventory-item img').count(), 0);
    await noOverflow();
    await page.goto(`${baseUrl}/inventory?item=stack:peacebloom`);
    await page.locator('.inventory-detail h2').filter({ hasText: '宁神花' }).waitFor();
    assert.equal(await tab('种植材料').getAttribute('aria-pressed'), 'true');
    await page.getByRole('button', { name: '关闭物品详情' }).click();
    if (viewport.width === 320) {
      await page.evaluate(() => document.documentElement.style.fontSize = '125%');
      await noOverflow(); await capture('materials-large-text');
      await tab('武器').click(); await page.locator('.inventory-items--weapons').waitFor(); await noOverflow(); await capture('weapons-large-text');
    }
    assert.deepEqual(unexpected, []); assert.deepEqual(errors, []);
    console.log(`PASS inventory grid ${viewport.width}x${viewport.height}: categories, geometry, images, detail, selection, recovery, deep link`);
  } finally { await context.close(); }
}

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    for (const viewport of [{ width: 390, height: 844 }, { width: 375, height: 667 }, { width: 320, height: 568 }, { width: 1440, height: 1000 }])
      await scene(browser, `http://127.0.0.1:${server.address().port}`, viewport);
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
