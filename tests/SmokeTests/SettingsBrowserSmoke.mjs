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
    const updatedTimeZoneId = 'Dateline Standard Time';

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
        ASPNETCORE_URLS: process.env.ASPNETCORE_URLS
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
    await client.captureScreenshot(settingsScreenshotPath);    const settingsPageState = await client.evaluate(`(() => {
      const select = document.querySelector('select[name="Form.PreferredTimeZoneId"]');
      const token = document.querySelector('input[name="__RequestVerificationToken"]');
      const refreshButton = document.querySelector('form[action$="/Settings/RefreshClockDrift"] button[type="submit"]');
      const guardThresholdCell = Array.from(document.querySelectorAll('td')).find(cell => cell.innerText.trim() === 'Threshold')?.nextElementSibling;
      const guardReasonCell = Array.from(document.querySelectorAll('td')).find(cell => cell.innerText.trim() === 'Readable reason')?.nextElementSibling;
      const helperText = Array.from(document.querySelectorAll('.cb-helper-text')).map(node => node.innerText.trim()).join(' | ');
      const opsNoticeText = Array.from(document.querySelectorAll('.cb-validation-summary-warning')).map(node => node.innerText.trim()).join(' | ');
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

    if (!/display timezone/i.test(settingsPageState.helperText)) {
      throw new Error('Display-only timezone helper text was not rendered on /Settings.');
    }

    if (!/Super Admin\/Ops/i.test(settingsPageState.opsNoticeText)) {
      throw new Error('Ops scope notice was not rendered on /Settings.');
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
      const summary = document.querySelector('[data-cb-ops-drift-summary]');
      const reason = document.querySelector('[data-cb-ops-drift-reason]');
      return {
        summaryText: summary ? summary.innerText.trim() : '',
        reasonText: reason ? reason.innerText.trim() : ''
      };
    })()`);

    if (!dashboardState.summaryText || !dashboardState.reasonText) {
      throw new Error('Dashboard drift summary did not render readable text.');
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
      dashboardDriftSummaryText: String(dashboardState.summaryText ?? ''),
      dashboardDriftReasonText: String(dashboardState.reasonText ?? ''),
      screenshots: {
        anonymous: anonymousScreenshotPath,
        settings: settingsScreenshotPath,
        saved: savedScreenshotPath,
        invalid: invalidScreenshotPath,
        dashboard: dashboardScreenshotPath
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
    console.log(`DashboardDriftSummary=${summary.dashboardDriftSummaryText}`);
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








