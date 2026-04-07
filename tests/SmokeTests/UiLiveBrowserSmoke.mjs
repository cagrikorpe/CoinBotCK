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
  throw new Error('UI live browser smoke arguments are incomplete.');
}

const browserProfileDirectory = path.join(diagDirectory, `edge-profile-${mode}`);
const browserStdOutPath = path.join(diagDirectory, `edge.${mode}.stdout.log`);
const browserStdErrPath = path.join(diagDirectory, `edge.${mode}.stderr.log`);
const registerScreenshotPath = path.join(diagDirectory, 'ui-live-register.png');
const homeScreenshotPath = path.join(diagDirectory, 'ui-live-home.png');
const aiRobotScreenshotPath = path.join(diagDirectory, 'ui-live-ai-robot.png');
const browserSummaryPath = path.join(diagDirectory, 'ui-live-browser-summary.json');

const browserPathCandidates = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'
];

const browserPath = browserPathCandidates.find(candidate => existsSync(candidate));
if (!browserPath) {
  throw new Error('No supported browser executable was found for the UI live browser smoke test.');
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
    const debugPort = 44000 + Math.floor(Math.random() * 1000);

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

      setValue('input[name="FullName"]', 'UI Live Smoke User');
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

function normalizeText(value) {
  return typeof value === 'string' ? value.replace(/\s+/g, ' ').trim() : '';
}

function compactText(value) {
  return normalizeText(value).replace(/[^a-z0-9]+/gi, '').toLowerCase();
}

async function inspectRuntimeUi() {
  const summary = await runBrowserAutomation(async client => {
    await login(client, '/');

    await client.navigate(`${baseUrl}/`);
    await client.waitForReady();
    await client.waitForLocationContains('/');
    await client.evaluate(`(() => { document.querySelector('[data-cb-operations-summary]')?.scrollIntoView({ block: 'center' }); return true; })()`);
    await sleep(500);
    await client.captureScreenshot(homeScreenshotPath);

    const homeState = await client.evaluate(`(() => {
      const text = selector => document.querySelector(selector)?.innerText?.replace(/\s+/g, ' ')?.trim() || '';
      const attr = (selector, name) => document.querySelector(selector)?.getAttribute(name) || '';
      const rows = selector => Array.from(document.querySelectorAll(selector));
      const positionRows = rows('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row]').filter(row => row.querySelector('[data-cb-open-position-symbol]'));
      const recentOrderRows = rows('[data-cb-recent-orders="true"] tbody [data-cb-recent-order-row]').filter(row => row.querySelector('[data-cb-recent-order-symbol]'));
      return {
        tradeMaster: text('[data-cb-ops-trade-master]'),
        tradeMasterCode: attr('[data-cb-ops-trade-master]', 'data-cb-ops-trade-master-code'),
        tradingMode: text('[data-cb-ops-trading-mode]'),
        pilotActivation: text('[data-cb-ops-pilot-activation]'),
        latestNoTrade: text('[data-cb-ops-latest-no-trade]'),
        latestNoTradeSummary: text('[data-cb-ops-latest-no-trade-summary]'),
        latestNoTradeCode: attr('[data-cb-ops-latest-no-trade-summary]', 'data-cb-ops-latest-no-trade-code'),
        latestReject: text('[data-cb-ops-latest-reject]'),
        latestRejectSummary: text('[data-cb-ops-latest-reject-summary]'),
        latestRejectCode: attr('[data-cb-ops-latest-reject-summary]', 'data-cb-ops-latest-reject-code'),
        marketReadiness: text('[data-cb-ops-market-state]'),
        privatePlane: text('[data-cb-ops-private-plane-state]'),
        equityEstimate: text('[data-cb-equity-estimate]'),
        dailyPnl: text('[data-cb-equity-daily-pnl]'),
        openPositionEffect: text('[data-cb-equity-open-position-effect]'),
        closedTradeEffect: text('[data-cb-equity-closed-trade-effect]'),
        performanceSummary: text('[data-cb-equity-summary]'),
        equityEmpty: text('[data-cb-equity-empty]'),
        equityPointCount: rows('[data-cb-equity-points="true"] [data-cb-equity-point-row]').length,
        openPositionCount: positionRows.length,
        firstPosition: positionRows.length === 0 ? null : {
          symbol: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-symbol]'),
          direction: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-direction]'),
          quantity: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-quantity]'),
          entry: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-entry]'),
          breakEven: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-break-even]'),
          current: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-current]'),
          unrealized: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-unrealized]'),
          realized: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-realized]'),
          margin: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-margin]'),
          sync: text('[data-cb-open-positions-table="true"] tbody [data-cb-open-position-row] [data-cb-open-position-sync]')
        },
        recentOrders: recentOrderRows.map(row => ({
          symbol: row.querySelector('[data-cb-recent-order-symbol]')?.innerText?.trim() || '',
          state: row.querySelector('[data-cb-recent-order-state]')?.innerText?.trim() || '',
          resultCode: row.querySelector('[data-cb-recent-order-result-code]')?.getAttribute('data-cb-recent-order-result-code-value') || row.querySelector('[data-cb-recent-order-result-code]')?.innerText?.trim() || '',
          fill: row.querySelector('[data-cb-recent-order-fill]')?.innerText?.trim() || '',
          pnl: row.querySelector('[data-cb-recent-order-pnl]')?.innerText?.trim() || '',
          reconciliation: row.querySelector('[data-cb-recent-order-reconciliation]')?.innerText?.trim() || '',
          reason: row.querySelector('[data-cb-recent-order-reason]')?.innerText?.replace(/\s+/g, ' ')?.trim() || ''
        }))
      };
    })()`);

    if (!normalizeText(homeState.tradeMaster) || !compactText(homeState.tradeMasterCode).includes('disarmed')) {
      throw new Error('Trade master control state was not rendered deterministically on the dashboard. Value=' + homeState.tradeMaster + ' Code=' + homeState.tradeMasterCode);
    }
    if (!compactText(homeState.latestRejectCode).includes('trademasterdisarmed') && !compactText(homeState.latestNoTradeCode).includes('trademasterdisarmed')) {
      throw new Error('TradeMasterDisarmed reason was not rendered on the dashboard. RejectCode=' + homeState.latestRejectCode + ' NoTradeCode=' + homeState.latestNoTradeCode);
    }
    if (!normalizeText(homeState.equityEstimate) || !normalizeText(homeState.dailyPnl) || !normalizeText(homeState.openPositionEffect) || !normalizeText(homeState.closedTradeEffect)) {
      throw new Error('Equity summary values were not rendered on the dashboard.');
    }
    if (!normalizeText(homeState.performanceSummary)) {
      throw new Error('Performance summary text was not rendered on the dashboard.');
    }
    if (homeState.equityPointCount < 2 && !normalizeText(homeState.equityEmpty)) {
      throw new Error('Equity surface rendered neither chart points nor an honest empty state.');
    }
    if (homeState.openPositionCount < 1 || !homeState.firstPosition?.symbol || !homeState.firstPosition?.unrealized || !homeState.firstPosition?.realized) {
      throw new Error('Open positions table was not rendered with real data.');
    }
    if ((homeState.recentOrders || []).length < 2) {
      throw new Error('Recent orders did not render both fill and reject rows.');
    }
    if (!(homeState.recentOrders || []).some(item => compactText(item.state).includes('rejected') && compactText(item.resultCode).includes('trademasterdisarmed'))) {
      throw new Error('Reject/failure row was not rendered on the dashboard.');
    }
    if (!(homeState.recentOrders || []).some(item => compactText(item.state).includes('filled') && normalizeText(item.fill))) {
      throw new Error('Filled order row was not rendered on the dashboard.');
    }

    await client.navigate(`${baseUrl}/AiRobot`);
    await client.waitForReady();
    await client.waitForLocationContains('/AiRobot');
    await client.evaluate(`(() => { document.querySelector('[data-cb-ai-robot-page="true"]')?.scrollIntoView({ block: 'start' }); return true; })()`);
    await sleep(500);
    await client.captureScreenshot(aiRobotScreenshotPath);

    const aiRobotState = await client.evaluate(`(() => {
      const text = selector => document.querySelector(selector)?.innerText?.replace(/\s+/g, ' ')?.trim() || '';
      const attr = (selector, name) => document.querySelector(selector)?.getAttribute(name) || '';
      const decisionRows = Array.from(document.querySelectorAll('[data-cb-ai-robot-page="true"] [data-cb-ai-decision-row]')).filter(row => row.querySelector('[data-cb-ai-decision-symbol]'));
      return {
        scoringCoverage: text('[data-cb-ai-scoring-coverage]'),
        averageOutcome: text('[data-cb-ai-average-outcome]'),
        tradeMaster: text('[data-cb-ai-control-trade-master]'),
        tradeMasterCode: attr('[data-cb-ai-control-trade-master]', 'data-cb-ai-control-trade-master-code'),
        tradingMode: text('[data-cb-ai-control-mode]'),
        pilotActivation: text('[data-cb-ai-control-pilot]'),
        marketReadiness: text('[data-cb-ai-readiness-market]'),
        privatePlane: text('[data-cb-ai-readiness-private-plane]'),
        latestNoTrade: text('[data-cb-ai-latest-no-trade]'),
        latestNoTradeSummary: text('[data-cb-ai-latest-no-trade-summary]'),
        latestNoTradeSummaryValue: attr('[data-cb-ai-latest-no-trade-summary]', 'data-cb-ai-latest-no-trade-summary-value'),
        latestReject: text('[data-cb-ai-latest-reject]'),
        latestRejectStatus: attr('[data-cb-ai-latest-reject]', 'data-cb-ai-latest-reject-status'),
        latestRejectSummaryValue: attr('[data-cb-ai-latest-reject]', 'data-cb-ai-latest-reject-summary'),
        decisionCount: decisionRows.length,
        firstDecision: decisionRows.length === 0 ? null : {
          symbol: decisionRows[0].querySelector('[data-cb-ai-decision-symbol]')?.innerText?.trim() || '',
          strategySummary: decisionRows[0].querySelector('[data-cb-ai-decision-strategy-summary]')?.innerText?.replace(/\s+/g, ' ')?.trim() || '',
          overlaySummary: decisionRows[0].querySelector('[data-cb-ai-decision-overlay-summary]')?.innerText?.replace(/\s+/g, ' ')?.trim() || '',
          finalReason: decisionRows[0].querySelector('[data-cb-ai-decision-final-reason]')?.innerText?.replace(/\s+/g, ' ')?.trim() || '',
          outcome: decisionRows[0].querySelector('[data-cb-ai-decision-outcome]')?.innerText?.replace(/\s+/g, ' ')?.trim() || '',
          topFeatureHints: decisionRows[0].querySelector('[data-cb-ai-decision-top-feature-hints]')?.innerText?.replace(/\s+/g, ' ')?.trim() || ''
        }
      };
    })()`);

    if (!normalizeText(aiRobotState.tradeMaster) || !compactText(aiRobotState.tradeMasterCode).includes('disarmed')) {
      throw new Error('AI Robot trade master control state was not rendered deterministically. Value=' + aiRobotState.tradeMaster + ' Code=' + aiRobotState.tradeMasterCode);
    }
    if (!compactText(aiRobotState.latestRejectStatus).includes('trademasterdisarmed') && !compactText(aiRobotState.latestNoTradeSummaryValue).includes('trademasterdisarmed')) {
      throw new Error('AI Robot TradeMasterDisarmed reason was not rendered. RejectStatus=' + aiRobotState.latestRejectStatus + ' NoTrade=' + aiRobotState.latestNoTradeSummaryValue);
    }
    if (aiRobotState.decisionCount < 1 || !aiRobotState.firstDecision?.symbol || !aiRobotState.firstDecision?.strategySummary || !aiRobotState.firstDecision?.overlaySummary || !aiRobotState.firstDecision?.finalReason || !aiRobotState.firstDecision?.topFeatureHints) {
      throw new Error('AI Robot explainability fields were not rendered with real data.');
    }

    const browserSummary = {
      home: homeState,
      aiRobot: aiRobotState,
      screenshots: {
        register: registerScreenshotPath,
        home: homeScreenshotPath,
        aiRobot: aiRobotScreenshotPath
      },
      logs: {
        browserStdOut: browserStdOutPath,
        browserStdErr: browserStdErrPath
      }
    };

    writeFileSync(browserSummaryPath, JSON.stringify(browserSummary, null, 2));
    return browserSummary;
  });

  console.log(`HomeTradeMaster=${summary.home.tradeMaster}`);
  console.log(`HomeLatestReject=${summary.home.latestRejectSummary}`);
  console.log(`HomeEquityEstimate=${summary.home.equityEstimate}`);
  console.log(`HomeOpenPositionCount=${summary.home.openPositionCount}`);
  console.log(`HomeRecentOrderCount=${summary.home.recentOrders.length}`);
  console.log(`AiRobotTradeMaster=${summary.aiRobot.tradeMaster}`);
  console.log(`AiRobotLatestReject=${summary.aiRobot.latestReject}`);
  console.log(`AiRobotDecisionCount=${summary.aiRobot.decisionCount}`);
  console.log(`BrowserSummaryPath=${browserSummaryPath}`);
}

if (mode === 'register') {
  await registerUser();
} else if (mode === 'inspect') {
  await inspectRuntimeUi();
} else {
  throw new Error(`Unsupported UI live browser smoke mode: ${mode}`);
}




