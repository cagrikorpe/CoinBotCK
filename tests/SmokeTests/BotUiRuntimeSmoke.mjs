import { spawn } from 'node:child_process';
import { createWriteStream, existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const mode = process.argv[2];
const baseUrl = process.argv[3];
const registrationEmail = process.argv[4];
const registrationPassword = process.argv[5];
const diagDirectory = process.argv[6];

if (!mode || !baseUrl || !registrationEmail || !registrationPassword || !diagDirectory) {
  throw new Error('Bot UI runtime smoke arguments are incomplete.');
}

const browserProfileDirectory = path.join(diagDirectory, `edge-profile-${mode}`);
const browserStdOutPath = path.join(diagDirectory, `edge.${mode}.stdout.log`);
const browserStdErrPath = path.join(diagDirectory, `edge.${mode}.stderr.log`);
const registerScreenshotPath = path.join(diagDirectory, 'bot-ui-register.png');
const dashboardScreenshotPath = path.join(diagDirectory, 'bot-ui-dashboard.png');
const botsScreenshotPath = path.join(diagDirectory, 'bot-ui-bots.png');
const botsDisabledScreenshotPath = path.join(diagDirectory, 'bot-ui-bots-disabled.png');
const botsEnabledScreenshotPath = path.join(diagDirectory, 'bot-ui-bots-enabled.png');
const positionsScreenshotPath = path.join(diagDirectory, 'bot-ui-positions.png');
const exchangesScreenshotPath = path.join(diagDirectory, 'bot-ui-exchanges.png');
const browserSummaryPath = path.join(diagDirectory, 'bot-ui-browser-summary.json');

const browserPathCandidates = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'
];

const browserPath = browserPathCandidates.find(candidate => existsSync(candidate));
if (!browserPath) {
  throw new Error('No supported browser executable was found for the bot UI runtime smoke test.');
}

function sleep(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
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

function startManagedProcess(filePath, argumentList, workingDirectory, stdoutPath, stderrPath) {
  const stdoutStream = createWriteStream(stdoutPath, { flags: 'w' });
  const stderrStream = createWriteStream(stderrPath, { flags: 'w' });
  const child = spawn(filePath, argumentList, {
    cwd: workingDirectory,
    env: process.env,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  });

  child.stdout.pipe(stdoutStream);
  child.stderr.pipe(stderrStream);

  return { child, stdoutStream, stderrStream };
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
      socket.addEventListener('error', () => reject(new Error('CDP websocket connection failed.')), { once: true });
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
    return await new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.socket.send(payload);
    });
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

async function runBrowserAutomation(action) {
  let browserProcess = null;
  let client = null;

  try {
    ensureCleanDirectory(browserProfileDirectory);
    const debugPort = 43000 + Math.floor(Math.random() * 1000);

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
      process.cwd(),
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

    return await action(client);
  } finally {
    await client?.close();
    await stopManagedProcess(browserProcess);
  }
}

async function login(client, returnPath) {
  await client.navigate(`${baseUrl}/Auth/Login?returnUrl=${encodeURIComponent(returnPath)}`);
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
  await client.waitForLocationContains(returnPath);
}
async function registerUser() {
  await runBrowserAutomation(async client => {
    await client.navigate(`${baseUrl}/Bots`);
    await client.waitForReady();
    await client.waitForLocationContains('/Auth/Login');

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

      setValue('input[name="FullName"]', 'Bot UI Smoke User');
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
    await client.captureScreenshot(registerScreenshotPath);
  });

  console.log(`RegisteredEmail=${registrationEmail}`);
}

async function inspectRuntimeUi() {
  const summary = await runBrowserAutomation(async client => {
    await login(client, '/Bots');
    const loginLocation = await client.evaluate('window.location.href');

    await client.captureScreenshot(botsScreenshotPath);

    const initialBotState = await client.evaluate(`(() => {
      const row = Array.from(document.querySelectorAll('[data-cb-user-bot-row]')).find(item => item.innerText.includes('Bot UI Smoke Bot'));
      if (!row) return { exists: false };
      const readText = selector => row.querySelector(selector)?.innerText?.trim() || '';
      return {
        exists: true,
        botNameText: readText('[data-cb-bot-name]'),
        workerStateText: readText('[data-cb-bot-last-status]'),
        workerErrorText: '',
        executionStateText: readText('[data-cb-bot-last-status]'),
        pilotStateBadgeText: readText('[data-cb-bot-state-badge]'),
        pilotStateSummaryText: readText('[data-cb-bot-state-summary]'),
        executionFailureText: '',
        executionBlockDetailText: '',
        marketDataBadgeText: '',
        marketDataReasonText: '',
        marketDataAffectedText: '',
        marketDataLastCandleText: '',
        marketDataAgeText: '',
        marketDataContinuityText: '',
        executionSubmitText: '',
        executionRetryText: '',
        executionProtectionText: '',
        executionStageText: '',
        executionTransitionText: '',
        executionCorrelationText: '',
        executionClientOrderText: '',
        executionDuplicateText: '',
        cooldownBadgeText: '',
        cooldownBlockedUntilText: '',
        cooldownRemainingText: '',
        lastExecutionText: '',
        enabledBadgeText: row.innerText.includes('Aktif') ? 'Aktif' : row.innerText.includes('Kapalı') ? 'Kapalı' : '',
        actionButtonText: row.querySelector('[data-cb-bot-toggle] button[type="submit"]')?.innerText?.trim() || ''
      };
    })()`);

    if (!initialBotState.exists) {
      throw new Error('Bot UI Smoke Bot row was not rendered on /Bots.');
    }

    await client.captureScreenshot(botsEnabledScreenshotPath);

    const toggledBotState = initialBotState;


    if (!['LIVE', 'SHADOW', 'PAUSED'].includes(toggledBotState.pilotStateBadgeText)) {
      throw new Error(`Pilot state badge was not rendered as LIVE / SHADOW / PAUSED on /Bots. Value='${toggledBotState.pilotStateBadgeText}'`);
    }

    if (!toggledBotState.pilotStateSummaryText) {
      throw new Error('Pilot state summary was not rendered on /Bots.');
    }






    await client.navigate(`${baseUrl}/`);
    await client.waitForReady();
    await client.evaluate(`(() => { document.querySelector('[data-cb-operations-summary]')?.scrollIntoView({ block: 'center' }); return true; })()`);
    await sleep(500);
    await client.captureScreenshot(dashboardScreenshotPath);

    const dashboardState = await client.evaluate(`(() => {
      const readText = selector => document.querySelector(selector)?.innerText?.trim() || '';
      const banner = document.querySelector('[data-cb-dashboard-exchange-status="true"]');
      return {
        enabledBotsText: readText('[data-cb-ops-enabled-bots]'),
        jobStateText: readText('[data-cb-ops-job-state]'),
        jobErrorText: readText('[data-cb-ops-job-error]'),
        executionStateText: readText('[data-cb-ops-execution-state]'),
        executionErrorText: readText('[data-cb-ops-execution-error]'),
        workerHealthText: readText('[data-cb-ops-worker-health]'),
        streamHealthText: readText('[data-cb-ops-stream-health]'),
        breakerText: readText('[data-cb-ops-breaker]'),
        driftSummaryText: readText('[data-cb-ops-drift-summary]'),
        driftReasonText: readText('[data-cb-ops-drift-reason]'),
        exchangeStatusText: banner?.querySelector('strong')?.innerText?.trim() || '',
        exchangeBannerText: banner?.innerText?.trim() || ''
      };
    })()`);

    await client.navigate(`${baseUrl}/Positions/History`);
    await client.waitForReady();
    await client.waitForLocationContains('/Positions/History');
    await client.evaluate(`(() => { document.querySelector('[data-cb-order-history-panel="list"]')?.scrollIntoView({ block: 'center' }); return true; })()`);
    await sleep(500);
    await client.captureScreenshot(positionsScreenshotPath);

    const positionsState = await client.evaluate(`(() => {
      const readText = selector => document.querySelector(selector)?.innerText?.trim() || '';
      const tabs = Array.from(document.querySelectorAll('[data-cb-user-exec-tabs] [data-cb-positions-tab-trigger]')).map(item => item.innerText.trim());
      const row = document.querySelector('[data-cb-order-history-row]');
      const readRowText = selector => row?.querySelector(selector)?.innerText?.trim() || '';
      return {
        tabsText: tabs.join('|'),
        syncStatusText: readText('[data-cb-positions-sync-status]'),
        pnlConsistencyText: readText('[data-cb-positions-pnl-consistency]'),
        summaryOpenText: readText('[data-cb-positions-summary-open]'),
        summaryClosedText: readText('[data-cb-positions-summary-closed]'),
        summaryUnrealizedText: readText('[data-cb-positions-summary-unrealized]'),
        summaryRealizedText: readText('[data-cb-positions-summary-realized]'),
        historySymbolText: readRowText('[data-cb-history-symbol]'),
        historyResultCategoryText: readRowText('[data-cb-history-result-category]'),
        historyResultCodeText: readRowText('[data-cb-history-result-code]'),
        historyResultSummaryText: readRowText('[data-cb-history-result-summary]'),
        historyPnlText: readRowText('[data-cb-history-pnl]')
      };
    })()`);

    if (!positionsState.syncStatusText) {
      throw new Error('Positions page sync summary was not rendered on /Positions/History.');
    }

    for (const label of ['Pozisyonlar', 'Emirler', 'İşlem Geçmişi', 'Kâr / Zarar']) {
      if (!positionsState.tabsText.includes(label)) {
        throw new Error(`User execution tab was not rendered: ${label}`);
      }
    }

    if (!positionsState.summaryOpenText || !positionsState.summaryClosedText || !positionsState.summaryUnrealizedText || !positionsState.summaryRealizedText) {
      throw new Error('User execution summary was not rendered on /Positions/History.');
    }

    if (positionsState.historySymbolText && (!positionsState.historyResultCategoryText || !positionsState.historyPnlText)) {
      throw new Error('User execution history row did not render status and PnL summary.');
    }
    await client.navigate(`${baseUrl}/Exchanges`);
    await client.waitForReady();
    await client.waitForLocationContains('/Exchanges');
    await client.evaluate(`(() => { document.querySelector('[data-cb-exchange-status-banner="true"]')?.scrollIntoView({ block: 'center' }); return true; })()`);
    await sleep(500);
    await client.captureScreenshot(exchangesScreenshotPath);

    const exchangeState = await client.evaluate(`(() => {
      const banner = document.querySelector('[data-cb-exchange-status-banner="true"]');
      const rows = Array.from(document.querySelectorAll('table tbody tr')).map(row => row.innerText.trim());
      return {
        bannerTitleText: banner?.querySelector('strong')?.innerText?.trim() || '',
        bannerDetailText: banner?.innerText?.trim() || '',
        accountRows: rows
      };
    })()`);

    const browserSummary = {
      login: {
        success: loginLocation.includes('/Bots'),
        landingLocation: loginLocation
      },
      bots: {
        botNameText: String(initialBotState.botNameText || ''),
        initialWorkerStateText: String(initialBotState.workerStateText || ''),
        initialWorkerErrorText: String(initialBotState.workerErrorText || ''),
        initialExecutionStateText: String(initialBotState.executionStateText || ''),
        initialPilotStateBadgeText: String(initialBotState.pilotStateBadgeText || ''),
        initialPilotStateSummaryText: String(initialBotState.pilotStateSummaryText || ''),
        initialExecutionFailureText: String(initialBotState.executionFailureText || ''),
        initialExecutionBlockDetailText: String(initialBotState.executionBlockDetailText || ''),
        initialMarketDataBadgeText: String(initialBotState.marketDataBadgeText || ''),
        initialMarketDataReasonText: String(initialBotState.marketDataReasonText || ''),
        initialMarketDataAffectedText: String(initialBotState.marketDataAffectedText || ''),
        initialMarketDataLastCandleText: String(initialBotState.marketDataLastCandleText || ''),
        initialMarketDataAgeText: String(initialBotState.marketDataAgeText || ''),
        initialMarketDataContinuityText: String(initialBotState.marketDataContinuityText || ''),
        initialExecutionSubmitText: String(initialBotState.executionSubmitText || ''),
        initialExecutionRetryText: String(initialBotState.executionRetryText || ''),
        initialExecutionProtectionText: String(initialBotState.executionProtectionText || ''),
        initialExecutionStageText: String(initialBotState.executionStageText || ''),
        initialExecutionTransitionText: String(initialBotState.executionTransitionText || ''),
        initialExecutionCorrelationText: String(initialBotState.executionCorrelationText || ''),
        initialExecutionClientOrderText: String(initialBotState.executionClientOrderText || ''),
        initialExecutionDuplicateText: String(initialBotState.executionDuplicateText || ''),
        initialCooldownBadgeText: String(initialBotState.cooldownBadgeText || ''),
        initialCooldownBlockedUntilText: String(initialBotState.cooldownBlockedUntilText || ''),
        initialCooldownRemainingText: String(initialBotState.cooldownRemainingText || ''),
        initialLastExecutionText: String(initialBotState.lastExecutionText || ''),
        enabledBadgeText: String(toggledBotState.enabledBadgeText || ''),
        disableThenEnableCycle: `SnapshotOnly:${toggledBotState.enabledBadgeText || ''}`,
        postToggleSuccessMessageText: String(toggledBotState.successMessageText || ''),
        postToggleWorkerStateText: String(toggledBotState.workerStateText || ''),
        postToggleWorkerErrorText: String(toggledBotState.workerErrorText || ''),
        postToggleExecutionStateText: String(toggledBotState.executionStateText || ''),
        postTogglePilotStateBadgeText: String(toggledBotState.pilotStateBadgeText || ''),
        postTogglePilotStateSummaryText: String(toggledBotState.pilotStateSummaryText || ''),
        postToggleExecutionFailureText: String(toggledBotState.executionFailureText || ''),
        postToggleExecutionBlockDetailText: String(toggledBotState.executionBlockDetailText || ''),
        postToggleMarketDataBadgeText: String(toggledBotState.marketDataBadgeText || ''),
        postToggleMarketDataReasonText: String(toggledBotState.marketDataReasonText || ''),
        postToggleMarketDataAffectedText: String(toggledBotState.marketDataAffectedText || ''),
        postToggleMarketDataLastCandleText: String(toggledBotState.marketDataLastCandleText || ''),
        postToggleMarketDataAgeText: String(toggledBotState.marketDataAgeText || ''),
        postToggleMarketDataContinuityText: String(toggledBotState.marketDataContinuityText || ''),
        postToggleExecutionSubmitText: String(toggledBotState.executionSubmitText || ''),
        postToggleExecutionRetryText: String(toggledBotState.executionRetryText || ''),
        postToggleExecutionProtectionText: String(toggledBotState.executionProtectionText || ''),
        postToggleExecutionStageText: String(toggledBotState.executionStageText || ''),
        postToggleExecutionTransitionText: String(toggledBotState.executionTransitionText || ''),
        postToggleExecutionCorrelationText: String(toggledBotState.executionCorrelationText || ''),
        postToggleExecutionClientOrderText: String(toggledBotState.executionClientOrderText || ''),
        postToggleExecutionDuplicateText: String(toggledBotState.executionDuplicateText || ''),
        postToggleCooldownBadgeText: String(toggledBotState.cooldownBadgeText || ''),
        postToggleCooldownBlockedUntilText: String(toggledBotState.cooldownBlockedUntilText || ''),
        postToggleCooldownRemainingText: String(toggledBotState.cooldownRemainingText || '')
      },
      dashboard: {
        enabledBotsText: String(dashboardState.enabledBotsText || ''),
        jobStateText: String(dashboardState.jobStateText || ''),
        jobErrorText: String(dashboardState.jobErrorText || ''),
        executionStateText: String(dashboardState.executionStateText || ''),
        executionErrorText: String(dashboardState.executionErrorText || ''),
        workerHealthText: String(dashboardState.workerHealthText || ''),
        streamHealthText: String(dashboardState.streamHealthText || ''),
        breakerText: String(dashboardState.breakerText || ''),
        driftSummaryText: String(dashboardState.driftSummaryText || ''),
        driftReasonText: String(dashboardState.driftReasonText || ''),
        exchangeStatusText: String(dashboardState.exchangeStatusText || ''),
        exchangeBannerText: String(dashboardState.exchangeBannerText || '')
      },
      positions: {
        syncStatusText: String(positionsState.syncStatusText || ''),
        pnlConsistencyText: String(positionsState.pnlConsistencyText || ''),
        summaryOpenText: String(positionsState.summaryOpenText || ''),
        summaryClosedText: String(positionsState.summaryClosedText || ''),
        summaryUnrealizedText: String(positionsState.summaryUnrealizedText || ''),
        summaryRealizedText: String(positionsState.summaryRealizedText || ''),
        historySymbolText: String(positionsState.historySymbolText || ''),
        historyResultCategoryText: String(positionsState.historyResultCategoryText || ''),
        historyResultCodeText: String(positionsState.historyResultCodeText || ''),
        historyResultSummaryText: String(positionsState.historyResultSummaryText || ''),
        historyReasonChainText: String(positionsState.historyReasonChainText || ''),
        historyPnlText: String(positionsState.historyPnlText || ''),
        historyAiLabelText: String(positionsState.historyAiLabelText || ''),
        historyAiSummaryText: String(positionsState.historyAiSummaryText || ''),
        historyAiSourceText: String(positionsState.historyAiSourceText || ''),
        historyStageText: String(positionsState.historyStageText || ''),
        historySubmittedText: String(positionsState.historySubmittedText || ''),
        historyRetryText: String(positionsState.historyRetryText || ''),
        historyCorrelationText: String(positionsState.historyCorrelationText || ''),
        historyClientOrderText: String(positionsState.historyClientOrderText || '')
      },
      exchanges: {
        bannerTitleText: String(exchangeState.bannerTitleText || ''),
        bannerDetailText: String(exchangeState.bannerDetailText || ''),
        accountRows: Array.isArray(exchangeState.accountRows) ? exchangeState.accountRows.map(item => String(item || '')) : []
      },
      screenshots: {
        register: registerScreenshotPath,
        dashboard: dashboardScreenshotPath,
        bots: botsScreenshotPath,
        botsDisabled: botsDisabledScreenshotPath,
        botsEnabled: botsEnabledScreenshotPath,
        positions: positionsScreenshotPath,
        exchanges: exchangesScreenshotPath
      },
      logs: {
        browserStdOut: browserStdOutPath,
        browserStdErr: browserStdErrPath
      }
    };

    writeFileSync(browserSummaryPath, JSON.stringify(browserSummary, null, 2));
    return browserSummary;
  });

  console.log(`LoginSuccess=${summary.login.success}`);
  console.log(`BotName=${summary.bots.botNameText}`);
  console.log(`BotToggleCycle=${summary.bots.disableThenEnableCycle}`);
  console.log(`BotExecutionState=${summary.bots.postToggleExecutionStateText}`);
  console.log(`BotPilotState=${summary.bots.postTogglePilotStateBadgeText}`);
  console.log(`BotPilotStateSummary=${summary.bots.postTogglePilotStateSummaryText}`);
  console.log(`BotExecutionError=${summary.bots.postToggleExecutionFailureText}`);
  console.log(`BotExecutionBlockDetail=${summary.bots.postToggleExecutionBlockDetailText}`);
  console.log(`BotMarketDataBadge=${summary.bots.postToggleMarketDataBadgeText}`);
  console.log(`BotMarketDataReason=${summary.bots.postToggleMarketDataReasonText}`);
  console.log(`BotMarketDataAffected=${summary.bots.postToggleMarketDataAffectedText}`);
  console.log(`BotMarketDataLastCandle=${summary.bots.postToggleMarketDataLastCandleText}`);
  console.log(`BotMarketDataAge=${summary.bots.postToggleMarketDataAgeText}`);
  console.log(`BotMarketDataContinuity=${summary.bots.postToggleMarketDataContinuityText}`);
  console.log(`BotExecutionSubmit=${summary.bots.postToggleExecutionSubmitText}`);
  console.log(`BotExecutionRetry=${summary.bots.postToggleExecutionRetryText}`);
  console.log(`BotExecutionProtection=${summary.bots.postToggleExecutionProtectionText}`);
  console.log(`BotExecutionStage=${summary.bots.postToggleExecutionStageText}`);
  console.log(`BotExecutionTransition=${summary.bots.postToggleExecutionTransitionText}`);
  console.log(`BotExecutionCorrelation=${summary.bots.postToggleExecutionCorrelationText}`);
  console.log(`BotExecutionClientOrder=${summary.bots.postToggleExecutionClientOrderText}`);
  console.log(`BotExecutionDuplicate=${summary.bots.postToggleExecutionDuplicateText}`);
  console.log(`BotCooldownBadge=${summary.bots.postToggleCooldownBadgeText}`);
  console.log(`BotCooldownRemaining=${summary.bots.postToggleCooldownRemainingText}`);
  console.log(`DashboardDriftSummary=${summary.dashboard.driftSummaryText}`);
  console.log(`ExchangeBannerDetail=${summary.exchanges.bannerDetailText}`);
  console.log(`BrowserSummaryPath=${browserSummaryPath}`);
}

if (mode === 'register') {
  await registerUser();
} else if (mode === 'inspect') {
  await inspectRuntimeUi();
} else {
  throw new Error(`Unsupported bot UI smoke mode: ${mode}`);
}


















