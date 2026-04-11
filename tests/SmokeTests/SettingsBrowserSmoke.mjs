import { spawn } from 'node:child_process';
import { createWriteStream, existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..', '..');
const diagDirectory = path.join(repoRoot, '.diag', 'settings-browser-smoke');
const browserProfileDirectory = path.join(diagDirectory, 'edge-profile');
const summaryPath = path.join(diagDirectory, 'settings-browser-smoke-summary.json');
const webStdOutPath = path.join(diagDirectory, 'web.stdout.log');
const webStdErrPath = path.join(diagDirectory, 'web.stderr.log');
const browserStdOutPath = path.join(diagDirectory, 'edge.stdout.log');
const browserStdErrPath = path.join(diagDirectory, 'edge.stderr.log');
const anonymousScreenshotPath = path.join(diagDirectory, 'settings-anonymous.png');
const settingsScreenshotPath = path.join(diagDirectory, 'settings-page.png');
const refreshScreenshotPath = path.join(diagDirectory, 'settings-refresh.png');
const savedScreenshotPath = path.join(diagDirectory, 'settings-saved.png');
const invalidScreenshotPath = path.join(diagDirectory, 'settings-invalid.png');
const dashboardScreenshotPath = path.join(diagDirectory, 'dashboard-drift-summary.png');
const adminSettingsScreenshotPath = path.join(diagDirectory, 'admin-settings-page.png');
const adminOverviewScreenshotPath = path.join(diagDirectory, 'admin-overview-page.png');
const adminAuditScreenshotPath = path.join(diagDirectory, 'admin-audit-page.png');

const browserPathCandidates = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'
];

const browserPath = browserPathCandidates.find(candidate => existsSync(candidate));
if (!browserPath) {
  throw new Error('No supported browser executable was found for the settings smoke test.');
}

function sleep(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function getFreeTcpPort() {
  return await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') {
        reject(new Error('Unable to resolve a free TCP port.'));
        return;
      }

      const port = address.port;
      server.close(error => {
        if (error) {
          reject(error);
          return;
        }

        resolve(port);
      });
    });
  });
}

async function waitUntil(name, condition, timeoutMilliseconds = 30000, intervalMilliseconds = 250) {
  const startedAt = Date.now();
  let lastError = null;

  while (Date.now() - startedAt < timeoutMilliseconds) {
    try {
      if (await condition()) {
        return;
      }
    } catch (error) {
      lastError = error;
    }

    await sleep(intervalMilliseconds);
  }

  if (lastError) {
    throw new Error(`Timed out while waiting for ${name}. Last error: ${lastError.message}`);
  }

  throw new Error(`Timed out while waiting for ${name}.`);
}

function ensureCleanDirectory(directoryPath) {
  if (existsSync(directoryPath)) {
    rmSync(directoryPath, { recursive: true, force: true });
  }

  mkdirSync(directoryPath, { recursive: true });
}

function startManagedProcess(filePath, argumentList, workingDirectory, stdoutPath, stderrPath, extraEnvironment = {}) {
  const stdoutStream = createWriteStream(stdoutPath, { flags: 'w' });
  const stderrStream = createWriteStream(stderrPath, { flags: 'w' });
  const child = spawn(filePath, argumentList, {
    cwd: workingDirectory,
    env: {
      ...process.env,
      ...extraEnvironment
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  });

  child.stdout.pipe(stdoutStream);
  child.stderr.pipe(stderrStream);

  return {
    child,
    stdoutStream,
    stderrStream
  };
}

async function stopManagedProcess(processHandle) {
  if (!processHandle) {
    return;
  }

  const { child, stdoutStream, stderrStream } = processHandle;

  if (child && child.exitCode === null && !child.killed) {
    child.kill('SIGKILL');
    await Promise.race([
      new Promise(resolve => child.once('exit', resolve)),
      sleep(5000)
    ]);
  }

  stdoutStream?.end();
  stderrStream?.end();
}

class CdpClient {
  constructor(webSocketDebuggerUrl) {
    this.webSocketDebuggerUrl = webSocketDebuggerUrl;
    this.nextId = 0;
    this.pending = new Map();
    this.socket = null;
  }

  async connect() {
    await new Promise((resolve, reject) => {
      const socket = new WebSocket(this.webSocketDebuggerUrl);
      this.socket = socket;

      socket.addEventListener('open', () => resolve(), { once: true });
      socket.addEventListener('error', event => {
        reject(new Error(`CDP websocket connection failed: ${event?.message ?? 'unknown error'}`));
      }, { once: true });

      socket.addEventListener('message', event => {
        const message = JSON.parse(event.data);
        if (typeof message.id === 'number' && this.pending.has(message.id)) {
          const entry = this.pending.get(message.id);
          this.pending.delete(message.id);

          if (message.error) {
            entry.reject(new Error(`CDP command failed: ${message.error.message}`));
            return;
          }

          entry.resolve(message.result);
        }
      });
    });
  }

  async close() {
    if (!this.socket) {
      return;
    }

    if (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING) {
      this.socket.close();
      await sleep(250);
    }
  }

  async send(method, params = {}) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      throw new Error('CDP websocket is not open.');
    }

    const id = ++this.nextId;
    const payload = JSON.stringify({ id, method, params });

    const result = await new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.socket.send(payload);
    });

    return result;
  }

  async evaluate(expression, awaitPromise = false) {
    const result = await this.send('Runtime.evaluate', {
      expression,
      returnByValue: true,
      awaitPromise
    });

    if (result.exceptionDetails) {
      throw new Error(`Runtime.evaluate failed for expression: ${expression}`);
    }

    return result.result?.value;
  }

  async navigate(url) {
    await this.send('Page.navigate', { url });
  }

  async waitForReady(timeoutMilliseconds = 30000) {
    await waitUntil('browser page ready', async () => {
      const readyState = await this.evaluate('document.readyState');
      return readyState === 'complete';
    }, timeoutMilliseconds);
  }

  async waitForLocationContains(fragment, timeoutMilliseconds = 30000) {
    await waitUntil(`location containing '${fragment}'`, async () => {
      const location = await this.evaluate('window.location.href');
      return typeof location === 'string' && location.includes(fragment);
    }, timeoutMilliseconds);
  }

  async captureScreenshot(filePath) {
    const result = await this.send('Page.captureScreenshot', {
      format: 'png',
      fromSurface: true
    });

    writeFileSync(filePath, Buffer.from(result.data, 'base64'));
  }
}

async function setInputValueSecurely(client, selector, value) {
  await client.evaluate(`(() => {
    const element = document.querySelector(${JSON.stringify(selector)});
    if (!element) throw new Error('Element not found for secure input.');
    element.focus();
    element.value = '';
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);

  if (value) {
    await client.send('Input.insertText', { text: value });
  }

  await client.evaluate(`(() => {
    const element = document.querySelector(${JSON.stringify(selector)});
    if (!element) throw new Error('Element not found after secure input.');
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
}

(async () => {
  const originalEnvironment = {
    ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT,
    ASPNETCORE_URLS: process.env.ASPNETCORE_URLS,
    DOTNET_CLI_HOME: process.env.DOTNET_CLI_HOME,
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: process.env.DOTNET_SKIP_FIRST_TIME_EXPERIENCE,
    DOTNET_NOLOGO: process.env.DOTNET_NOLOGO
  };

  let webProcess = null;
  let browserProcess = null;
  let client = null;

  try {
    ensureCleanDirectory(diagDirectory);
    ensureCleanDirectory(browserProfileDirectory);

    const appPort = await getFreeTcpPort();
    const debugPort = await getFreeTcpPort();
    const baseUrl = `http://127.0.0.1:${appPort}`;
    const randomSuffix = crypto.randomUUID().replace(/-/g, '');
    const registrationEmail = `settings.smoke.${randomSuffix}@coinbot.test`;
    const registrationPassword = 'Passw0rd!Smoke1';
    const adminEmail = `settings.admin.${randomSuffix}@coinbot.test`;
    const adminPassword = 'Passw0rd!Admin1';
    const updatedTimeZoneId = 'Dateline Standard Time';
    const testBinanceApiKey = process.env.TEST_BINANCE_API_KEY || '';
    const testBinanceApiSecret = process.env.TEST_BINANCE_API_SECRET || '';
    const testBinanceMode = String(process.env.TEST_BINANCE_IS_DEMO || 'true').toLowerCase();
    const testBinanceIsDemo = !['false', '0', 'live'].includes(testBinanceMode);
    const shouldRunBinanceSetupProbe = Boolean(testBinanceApiKey && testBinanceApiSecret);
    let adminOverviewBinanceSetupState = {
      attempted: false,
      skippedReason: shouldRunBinanceSetupProbe ? '' : 'TEST_BINANCE_API_KEY/TEST_BINANCE_API_SECRET not set',
      resultKind: '',
      resultText: '',
      successVisible: false,
      errorVisible: false
    };

    process.env.DOTNET_CLI_HOME = path.join(repoRoot, '.dotnet');
    process.env.DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1';
    process.env.DOTNET_NOLOGO = '1';
    process.env.ASPNETCORE_ENVIRONMENT = 'Development';
    process.env.ASPNETCORE_URLS = baseUrl;

    webProcess = startManagedProcess(
      'dotnet',
      ['run', '--project', 'src/CoinBot.Web/CoinBot.Web.csproj', '--no-build', '--no-launch-profile'],
      repoRoot,
      webStdOutPath,
      webStdErrPath,
      {
        DOTNET_CLI_HOME: process.env.DOTNET_CLI_HOME,
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE: process.env.DOTNET_SKIP_FIRST_TIME_EXPERIENCE,
        DOTNET_NOLOGO: process.env.DOTNET_NOLOGO,
        ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT,
        ASPNETCORE_URLS: process.env.ASPNETCORE_URLS,
        IdentitySeed__SuperAdminEmail: adminEmail,
        IdentitySeed__SuperAdminPassword: adminPassword,
        IdentitySeed__SuperAdminFullName: 'Settings Smoke Admin'
      }
    );

    await waitUntil('web host readiness', async () => {
      const response = await fetch(`${baseUrl}/Auth/Login`, { redirect: 'manual' });
      return response.status === 200 || response.status === 302;
    }, 45000);

    browserProcess = startManagedProcess(
      browserPath,
      [
        '--headless=new',
        '--disable-gpu',
        '--no-first-run',
        '--no-default-browser-check',
        `--remote-debugging-port=${debugPort}`,
        `--user-data-dir=${browserProfileDirectory}`,
        'about:blank'
      ],
      repoRoot,
      browserStdOutPath,
      browserStdErrPath
    );

    await waitUntil('browser devtools endpoint', async () => {
      const response = await fetch(`http://127.0.0.1:${debugPort}/json/list`);
      const targets = await response.json();
      return Array.isArray(targets) && targets.some(target => target.type === 'page');
    }, 30000);

    const targetsResponse = await fetch(`http://127.0.0.1:${debugPort}/json/list`);
    const pageTargets = (await targetsResponse.json()).filter(target => target.type === 'page');
    const pageTarget = pageTargets.find(target => target.url === 'about:blank') ?? pageTargets[0];

    if (!pageTarget?.webSocketDebuggerUrl) {
      throw new Error('Browser devtools page target did not expose a webSocketDebuggerUrl.');
    }

    client = new CdpClient(pageTarget.webSocketDebuggerUrl);
    await client.connect();
    await client.send('Page.enable');
    await client.send('Runtime.enable');
    await client.send('Network.enable');

    await client.navigate(`${baseUrl}/Settings`);
    await client.waitForReady();
    await client.waitForLocationContains('/Auth/Login');
    await client.captureScreenshot(anonymousScreenshotPath);
    const anonymousLocation = await client.evaluate('window.location.href');

    await client.navigate(`${baseUrl}/Auth/Register`);
    await client.waitForReady();
    await client.waitForLocationContains('/Auth/Register');

    await client.evaluate(`(() => {
      const setValue = (selector, value) => {
        const element = document.querySelector(selector);
        if (!element) throw new Error('Element not found: ' + selector);
        element.value = value;
        element.dispatchEvent(new Event('input', { bubbles: true }));
        element.dispatchEvent(new Event('change', { bubbles: true }));
      };

      setValue('input[name="FullName"]', 'Settings Smoke User');
      setValue('input[name="Email"]', ${JSON.stringify(registrationEmail)});
      setValue('input[name="Password"]', ${JSON.stringify(registrationPassword)});
      setValue('input[name="ConfirmPassword"]', ${JSON.stringify(registrationPassword)});

      const checkbox = document.querySelector('input[name="AcceptRiskDisclosure"]');
      if (!checkbox) throw new Error('Risk disclosure checkbox not found.');
      checkbox.checked = true;
      checkbox.dispatchEvent(new Event('change', { bubbles: true }));

      const form = document.querySelector('form[action$="/Auth/Register"]');
      if (!form) throw new Error('Register form was not found.');
      form.submit();
      return true;
    })()`);

        await client.waitForReady();
    await client.waitForLocationContains('/Auth/Login');

    await client.navigate(`${baseUrl}/Settings`);
    await client.waitForReady();
    await client.waitForLocationContains('/Auth/Login');

    await client.evaluate(`(() => {
      const setValue = (selector, value) => {
        const element = document.querySelector(selector);
        if (!element) throw new Error('Element not found: ' + selector);
        element.value = value;
        element.dispatchEvent(new Event('input', { bubbles: true }));
        element.dispatchEvent(new Event('change', { bubbles: true }));
      };

      setValue('input[name="EmailOrUserName"]', ${JSON.stringify(registrationEmail)});
      setValue('input[name="Password"]', ${JSON.stringify(registrationPassword)});
      const form = document.querySelector('form[action$="/Auth/Login"]');
      if (!form) throw new Error('Login form was not found.');
      form.submit();
      return true;
    })()`);

    await client.waitForReady();
    await client.waitForLocationContains('/Settings');
    await client.captureScreenshot(settingsScreenshotPath);
    const settingsPageState = await client.evaluate(`(() => {
      const select = document.querySelector('select[name="Form.PreferredTimeZoneId"]');
      const token = document.querySelector('input[name="__RequestVerificationToken"]');
      const refreshButton = document.querySelector('form[action$="/Settings/RefreshClockDrift"] button[type="submit"]');
      const guardThresholdCell = Array.from(document.querySelectorAll('td')).find(cell => cell.innerText.trim() === 'Threshold')?.nextElementSibling;
      const guardReasonCell = Array.from(document.querySelectorAll('td')).find(cell => cell.innerText.trim() === 'Readable reason')?.nextElementSibling;
      const helperText = Array.from(document.querySelectorAll('.cb-helper-text')).map(node => node.innerText.trim()).join(' | ');
      const opsNoticeText = Array.from(document.querySelectorAll('.cb-validation-summary-info, .cb-validation-summary-warning')).map(node => node.innerText.trim()).join(' | ');
      return {
        selectExists: !!select,
        optionCount: select ? select.options.length : 0,
        currentValue: select ? select.value : null,
        hasToken: !!token,
        refreshButtonExists: !!refreshButton,
        guardThresholdText: guardThresholdCell ? guardThresholdCell.innerText.trim() : '',
        guardReasonText: guardReasonCell ? guardReasonCell.innerText.trim() : '',
        helperText,
        opsNoticeText
      };
    })()`);

    if (!settingsPageState.selectExists) {
      throw new Error('Timezone select was not rendered on /Settings.');
    }

    if (settingsPageState.optionCount <= 1) {
      throw new Error(`Timezone option count was not sufficient: ${settingsPageState.optionCount}`);
    }

    if (!settingsPageState.hasToken) {
      throw new Error('Antiforgery token was not rendered on /Settings.');
    }

    if (settingsPageState.refreshButtonExists) {
      throw new Error('Server time sync refresh button should not be rendered for a normal user on /Settings.');
    }

    if (settingsPageState.guardThresholdText || settingsPageState.guardReasonText) {
      throw new Error('Operational drift guard fields should not be rendered on the normal user settings page.');
    }

    if (!/zaman bilgisini/i.test(settingsPageState.helperText)) {
      throw new Error('Display-only timezone helper text was not rendered on /Settings.');
    }
    if (!/kullanım tercihleri/i.test(settingsPageState.opsNoticeText)) {
      throw new Error('User settings scope notice was not rendered on /Settings.');
    }

    if (/Super Admin|Global Ayarlar|drift guard|server-time/i.test(settingsPageState.opsNoticeText)) {
      throw new Error('Normal user settings page should not surface admin operational jargon.');
    }

    await client.evaluate(`(() => {
      const select = document.querySelector('select[name="Form.PreferredTimeZoneId"]');
      if (!select) throw new Error('Timezone select was not found.');
      if (!Array.from(select.options).some(option => option.value === ${JSON.stringify(updatedTimeZoneId)})) {
        throw new Error('Expected timezone option not found.');
      }

      select.value = ${JSON.stringify(updatedTimeZoneId)};
      select.dispatchEvent(new Event('input', { bubbles: true }));
      select.dispatchEvent(new Event('change', { bubbles: true }));
      const form = select.form;
      if (!form) throw new Error('Settings form was not found.');
      form.submit();
      return true;
    })()`);

    await client.waitForReady();
    await client.waitForLocationContains('/Settings');
    await client.captureScreenshot(savedScreenshotPath);

    const postSaveState = await client.evaluate(`(() => {
      const select = document.querySelector('select[name="Form.PreferredTimeZoneId"]');
      const success = document.querySelector('.cb-validation-summary-success');
      return {
        selectedValue: select ? select.value : null,
        successVisible: !!success,
        successText: success ? success.innerText.trim() : ''
      };
    })()`);

    if (postSaveState.selectedValue !== updatedTimeZoneId) {
      throw new Error(`Saved timezone mismatch. Expected '${updatedTimeZoneId}' but got '${postSaveState.selectedValue}'.`);
    }

    const antiforgeryResult = await client.evaluate(`(async () => {
      const payload = new URLSearchParams();
      payload.set('Form.PreferredTimeZoneId', ${JSON.stringify(updatedTimeZoneId)});

      const response = await fetch('/Settings', {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded'
        },
        body: payload.toString()
      });

      return { status: response.status, ok: response.ok };
    })()`, true);

    if (antiforgeryResult.ok || antiforgeryResult.status < 400) {
      throw new Error(`Antiforgery validation did not reject the tokenless POST. Status: ${antiforgeryResult.status}`);
    }

    await client.evaluate(`(() => {
      const select = document.querySelector('select[name="Form.PreferredTimeZoneId"]');
      if (!select) throw new Error('Timezone select was not found for invalid-post scenario.');

      let option = Array.from(select.options).find(item => item.value === 'Invalid/Zone');
      if (!option) {
        option = document.createElement('option');
        option.value = 'Invalid/Zone';
        option.text = 'Invalid/Zone';
        select.appendChild(option);
      }

      select.value = 'Invalid/Zone';
      select.dispatchEvent(new Event('input', { bubbles: true }));
      select.dispatchEvent(new Event('change', { bubbles: true }));
      const form = select.form;
      if (!form) throw new Error('Settings form was not found for invalid-post scenario.');
      form.submit();
      return true;
    })()`);

    await client.waitForReady();
    await client.waitForLocationContains('/Settings');
    await client.captureScreenshot(invalidScreenshotPath);

    const invalidState = await client.evaluate(`(() => {
      const summary = document.querySelector('.cb-validation-summary-danger');
      const field = document.querySelector('[data-valmsg-for="Form.PreferredTimeZoneId"]');
      return {
        hasError: !!summary || !!field?.innerText?.trim(),
        errorText: summary ? summary.innerText.trim() : (field ? field.innerText.trim() : '')
      };
    })()`);

    if (!invalidState.hasError) {
      throw new Error('Invalid timezone submission did not surface a validation error.');
    }

    await client.navigate(`${baseUrl}/Settings`);
    await client.waitForReady();
    await client.waitForLocationContains('/Settings');

    const reloadedState = await client.evaluate(`(() => {
      const select = document.querySelector('select[name="Form.PreferredTimeZoneId"]');
      return { selectedValue: select ? select.value : null };
    })()`);

    if (reloadedState.selectedValue !== updatedTimeZoneId) {
      throw new Error(`Reloaded timezone mismatch. Expected '${updatedTimeZoneId}' but got '${reloadedState.selectedValue}'.`);
    }


    await client.navigate(`${baseUrl}/`);
    await client.waitForReady();
    await client.waitForLocationContains('/');
    await client.captureScreenshot(dashboardScreenshotPath);
    const dashboardState = await client.evaluate(`(() => {
      const pageText = document.body.textContent || '';
      const primaryNavLabels = Array.from(document.querySelectorAll('.cb-menu-nav .menu-link .menu-text'))
        .map(item => item.innerText.trim())
        .filter(Boolean);
      return {
        hasUserShellHome: !!document.querySelector('[data-cb-user-shell-home]'),
        primaryNavLabels,
        primaryNavCount: primaryNavLabels.length,
        hasHome: primaryNavLabels.includes('Ana Sayfa'),
        hasBots: primaryNavLabels.includes('Botlarım'),
        hasTrades: primaryNavLabels.includes('İşlemler'),
        hasSettings: primaryNavLabels.includes('Ayarlar'),
        adminSettingsLinkVisible: !!Array.from(document.querySelectorAll('a')).find(link => (link.getAttribute('href') || '').toLowerCase().includes('/admin/settings')),
        hasAdminOperationsText: pageText.includes('Operations Dashboard') || pageText.includes('TradeMaster') || pageText.includes('PilotActivation') || pageText.includes('Global Ayarlar'),
        hasDailySummary: pageText.includes('Günlük özet')
      };
    })()`);

    if (!dashboardState.hasUserShellHome
        || dashboardState.primaryNavCount !== 4
        || !dashboardState.hasHome
        || !dashboardState.hasBots
        || !dashboardState.hasTrades
        || !dashboardState.hasSettings
        || !dashboardState.hasDailySummary
        || dashboardState.hasAdminOperationsText) {
      throw new Error('User shell did not render the expected ultra soft 4-item flow.');
    }

    const userNavigationState = { adminSettingsLinkVisible: Boolean(dashboardState.adminSettingsLinkVisible) };

    if (userNavigationState.adminSettingsLinkVisible) {
      throw new Error('Normal user shell should not render an admin global settings shortcut.');
    }

    await client.navigate(`${baseUrl}/admin/Overview`);
    await client.waitForReady();

    const userOverviewState = await client.evaluate(`(() => ({
      location: window.location.href,
      hasOperationsCenter: !!document.querySelector('[data-cb-super-admin-operations-center]')
    }))()`);

    if (userOverviewState.location.toLowerCase().includes('/admin/overview') && userOverviewState.hasOperationsCenter) {
      throw new Error('Normal user should not be able to access the super admin operations center.');
    }
    await client.navigate(`${baseUrl}/admin/Audit`);
    await client.waitForReady();

    const userAuditState = await client.evaluate(`(() => ({
      location: window.location.href,
      hasDecisionCenter: !!document.querySelector('[data-cb-admin-decision-center]')
    }))()`);

    if (userAuditState.location.toLowerCase().includes('/admin/audit') && userAuditState.hasDecisionCenter) {
      throw new Error('Normal user should not be able to access the incident / audit / decision center.');
    }

    await client.send('Network.clearBrowserCookies');
    await client.navigate(`${baseUrl}/admin/Settings`);
    await client.waitForReady();
    await client.waitForLocationContains('/Auth/Login');

    await client.evaluate(`(() => {
      const setValue = (selector, value) => {
        const element = document.querySelector(selector);
        if (!element) throw new Error('Element not found: ' + selector);
        element.value = value;
        element.dispatchEvent(new Event('input', { bubbles: true }));
        element.dispatchEvent(new Event('change', { bubbles: true }));
      };

      setValue('input[name="EmailOrUserName"]', ${JSON.stringify(adminEmail)});
      setValue('input[name="Password"]', ${JSON.stringify(adminPassword)});
      const form = document.querySelector('form[action$="/Auth/Login"]');
      if (!form) throw new Error('Admin login form was not found.');
      form.submit();
      return true;
    })()`);

    await client.waitForReady();
    await client.waitForLocationContains('/admin/Settings');
    await client.captureScreenshot(adminSettingsScreenshotPath);
    const adminSettingsState = await client.evaluate(`(() => {
      const overviewText = document.querySelector('#cb_admin_settings_tab_overview')?.textContent || '';
      const criticalText = document.querySelector('#cb_admin_settings_tab_critical')?.textContent || '';
      const activationText = document.querySelector('#cb_admin_settings_tab_activation')?.textContent || '';
      const pageText = document.body.textContent || '';

      return {
        pageTitle: document.querySelector('.cb-page-title, .cb-admin-page-title, .cb-admin-section-title')?.innerText?.trim() || '',
        tabCount: document.querySelectorAll('[id^="cb_admin_settings_tab_link_"]').length,
        hasOverviewTab: !!document.querySelector('#cb_admin_settings_tab_link_overview'),
        hasWizardTab: !!document.querySelector('#cb_admin_settings_tab_link_wizard'),
        hasCriticalTab: !!document.querySelector('#cb_admin_settings_tab_link_critical'),
        hasSyncTab: !!document.querySelector('#cb_admin_settings_tab_link_sync'),
        hasActivationTab: !!document.querySelector('#cb_admin_settings_tab_link_activation'),
        hasDetailsTab: !!document.querySelector('#cb_admin_settings_tab_link_details'),
        activeTabText: document.querySelector('.cb-admin-settings-nav-link.active')?.innerText?.trim() || '',
        hasReadOnlyText: overviewText.includes('Canli Snapshot / Salt Okunur'),
        hasWritableText: criticalText.includes('Yazilabilir Konfigürasyon'),
        hasActivationPanel: activationText.includes('Sistem Aktivasyon Kontrol Merkezi') || activationText.includes('Aktif edilebilir mi'),
        hasTimezoneSelect: !!document.querySelector('select[name="Form.PreferredTimeZoneId"]'),
        hasGlobalSettingsMenuLabel: pageText.includes('Global Ayarlar')
      };
    })()`);
    if (adminSettingsState.tabCount < 6 || !adminSettingsState.hasOverviewTab || !adminSettingsState.hasWizardTab || !adminSettingsState.hasCriticalTab || !adminSettingsState.hasSyncTab || !adminSettingsState.hasActivationTab || !adminSettingsState.hasDetailsTab) {
      throw new Error('Admin / Settings tabs did not render the expected operational sections.');
    }

    if (!/Genel Durum/i.test(adminSettingsState.activeTabText)) {
      throw new Error('Admin / Settings did not open on the expected overview tab.');
    }

    if (!adminSettingsState.hasReadOnlyText || !adminSettingsState.hasWritableText || !adminSettingsState.hasActivationPanel) {
      throw new Error('Admin / Settings did not render the expected read-only, writable, and activation cues.');
    }

    if (adminSettingsState.hasTimezoneSelect) {
      throw new Error('Admin / Settings should not render the normal user timezone form.');
    }

    if (!adminSettingsState.hasGlobalSettingsMenuLabel) {
      throw new Error('Admin shell did not render the Global Ayarlar label.');
    }

    const adminActivationState = await client.evaluate(`(() => {
      const activationPane = document.querySelector('#cb_admin_settings_tab_activation');
      const activationCenter = activationPane?.querySelector('[data-cb-admin-activation-control-center]');
      return {
        activationTabText: document.querySelector('#cb_admin_settings_tab_link_activation')?.innerText?.trim() || '',
        activationTabTarget: document.querySelector('#cb_admin_settings_tab_link_activation')?.getAttribute('href') || '',
        activationPaneExists: !!activationPane,
        activationSummaryVisible: (activationPane?.textContent || '').includes('Sistem Aktivasyon Kontrol Merkezi'),
        activationCenterVisible: !!activationCenter,
        decisionCode: activationCenter?.getAttribute('data-cb-admin-activation-decision-code') || '',
        decisionType: activationCenter?.getAttribute('data-cb-admin-activation-decision-type') || '',
        activatable: activationCenter?.getAttribute('data-cb-admin-activation-activatable') || '',
        checklistCount: activationPane?.querySelectorAll('[data-cb-admin-activation-check-item]').length || 0,
        switchCount: activationPane?.querySelectorAll('[data-cb-admin-activation-switch]').length || 0,
        hasActivateCard: !!activationPane?.querySelector('[data-cb-admin-activation-action-card="activate"]'),
        hasDeactivateCard: !!activationPane?.querySelector('[data-cb-admin-activation-action-card="deactivate"]'),
        hasActivateConfirmationInput: !!activationPane?.querySelector('[data-cb-admin-activation-action-card="activate"] input[name="reauthToken"][placeholder="ONAYLA"]'),
        hasActivateNoopState: !!activationPane?.querySelector('[data-cb-admin-activation-action-card="activate"] .cb-admin-info-strip-meta') && (activationPane?.querySelector('[data-cb-admin-activation-action-card="activate"] .cb-admin-info-strip-meta')?.innerText?.trim() || '') === 'No-op',
        hasDeactivateConfirmationInput: !!activationPane?.querySelector('[data-cb-admin-activation-action-card="deactivate"] input[name="reauthToken"][placeholder="ONAYLA"]'),
        hasEmergencySection: !!activationPane?.querySelector('[data-cb-admin-emergency-actions]'),
        hasCrisisPanelLink: !!activationPane?.querySelector('a[href="#cb_admin_settings_crisis_panel"]')
      };
    })()`);

    if (!/Aktivasyon/i.test(adminActivationState.activationTabText)
        || adminActivationState.activationTabTarget !== '#cb_admin_settings_tab_activation'
        || !adminActivationState.activationPaneExists
        || !adminActivationState.activationSummaryVisible
        || !adminActivationState.activationCenterVisible
        || !adminActivationState.decisionCode
        || adminActivationState.checklistCount < 7
        || adminActivationState.switchCount < 6
        || !adminActivationState.hasActivateCard
        || !adminActivationState.hasDeactivateCard
        || (!adminActivationState.hasActivateConfirmationInput && !adminActivationState.hasActivateNoopState)
        || !adminActivationState.hasDeactivateConfirmationInput
        || !adminActivationState.hasEmergencySection
        || !adminActivationState.hasCrisisPanelLink) {
      throw new Error('Admin / Settings activation control center did not render the expected guarded surface.');
    }

    await client.navigate(`${baseUrl}/admin/Overview`);
    await client.waitForReady();
    await client.waitForLocationContains('/admin/Overview');
    await client.captureScreenshot(adminOverviewScreenshotPath);

    const adminOverviewState = await client.evaluate(`(() => {
      const pageText = (document.body.textContent || '').toLowerCase();
      const initialActiveTabText = document.querySelector('#cb_super_admin_flow_tab_nav .nav-link.active')?.innerText?.trim() || '';
      const showTab = key => {
        const link = document.querySelector('[data-cb-super-admin-flow-tab-link="' + key + '"]');
        if (!link) {
          return false;
        }

        if (window.jQuery && window.jQuery.fn && window.jQuery.fn.tab) {
          window.jQuery(link).tab('show');
        } else {
          link.click();
        }

        const target = link.getAttribute('href');
        const pane = target ? document.querySelector(target) : null;
        return link.classList.contains('active') && !!pane;
      };

      const setupTabOpens = showTab('setup');
      const activationTabOpens = showTab('activation');
      const monitoringTabOpens = showTab('monitoring');
      const advancedTabOpens = showTab('advanced');
      const setupText = document.querySelector('#cb_super_admin_flow_tab_setup')?.textContent || '';
      const activationText = document.querySelector('#cb_super_admin_flow_tab_activation')?.textContent || '';
      const monitoringText = document.querySelector('#cb_super_admin_flow_tab_monitoring')?.textContent || '';
      const advancedText = document.querySelector('#cb_super_admin_flow_tab_advanced')?.textContent || '';
      const activateButton = document.querySelector('[data-cb-super-admin-activation-action="activate"] button[type="submit"]');
      const activateReason = document.querySelector('[data-cb-super-admin-action-reason="activate"]')?.innerText?.trim() || '';
      const primaryNavLabels = Array.from(document.querySelectorAll('[data-cb-super-admin-primary-link] .menu-text')).map(item => item.innerText.trim()).filter(Boolean);
      const sidebarTexts = Array.from(document.querySelectorAll('.cb-admin-menu-nav .menu-text')).map(item => item.innerText.trim()).filter(Boolean);
      const jargonTokens = [
        'evidence missing',
        'degraded chain',
        'continuity snapshot unavailable',
        'rollout blocker unresolved',
        'audit visibility insufficient'
      ];
      const mainFlowText = (setupText + ' ' + activationText + ' ' + monitoringText).toLowerCase();
      const technicalDetailTokens = [
        'audit ve trace',
        'incident',
        'rollout kanit',
        'diagnostik',
        'before/after',
        'timeline'
      ];
      const monitoringTechnicalTokens = [
        'incident timeline',
        'trace',
        'health breakdown',
        'debug',
        'evidence',
        'audit timeline'
      ];
      return {
        tabCount: document.querySelectorAll('[data-cb-super-admin-flow-tab-link]').length,
        initialActiveTabText,
        activeTabText: document.querySelector('#cb_super_admin_flow_tab_nav .nav-link.active')?.innerText?.trim() || '',
        hasSimpleFlow: !!document.querySelector('[data-cb-super-admin-simple-flow]'),
        hasSetupSection: !!document.querySelector('[data-cb-super-admin-flow-section="setup"]'),
        hasActivationSection: !!document.querySelector('[data-cb-super-admin-flow-section="activation"]'),
        hasMonitoringSection: !!document.querySelector('[data-cb-super-admin-flow-section="monitoring"]'),
        hasAdvancedSection: !!document.querySelector('[data-cb-super-admin-flow-section="advanced"]'),
        setupAccessible: document.querySelector('[data-cb-super-admin-flow-section="setup"]')?.getAttribute('data-cb-super-admin-flow-accessible') === 'true',
        activationAccessible: document.querySelector('[data-cb-super-admin-flow-section="activation"]')?.getAttribute('data-cb-super-admin-flow-accessible') === 'true',
        monitoringAccessible: document.querySelector('[data-cb-super-admin-flow-section="monitoring"]')?.getAttribute('data-cb-super-admin-flow-accessible') === 'true',
        advancedAccessible: document.querySelector('[data-cb-super-admin-flow-section="advanced"]')?.getAttribute('data-cb-super-admin-flow-accessible') === 'true',
        setupCardCount: document.querySelectorAll('#cb_super_admin_flow_tab_setup .cb-admin-summary-card').length,
        monitoringCardCount: document.querySelectorAll('#cb_super_admin_flow_tab_monitoring .cb-admin-summary-card').length,
        advancedLinkCount: document.querySelectorAll('#cb_super_admin_flow_tab_advanced a[href]').length,
        hasSetupForm: !!document.querySelector('[data-cb-super-admin-setup-form]'),
        hasActivationPanel: !!document.querySelector('[data-cb-super-admin-activation-panel]'),
        activationStatusCount: document.querySelectorAll('#cb_super_admin_flow_tab_activation [data-cb-super-admin-activation-status-item]').length,
        hasActivationActionsPanel: !!document.querySelector('[data-cb-super-admin-activation-actions]'),
        activationHasEmergency: activationText.includes('Acil Durdur'),
        activationHasTechnicalJargon: ['checklist', 'blocker', 'evidence', 'timeline', 'aktivasyon detaylari', 'readiness'].some(token => activationText.toLowerCase().includes(token)),
        hasMonitoringPanel: !!document.querySelector('[data-cb-super-admin-monitoring-panel]'),
        hasAdvancedLinks: !!document.querySelector('[data-cb-super-admin-advanced-links]'),
        hasConnectionTest: setupText.includes('Baglantiyi Test Et'),
        hasSaveAndContinue: setupText.includes('Kaydet ve Devam Et'),
        hasSetupApiKey: !!document.querySelector('#setupApiKey'),
        hasSetupSecretKey: !!document.querySelector('#setupApiSecret'),
        hasSetupConnectionName: !!document.querySelector('#setupConnectionName'),
        setupHasTechnicalSetupFields: setupText.includes('Live izin referansi') || setupText.includes('Onay ibaresi') || setupText.includes('fingerprint') || setupText.includes('permission'),
        hasPrepareAction: activationText.includes('Sistemi Hazirla'),
        hasActivateAction: activationText.includes('Sistemi Aktif Et'),
        hasDeactivateAction: activationText.includes('Sistemi Kapat'),
        hasMonitoringRefresh: monitoringText.includes('Yenile'),
        hasMonitoringEmergency: monitoringText.includes('Acil Durdur'),
        hasMonitoringStopSummary: monitoringText.includes('Son durdurma nedeni'),
        hasMonitoringRunningBotCount: monitoringText.includes('Calisan bot sayisi'),
        hasMonitoringLastOperationTime: monitoringText.includes('Son islem zamani'),
        hasMonitoringLastErrorSummary: monitoringText.includes('Son hata'),
        monitoringHasShutdownAction: monitoringText.includes('Sistemi Kapat'),
        monitoringHasTechnicalDetails: monitoringTechnicalTokens.some(token => monitoringText.toLowerCase().includes(token)),
        setupTabOpens,
        activationTabOpens,
        monitoringTabOpens,
        advancedTabOpens,
        activateDisabled: !!activateButton?.disabled,
        activateReason,
        activateReasonLineCount: activateReason ? (activateReason.indexOf(String.fromCharCode(10)) >= 0 ? 2 : 1) : 0,
        hasTechnicalCentersVisible: pageText.includes('runtime & health center') || pageText.includes('user / bot governance center') || pageText.includes('exchange / credential governance') || pageText.includes('policy / limit governance'),
        hasTechnicalJargon: jargonTokens.some(token => pageText.includes(token) || activateReason.toLowerCase().includes(token)),
        mainFlowHasTechnicalDetails: technicalDetailTokens.some(token => mainFlowText.includes(token)),
        primaryNavCount: primaryNavLabels.length,
        primaryNavLabels,
        primaryNavHasSetup: primaryNavLabels.includes('Sistem Kurulumu'),
        primaryNavHasActivation: primaryNavLabels.includes('Sistemi Aktiflestir'),
        primaryNavHasMonitoring: primaryNavLabels.includes('Sistemi Izle'),
        primaryNavHasUsers: primaryNavLabels.includes('Kullanicilar'),
        primaryNavHasAdvanced: primaryNavLabels.includes('Gelismis'),
        sidebarHasTechnicalEntries: sidebarTexts.some(text => ['Global Search', 'Exchange Hesapları', 'Bot Operasyonları', 'Strategy/AI İzleme', 'Audit & Log Merkezi', 'Incident Merkezi', 'Global Ayarlar'].includes(text)),
        advancedTextVisible: advancedText.includes('Audit ve Trace') && advancedText.includes('Incidents') && advancedText.includes('Loglar / Diagnostik') && advancedText.includes('Rollout Kanitlari') && advancedText.includes('Trace arama') && advancedText.includes('Execution debugger') && advancedText.includes('Idempotency / rebuild')
      };
    })()`);

    if (adminOverviewState.tabCount < 4
        || !/Sistem Kurulumu/i.test(adminOverviewState.initialActiveTabText)
        || !adminOverviewState.hasSimpleFlow
        || !adminOverviewState.hasSetupSection
        || !adminOverviewState.hasActivationSection
        || !adminOverviewState.hasMonitoringSection
        || !adminOverviewState.hasAdvancedSection
        || !adminOverviewState.setupAccessible
        || !adminOverviewState.activationAccessible
        || !adminOverviewState.monitoringAccessible
        || !adminOverviewState.advancedAccessible
        || adminOverviewState.setupCardCount < 3
        || adminOverviewState.monitoringCardCount !== 6
        || adminOverviewState.advancedLinkCount < 6
        || !adminOverviewState.hasSetupForm
        || !adminOverviewState.hasActivationPanel
        || adminOverviewState.activationStatusCount !== 3
        || !adminOverviewState.hasActivationActionsPanel
        || !adminOverviewState.activationHasEmergency
        || adminOverviewState.activationHasTechnicalJargon
        || !adminOverviewState.hasMonitoringPanel
        || !adminOverviewState.hasAdvancedLinks
        || !adminOverviewState.hasConnectionTest
        || !adminOverviewState.hasSaveAndContinue
        || !adminOverviewState.hasSetupApiKey
        || !adminOverviewState.hasSetupSecretKey
        || !adminOverviewState.hasSetupConnectionName
        || adminOverviewState.setupHasTechnicalSetupFields
        || !adminOverviewState.hasPrepareAction
        || !adminOverviewState.hasActivateAction
        || !adminOverviewState.hasDeactivateAction
        || !adminOverviewState.hasMonitoringRefresh
        || !adminOverviewState.hasMonitoringEmergency
        || !adminOverviewState.hasMonitoringStopSummary
        || !adminOverviewState.hasMonitoringRunningBotCount
        || !adminOverviewState.hasMonitoringLastOperationTime
        || !adminOverviewState.hasMonitoringLastErrorSummary
        || adminOverviewState.monitoringHasShutdownAction
        || adminOverviewState.monitoringHasTechnicalDetails
        || !adminOverviewState.setupTabOpens
        || !adminOverviewState.activationTabOpens
        || !adminOverviewState.monitoringTabOpens
        || !adminOverviewState.advancedTabOpens
        || (adminOverviewState.activateDisabled && (!adminOverviewState.activateReason || adminOverviewState.activateReasonLineCount !== 1))
        || !adminOverviewState.advancedTextVisible
        || adminOverviewState.hasTechnicalCentersVisible
        || adminOverviewState.hasTechnicalJargon
        || adminOverviewState.mainFlowHasTechnicalDetails
        || adminOverviewState.primaryNavCount !== 5
        || !adminOverviewState.primaryNavHasSetup
        || !adminOverviewState.primaryNavHasActivation
        || !adminOverviewState.primaryNavHasMonitoring
        || !adminOverviewState.primaryNavHasUsers
        || !adminOverviewState.primaryNavHasAdvanced
        || adminOverviewState.sidebarHasTechnicalEntries) {
      throw new Error('Admin / Overview did not render the expected always-open super admin flow.');
    }

    if (shouldRunBinanceSetupProbe) {
      await client.evaluate(`(() => {
        const link = document.querySelector('[data-cb-super-admin-flow-tab-link="setup"]');
        if (!link) throw new Error('Setup tab link was not found.');
        if (window.jQuery && window.jQuery.fn && window.jQuery.fn.tab) {
          window.jQuery(link).tab('show');
        } else {
          link.click();
        }

        const selectedMode = document.querySelector('input[name="isDemo"][value="${testBinanceIsDemo ? 'true' : 'false'}"]');
        if (!selectedMode) throw new Error('Setup environment option was not found.');
        selectedMode.checked = true;
        selectedMode.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      })()`);

      await setInputValueSecurely(client, '#setupConnectionName', 'Smoke Binance');
      await setInputValueSecurely(client, '#setupApiKey', testBinanceApiKey);
      await setInputValueSecurely(client, '#setupApiSecret', testBinanceApiSecret);

      await client.evaluate(`(() => {
        const form = document.querySelector('[data-cb-super-admin-setup-form] form');
        if (!form) throw new Error('Setup form was not found.');
        const button = form.querySelector('button[name="setupCommand"][value="test"]');
        if (!button) throw new Error('Setup test button was not found.');
        if (button.disabled) throw new Error('Setup test button was disabled.');
        if (form.requestSubmit) {
          form.requestSubmit(button);
        } else {
          button.click();
        }
        return true;
      })()`);

      await client.waitForReady(90000);
      await client.waitForLocationContains('/admin/Overview', 90000);

      adminOverviewBinanceSetupState = await client.evaluate(`(() => {
        const result = document.querySelector('[data-cb-super-admin-setup-result]');
        const resultText = result ? result.innerText.trim() : '';
        return {
          attempted: true,
          skippedReason: '',
          resultKind: result?.getAttribute('data-cb-super-admin-setup-result') || '',
          resultText,
          successVisible: result?.getAttribute('data-cb-super-admin-setup-result') === 'success' && /API erisimi basarili|Binance baglantisi dogrulandi/i.test(resultText),
          errorVisible: result?.getAttribute('data-cb-super-admin-setup-result') === 'error'
        };
      })()`);

      if (!adminOverviewBinanceSetupState.successVisible) {
        throw new Error(`Binance setup connection test did not surface the success message. Result=${adminOverviewBinanceSetupState.resultKind}; Message=${adminOverviewBinanceSetupState.resultText}`);
      }
    }

    await client.navigate(`${baseUrl}/Settings`);
    await client.waitForReady();

    const superAdminUserFlowState = await client.evaluate(`(() => ({
      location: window.location.href,
      hasOperationsCenter: !!document.querySelector('[data-cb-super-admin-simple-flow]'),
      hasUserSettingsForm: !!document.querySelector('select[name="Form.PreferredTimeZoneId"]')
    }))()`);

    const superAdminRedirectLocation = superAdminUserFlowState.location.toLowerCase();
    if ((!superAdminRedirectLocation.endsWith('/admin') && !superAdminRedirectLocation.includes('/admin/overview'))
        || !superAdminUserFlowState.hasOperationsCenter
        || superAdminUserFlowState.hasUserSettingsForm) {
      throw new Error(`Super admin should be redirected away from the normal user settings flow. Location=${superAdminUserFlowState.location}; HasOps=${superAdminUserFlowState.hasOperationsCenter}; HasUserSettingsForm=${superAdminUserFlowState.hasUserSettingsForm}`);
    }
    await client.navigate(`${baseUrl}/Settings/Mfa`);
    await client.waitForReady();

    const superAdminMfaState = await client.evaluate(`(() => ({
      location: window.location.href,
      hasAuthenticatorSetup: document.body.textContent.includes('Authenticator kurulumu')
    }))()`);

    if (!String(superAdminMfaState.location ?? '').toLowerCase().includes('/settings/mfa')
        || !superAdminMfaState.hasAuthenticatorSetup) {
      throw new Error(`Super admin MFA page should remain reachable. Location=${superAdminMfaState.location}; HasSetup=${superAdminMfaState.hasAuthenticatorSetup}`);
    }

    await client.navigate(`${baseUrl}/admin/Audit`);
    await client.waitForReady();
    await client.waitForLocationContains('/admin/Audit');
    await client.captureScreenshot(adminAuditScreenshotPath);

    const adminAuditState = await client.evaluate(`(() => {
      const pageText = document.body.textContent || '';
      const detailText = document.querySelector('[data-cb-admin-decision-detail]')?.textContent || '';
      const rowCount = document.querySelectorAll('[data-cb-admin-decision-row]').length;
      return {
        hasDecisionCenter: !!document.querySelector('[data-cb-admin-decision-center]'),
        hasOutcomeFilter: !!document.querySelector('#cb_admin_audit_filter_outcome'),
        hasReasonCodeFilter: !!document.querySelector('#cb_admin_audit_filter_reason_code'),
        summaryCardCount: document.querySelectorAll('[data-cb-admin-decision-summary] .cb-admin-summary-card').length,
        rowCount,
        detailVisible: !!document.querySelector('[data-cb-admin-decision-detail]'),
        detailHasEmptyState: /Secili kayit yok/i.test(detailText),
        detailHasDecisionCode: !!document.querySelector('[data-cb-admin-decision-code]'),
        hasBeforeAfter: !!document.querySelector('[data-cb-admin-before]') && !!document.querySelector('[data-cb-admin-after]'),
        hasTraceSection: !!document.querySelector('[data-cb-admin-trace-section]'),
        hasApprovalHistory: !!document.querySelector('[data-cb-admin-approval-history]'),
        hasIncidentTimeline: !!document.querySelector('[data-cb-admin-incident-timeline]'),
        hasAuditTrail: !!document.querySelector('[data-cb-admin-audit-trail]'),
        pageTitle: document.querySelector('.cb-page-title, .cb-admin-page-title, .cb-admin-section-title')?.innerText?.trim() || '',
        hasUnavailableFallback: pageText.includes('Unavailable')
      };
    })()`);

    if (!adminAuditState.hasDecisionCenter
        || !adminAuditState.hasOutcomeFilter
        || !adminAuditState.hasReasonCodeFilter
        || adminAuditState.summaryCardCount < 4
        || !adminAuditState.detailVisible
        || (adminAuditState.rowCount === 0 && !adminAuditState.detailHasEmptyState)
        || (adminAuditState.rowCount > 0 && (!adminAuditState.detailHasDecisionCode || !adminAuditState.hasBeforeAfter || !adminAuditState.hasTraceSection || !adminAuditState.hasApprovalHistory || !adminAuditState.hasIncidentTimeline || !adminAuditState.hasAuditTrail))) {
      throw new Error('Admin / Audit did not render the expected incident / audit / decision center surface.');
    }

    const summary = {
      baseUrl,
      anonymousLocation,
      timeZoneOptionCount: Number(settingsPageState.optionCount),
      initialPreferredTimeZoneId: String(settingsPageState.currentValue ?? ''),
      refreshButtonVisible: Boolean(settingsPageState.refreshButtonExists),
      operationalFieldsHidden: !settingsPageState.guardThresholdText && !settingsPageState.guardReasonText,
      displayOnlyHelperText: String(settingsPageState.helperText ?? ''),
      opsNoticeText: String(settingsPageState.opsNoticeText ?? ''),
      savedPreferredTimeZoneId: String(postSaveState.selectedValue ?? ''),
      reloadedPreferredTimeZoneId: String(reloadedState.selectedValue ?? ''),
      saveSuccessVisible: Boolean(postSaveState.successVisible),
      saveSuccessText: String(postSaveState.successText ?? ''),
      antiforgeryStatus: Number(antiforgeryResult.status),
      invalidErrorVisible: Boolean(invalidState.hasError),
      invalidErrorText: String(invalidState.errorText ?? ''),
      userShellHomeVisible: Boolean(dashboardState.hasUserShellHome),
      userShellPrimaryNavLabels: Array.isArray(dashboardState.primaryNavLabels) ? dashboardState.primaryNavLabels.join(' | ') : '',
      userAdminLinkVisible: Boolean(userNavigationState.adminSettingsLinkVisible),
      adminSettingsTabCount: Number(adminSettingsState.tabCount),
      adminSettingsActiveTabText: String(adminSettingsState.activeTabText ?? ''),
      adminSettingsHasReadOnlyText: Boolean(adminSettingsState.hasReadOnlyText),
      adminSettingsHasWritableText: Boolean(adminSettingsState.hasWritableText),
      adminSettingsHasActivationPanel: Boolean(adminSettingsState.hasActivationPanel),
      adminActivationTabText: String(adminActivationState.activationTabText ?? ''),
      adminActivationPaneVisible: Boolean(adminActivationState.activationPaneExists),
      adminActivationDecisionCode: String(adminActivationState.decisionCode ?? ''),
      adminActivationDecisionType: String(adminActivationState.decisionType ?? ''),
      adminActivationActivatable: String(adminActivationState.activatable ?? ''),
      adminActivationChecklistCount: Number(adminActivationState.checklistCount ?? 0),
      userOverviewDenied: !String(userOverviewState.location ?? '').toLowerCase().includes('/admin/overview') || !Boolean(userOverviewState.hasOperationsCenter),
      userAuditDenied: !String(userAuditState.location ?? '').toLowerCase().includes('/admin/audit') || !Boolean(userAuditState.hasDecisionCenter),
      superAdminMfaAccessible: String(superAdminMfaState.location ?? '').toLowerCase().includes('/settings/mfa') && Boolean(superAdminMfaState.hasAuthenticatorSetup),
      superAdminUserFlowRedirected: Boolean(superAdminUserFlowState.hasOperationsCenter) && !Boolean(superAdminUserFlowState.hasUserSettingsForm) && (() => { const location = String(superAdminUserFlowState.location ?? '').toLowerCase(); return location.endsWith('/admin') || location.includes('/admin/overview'); })(),
      adminOverviewTabCount: Number(adminOverviewState.tabCount ?? 0),
      adminOverviewActiveTabText: String(adminOverviewState.initialActiveTabText ?? adminOverviewState.activeTabText ?? ''),
      adminOverviewHasSimpleFlow: Boolean(adminOverviewState.hasSimpleFlow),
      adminOverviewHasSetupSection: Boolean(adminOverviewState.hasSetupSection),
      adminOverviewHasActivationSection: Boolean(adminOverviewState.hasActivationSection),
      adminOverviewHasMonitoringSection: Boolean(adminOverviewState.hasMonitoringSection),
      adminOverviewHasAdvancedSection: Boolean(adminOverviewState.hasAdvancedSection),
      adminOverviewSetupCardCount: Number(adminOverviewState.setupCardCount ?? 0),
      adminOverviewMonitoringCardCount: Number(adminOverviewState.monitoringCardCount ?? 0),
      adminOverviewMonitoringHasTechnicalDetails: Boolean(adminOverviewState.monitoringHasTechnicalDetails),
      adminOverviewAdvancedLinkCount: Number(adminOverviewState.advancedLinkCount ?? 0),
      adminOverviewSetupTabOpens: Boolean(adminOverviewState.setupTabOpens),
      adminOverviewActivationTabOpens: Boolean(adminOverviewState.activationTabOpens),
      adminOverviewMonitoringTabOpens: Boolean(adminOverviewState.monitoringTabOpens),
      adminOverviewAdvancedTabOpens: Boolean(adminOverviewState.advancedTabOpens),
      adminOverviewActivateDisabled: Boolean(adminOverviewState.activateDisabled),
      adminOverviewActivateReason: String(adminOverviewState.activateReason ?? ''),
      adminOverviewHasTechnicalJargon: Boolean(adminOverviewState.hasTechnicalJargon),
      adminOverviewPrimaryNavCount: Number(adminOverviewState.primaryNavCount ?? 0),
      adminOverviewPrimaryNavLabels: Array.isArray(adminOverviewState.primaryNavLabels) ? adminOverviewState.primaryNavLabels.join(' | ') : '',
      adminOverviewBinanceSetupAttempted: Boolean(adminOverviewBinanceSetupState.attempted),
      adminOverviewBinanceSetupSkippedReason: String(adminOverviewBinanceSetupState.skippedReason ?? ''),
      adminOverviewBinanceSetupResultKind: String(adminOverviewBinanceSetupState.resultKind ?? ''),
      adminOverviewBinanceSetupResultText: String(adminOverviewBinanceSetupState.resultText ?? ''),
      adminOverviewBinanceSetupSuccessVisible: Boolean(adminOverviewBinanceSetupState.successVisible),
      adminAuditVisible: Boolean(adminAuditState.hasDecisionCenter),
      adminAuditOutcomeFilterVisible: Boolean(adminAuditState.hasOutcomeFilter),
      adminAuditReasonCodeFilterVisible: Boolean(adminAuditState.hasReasonCodeFilter),
      adminAuditSummaryCardCount: Number(adminAuditState.summaryCardCount ?? 0),
      adminAuditRowCount: Number(adminAuditState.rowCount ?? 0),
      adminAuditDetailVisible: Boolean(adminAuditState.detailVisible),
      screenshots: {
        anonymous: anonymousScreenshotPath,
        settings: settingsScreenshotPath,
        saved: savedScreenshotPath,
        invalid: invalidScreenshotPath,
        dashboard: dashboardScreenshotPath,
        adminSettings: adminSettingsScreenshotPath,
        adminOverview: adminOverviewScreenshotPath,
        adminAudit: adminAuditScreenshotPath
      },
      logs: {
        webStdOut: webStdOutPath,
        webStdErr: webStdErrPath,
        browserStdOut: browserStdOutPath,
        browserStdErr: browserStdErrPath
      }
    };

    writeFileSync(summaryPath, JSON.stringify(summary, null, 2));

    console.log(`AnonymousLocation=${anonymousLocation}`);
    console.log(`TimeZoneOptionCount=${summary.timeZoneOptionCount}`);
    console.log(`OperationalFieldsHidden=${summary.operationalFieldsHidden}`);
    console.log(`OpsNoticeText=${summary.opsNoticeText}`);
    console.log(`SavedPreferredTimeZoneId=${summary.savedPreferredTimeZoneId}`);
    console.log(`ReloadedPreferredTimeZoneId=${summary.reloadedPreferredTimeZoneId}`);
    console.log(`AntiforgeryStatus=${summary.antiforgeryStatus}`);
        console.log(`InvalidErrorVisible=${summary.invalidErrorVisible}`);
    console.log(`UserShellHomeVisible=${summary.userShellHomeVisible}`);
    console.log(`UserAdminLinkVisible=${summary.userAdminLinkVisible}`);
    console.log(`AdminSettingsTabCount=${summary.adminSettingsTabCount}`);
    console.log(`AdminSettingsActiveTab=${summary.adminSettingsActiveTabText}`);
    console.log(`AdminActivationTab=${summary.adminActivationTabText}`);
    console.log(`AdminActivationPaneVisible=${summary.adminActivationPaneVisible}`);
    console.log(`AdminActivationDecisionCode=${summary.adminActivationDecisionCode}`);
    console.log(`AdminActivationDecisionType=${summary.adminActivationDecisionType}`);
    console.log(`AdminActivationActivatable=${summary.adminActivationActivatable}`);
        console.log(`AdminActivationChecklistCount=${summary.adminActivationChecklistCount}`);
    console.log(`UserAuditDenied=${summary.userAuditDenied}`);
    console.log(`SuperAdminMfaAccessible=${summary.superAdminMfaAccessible}`);
    console.log(`SuperAdminUserFlowRedirected=${summary.superAdminUserFlowRedirected}`);
    console.log(`AdminOverviewHasSimpleFlow=${summary.adminOverviewHasSimpleFlow}`);
    console.log(`AdminOverviewSetupCardCount=${summary.adminOverviewSetupCardCount}`);
    console.log(`AdminOverviewMonitoringCardCount=${summary.adminOverviewMonitoringCardCount}`);
    console.log(`AdminOverviewMonitoringHasTechnicalDetails=${summary.adminOverviewMonitoringHasTechnicalDetails}`);
    console.log(`AdminOverviewAdvancedLinkCount=${summary.adminOverviewAdvancedLinkCount}`);
    console.log(`AdminOverviewSetupTabOpens=${summary.adminOverviewSetupTabOpens}`);
    console.log(`AdminOverviewActivationTabOpens=${summary.adminOverviewActivationTabOpens}`);
    console.log(`AdminOverviewMonitoringTabOpens=${summary.adminOverviewMonitoringTabOpens}`);
    console.log(`AdminOverviewAdvancedTabOpens=${summary.adminOverviewAdvancedTabOpens}`);
    console.log(`AdminOverviewActivateDisabled=${summary.adminOverviewActivateDisabled}`);
    console.log(`AdminOverviewActivateReason=${summary.adminOverviewActivateReason}`);
    console.log(`AdminOverviewPrimaryNavCount=${summary.adminOverviewPrimaryNavCount}`);
    console.log(`AdminOverviewPrimaryNavLabels=${summary.adminOverviewPrimaryNavLabels}`);
    console.log(`AdminOverviewBinanceSetupAttempted=${summary.adminOverviewBinanceSetupAttempted}`);
    console.log(`AdminOverviewBinanceSetupSuccessVisible=${summary.adminOverviewBinanceSetupSuccessVisible}`);
    console.log(`AdminOverviewBinanceSetupResultKind=${summary.adminOverviewBinanceSetupResultKind}`);
    console.log(`AdminAuditVisible=${summary.adminAuditVisible}`);
    console.log(`AdminAuditSummaryCardCount=${summary.adminAuditSummaryCardCount}`);
    console.log(`AdminAuditRowCount=${summary.adminAuditRowCount}`);
    console.log(`SummaryPath=${summaryPath}`);
  } finally {
    await client?.close();
    await stopManagedProcess(browserProcess);
    await stopManagedProcess(webProcess);

    for (const [key, value] of Object.entries(originalEnvironment)) {
      if (typeof value === 'undefined') {
        delete process.env[key];
      } else {
        process.env[key] = value;
      }
    }
  }
})().catch(error => {
  console.error(error?.stack ?? error?.message ?? String(error));
  process.exit(1);
});

