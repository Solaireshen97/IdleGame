// Run against a published client; all game APIs are local fixtures, with no real account or database.
// dotnet publish Game.Client -c Release -o .codex-tmp/dialog-refresh-regression/client
// node tools/verify-dungeon-dialog.cjs .codex-tmp/dialog-refresh-regression/client/wwwroot
// Add --formation-preview to verify the visual formation selector and capture mobile screenshots.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { createRequire } = require('node:module');
const { chromium } = createRequire(path.resolve(__dirname, '../Game.Server.Tests/Client/package.json'))('playwright');

const root = path.resolve(process.argv[2] || '.codex-tmp/dialog-refresh-regression/client/wwwroot');
assert(fs.existsSync(path.join(root, '_framework/blazor.webassembly.js')), 'Publish the client before running this check.');
const visualMode = process.argv.includes('--formation-preview');
const output = path.resolve(visualMode ? '.codex-tmp/formation-visual-preview/evidence' : '.codex-tmp/dialog-refresh-regression/evidence');
fs.mkdirSync(output, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json',
  '.wasm': 'application/wasm', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = http.createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1');
  const file = path.resolve(root, '.' + decodeURIComponent(url.pathname));
  if (!file.startsWith(root + path.sep)) { response.writeHead(404).end(); return; }
  const resolved = fs.existsSync(file) && fs.statSync(file).isFile() ? file
    : path.extname(url.pathname) ? null : path.join(root, 'index.html');
  if (!resolved) { response.writeHead(404).end(); return; }
  response.writeHead(200, { 'Content-Type': types[path.extname(resolved)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
  fs.createReadStream(resolved).pipe(response);
});

const weapon = { source: '击杀掉落', name: '赤焰长剑', kind: 'Weapon', code: 'test-weapon', quantity: 1,
  chancePercent: 10, weapon: { element: 0, itemLevel: 10, attack: 30, maxHp: 40, skills: [] } };
const monsters = Array.from({ length: 5 }, (_, i) => ({ name: i === 4 ? '熔岩领主' : `熔岩守卫${i + 1}`,
  element: 0, maxHp: 1000, attack: 30, defense: 10, waveNumber: i + 1, position: 1, isBoss: i === 4,
  drops: [weapon, { source: '击杀掉落', name: '武器碎片', kind: 'Material', code: 'weapon-fragment-t1', quantity: 2, chancePercent: 50 }] }));
const dungeon = { dungeonId: 101, code: 'test-depths', name: '熔渊裂隙·深层', regionCode: 'test-region',
  regionName: '赤焰荒原', dungeonKind: 'Dungeon', supportsDepths: true, stage: 1, maximumDepth: 10,
  depthLevel: 1, minimumLevel: 1, recommendedLevel: 10, waveCount: 5, monsterCount: 5, slotCount: 5,
  monsterName: '熔岩领主', monsterElement: 0, monsterMaxHp: 1000, monsterAttack: 30, monsterDefense: 10,
  partyHpPercentages: [100], monsters, rewardPreview: [],
  depths: Array.from({ length: 10 }, (_, i) => ({ depthLevel: i + 1, isUnlocked: i < 4, isChallenge: i >= 4,
    statMultiplier: 1, attackMultiplier: 1, addedMechanics: [] })) };
const secondDungeon = { ...dungeon, dungeonId: 102, code: 'test-hunt', name: '燧谷野猪讨伐', supportsDepths: false,
  maximumDepth: 1, depths: [], waveCount: 1, monsterCount: 1, monsters: [{ ...monsters[0], name: '燧谷野猪' }] };
const progressEntry = { dungeonId: 101, unlockedDepth: 4, characterHighestDepth: 1, masteryLevel: 1,
  canEnter: true, autoUnlocked: true, isClearedByCurrentUser: true };
const character = { characterId: 1, name: '测试骑士', professionCode: 'swordsman', professionName: '骑士',
  level: 10, hp: 1000, maxHp: 1000, attack: 100, experience: 0, experienceToNextLevel: 1000 };
const knightSkills = [ ['sword-slash', '斩击'], ['knight-faith-barrier', '信仰壁垒'], ['knight-rebuke', '责难'],
  ['knight-invigorate', '振奋精神'], ['knight-holy-aura', '神圣光环'] ].map(([code, name]) => ({ code, name }));
const mageSkills = [ ['mage-arcane-bolt', '奥术飞弹'], ['mage-frost-bolt', '寒冰箭'], ['mage-scorch', '灼烧'],
  ['mage-spellbreak', '法术反制'], ['mage-arcane-domain', '奥术领域'] ].map(([code, name]) => ({ code, name }));
const formation = { id: 1, characterId: 1, name: '焰锋突击', groupElement: 0, position: 1, version: 1,
  loadout: { professionCode: 'swordsman', weapons: [{ slotIndex: 1, weaponId: 11 }],
    skills: knightSkills.map((skill, i) => ({ slotIndex: i + 1, skillCode: skill.code, autoUseEnabled: i < 3 })) },
  preview: { canDeploy: true, professionName: '骑士', level: 10, attack: 900, maxHp: 1320, issues: [],
    mainWeapon: { weaponId: 11, code: 'ember-blade', name: '余烬长剑', element: 0, qualityRank: 2 }, availableSkills: knightSkills } };
const mageFormation = { id: 2, characterId: 1, name: '奥术火力', groupElement: 0, position: 2, version: 3,
  loadout: { professionCode: 'mage', weapons: [{ slotIndex: 1, weaponId: 12 }],
    skills: mageSkills.slice(0, 3).map((skill, i) => ({ slotIndex: i + 1, skillCode: skill.code, autoUseEnabled: true })) },
  preview: { canDeploy: true, professionName: '法师', level: 12, attack: 1200, maxHp: 980, issues: [],
    mainWeapon: { weaponId: 12, code: 'apprentice-wand', name: '学徒魔杖', element: 0, qualityRank: 1 }, availableSkills: mageSkills } };
const invalidFormation = { ...mageFormation, id: 3, name: '待补全的编队', position: 3,
  loadout: { professionCode: 'mage', weapons: [], skills: [] },
  preview: { ...mageFormation.preview, canDeploy: false, mainWeapon: null,
    issues: [{ code: 'MainWeaponRequired', section: 'Weapons', message: '出战需要配置主武器。' }] } };

async function scene(browser, baseUrl, viewport, depth) {
  const context = await browser.newContext({ viewport, isMobile: true, hasTouch: true });
  const errors = [], unexpected = [];
  let progressReads = 0;
  const label = `${viewport.width}x${viewport.height}-lv${depth}`;
  try {
    await context.addInitScript(() => localStorage.setItem('authToken', 'local-dialog-test'));
    await context.route('**/*', async route => {
      const request = route.request(), url = new URL(request.url());
      const json = body => route.fulfill({ json: body });
      if (url.pathname === '/appsettings.json' || url.pathname === '/appsettings.Production.json') return json({ ApiBaseUrl: baseUrl + '/' });
      if (!url.pathname.startsWith('/api/')) return route.continue();
      // The lobby initializes its story tracker on entry. Fulfill that unrelated
      // request locally as well; no fixture request reaches a game server.
      if ((url.pathname === '/api/story/initialize' && request.method() === 'POST') ||
          (url.pathname === '/api/story' && request.method() === 'GET')) return json({ quests: [] });
      if (request.method() !== 'GET') { unexpected.push(request.method() + ' ' + url.pathname); return route.fulfill({ status: 405 }); }
      switch (url.pathname) {
        case '/api/user/me': return json({ userId: 1, userName: 'fixture', activeCharacterId: 1, characterCount: 1, gold: 100 });
        case '/api/user/character': return json(character);
        case '/api/user/characters': return json([character]);
        case '/api/rooms': return json([]);
        case '/api/content': return json({ schemaVersion: 1, version: 'dialog-regression', professions: [],
          regions: [{ code: 'test-region', name: '赤焰荒原', minimumLevel: 1, maximumLevel: 20, featuredElement: 0 }],
          dungeons: [dungeon, secondDungeon] });
        case '/api/content/progress':
          progressReads++;
          return json({ userId: 1, characterId: 1, currentCharacterLevel: 10,
            dungeons: [101, 102].map(dungeonId => ({ ...progressEntry, dungeonId, isClearedByCurrentUser: progressReads % 2 === 0 })) });
        case '/api/user/characters/1/formations': return json({ characterId: 1, canApply: true, formations: [formation, mageFormation, invalidFormation] });
        case '/api/user/characters/1/formations/recommend': return json({ selection: { mode: 'SavedFormation', formationId: 1, expectedVersion: 1 }, issues: [] });
        case '/api/dungeons/101': return json({ ...dungeon, ...progressEntry, depthLevel: Number(url.searchParams.get('depthLevel')) });
        default: unexpected.push(request.method() + ' ' + url.pathname); return route.fulfill({ status: 404 });
      }
    });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    page.setDefaultTimeout(15000);
    const waitForPoll = async () => {
      const previousReads = progressReads;
      await assertEventually(() => progressReads > previousReads, 35000);
      // Wait for the refreshed progress to reach the parent DOM, not just the HTTP request.
      await page.waitForFunction(({ expected, id }) =>
        document.querySelector(`#dungeon-${id} .dungeon-card__bottom small`)?.textContent === expected,
        { expected: progressReads % 2 === 0 ? '已通关 ›' : '查看挑战 ›', id: visualMode ? 102 : 101 });
    };
    await page.goto(`${baseUrl}/rooms?dungeonId=${visualMode ? 102 : 101}&depth=${depth}`);
    await page.locator('.formation-choice').waitFor({ timeout: 60000 });
    if (visualMode) {
      await verifyFormationPreview(page, viewport, waitForPoll);
      assert.deepEqual(unexpected, []);
      assert.deepEqual(errors, []);
      return { viewport, progressReads, passed: true };
    }
    const activeTab = () => page.locator('.monster-detail-tabs button[aria-pressed="true"]').innerText();
    const tab = name => page.locator('.monster-detail-tabs').getByRole('button', { name, exact: true });
    const scroll = page.locator('.monster-detail-scroll');
    assert.equal(await page.locator('.depth-step[aria-pressed="true"] strong').innerText(), String(depth));
    await page.locator('.monster-settings-toggle').click();
    await page.getByRole('checkbox', { name: '自动循环' }).uncheck();
    await page.getByRole('checkbox', { name: '开放房间' }).check();
    await tab('战利品').click();
    await scroll.evaluate(element => { element.scrollTop = 140; window.regressionScroll = element; });
    const scrollTop = await scroll.evaluate(element => element.scrollTop);
    assert(scrollTop > 0, 'The mobile fixture must have scrollable content.');
    // Two actual 30-second lobby polls; no manual refresh or replacement UI.
    await waitForPoll();
    await waitForPoll();
    assert.equal(await activeTab(), '战利品', `${label}: polling reset the selected tab`);
    assert.equal(await page.locator('.monster-depth-return b').innerText(), `LV${depth}`);
    assert.equal(await scroll.evaluate(element => element === window.regressionScroll), true);
    assert.equal(await scroll.evaluate(element => element.scrollTop), scrollTop);
    assert.equal(await page.locator('.monster-settings-toggle').getAttribute('aria-expanded'), 'true');
    assert.equal(await page.getByRole('checkbox', { name: '自动循环' }).isChecked(), false);
    assert.equal(await page.getByRole('checkbox', { name: '开放房间' }).isChecked(), true);
    await page.screenshot({ path: path.join(output, `${label}-drops.png`) });
    console.log(`PASS ${label}: loot tab, depth, settings, scroll and dialog node survive two background polls`);

    await page.locator('.monster-drop--weapon').first().click();
    await page.locator('.weapon-detail-dialog').waitFor();
    await waitForPoll();
    assert.equal(await page.locator('.weapon-detail-dialog').count(), 1);
    await page.getByRole('button', { name: '关闭武器详情', exact: true }).click();
    assert.equal(await activeTab(), '战利品');
    assert.equal(await page.locator('.monster-depth-return b').innerText(), `LV${depth}`);
    console.log(`PASS ${label}: weapon preview returns to the same loot tab after polling`);

    await tab('敌情').click();
    await page.locator('.monster-selector__item').first().click();
    await waitForPoll();
    assert.equal(await activeTab(), '敌情');
    assert.equal(await page.locator('.monster-selector__item[aria-pressed="true"] strong').innerText(), '熔岩守卫1');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    const footer = await page.locator('.monster-create-button').boundingBox();
    assert(footer.y >= 0 && footer.y + footer.height <= viewport.height, 'Start button must remain on screen.');
    console.log(`PASS ${label}: enemy selection survives polling; action button remains in the mobile viewport`);

    await page.getByRole('button', { name: '关闭详情', exact: true }).click();
    await page.locator('#dungeon-101').click();
    assert.equal(await activeTab(), '选择难度');
    assert.equal(await page.locator('.depth-step[aria-pressed="true"] strong').innerText(), '1');
    await page.getByRole('button', { name: '关闭详情', exact: true }).click();
    await page.locator('#dungeon-102').click();
    assert.equal(await activeTab(), '敌情');
    assert.equal(await page.locator('.monster-detail-tabs button').count(), 2);
    assert.deepEqual(errors, []);
    assert.deepEqual(unexpected, []);
    console.log(`PASS ${label}: reopening and switching dungeons initialize correctly; no runtime errors or writes`);
    return { viewport, depth, progressReads, passed: true };
  } finally { await context.close(); }
}

async function verifyFormationPreview(page, viewport, waitForPoll) {
  const card = page.locator('.formation-choice');
  const label = `${viewport.width}x${viewport.height}`;
  const checkImages = async () => {
    await page.waitForFunction(() => [...document.querySelectorAll('.formation-selector img')].every(img => img.complete && img.naturalWidth > 0));
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    assert.equal(await card.evaluate(element => element.scrollWidth > element.clientWidth), false);
  };
  await card.scrollIntoViewIfNeeded();
  await page.locator('.monster-detail-header h2').click();
  await checkImages();
  assert.match(await card.locator('.loadout-portrait img').getAttribute('src'), /swordsman\.png$/);
  assert.match(await card.locator('.loadout-main img').getAttribute('src'), /ember-blade\.png$/);
  assert.equal(await card.locator('.loadout-skill img').count(), 5);
  assert.deepEqual(await card.locator('.loadout-skill > small').allTextContents(), knightSkills.map(skill => skill.name));
  const footer = await page.locator('.monster-create-button').boundingBox();
  assert(footer.y >= 0 && footer.y + footer.height <= viewport.height);
  assert.equal(await page.locator('.monster-selector').count(), 0, 'Single-enemy encounters do not need a duplicate selector.');
  if (viewport.height >= 667) {
    const overflow = await page.locator('.monster-detail-scroll').evaluate(element => element.scrollHeight - element.clientHeight);
    assert(overflow <= 1, `Battle preparation should fit on one screen at ${label}; overflow ${overflow}px`);
  }
  await page.screenshot({ path: path.join(output, `${label}-selected.png`) });
  await card.click();
  const selectedOption = page.locator('.formation-option[aria-pressed="true"]');
  assert.equal(await selectedOption.locator('.loadout-copy > strong').innerText(), formation.name);
  const invalid = page.locator('.formation-option').filter({ hasText: invalidFormation.name });
  assert.equal(await invalid.isDisabled(), true);
  assert.equal(await invalid.locator('.loadout-main img').count(), 0);
  assert.equal(await invalid.locator('.loadout-main [role="img"]').getAttribute('aria-label'), '未配置主武器');
  await checkImages();
  await page.locator('.formation-options').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(output, `${label}-choices.png`) });
  await page.locator('.formation-option').filter({ hasText: mageFormation.name }).click();
  await page.locator('.formation-options').waitFor({ state: 'hidden' });
  assert.match(await card.locator('.loadout-portrait img').getAttribute('src'), /mage\.png$/);
  assert.match(await card.locator('.loadout-main img').getAttribute('src'), /apprentice-wand\.png$/);
  assert.equal(await card.locator('.loadout-skill img').count(), 3);
  assert.equal(await card.locator('.loadout-skill--empty').count(), 2);
  assert.deepEqual(await card.locator('.loadout-skill > small').allTextContents(), ['奥术飞弹', '寒冰箭', '灼烧', '空槽', '空槽']);
  await card.scrollIntoViewIfNeeded();
  await checkImages();
  await page.screenshot({ path: path.join(output, `${label}-mage.png`) });
  if (viewport.width === 320) {
    const largerText = await page.addStyleTag({ content: 'html { font-size: 125% !important; }' });
    await checkImages();
    const enlargedFooter = await page.locator('.monster-create-button').boundingBox();
    assert(enlargedFooter.y >= 0 && enlargedFooter.y + enlargedFooter.height <= viewport.height);
    await largerText.evaluate(element => element.remove());
  }
  await waitForPoll();
  assert.equal(await card.locator('.loadout-copy > strong').innerText(), mageFormation.name);
  assert.match(await card.locator('.loadout-main img').getAttribute('src'), /apprentice-wand\.png$/);
  await page.getByRole('button', { name: '关闭详情', exact: true }).click();
  await page.locator('#dungeon-102').click();
  await page.locator('.formation-choice .loadout-copy > strong').filter({ hasText: mageFormation.name }).waitFor();
  assert.equal(await page.locator('.monster-create-button').isEnabled(), true);
  await card.click();
  assert.equal(await page.locator('.formation-manage').isEnabled(), true);
  console.log(`PASS ${label}: saved profession/main weapon/equipped skills, empty slots, disabled choices, switch, polling, reopen and edit entry`);
}

async function assertEventually(predicate, timeout) {
  const deadline = Date.now() + timeout;
  while (!predicate()) {
    assert(Date.now() < deadline, 'The expected automatic lobby refresh did not occur.');
    await new Promise(resolve => setTimeout(resolve, 100));
  }
}

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ headless: true,
    channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined) });
  try {
    const baseUrl = `http://127.0.0.1:${server.address().port}`;
    const results = await Promise.allSettled([
      scene(browser, baseUrl, { width: 375, height: 667 }, 1),
      scene(browser, baseUrl, { width: 390, height: 844 }, 2),
      ...(visualMode ? [scene(browser, baseUrl, { width: 320, height: 568 }, 1)] : [])
    ]);
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify(results.map(result =>
      result.status === 'fulfilled' ? result.value : { passed: false, error: String(result.reason) }), null, 2));
    for (const result of results) if (result.status === 'rejected') throw result.reason;
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
