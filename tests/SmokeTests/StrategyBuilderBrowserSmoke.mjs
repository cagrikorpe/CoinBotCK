import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { createWriteStream, existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..', '..');
const diagDirectory = path.join(repoRoot, '.diag', 'strategy-builder-browser-smoke');
const browserProfileDirectory = path.join(diagDirectory, 'edge-profile');
const summaryPath = path.join(diagDirectory, 'strategy-builder-browser-smoke-summary.json');
const webStdOutPath = path.join(diagDirectory, 'web.stdout.log');
const webStdErrPath = path.join(diagDirectory, 'web.stderr.log');
const browserStdOutPath = path.join(diagDirectory, 'edge.stdout.log');
const browserStdErrPath = path.join(diagDirectory, 'edge.stderr.log');
const pageScreenshotPath = path.join(diagDirectory, 'strategy-builder-page.png');
const invalidScreenshotPath = path.join(diagDirectory, 'strategy-builder-invalid.png');
const advancedScreenshotPath = path.join(diagDirectory, 'strategy-builder-advanced.png');
const failureScreenshotPath = path.join(diagDirectory, 'strategy-builder-failure.png');

const browserPathCandidates = [
  process.env.BROWSER_PATH,
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe'
].filter(Boolean);

const browserPath = browserPathCandidates.find(candidate => existsSync(candidate));
if (!browserPath) {
  throw new Error('No supported browser executable was found for the strategy builder smoke test. Set BROWSER_PATH to override.');
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
    }
    catch (error) {
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

  async waitForLocationContains(fragment, timeoutMilliseconds = 30000, ignoreCase = false) {
    await waitUntil(`location containing '${fragment}'`, async () => {
      const location = await this.evaluate('window.location.href');
      if (typeof location !== 'string') {
        return false;
      }

      if (ignoreCase) {
        return location.toLowerCase().includes(String(fragment).toLowerCase());
      }

      return location.includes(fragment);
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

async function setInputValue(client, selector, value) {
  await client.evaluate(`(() => {
    const element = document.querySelector(${JSON.stringify(selector)});
    if (!element) throw new Error('Element not found: ' + ${JSON.stringify(selector)});
    element.focus();
    element.value = ${JSON.stringify(value)};
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
}

async function clickElement(client, selector) {
  await client.evaluate(`(() => {
    const element = document.querySelector(${JSON.stringify(selector)});
    if (!element) throw new Error('Element not found: ' + ${JSON.stringify(selector)});
    element.click();
    return true;
  })()`);
}


async function waitForLoginCompletion(client, timeoutMilliseconds = 30000) {
  await waitUntil('successful login completion', async () => {
    const state = await client.evaluate(`(() => ({
      href: window.location.href || '',
      hasLoginForm: !!document.querySelector('form[action$="/Auth/Login"]'),
      authError: (document.querySelector('.validation-summary-errors, .text-danger.validation-summary-errors, [data-valmsg-summary="true"]')?.textContent || '').trim()
    }))()`);

    if (state.authError) {
      throw new Error(`Login validation failed: ${state.authError}`);
    }

    return !state.hasLoginForm && !String(state.href).toLowerCase().includes('/auth/login');
  }, timeoutMilliseconds);
}

async function waitForStrategyBuilderReady(client, timeoutMilliseconds = 30000) {
  await waitUntil('strategy builder route resolution', async () => {
    const state = await client.evaluate(`(() => ({
      href: window.location.href || '',
      title: document.title || '',
      builderExists: !!document.querySelector('[data-cb-strategy-builder]'),
      hasLoginForm: !!document.querySelector('form[action$="/Auth/Login"]'),
      hasAccessDeniedText: /yetki gerekli|access denied/i.test(document.body?.textContent || ''),
      hasTemplateCard: document.querySelectorAll('[data-cb-template-card]').length > 0
    }))()`);

    const href = String(state.href).toLowerCase();
    if (state.builderExists || state.hasTemplateCard) {
      return true;
    }

    if (state.hasLoginForm || href.includes('/auth/login')) {
      throw new Error(`StrategyBuilder redirected to login. Current URL: ${state.href}`);
    }

    if (href.includes('/auth/accessdenied') || state.hasAccessDeniedText) {
      throw new Error(`StrategyBuilder redirected to access denied. Current URL: ${state.href}`);
    }

    return href.includes('/strategybuilder');
  }, timeoutMilliseconds);
}

async function runStep(steps, name, action) {
  const startedAt = new Date().toISOString();
  console.log(`[STEP] ${name}`);

  try {
    const details = await action();
    const finishedAt = new Date().toISOString();
    steps.push({ name, status: 'passed', startedAt, finishedAt, details: details ?? null });
    console.log(`[PASS] ${name}`);
  }
  catch (error) {
    const finishedAt = new Date().toISOString();
    steps.push({ name, status: 'failed', startedAt, finishedAt, error: error.message });
    console.error(`[FAIL] ${name}: ${error.message}`);
    throw new Error(`${name} failed: ${error.message}`);
  }
}

(async () => {
  const steps = [];
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
  let currentStepName = 'startup';
  let baseUrl = '';

  try {
    ensureCleanDirectory(diagDirectory);
    ensureCleanDirectory(browserProfileDirectory);

    const appPort = await getFreeTcpPort();
    const debugPort = await getFreeTcpPort();
    baseUrl = `http://127.0.0.1:${appPort}`;
    const randomSuffix = crypto.randomUUID().replace(/-/g, '');
    const registrationEmail = `strategy.builder.smoke.${randomSuffix}@coinbot.test`;
    const registrationPassword = 'Passw0rd!Smoke1';

    process.env.DOTNET_CLI_HOME = path.join(repoRoot, '.dotnet');
    process.env.DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1';
    process.env.DOTNET_NOLOGO = '1';
    process.env.ASPNETCORE_ENVIRONMENT = 'Development';
    process.env.ASPNETCORE_URLS = baseUrl;

    currentStepName = 'web-host-start';
    webProcess = startManagedProcess(
      'dotnet',
      ['run', '--project', 'src/CoinBot.Web/CoinBot.Web.csproj', '--no-launch-profile'],
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
    }, 60000);

    currentStepName = 'browser-start';
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

    currentStepName = 'register-and-login';
    await runStep(steps, 'register and login', async () => {
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

        setValue('input[name="FullName"]', 'Strategy Builder Smoke User');
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
      await waitForLoginCompletion(client);
      return {
        registrationEmail,
        postLoginLocation: await client.evaluate('window.location.href')
      };
    });

    currentStepName = 'page-open';
    await runStep(steps, 'strategy builder page opens', async () => {
      await client.navigate(`${baseUrl}/strategybuilder`);
      await client.waitForReady();
      await waitForStrategyBuilderReady(client);
      await client.waitForLocationContains('/strategybuilder', 30000, true);
      await client.captureScreenshot(pageScreenshotPath);

      const state = await client.evaluate(`(() => {
        return {
          location: window.location.href || '',
          builderExists: !!document.querySelector('[data-cb-strategy-builder]'),
          templateCount: document.querySelectorAll('[data-cb-template-card]').length,
          previewExists: !!document.querySelector('[data-cb-builder-json-preview]'),
          submitDisabled: !!document.querySelector('[data-cb-template-start-submit]')?.disabled
        };
      })()`);

      if (!state.builderExists) {
        throw new Error('Strategy builder root was not rendered.');
      }

      if (state.templateCount < 1) {
        throw new Error('No template cards were rendered on /StrategyBuilder.');
      }

      if (!state.previewExists) {
        throw new Error('Preview JSON block was not rendered.');
      }

      if (!state.submitDisabled) {
        throw new Error('Submit button should be disabled before template selection.');
      }

      return state;
    });

    currentStepName = 'template-select-hydrate';
    await runStep(steps, 'template selection hydrates form', async () => {
      const firstCardState = await client.evaluate(`(() => {
        const card = document.querySelector('[data-cb-template-card]');
        if (!card) {
          throw new Error('Template card was not found.');
        }

        card.click();
        return {
          templateKey: card.getAttribute('data-cb-template-key') || '',
          templateName: card.getAttribute('data-cb-template-name') || ''
        };
      })()`);

      await waitUntil('hydrated builder form', async () => {
        const formState = await client.evaluate(`(() => {
          return {
            templateKeyValue: document.querySelector('[data-cb-builder-template-key]')?.value || '',
            templateNameValue: document.querySelector('[data-cb-builder-template-name]')?.value || '',
            sectionCountValue: document.querySelector('[data-cb-builder-section-count]')?.value || '',
            sectionCards: document.querySelectorAll('[data-cb-builder-section-card]').length,
            previewText: document.querySelector('[data-cb-builder-json-preview]')?.textContent || '',
            submitDisabled: !!document.querySelector('[data-cb-template-start-submit]')?.disabled
          };
        })()`);

        return formState.templateKeyValue === firstCardState.templateKey
          && formState.templateNameValue === firstCardState.templateName
          && Number(formState.sectionCountValue) >= 1
          && formState.sectionCards >= 1
          && formState.previewText.includes(firstCardState.templateKey)
          && formState.submitDisabled === false;
      }, 30000);

      const hydratedState = await client.evaluate(`(() => {
        return {
          templateKeyValue: document.querySelector('[data-cb-builder-template-key]')?.value || '',
          templateNameValue: document.querySelector('[data-cb-builder-template-name]')?.value || '',
          sectionCountValue: document.querySelector('[data-cb-builder-section-count]')?.value || '',
          sectionCards: document.querySelectorAll('[data-cb-builder-section-card]').length,
          validationBadge: document.querySelector('[data-cb-builder-validation-badge]')?.textContent?.trim() || '',
          submitDisabled: !!document.querySelector('[data-cb-template-start-submit]')?.disabled
        };
      })()`);

      return { ...firstCardState, ...hydratedState };
    });

    currentStepName = 'preview-live-update';
    await runStep(steps, 'preview json updates when form changes', async () => {
      const updatedTemplateName = `Smoke Preview ${randomSuffix.slice(0, 8)}`;
      await setInputValue(client, '[data-cb-builder-template-name]', updatedTemplateName);

      await waitUntil('preview live update', async () => {
        const previewState = await client.evaluate(`(() => {
          return {
            previewText: document.querySelector('[data-cb-builder-json-preview]')?.textContent || '',
            hiddenJsonValue: document.querySelector('[data-cb-builder-definition-json]')?.value || ''
          };
        })()`);

        return previewState.previewText.includes(updatedTemplateName)
          && previewState.hiddenJsonValue.includes(updatedTemplateName);
      }, 30000);

      const state = await client.evaluate(`(() => {
        return {
          templateNameValue: document.querySelector('[data-cb-builder-template-name]')?.value || '',
          previewContainsUpdatedName: (document.querySelector('[data-cb-builder-json-preview]')?.textContent || '').includes(${JSON.stringify(updatedTemplateName)})
        };
      })()`);

      if (!state.previewContainsUpdatedName) {
        throw new Error('Preview JSON did not reflect the changed template name.');
      }

      return state;
    });

    currentStepName = 'invalid-input-submit-disable';
    await runStep(steps, 'invalid input disables submit', async () => {
      const originalPath = await client.evaluate(`(() => {
        const input = document.querySelector('[data-cb-builder-rule-path="0:0"]');
        if (!input) {
          throw new Error('First rule path input was not found.');
        }

        return input.value || '';
      })()`);

      await setInputValue(client, '[data-cb-builder-rule-path="0:0"]', '');

      await waitUntil('validation failure and disabled submit', async () => {
        const invalidState = await client.evaluate(`(() => {
          return {
            submitDisabled: !!document.querySelector('[data-cb-template-start-submit]')?.disabled,
            validationBadge: document.querySelector('[data-cb-builder-validation-badge]')?.textContent?.trim() || '',
            selectionSummary: document.querySelector('[data-cb-template-selection-summary]')?.textContent?.trim() || ''
          };
        })()`);

        return invalidState.submitDisabled
          && /başarısız/i.test(invalidState.validationBadge)
          && /save kilitlendi/i.test(invalidState.selectionSummary);
      }, 30000);

      await client.captureScreenshot(invalidScreenshotPath);

      await setInputValue(client, '[data-cb-builder-rule-path="0:0"]', originalPath);

      await waitUntil('validation recovery and enabled submit', async () => {
        const recoveredState = await client.evaluate(`(() => {
          return {
            submitDisabled: !!document.querySelector('[data-cb-template-start-submit]')?.disabled,
            validationBadge: document.querySelector('[data-cb-builder-validation-badge]')?.textContent?.trim() || ''
          };
        })()`);

        return !recoveredState.submitDisabled && /geçti/i.test(recoveredState.validationBadge);
      }, 30000);

      return { invalidatedPath: true, restoredPath: true };
    });

    currentStepName = 'advanced-mode-toggle';
    await runStep(steps, 'advanced mode toggles open and closed', async () => {
      await clickElement(client, '[data-cb-builder-advanced-toggle]');

      await waitUntil('advanced panel open', async () => {
        const openState = await client.evaluate(`(() => {
          const panel = document.querySelector('[data-cb-builder-advanced-panel]');
          const toggle = document.querySelector('[data-cb-builder-advanced-toggle]');
          return {
            hidden: panel ? panel.classList.contains('d-none') : true,
            toggleText: toggle?.textContent?.trim() || ''
          };
        })()`);

        return openState.hidden === false && /kapat/i.test(openState.toggleText);
      }, 30000);

      await clickElement(client, '[data-cb-builder-advanced-toggle]');

      await waitUntil('advanced panel closed', async () => {
        const closedState = await client.evaluate(`(() => {
          const panel = document.querySelector('[data-cb-builder-advanced-panel]');
          const toggle = document.querySelector('[data-cb-builder-advanced-toggle]');
          return {
            hidden: panel ? panel.classList.contains('d-none') : false,
            toggleText: toggle?.textContent?.trim() || ''
          };
        })()`);

        return closedState.hidden === true && /advanced mode$/i.test(closedState.toggleText);
      }, 30000);

      await clickElement(client, '[data-cb-builder-advanced-toggle]');
      await waitUntil('advanced panel reopen', async () => {
        return await client.evaluate(`(() => {
          const panel = document.querySelector('[data-cb-builder-advanced-panel]');
          return !!panel && !panel.classList.contains('d-none');
        })()`);
      }, 30000);

      await client.captureScreenshot(advancedScreenshotPath);
      return { reopenedForApplySteps: true };
    });

    currentStepName = 'valid-json-apply';
    await runStep(steps, 'valid advanced json applies to form', async () => {
      const currentPreviewJson = await client.evaluate(`(() => document.querySelector('[data-cb-builder-json-preview]')?.textContent || '')()`);
      const parsed = JSON.parse(currentPreviewJson);
      const appliedTemplateName = `Advanced Applied ${randomSuffix.slice(0, 8)}`;
      parsed.metadata = parsed.metadata || {};
      parsed.metadata.templateName = appliedTemplateName;
      const nextJson = JSON.stringify(parsed, null, 2);

      await client.evaluate(`(() => {
        const textarea = document.querySelector('[data-cb-builder-advanced-json]');
        if (!textarea) throw new Error('Advanced textarea was not found.');
        textarea.value = ${JSON.stringify(nextJson)};
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        textarea.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      })()`);
      await clickElement(client, '[data-cb-builder-advanced-apply]');

      await waitUntil('advanced apply hydrate', async () => {
        const appliedState = await client.evaluate(`(() => {
          return {
            templateNameValue: document.querySelector('[data-cb-builder-template-name]')?.value || '',
            previewText: document.querySelector('[data-cb-builder-json-preview]')?.textContent || '',
            statusText: document.querySelector('[data-cb-builder-advanced-status]')?.textContent?.trim() || ''
          };
        })()`);

        return appliedState.templateNameValue === appliedTemplateName
          && appliedState.previewText.includes(appliedTemplateName)
          && /forma uygulandı/i.test(appliedState.statusText);
      }, 30000);

      return { appliedTemplateName };
    });

    currentStepName = 'invalid-json-reject';
    await runStep(steps, 'invalid advanced json is rejected', async () => {
      const baselineState = await client.evaluate(`(() => {
        return {
          templateNameValue: document.querySelector('[data-cb-builder-template-name]')?.value || '',
          previewText: document.querySelector('[data-cb-builder-json-preview]')?.textContent || ''
        };
      })()`);

      await client.evaluate(`(() => {
        const textarea = document.querySelector('[data-cb-builder-advanced-json]');
        if (!textarea) throw new Error('Advanced textarea was not found for invalid apply.');
        textarea.value = '{ invalid json';
        textarea.dispatchEvent(new Event('input', { bubbles: true }));
        textarea.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      })()`);
      await clickElement(client, '[data-cb-builder-advanced-apply]');

      await waitUntil('advanced reject status', async () => {
        const rejectedState = await client.evaluate(`(() => {
          return {
            templateNameValue: document.querySelector('[data-cb-builder-template-name]')?.value || '',
            previewText: document.querySelector('[data-cb-builder-json-preview]')?.textContent || '',
            statusText: document.querySelector('[data-cb-builder-advanced-status]')?.textContent?.trim() || ''
          };
        })()`);

        return rejectedState.templateNameValue === baselineState.templateNameValue
          && rejectedState.previewText === baselineState.previewText
          && /çözümlenemedi/i.test(rejectedState.statusText);
      }, 30000);

      const finalState = await client.evaluate(`(() => {
        return {
          statusText: document.querySelector('[data-cb-builder-advanced-status]')?.textContent?.trim() || '',
          submitDisabled: !!document.querySelector('[data-cb-template-start-submit]')?.disabled
        };
      })()`);

      if (finalState.submitDisabled) {
        throw new Error('Submit should stay enabled after invalid advanced JSON is rejected without mutating the form.');
      }

      return finalState;
    });

    writeFileSync(summaryPath, JSON.stringify({
      outcome: 'passed',
      baseUrl,
      browserPath,
      steps,
      artifacts: {
        summaryPath,
        pageScreenshotPath,
        invalidScreenshotPath,
        advancedScreenshotPath,
        webStdOutPath,
        webStdErrPath,
        browserStdOutPath,
        browserStdErrPath
      }
    }, null, 2));

    console.log(`Strategy builder browser smoke passed. Summary: ${summaryPath}`);
  }
  catch (error) {
    if (client) {
      try {
        await client.captureScreenshot(failureScreenshotPath);
      }
      catch {
        // ignored
      }
    }

    writeFileSync(summaryPath, JSON.stringify({
      outcome: 'failed',
      failedStep: currentStepName,
      baseUrl,
      browserPath,
      error: error.message,
      steps,
      artifacts: {
        summaryPath,
        failureScreenshotPath,
        pageScreenshotPath,
        invalidScreenshotPath,
        advancedScreenshotPath,
        webStdOutPath,
        webStdErrPath,
        browserStdOutPath,
        browserStdErrPath
      }
    }, null, 2));

    console.error(`Strategy builder browser smoke failed at '${currentStepName}'. Summary: ${summaryPath}`);
    console.error(error);
    process.exitCode = 1;
  }
  finally {
    await client?.close();
    await stopManagedProcess(browserProcess);
    await stopManagedProcess(webProcess);

    process.env.ASPNETCORE_ENVIRONMENT = originalEnvironment.ASPNETCORE_ENVIRONMENT;
    process.env.ASPNETCORE_URLS = originalEnvironment.ASPNETCORE_URLS;
    process.env.DOTNET_CLI_HOME = originalEnvironment.DOTNET_CLI_HOME;
    process.env.DOTNET_SKIP_FIRST_TIME_EXPERIENCE = originalEnvironment.DOTNET_SKIP_FIRST_TIME_EXPERIENCE;
    process.env.DOTNET_NOLOGO = originalEnvironment.DOTNET_NOLOGO;
  }
})();
