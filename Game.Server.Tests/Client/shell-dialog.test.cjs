const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');

const moduleUrl = 'data:text/javascript;base64,' + fs.readFileSync(
    path.resolve(__dirname, '../../Game.Client/wwwroot/js/shell-dialog.js')).toString('base64');

async function scene(run) {
    const browser = await chromium.launch({ headless: true,
        channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined) });
    try {
        const page = await browser.newPage({ viewport: { width: 375, height: 667 } });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.setContent(`<style>dialog { width:270px; height:150px; }
            button { min-height:44px; } body { min-height:1500px; }</style>
            <button id="opener">Open</button><button id="background">Background</button>
            <dialog id="first"><button id="inside">Inside</button></dialog>
            <dialog id="second"><button>Nested dialog</button></dialog>`);
        await page.evaluate(async url => {
            window.dialogs = await import(url);
            window.closeEvents = [];
            window.registrations = {};
            for (const id of ['first', 'second']) {
                registrations[id] = dialogs.connect(document.getElementById(id), {
                    invokeMethodAsync: async method => closeEvents.push({ id, method })
                });
            }
        }, moduleUrl);
        await run(page);
        assert.deepEqual(errors, []);
    } finally { await browser.close(); }
}

test('Escape closes the modal, restores focus and preserves the prior scroll setting', async () => {
    await scene(async page => {
        await page.locator('#opener').focus();
        await page.evaluate(() => {
            document.documentElement.style.overflow = 'auto';
            dialogs.setOpen(document.querySelector('#first'), true);
            document.querySelector('#background').focus();
        });
        assert.equal(await page.evaluate(() => document.activeElement.id), 'inside');
        assert.equal(await page.evaluate(() => document.documentElement.style.overflow), 'hidden');
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => closeEvents.length === 1);
        assert.equal(await page.evaluate(() => document.activeElement.id), 'opener');
        assert.equal(await page.evaluate(() => document.documentElement.style.overflow), 'auto');
        assert.deepEqual(await page.evaluate(() => closeEvents), [{ id: 'first', method: 'OnNativeClose' }]);
    });
});

test('dragging from the sheet onto the backdrop does not dismiss it; a backdrop click does', async () => {
    await scene(async page => {
        await page.evaluate(() => dialogs.setOpen(document.querySelector('#first'), true));
        const button = await page.locator('#inside').boundingBox();
        await page.mouse.move(button.x + 10, button.y + 10);
        await page.mouse.down();
        await page.mouse.move(5, 5);
        await page.mouse.up();
        assert.equal(await page.locator('#first').evaluate(element => element.open), true);
        await page.mouse.click(5, 5);
        await page.waitForFunction(() => closeEvents.length === 1);
        assert.equal(await page.locator('#first').evaluate(element => element.open), false);
        assert.equal(await page.evaluate(() => document.documentElement.style.overflow), '');
    });
});

test('closing a nested sheet keeps scrolling locked until the remaining sheet closes', async () => {
    await scene(async page => {
        await page.evaluate(() => {
            dialogs.setOpen(document.querySelector('#first'), true);
            dialogs.setOpen(document.querySelector('#second'), true);
        });
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => closeEvents.length === 1);
        assert.equal(await page.evaluate(() => document.documentElement.style.overflow), 'hidden');
        assert.equal(await page.locator('#first').evaluate(element => element.open), true);
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => closeEvents.length === 2);
        assert.equal(await page.evaluate(() => document.documentElement.style.overflow), '');
    });
});

test('navigation can remove an open sheet before disposal without leaving scrolling locked', async () => {
    await scene(async page => {
        await page.evaluate(() => {
            dialogs.setOpen(document.querySelector('#first'), true);
            document.querySelector('#first').remove();
            dialogs.disconnect(registrations.first);
            dialogs.disconnect(registrations.first);
        });
        assert.equal(await page.evaluate(() => document.documentElement.style.overflow), '');
        await page.evaluate(() => dialogs.setOpen(document.querySelector('#second'), true));
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => closeEvents.length === 1);
        assert.deepEqual(await page.evaluate(() => closeEvents), [{ id: 'second', method: 'OnNativeClose' }]);
    });
});

test('nested dialog contains Tab and Escape even with an ancestor keyboard handler', async () => {
    await scene(async page => {
        await page.evaluate(() => {
            window.ancestorKeys = [];
            document.body.addEventListener('keydown', event => {
                if (event.key === 'Tab' || event.key === 'Escape') ancestorKeys.push(event.key);
            });
            dialogs.setOpen(document.querySelector('#first'), true);
        });
        for (const key of ['Tab', 'Tab', 'Shift+Tab']) {
            await page.keyboard.press(key);
            assert.equal(await page.evaluate(() => document.activeElement.id), 'inside');
        }
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => closeEvents.length === 1);
        assert.deepEqual(await page.evaluate(() => ancestorKeys), []);
    });
});

test('closing returns focus to a refreshed item with the same identity', async () => {
    await scene(async page => {
        await page.locator('#opener').focus();
        await page.evaluate(() => {
            dialogs.setOpen(document.querySelector('#first'), true);
            const old = document.querySelector('#opener');
            old.replaceWith(old.cloneNode(true));
            dialogs.setOpen(document.querySelector('#first'), true);
        });
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => closeEvents.length === 1);
        assert.equal(await page.evaluate(() => document.activeElement.id), 'opener');
    });
});
