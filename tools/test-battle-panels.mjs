import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const uiSource = readFileSync(new URL('../Game.Client/wwwroot/js/battle-ui.js', import.meta.url), 'utf8');
const combatSource = readFileSync(new URL('../Game.Client/wwwroot/js/battle-combat.js', import.meta.url), 'utf8');

function classList() {
    const values = new Set();
    const writes = [];
    return {
        writes,
        contains: name => values.has(name),
        toggle(name, enabled) { writes.push([name, enabled]); enabled ? values.add(name) : values.delete(name); },
        add(...names) { names.forEach(name => { values.add(name); writes.push([name, true]); }); },
        remove(...names) { names.forEach(name => { values.delete(name); writes.push([name, false]); }); }
    };
}

function setupUi() {
    const document = { documentElement: { classList: classList() }, body: { classList: classList() } };
    const window = {};
    runInNewContext(uiSource, { window, document });
    return { ui: window.battleUi, html: document.documentElement.classList, body: document.body.classList };
}

test('a mounted battle reserves scrollbar space before its first dialog, and releases it on navigation', () => {
    const { ui, html, body } = setupUi();
    ui.setDialogState('room', false);
    assert.equal(html.contains('battle-dialog-host'), true);
    assert.equal(body.contains('battle-dialog-open'), false);
    ui.setDialogState('room', true);
    assert.equal(body.contains('battle-dialog-open'), true);
    ui.setDialogState('room', false);
    assert.equal(body.contains('battle-dialog-open'), false);
    assert.equal(html.contains('battle-dialog-host'), true);
    ui.releaseDialogState('room');
    assert.equal(html.contains('battle-dialog-host'), false);
});

test('repeated live room updates do not rewrite scroll-lock classes', () => {
    const { ui, html, body } = setupUi();
    ui.setDialogState('room', false);
    ui.setDialogState('room', true);
    const writes = html.writes.length + body.writes.length;
    for (let tick = 0; tick < 120; tick++) ui.setDialogState('room', true);
    assert.equal(html.writes.length + body.writes.length, writes);
    assert.equal(body.contains('battle-dialog-open'), true);
});

test('disposing an old battle cannot unlock another mounted battle dialog', () => {
    const { ui, html, body } = setupUi();
    ui.setDialogState('old', true);
    ui.setDialogState('new', true);
    ui.releaseDialogState('old');
    assert.equal(body.contains('battle-dialog-open'), true);
    ui.releaseDialogState('new');
    assert.equal(body.contains('battle-dialog-open'), false);
    assert.equal(html.contains('battle-dialog-host'), false);
    const writes = body.writes.length;
    ui.releaseDialogState('new');
    assert.equal(body.writes.length, writes);
});

function setupCombat() {
    const observers = new Set();
    const document = new EventTarget();
    document.body = {};
    document.hidden = false;
    class Element {
        isConnected = true;
        covered = false;
        sprite = null;
        queries = 0;
        dataset = {};
        classList = classList();
        closest() { return this.covered ? {} : null; }
        querySelector(selector) {
            this.queries++;
            return selector === '.battle-field' || selector === '.combat-fx-layer' ? {} : null;
        }
        querySelectorAll(selector) { return selector.includes('img') && this.sprite ? [this.sprite] : []; }
    }
    class Observer {
        constructor(callback) { this.callback = callback; }
        observe() { observers.add(this); }
        disconnect() { observers.delete(this); }
    }
    const window = {};
    runInNewContext(combatSource, {
        window, document, HTMLElement: Element, MutationObserver: Observer,
        AbortController, setTimeout, clearTimeout, performance,
        matchMedia: () => ({ matches: true })
    });
    return { combat: window.battleCombat, root: new Element(), observers };
}

const plan = { events: [], finalVitals: {}, hitCount: 1, totalDamage: 20, defeated: false };

test('covered battle ignores visual playback requests and resumes new rounds after closing', async () => {
    const { combat, root } = setupCombat();
    root.covered = true;
    for (let tick = 0; tick < 10; tick++) await combat.play(root, plan);
    assert.equal(root.queries, 0);
    assert.equal(root.classList.writes.length, 0);
    root.covered = false;
    await combat.play(root, plan);
    assert.ok(root.classList.writes.some(([name, enabled]) => name === 'combat-playing' && enabled));
    assert.equal(root.classList.contains('combat-playing'), false);
});

test('opening a dialog during sprite loading cancels playback and cleans up its observer', async () => {
    const { combat, root, observers } = setupCombat();
    root.sprite = { src: 'fixture.png', naturalWidth: 1, decode: () => new Promise(() => {}) };
    const playback = combat.play(root, plan);
    assert.equal(observers.size, 1);
    root.covered = true;
    for (const observer of observers) observer.callback();
    await playback;
    assert.equal(observers.size, 0);
    assert.equal(root.classList.contains('combat-preparing'), false);
    assert.equal(root.classList.writes.some(([name, enabled]) => name === 'combat-playing' && enabled), false);
});

test('sprite completion cannot start an attack behind a newly opened dialog', async () => {
    const { combat, root, observers } = setupCombat();
    let loaded;
    root.sprite = { src: 'fixture.png', naturalWidth: 1, decode: () => new Promise(resolve => { loaded = resolve; }) };
    const playback = combat.play(root, plan);
    root.covered = true;
    loaded();
    await playback;
    assert.equal(observers.size, 0);
    assert.equal(root.classList.writes.some(([name, enabled]) => name === 'combat-playing' && enabled), false);
});
