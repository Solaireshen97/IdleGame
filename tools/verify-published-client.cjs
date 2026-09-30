const path = require('node:path');
const fs = require('node:fs');
const { createRequire } = require('node:module');

async function main() {
  const [baseUrl, outputDirectory] = process.argv.slice(2);
  const url = new URL(baseUrl);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port) {
    throw new Error('Browser startup verification requires a random local HTTP endpoint.');
  }
  const artifactRoot = path.resolve(__dirname, '../artifacts/server-release-smoke');
  const output = path.resolve(outputDirectory);
  if (path.dirname(output) !== artifactRoot || !/^[a-f0-9]{32}$/.test(path.basename(output))) {
    throw new Error('Browser evidence must remain in the isolated smoke artifact directory.');
  }
  const packagePath = path.resolve(__dirname, '../Game.Server.Tests/Client/package.json');
  const requireClient = createRequire(packagePath);
  const locked = requireClient(packagePath).devDependencies.playwright;
  const installed = requireClient('playwright/package.json').version;
  if (installed !== locked) throw new Error(`Playwright ${locked} is required; install the existing frozen client lockfile.`);
  const { chromium } = requireClient('playwright');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const failures = [];
  let expectedAnonymous401 = 0;
  let page;
  try {
    page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
    page.on('pageerror', error => failures.push(`Page error: ${error.message}`));
    page.on('console', message => {
      if (message.type() !== 'error') return;
      const location = message.location().url;
      if (location.startsWith(`${url.origin}/api/`) && /status of 401 \(Unauthorized\)/.test(message.text())) {
        // Startup queries intentionally run before the user logs in.
        expectedAnonymous401++;
        return;
      }
      failures.push(`Console error: ${message.text()}`);
    });
    page.on('requestfailed', request => {
      failures.push(`Resource failed: ${new URL(request.url()).pathname}: ${request.failure()?.errorText}`);
    });
    page.on('response', response => {
      const responseUrl = new URL(response.url());
      if (responseUrl.origin === url.origin && !responseUrl.pathname.startsWith('/api/') && response.status() >= 400) {
        failures.push(`Resource returned ${response.status()}: ${responseUrl.pathname}`);
      }
    });
    const navigation = await page.goto(`${url.origin}/login`, { waitUntil: 'domcontentloaded', timeout: 60000 });
    if (!navigation || navigation.status() !== 200) throw new Error('Published login route did not load.');
    await page.locator('#login-user-name').waitFor({ state: 'visible', timeout: 60000 });
    await page.locator('#login-password').waitFor({ state: 'visible', timeout: 10000 });
    const submit = page.getByRole('button', { name: '进入冒险', exact: true });
    await submit.waitFor({ state: 'visible', timeout: 10000 });
    if (!(await submit.isEnabled())) throw new Error('Blazor login screen is still checking or unavailable.');
    await page.waitForTimeout(500);
    if (await page.locator('.boot-screen').count()) throw new Error('The static boot screen was not replaced by Blazor.');
    if (await page.locator('#blazor-error-ui').isVisible()) throw new Error('Blazor reports a fatal runtime error.');
    if (failures.length) throw new Error(failures.join('\n'));
    await page.screenshot({ path: path.join(output, 'published-client-login.png'), fullPage: true });
    fs.writeFileSync(path.join(output, 'published-client-browser.json'), JSON.stringify({
      status: 'passed', checkedAtUtc: new Date().toISOString(), browser: 'Microsoft Edge',
      playwright: installed, page: '/login', actualBlazorLoginRendered: true,
      pageErrors: 0, unexpectedConsoleErrors: 0, failedResources: 0, expectedAnonymous401
    }, null, 2));
    console.log(`Published Blazor started in Microsoft Edge: real login UI rendered with no runtime/resource errors. Evidence: ${output}`);
  } catch (error) {
    if (page) await page.screenshot({ path: path.join(output, 'published-client-failed.png'), fullPage: true }).catch(() => {});
    throw error;
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
