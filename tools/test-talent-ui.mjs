import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../Game.Client/wwwroot/js/talent-ui.js', import.meta.url), 'utf8');

function setup() {
    class Element extends EventTarget {
        clientHeight = 200;
        clientWidth = 300;
        clientTop = 1;
        clientLeft = 1;
        scrollHeight = 500;
        top = 100;
        left = 10;
        scrollCalls = [];
        captures = new Set();
        classes = new Set();
        classList = { add: value => this.classes.add(value), remove: value => this.classes.delete(value) };
        get scrollTop() { return this._scrollTop ?? 0; }
        set scrollTop(value) { this._scrollTop = Math.max(0, Math.min(value, this.scrollHeight - this.clientHeight)); }
        scrollTo(options) { this.scrollCalls.push(options); this.scrollTop = options.top; }
        getBoundingClientRect() { return { top: this.top, left: this.left, bottom: this.top + this.clientHeight + 2 }; }
        setPointerCapture(id) { this.captures.add(id); }
        hasPointerCapture(id) { return this.captures.has(id); }
        releasePointerCapture(id) { this.captures.delete(id); }
        contains(node) { return node.parent === this; }
    }
    const window = new EventTarget();
    window.reducedMotion = false;
    window.matchMedia = () => ({ matches: window.reducedMotion });
    const nodes = new Map();
    const map = new Element();
    runInNewContext(source, { window, document: { getElementById: id => nodes.get(id) }, HTMLElement: Element });
    const ui = window.talentUi;
    ui.attach(map);
    const node = (id, offset) => {
        const result = {
            parent: map,
            getBoundingClientRect: () => ({ top: map.top + map.clientTop + offset - map.scrollTop, bottom: map.top + map.clientTop + offset + 76 - map.scrollTop }),
            focus: options => { result.focusOptions = options; }
        };
        nodes.set(id, result);
        return result;
    };
    return { map, window, ui, node };
}

function fire(target, type, properties = {}) {
    const event = new Event(type, { cancelable: true });
    Object.assign(event, { pointerType: 'mouse', pointerId: 1, button: 0, buttons: 1, isPrimary: true, clientX: 100, clientY: 200, detail: 1 }, properties);
    target.dispatchEvent(event);
    return event;
}

test('a tap or small mouse movement still selects a node', () => {
    const { map, window } = setup();
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 196 });
    fire(window, 'pointerup');
    assert.equal(map.scrollTop, 0);
    assert.equal(fire(map, 'click').defaultPrevented, false);
});

test('dragging scrolls the tree and suppresses only the release click', () => {
    const { map, window } = setup();
    fire(map, 'pointerdown');
    assert.equal(fire(map, 'pointermove', { clientY: 120 }).defaultPrevented, true);
    assert.equal(map.scrollTop, 80);
    assert.equal(map.classes.has('is-dragging'), true);
    assert.equal(map.hasPointerCapture(1), true);
    fire(window, 'pointerup');
    assert.equal(map.classes.has('is-dragging'), false);
    assert.equal(map.hasPointerCapture(1), false);
    assert.equal(fire(map, 'click').defaultPrevented, true);
    assert.equal(fire(map, 'click').defaultPrevented, false);
});

test('dragging respects both scroll boundaries', () => {
    const { map, window } = setup();
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: -500 });
    assert.equal(map.scrollTop, 300);
    fire(window, 'pointerup');
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 900 });
    assert.equal(map.scrollTop, 0);
});

test('touch gestures are left to native scrolling and tapping', () => {
    const { map, window } = setup();
    fire(map, 'pointerdown', { pointerType: 'touch' });
    assert.equal(fire(map, 'pointermove', { pointerType: 'touch', clientY: 100 }).defaultPrevented, false);
    fire(window, 'pointerup', { pointerType: 'touch' });
    assert.equal(map.scrollCalls.length, 0);
    assert.equal(map.captures.size, 0);
    assert.equal(fire(map, 'click').defaultPrevented, false);
});

test('cancelled drags release capture and do not block the next selection', () => {
    const { map } = setup();
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 100 });
    fire(map, 'pointercancel');
    assert.equal(map.captures.size, 0);
    assert.equal(map.classes.size, 0);
    assert.equal(fire(map, 'click').defaultPrevented, false);
});

test('keyboard clicks and a new mouse press are not swallowed after dragging', () => {
    const { map, window } = setup();
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 100 });
    fire(window, 'pointerup');
    assert.equal(fire(map, 'click', { detail: 0 }).defaultPrevented, false);
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 100 });
    fire(window, 'pointerup');
    fire(map, 'pointerdown');
    fire(window, 'pointerup');
    assert.equal(fire(map, 'click').defaultPrevented, false);
});

test('right clicks, scrollbar presses, other pointers and a tree that fits do not start a drag', () => {
    const { map } = setup();
    fire(map, 'pointerdown', { button: 2 });
    fire(map, 'pointermove', { clientY: 100 });
    fire(map, 'pointerdown', { clientX: 312 });
    fire(map, 'pointermove', { clientY: 100 });
    assert.equal(map.scrollCalls.length, 0);
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { pointerId: 2, clientY: 100 });
    assert.equal(map.scrollTop, 0);
    map.scrollHeight = map.clientHeight;
    fire(map, 'pointercancel');
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 100 });
    assert.equal(map.captures.size, 0);
});

test('repeated attachment and disposal do not leave extra drag handlers', () => {
    const { map, window, ui } = setup();
    ui.attach(map);
    fire(map, 'pointerdown');
    assert.equal(map.scrollCalls.length, 1);
    fire(map, 'pointermove', { clientY: 100 });
    ui.disconnect(map);
    assert.equal(map.captures.size, 0);
    assert.equal(map.classes.size, 0);
    const previous = map.scrollTop;
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 50 });
    fire(window, 'pointerup');
    assert.equal(map.scrollTop, previous);
    assert.equal(fire(map, 'click').defaultPrevented, false);
    ui.attach(map);
    fire(map, 'pointerdown');
    fire(map, 'pointermove', { clientY: 150 });
    assert.equal(map.scrollTop, previous + 50);
});

test('locating nodes scrolls only when needed and supports reduced motion', () => {
    const { map, window, ui, node } = setup();
    node('visible', 20);
    node('bottom', 400);
    node('top', 12);
    ui.reveal(map, 'visible');
    assert.equal(map.scrollCalls.length, 0);
    ui.reveal(map, 'bottom');
    assert.equal(map.scrollTop, 288);
    assert.equal(map.scrollCalls.at(-1).behavior, 'smooth');
    window.reducedMotion = true;
    ui.reveal(map, 'top');
    assert.equal(map.scrollTop, 0);
    assert.equal(map.scrollCalls.at(-1).behavior, 'instant');
    const visible = node('focus', 20);
    ui.focus('focus');
    assert.equal(visible.focusOptions.preventScroll, true);
    const unrelated = node('unrelated', 400);
    unrelated.parent = null;
    const calls = map.scrollCalls.length;
    ui.reveal(map, 'unrelated');
    ui.reveal(map, 'missing');
    assert.equal(map.scrollCalls.length, calls);
});
