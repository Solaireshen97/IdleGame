// Published-client acceptance against a disposable loopback release database only.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { randomUUID } = require('node:crypto');
const { createRequire } = require('node:module');

async function main() {
  const [baseUrl, outputDirectory, databasePath] = process.argv.slice(2);
  const endpoint = new URL(baseUrl);
  assert(endpoint.protocol === 'http:' && endpoint.hostname === '127.0.0.1' && endpoint.port &&
    !endpoint.username && !endpoint.password && endpoint.pathname === '/' && !endpoint.search && !endpoint.hash,
  'Only an explicit loopback HTTP endpoint is permitted.');
  const root = path.resolve(__dirname, '../artifacts/server-release-smoke');
  const output = path.resolve(outputDirectory);
  const database = path.resolve(databasePath);
  assert(path.dirname(output) === root && /^[a-f0-9]{32}$/.test(path.basename(output)), 'Invalid isolated run directory.');
  assert(path.dirname(database) === path.join(output, 'data') && fs.statSync(database).isFile(), 'Database must be directly inside this run/data.');
  for (const target of [output, database]) {
    for (let current = target; ; current = path.dirname(current)) {
      assert(!fs.lstatSync(current).isSymbolicLink(), `Symbolic links are forbidden: ${current}`);
      if (path.dirname(current) === current) break;
    }
  }
  const clientRequire = createRequire(path.resolve(__dirname, '../Game.Server.Tests/Client/package.json'));
  assert.equal(clientRequire('./package.json').devDependencies.playwright, '1.62.1');
  assert.equal(clientRequire('playwright/package.json').version, '1.62.1');
  const { DatabaseSync } = require('node:sqlite');
  const db = new DatabaseSync(database);
  db.exec('PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;');
  const config = JSON.parse(fs.readFileSync(path.join(output, 'server/appsettings.json'), 'utf8'));
  const checks = [], errors = [], commands = [], managementCommands = [];
  const evidence = { status: 'running', checkedAtUtc: new Date().toISOString(), browser: 'Microsoft Edge', playwright: '1.62.1', checks, commands, managementCommands, errors };
  const record = (name, detail = {}) => { checks.push({ name, passed: true, ...detail }); console.log(`PASS ${name}`); };
  const api = async (route, method = 'GET', body, expectedStatus = 200) => {
    const response = await fetch(`${endpoint.origin}${route}`, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await response.text();
    assert.equal(response.status, expectedStatus, `${method} ${route}: ${text}`);
    return text ? JSON.parse(text) : null;
  };
  let token, browser, page;
  try {
    const credentials = { userName: `inventory-${randomUUID().replaceAll('-', '').slice(0, 12)}`, password: `Inventory!${randomUUID()}` };
    const registered = await api('/api/user/register', 'POST', credentials);
    const login = await api('/api/user/login', 'POST', credentials);
    token = login.token;
    assert.equal(login.userId, registered.userId);
    const a = await api('/api/user/characters', 'POST', { name: 'Inventory A', professionCode: 'swordsman' });
    const b = await api('/api/user/characters', 'POST', { name: 'Inventory B', professionCode: 'swordsman' });
    await api('/api/user/character/select', 'POST', { characterId: a.characterId });
    const ids = [a.characterId, b.characterId];
    evidence.characters = ids;
    for (const id of ids) assert.equal(db.prepare('SELECT UserId FROM Characters WHERE Id=?').get(id).UserId, login.userId, 'Seeding must target only the new account.');
    const otherBefore = db.prepare('SELECT * FROM Characters WHERE UserId<>? ORDER BY Id').all(login.userId);
    const schema = table => db.prepare(`PRAGMA table_info("${table}")`).all();
    const insert = (table, values) => {
      const fields = schema(table).filter(column => Object.hasOwn(values, column.name) && column.name !== 'Id').map(column => column.name);
      assert(fields.length > 0);
      return Number(db.prepare(`INSERT INTO "${table}" (${fields.map(x => `"${x}"`).join(',')}) VALUES (${fields.map(() => '?').join(',')})`).run(...fields.map(x => values[x])).lastInsertRowid);
    };
    const recipe = config.WeaponBreakthrough.Recipes[0];
    const stackCodes = [...new Set([config.Consumables.Items[0].Code, config.Planting.Plants[0].SeedCode,
      config.Planting.Plants[0].MaterialCode, 'weapon-fragment-t1', recipe.FragmentCode, recipe.StoneCode,
      config.DungeonExchange.Offers[0].CurrencyCode, 'acceptance-unknown-stack'])];
    const samples = {};
    db.exec('BEGIN IMMEDIATE');
    try {
      for (const id of ids) {
        db.prepare('UPDATE Characters SET Gold=200000, Version=Version+1 WHERE Id=? AND UserId=?').run(id, login.userId);
        for (const code of stackCodes) {
          const existing = db.prepare('SELECT Id FROM CharacterItemStacks WHERE CharacterId=? AND ItemCode=?').get(id, code);
          if (existing) db.prepare('UPDATE CharacterItemStacks SET Quantity=?, Version=Version+1 WHERE Id=? AND CharacterId=?').run(id === a.characterId ? 5000 : 7000, existing.Id, id);
          else insert('CharacterItemStacks', { CharacterId: id, ItemCode: code, Quantity: id === a.characterId ? 5000 : 7000, Version: 0 });
        }
        for (const code of [config.SoulImprints.Items[0].Code, 'acceptance-unknown-soul']) insert('CharacterSoulImprints', {
          CharacterId: id, SoulImprintCode: code, EquippedSlotIndex: null, AutoUseEnabled: 0, AutoConditionOverride: null,
          AutoHpThresholdPercent: 50, IsLocked: 0, AcquiredAtUtc: new Date().toISOString(), Version: 0
        });
        const starter = db.prepare('SELECT * FROM CharacterWeapons WHERE CharacterId=? ORDER BY Id LIMIT 1').get(id);
        assert(starter, 'Fresh character needs a real starter template.');
        const skills = db.prepare('SELECT * FROM CharacterWeaponSkills WHERE WeaponId=? ORDER BY SlotIndex').all(starter.Id);
        assert(skills.length > 0);
        for (let i = 0; i < 45; i++) {
          const name = i === 0 ? 'Acceptance Sell' : i === 1 ? 'Acceptance Batch One' : i === 2 ? 'Acceptance Batch Two' : i === 3 ? 'Acceptance Growth' : i === 4 ? 'Acceptance Stale' : i === 44 ? '龟尾 Acceptance 44' : `ZZ Acceptance ${String(i).padStart(2, '0')}`;
          const weaponId = insert('CharacterWeapons', { ...starter, CharacterId: id, Origin: 'Drop', Name: name, EquippedSlotIndex: null, IsLocked: 0, QualityRank: 0, Version: 0 });
          for (const skill of skills) insert('CharacterWeaponSkills', { ...skill, WeaponId: weaponId });
          if (id === a.characterId) samples[name] = weaponId;
        }
      }
      db.exec('COMMIT');
    } catch (error) { db.exec('ROLLBACK'); throw error; }
    record('isolated-account-seeding', { characterIds: ids, extraWeaponsPerCharacter: 45 });
    const inventoryRoute = id => `/api/user/characters/${id}/inventory`;
    const weaponRoute = `/api/user/characters/${a.characterId}/weapons`;
    const quantity = code => db.prepare('SELECT Quantity FROM CharacterItemStacks WHERE CharacterId=? AND ItemCode=?').get(a.characterId, code).Quantity;
    const gold = () => db.prepare('SELECT Gold FROM Characters WHERE Id=?').get(a.characterId).Gold;
    const weapon = id => db.prepare('SELECT * FROM CharacterWeapons WHERE CharacterId=? AND Id=?').get(a.characterId, id);
    const overview = await api(inventoryRoute(a.characterId));
    assert.equal(overview.weaponCount, db.prepare('SELECT COUNT(*) AS Count FROM CharacterWeapons WHERE CharacterId=?').get(a.characterId).Count); assert.equal(overview.soulImprintCount, 2);
    for (const category of ['weapons', 'souls', 'supplies', 'planting', 'upgrade', 'exchange', 'other']) assert(overview.categories.find(x => x.category === category)?.rowCount > 0, `Missing category ${category}`);
    record('overview-categories-and-ownership');
    const { chromium } = clientRequire('playwright');
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    // Observe focus transitions without changing the UI's behavior, for failure diagnosis.
    await page.addInitScript(() => {
      let value;
      window.__inventoryFocusTrace = [];
      const focus = () => ({ tag: document.activeElement.tagName, class: document.activeElement.className });
      Object.defineProperty(window, 'inventoryUi', { configurable: true, get: () => value, set(api) {
        for (const name of ['focusDialog', 'restoreFocus', 'refocusDialog', 'connect', 'disconnect']) {
          const original = api[name];
          api[name] = function (...args) {
            const before = focus(), result = original.apply(this, args);
            window.__inventoryFocusTrace.push({ name, key: args[2], registration: name === 'connect' ? result : name === 'disconnect' ? args[0] : undefined, before, after: focus(),
              items: Array.from(document.querySelectorAll('.inventory-item')).map(item => ({ key: item.dataset.inventoryKey, disabled: item.disabled })) });
            return result;
          };
        }
        value = api;
      } });
    });
    page.setDefaultTimeout(15000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => {
      if (message.type() !== 'error') return;
      const location = message.location().url;
      const match = message.text().match(/^Failed to load resource: the server responded with a status of (401|409|503) \([^)]*\)$/);
      if (match && location.startsWith(`${endpoint.origin}/api/`) &&
        (match[1] === '401' || match[1] === '409' && location.endsWith('/weapons/dismantle') || match[1] === '503' && /\/inventory\?/.test(location))) return;
      errors.push(`Console error: ${message.text()}`);
    });
    page.on('request', request => {
      if (request.method() === 'POST' && /\/weapons\/(sell|dismantle)$/.test(new URL(request.url()).pathname)) commands.push({ route: new URL(request.url()).pathname, body: request.postDataJSON() });
      if (request.method() === 'POST' && /\/weapons\/.*(?:enhance|upgrade|craft)$/.test(new URL(request.url()).pathname)) managementCommands.push({ route: new URL(request.url()).pathname, body: request.postDataJSON() });
    });
    const visible = locator => locator.waitFor({ state: 'visible' });
    const refresh = () => page.getByRole('button', { name: '刷新仓库', exact: true }).click();
    const open = async name => { await page.getByRole('button').filter({ has: page.locator('strong', { hasText: new RegExp(`^${name}$`) }) }).click(); await visible(page.locator('.inventory-detail h2').filter({ hasText: name })); };
    const close = () => page.getByRole('button', { name: '关闭物品详情', exact: true }).click();
    const gotoInventory = async query => {
      await page.goto(`${endpoint.origin}/inventory?characterId=${a.characterId}${query || ''}`);
      await visible(page.locator('.inventory-summary'));
    };
    const waitWrite = async (suffix, action) => {
      const response = page.waitForResponse(r => new URL(r.url()).pathname.endsWith(suffix) && r.request().method() !== 'GET');
      await action(); const result = await response; assert.equal(result.status(), 200, await result.text());
      await page.waitForFunction(() => !document.querySelector('.inventory-detail button:disabled')?.textContent?.includes('处理中'));
      return result.json();
    };
    await page.goto(`${endpoint.origin}/login`, { timeout: 60000 });
    await page.locator('#login-user-name').waitFor({ state: 'visible', timeout: 60000 });
    await page.locator('#login-user-name').fill(credentials.userName);
    await page.locator('#login-password').fill(credentials.password);
    await page.getByRole('button', { name: '进入冒险', exact: true }).click();
    await page.waitForURL(url => !url.pathname.includes('/login'));
    const lifecycle = await page.evaluate(() => {
      let calls = 0;
      const registration = inventoryUi.connect({ invokeMethodAsync() { calls++; return Promise.resolve(); } });
      window.dispatchEvent(new Event('focus')); const whileConnected = calls;
      inventoryUi.disconnect(registration); window.dispatchEvent(new Event('focus'));
      return { registration, whileConnected, afterDisconnect: calls };
    });
    assert(Number.isInteger(lifecycle.registration) && lifecycle.registration > 0);
    assert.equal(lifecycle.whileConnected, 1); assert.equal(lifecycle.afterDisconnect, 1);
    record('visibility-client-registration-disconnect-lifecycle');
    await gotoInventory();
    assert((await page.locator('.inventory-summary').innerText()).includes('200,000'));
    assert.equal(overview.ownedStackKinds, db.prepare('SELECT COUNT(*) AS Count FROM CharacterItemStacks WHERE CharacterId=? AND Quantity>0').get(a.characterId).Count);
    for (const [category, label] of [['weapons', '武器'], ['souls', '魂印'], ['supplies', '补给'], ['planting', '种植材料'], ['upgrade', '养成材料'], ['exchange', '兑换材料'], ['other', '其他']]) {
      const button = page.locator('.inventory-categories').getByRole('button', { name: new RegExp(`^${label}`) });
      await button.click();
      await page.waitForFunction(name => document.querySelector('.inventory-list h2')?.textContent === name, label);
      // Keep auditing other flows, but fail the final run if any accessibility assertion fails.
      if (await button.getAttribute('aria-pressed') !== 'true') errors.push(`Selected category ${label} must expose aria-pressed=true.`);
      const expected = overview.categories.find(x => x.category === category).rowCount;
      await page.waitForFunction(count => document.querySelector('.inventory-list header')?.textContent.includes(`${count} 条结果`), expected);
      assert((await page.locator('.inventory-list header').innerText()).includes(`${expected} 条结果`));
    }
    assert.equal(await page.locator('.inventory-categories').getByRole('button', { name: /^全部/ }).count(), 0);
    await page.locator('.inventory-categories').getByRole('button', { name: /^武器/ }).click();
    record('all-category-buttons-and-filter-counts');
    await page.getByRole('searchbox', { name: '搜索物品' }).fill('Acceptance Batch');
    await page.waitForFunction(() => document.querySelectorAll('.inventory-row').length === 2);
    assert((await page.locator('.inventory-list').innerText()).includes('2 条结果'));
    await page.getByRole('searchbox', { name: '搜索物品' }).fill('');
    await page.getByRole('button', { name: '下一页', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.inventory-pagination')?.textContent.includes('第 2 /'));
    record('search-and-pagination');
    await page.locator('.inventory-header-tools select').selectOption(String(b.characterId));
    await page.waitForFunction(() => document.querySelector('.inventory-context')?.textContent.includes('Inventory B'));
    assert.equal((await api('/api/user/me')).activeCharacterId, a.characterId);
    await page.locator('.inventory-config-links').getByRole('button', { name: /管理编队/ }).click();
    await page.waitForURL(url => url.pathname === '/formations');
    assert.equal((await api('/api/user/me')).activeCharacterId, b.characterId);
    await visible(page.locator('[aria-label="已保存的编队"]'));
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await page.waitForTimeout(100);
    record('view-role-is-independent-business-navigation-selects-role');
    await gotoInventory('&category=weapons');
    await open('Acceptance Sell');
    await waitWrite('/lock', () => page.locator('.inventory-detail-actions').getByRole('button', { name: '锁定', exact: true }).click());
    await visible(page.locator('.inventory-detail-actions').getByRole('button', { name: '解锁', exact: true }));
    assert.equal(weapon(samples['Acceptance Sell']).IsLocked, 1);
    await waitWrite('/lock', () => page.locator('.inventory-detail-actions').getByRole('button', { name: '解锁', exact: true }).click());
    await visible(page.locator('.inventory-detail-actions').getByRole('button', { name: '锁定', exact: true }));
    assert.equal(weapon(samples['Acceptance Sell']).IsLocked, 0); record('lock-and-unlock');
    const sellGold = gold(), sellExpected = weapon(samples['Acceptance Sell']).SellGold;
    await page.locator('.inventory-detail-actions').getByRole('button', { name: '出售', exact: true }).click();
    await visible(page.getByRole('dialog', { name: '出售 1 件物品' }));
    assert(weapon(samples['Acceptance Sell'])); assert.equal(gold(), sellGold);
    await waitWrite('/sell', () => page.getByRole('button', { name: '确认出售', exact: true }).click());
    assert(!weapon(samples['Acceptance Sell'])); assert.equal(gold(), sellGold + sellExpected);
    record('single-sell-preview-confirm-exact-wallet-change');
    await page.getByRole('button', { name: '批量整理', exact: true }).click();
    for (const name of ['Acceptance Batch One', 'Acceptance Batch Two']) await page.getByRole('checkbox', { name: `选择${name}用于分解`, exact: true }).check();
    const fragmentBefore = quantity('weapon-fragment-t1');
    await page.locator('.inventory-batch-bar').getByRole('button', { name: /预览.*分解.*收益/ }).click();
    const dialog = page.getByRole('dialog', { name: '分解 2 件物品' }); await visible(dialog);
    assert.equal(quantity('weapon-fragment-t1'), fragmentBefore);
    const expectedFragments = ['Acceptance Batch One', 'Acceptance Batch Two'].reduce((sum, name) => sum + weapon(samples[name]).DismantleFragments, 0);
    await waitWrite('/dismantle', () => dialog.getByRole('button', { name: '确认分解', exact: true }).click());
    for (const name of ['Acceptance Batch One', 'Acceptance Batch Two']) assert(!weapon(samples[name]));
    assert.equal(quantity('weapon-fragment-t1'), fragmentBefore + expectedFragments);
    for (const command of commands) {
      assert.match(command.body.requestId, /^(?:[a-f0-9]{32}|[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})$/i); assert(command.body.expectedVersions.length > 0); assert(command.body.outcomeFingerprint.length > 0);
      const wallet = gold(), fragments = quantity('weapon-fragment-t1');
      await api(command.route, 'POST', command.body);
      assert.equal(gold(), wallet); assert.equal(quantity('weapon-fragment-t1'), fragments);
    }
    assert.equal(commands.length, 2); record('batch-preview-confirm-required-fields-and-idempotent-replay');
    const exitBatch = page.getByRole('button', { name: '退出批量', exact: true });
    if (await exitBatch.count()) await exitBatch.click();
    else await visible(page.getByRole('button', { name: '批量整理', exact: true }));
    await open('Acceptance Growth');
    const skillBefore = db.prepare('SELECT * FROM CharacterWeaponSkills WHERE WeaponId=? ORDER BY SlotIndex').all(samples['Acceptance Growth']);
    const enhance = page.locator('.inventory-detail').getByRole('button', { name: /^强化 ·/ }).first();
    const enhanceBefore = quantity('weapon-fragment-t1');
    await waitWrite('/enhance', () => enhance.click());
    await page.waitForFunction(() => document.querySelector('.growth-skill small')?.textContent.includes('Lv.2'));
    const skillAfter = db.prepare('SELECT * FROM CharacterWeaponSkills WHERE WeaponId=? ORDER BY SlotIndex').all(samples['Acceptance Growth']);
    assert.equal(skillAfter[0].EnhancementLevel, skillBefore[0].EnhancementLevel + 1); assert(quantity('weapon-fragment-t1') < enhanceBefore);
    const stoneBefore = quantity(recipe.StoneCode);
    await page.locator('.inventory-detail').getByRole('button', { name: /^使用 .*持有/ }).click();
    await visible(page.getByRole('dialog', { name: '确认武器突破' }));
    assert.equal(quantity(recipe.StoneCode), stoneBefore);
    await waitWrite('/quality/upgrade', () => page.getByRole('button', { name: '确认消耗并突破', exact: true }).click());
    assert.equal(weapon(samples['Acceptance Growth']).QualityRank, 1); assert.equal(quantity(recipe.StoneCode), stoneBefore - 1);
    await page.locator('[aria-labelledby="inventory-management-title"]').waitFor({ state: 'hidden' });
    record('weapon-enhance-and-universal-breakthrough');
    await page.getByRole('button', { name: '选择同名武器素材', exact: true }).click();
    await visible(page.locator('[aria-labelledby="inventory-material-title"]'));
    await page.keyboard.press('Escape'); await page.locator('[aria-labelledby="inventory-material-title"]').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('.inventory-detail')?.contains(document.activeElement));
    await page.getByRole('button', { name: '选择同名武器素材', exact: true }).click();
    const materialId = samples['ZZ Acceptance 05'];
    await page.locator('[aria-labelledby="inventory-material-title"]').getByRole('button').filter({ has: page.locator('strong', { hasText: /^ZZ Acceptance 05$/ }) }).click();
    await visible(page.getByRole('dialog', { name: '确认武器突破' }));
    assert((await page.getByRole('dialog', { name: '确认武器突破' }).innerText()).includes(`实例 #${materialId}`), 'Confirmation must name the material selected by the user.');
    assert(weapon(materialId));
    const beforeMaterialCount = db.prepare('SELECT COUNT(*) AS Count FROM CharacterWeapons WHERE CharacterId=?').get(a.characterId).Count;
    await waitWrite('/quality/upgrade', () => page.getByRole('button', { name: '确认消耗并突破', exact: true }).click());
    assert(!weapon(materialId)); assert.equal(weapon(samples['Acceptance Growth']).QualityRank, 2);
    assert.equal(db.prepare('SELECT COUNT(*) AS Count FROM CharacterWeapons WHERE CharacterId=?').get(a.characterId).Count, beforeMaterialCount - 1);
    record('same-template-material-picker-escape-focus-and-consumption');
    await page.locator('[aria-labelledby="inventory-management-title"]').waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: '选择同名武器素材', exact: true }).click();
    await visible(page.locator('[aria-labelledby="inventory-material-title"]'));
    await page.locator('[aria-labelledby="inventory-material-title"] .inventory-preview-items').evaluate(list => { list.scrollTop = list.scrollHeight; });
    const edgeMaterialId = samples['ZZ Acceptance 43'];
    await page.locator('[aria-labelledby="inventory-material-title"]').getByRole('button').filter({ has: page.locator('strong', { hasText: /^ZZ Acceptance 43$/ }) }).click();
    await visible(page.getByRole('dialog', { name: '确认武器突破' }));
    assert((await page.getByRole('dialog', { name: '确认武器突破' }).innerText()).includes(`实例 #${edgeMaterialId}`), 'Virtualized boundary selection must confirm the exact selected instance.');
    const beforeEdgeCount = db.prepare('SELECT COUNT(*) AS Count FROM CharacterWeapons WHERE CharacterId=?').get(a.characterId).Count;
    await waitWrite('/quality/upgrade', () => page.getByRole('button', { name: '确认消耗并突破', exact: true }).click());
    assert(!weapon(edgeMaterialId)); assert.equal(weapon(samples['Acceptance Growth']).QualityRank, 3);
    assert.equal(db.prepare('SELECT COUNT(*) AS Count FROM CharacterWeapons WHERE CharacterId=?').get(a.characterId).Count, beforeEdgeCount - 1);
    record('virtualized-boundary-material-selection-exact-consumption');
    await close(); await gotoInventory(`&item=${encodeURIComponent(`stack:${recipe.FragmentCode}`)}`);
    await visible(page.locator('.inventory-detail'));
    const craftFragments = quantity(recipe.FragmentCode), craftStone = quantity(recipe.StoneCode);
    await waitWrite('/breakthrough-stones/craft', () => page.getByRole('button', { name: '合成 1 枚', exact: true }).click());
    assert.equal(quantity(recipe.FragmentCode), craftFragments - recipe.FragmentsPerStone); assert.equal(quantity(recipe.StoneCode), craftStone + 1);
    record('breakthrough-stone-craft');
    await close(); await gotoInventory('&search=Acceptance Stale'); await open('Acceptance Stale');
    await page.locator('.inventory-detail-actions').getByRole('button', { name: '分解', exact: true }).click();
    await visible(page.getByRole('dialog', { name: '分解 1 件物品' }));
    await api(`${weaponRoute}/${samples['Acceptance Stale']}/lock`, 'PUT', { isLocked: true });
    const rejectedBefore = quantity('weapon-fragment-t1');
    const rejected = page.waitForResponse(r => r.url().endsWith('/weapons/dismantle') && r.request().method() === 'POST');
    await page.getByRole('button', { name: '确认分解', exact: true }).click();
    assert.equal((await rejected).status(), 409); assert(weapon(samples['Acceptance Stale'])); assert.equal(quantity('weapon-fragment-t1'), rejectedBefore);
    await visible(page.getByRole('dialog').getByRole('alert'));
    await page.waitForFunction(() => document.querySelector('.inventory-modal')?.contains(document.activeElement));
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);
    if (await page.getByRole('dialog').isVisible()) {
      errors.push(`Escape must close a rejected confirmation; actual focus: ${await page.evaluate(() => `${document.activeElement.tagName}.${document.activeElement.className}`)}`);
      await page.getByRole('dialog').getByRole('button', { name: '返回仓库', exact: true }).click();
    }
    await page.getByRole('dialog').waitFor({ state: 'hidden' }); await close();
    record('lock-after-preview-rejected-with-no-loss');
    await gotoInventory('&category=other&search=acceptance-unknown');
    await page.waitForFunction(() => document.querySelectorAll('.inventory-row').length === 2);
    await open('acceptance-unknown-soul');
    assert.equal(await page.locator('.inventory-detail-actions').getByRole('button', { name: '分解', exact: true }).isEnabled(), false);
    assert((await page.locator('.inventory-detail').innerText()).includes('已保留真实持有数量'));
    await close(); await gotoInventory('&category=other&search=acceptance-unknown');
    await open('acceptance-unknown-stack'); assert((await page.locator('.inventory-detail-stock').innerText()).includes('5,000')); await close();
    record('unknown-soul-and-stack-preserved-disabled');
    const deepId = samples['龟尾 Acceptance 44'];
    await gotoInventory(`&category=weapons&page=1&item=weapon:${deepId}`); await visible(page.locator('.inventory-detail h2').filter({ hasText: '龟尾 Acceptance 44' }));
    assert.equal(await page.locator('.inventory-row strong').filter({ hasText: /^龟尾 Acceptance 44$/ }).count(), 0);
    await refresh(); await visible(page.locator('.inventory-detail h2').filter({ hasText: '龟尾 Acceptance 44' }));
    for (const control of await page.locator('.inventory-detail button').all()) {
      await control.evaluate(button => button.scrollIntoView({ block: 'center', inline: 'nearest' }));
      await page.waitForTimeout(50);
      const visibility = await control.evaluate(button => {
        const rect = button.getBoundingClientRect();
        const clipped = [];
        for (let parent = button.parentElement; parent; parent = parent.parentElement) {
          if (!['hidden', 'clip', 'auto', 'scroll'].includes(getComputedStyle(parent).overflowX)) continue;
          const boundary = parent.getBoundingClientRect();
          if (rect.left < boundary.left - 1 || rect.right > boundary.right + 1) clipped.push(parent.className);
        }
        const top = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
        return { name: button.textContent.trim(), clipped, inViewport: rect.left >= 0 && rect.right <= innerWidth,
          hittable: top === button || button.contains(top) };
      });
      assert.deepEqual(visibility.clipped, [], `${visibility.name} is clipped by its shell.`);
      assert(visibility.inViewport && visibility.hittable, `${visibility.name} must be visible and unobstructed.`);
    }
    await page.screenshot({ path: path.join(output, 'inventory-desktop.png'), fullPage: true });
    record('deep-link-outside-page-survives-refresh');
    await close(); const oldSummary = await page.locator('.inventory-summary').innerText();
    await page.route('**/api/user/characters/*/inventory?*', route => route.fulfill({ status: 503, contentType: 'application/json', body: '"InventoryAcceptanceFailure"' }));
    await refresh(); await visible(page.getByRole('button', { name: '重新同步', exact: true }));
    assert.equal(await page.locator('.inventory-summary').innerText(), oldSummary);
    await page.unroute('**/api/user/characters/*/inventory?*'); await refresh(); record('failed-refresh-keeps-owned-quantities');
    for (const [route, loaded, destination] of [['/weapons', '.formation-edit-sheet', '/formations'], ['/supplies', '.formation-edit-sheet', '/formations'], ['/talents', '.formation-edit-sheet', '/formations'], ['/gathering', '.garden-summary'], ['/production', '.alchemy-bench'], ['/shop', '.shop-summary'], ['/formations', '[aria-label="已保存的编队"]']]) {
      const response = await page.goto(`${endpoint.origin}${route}`); assert.equal(response.status(), 200);
      await page.locator('main').waitFor({ state: 'visible' });
      await page.waitForFunction(() => document.querySelector('main')?.innerText.trim().length > 20);
      await visible(page.locator(loaded));
      assert.equal(await page.locator('#blazor-error-ui').isVisible(), false); assert.equal(new URL(page.url()).pathname, destination || route);
      assert.equal(await page.locator('.mobile-dock > *').count(), 4);
    }
    record('unified-formation-navigation-and-legacy-routes');
    await page.setViewportSize({ width: 390, height: 844 }); await gotoInventory('&search=Acceptance Growth');
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile viewport overflows horizontally.');
    await open('Acceptance Growth');
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile detail overflows horizontally.');
    await page.locator('.inventory-detail-actions').getByRole('button', { name: '出售', exact: true }).click();
    await visible(page.getByRole('dialog', { name: '出售 1 件物品' }));
    assert(await page.evaluate(() => document.querySelector('.inventory-modal').contains(document.activeElement)), 'Dialog must receive keyboard focus.');
    assert(await page.evaluate(() => {
      const close = document.querySelector('.inventory-modal button[aria-label="关闭确认"]');
      const rect = close.getBoundingClientRect(), hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
      const header = document.querySelector('.top-row');
      return (hit === close || close.contains(hit)) && (!header || Number(getComputedStyle(document.querySelector('.inventory-modal-backdrop')).zIndex) > Number(getComputedStyle(header).zIndex));
    }), 'Mobile dialog must remain above the persistent header.');
    await page.keyboard.press('Shift+Tab'); assert(await page.evaluate(() => document.querySelector('.inventory-modal').contains(document.activeElement)), 'Dialog must trap keyboard focus.');
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile confirm overflows horizontally.');
    await page.screenshot({ path: path.join(output, 'inventory-mobile.png'), fullPage: true });
    await page.keyboard.press('Escape'); await page.locator('.inventory-modal').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('.inventory-detail')?.contains(document.activeElement));
    assert(await page.evaluate(() => document.querySelector('.inventory-detail').contains(document.activeElement)), 'Closing confirm must restore detail focus.');
    await page.keyboard.press('Escape'); await page.locator('.inventory-detail').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.activeElement?.classList.contains('inventory-item'));
    assert(await page.evaluate(() => document.activeElement?.classList.contains('inventory-item')), 'Closing detail must restore item focus.');
    record('mobile-no-overflow-dialog-escape-and-focus');
    await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' }));
    await page.screenshot({ path: path.join(output, 'inventory-mobile-overview.png'), fullPage: false });
    await page.setViewportSize({ width: 1440, height: 1000 }); await gotoInventory('&category=weapons&search=Acceptance Growth'); await open('Acceptance Growth');
    await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' })); await page.waitForTimeout(100);
    await page.screenshot({ path: path.join(output, 'inventory-desktop-overview.png'), fullPage: false });
    assert.deepEqual(db.prepare('SELECT * FROM Characters WHERE UserId<>? ORDER BY Id').all(login.userId), otherBefore);
    assert.equal(await page.locator('#blazor-error-ui').isVisible(), false);
    assert.deepEqual(errors, []); record('other-accounts-untouched-and-no-runtime-errors');
    evidence.status = 'passed';
  } catch (error) {
    evidence.status = 'failed'; evidence.failure = error.stack;
    if (page) { await page.screenshot({ path: path.join(output, 'inventory-failed.png'), fullPage: true }).catch(() => {}); fs.writeFileSync(path.join(output, 'inventory-failed.html'), await page.content()); evidence.page = page.url(); evidence.focus = await page.evaluate(() => ({ tag: document.activeElement.tagName, class: document.activeElement.className })); evidence.focusTrace = await page.evaluate(() => window.__inventoryFocusTrace); }
    throw error;
  } finally {
    fs.writeFileSync(path.join(output, 'inventory-browser.json'), JSON.stringify(evidence, null, 2));
    if (browser) await browser.close(); db.close();
  }
}
main().catch(error => { console.error(error.stack); process.exitCode = 1; });
