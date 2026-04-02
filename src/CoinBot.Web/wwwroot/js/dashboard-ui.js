(function () {
    function resolveDashboardTimeZone(page) {
        const timeZoneId = page && page.getAttribute('data-cb-display-timezone-id')
            ? page.getAttribute('data-cb-display-timezone-id')
            : 'UTC';
        const timeZoneLabel = page && page.getAttribute('data-cb-display-timezone-label')
            ? page.getAttribute('data-cb-display-timezone-label')
            : timeZoneId;

        return {
            id: timeZoneId,
            label: timeZoneLabel
        };
    }

    function formatDecimal(value) {
        if (value === null || value === undefined || Number.isNaN(value)) {
            return 'Veri bekleniyor';
        }

        return Number(value).toLocaleString('en-US', {
            minimumFractionDigits: 0,
            maximumFractionDigits: 8
        });
    }

    function formatTimestamp(value, dashboardTimeZone) {
        if (!value) {
            return 'Henüz veri yok';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return 'Henüz veri yok';
        }

        try {
            return date.toLocaleTimeString('tr-TR', {
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit',
                timeZone: dashboardTimeZone.id
            }) + ' ' + dashboardTimeZone.label;
        } catch (error) {
            return date.toLocaleTimeString('tr-TR', {
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit',
                timeZone: 'UTC'
            }) + ' UTC';
        }
    }

    function buildMetaText(snapshot) {
        if (!snapshot.quoteAsset) {
            return 'Metadata bekleniyor';
        }

        const tickSize = snapshot.tickSize === null || snapshot.tickSize === undefined
            ? '—'
            : formatDecimal(snapshot.tickSize);
        const stepSize = snapshot.stepSize === null || snapshot.stepSize === undefined
            ? '—'
            : formatDecimal(snapshot.stepSize);

        return 'Tick ' + tickSize + ' • Step ' + stepSize;
    }

    function updateElements(selector, symbol, value) {
        document.querySelectorAll(selector + '[data-cb-symbol="' + symbol + '"]').forEach(function (element) {
            element.textContent = value;
        });
    }

    function updateFreshness(state, text) {
        const badge = document.querySelector('[data-cb-market-freshness-badge]');
        const time = document.querySelector('[data-cb-market-freshness-time]');

        if (badge) {
            badge.textContent = state.text;
            badge.className = 'cb-badge ' + state.className;
        }

        if (time) {
            time.textContent = text;
        }
    }

    function readValue(snapshot, camelCaseName, pascalCaseName) {
        if (!snapshot) {
            return null;
        }

        if (Object.prototype.hasOwnProperty.call(snapshot, camelCaseName)) {
            return snapshot[camelCaseName];
        }

        return Object.prototype.hasOwnProperty.call(snapshot, pascalCaseName)
            ? snapshot[pascalCaseName]
            : null;
    }

    function applySnapshot(snapshot, dashboardTimeZone) {
        const symbolValue = readValue(snapshot, 'symbol', 'Symbol');
        if (!symbolValue) {
            return;
        }

        const symbol = String(symbolValue).toUpperCase();
        const tradingStatus = readValue(snapshot, 'tradingStatus', 'TradingStatus') || 'UNKNOWN';
        const baseAsset = readValue(snapshot, 'baseAsset', 'BaseAsset') || symbol;
        const quoteAsset = readValue(snapshot, 'quoteAsset', 'QuoteAsset') || '';
        const isTradingEnabled = Boolean(readValue(snapshot, 'isTradingEnabled', 'IsTradingEnabled'));
        const pair = quoteAsset
            ? baseAsset + '/' + quoteAsset
            : symbol;
        const timestampText = formatTimestamp(
            readValue(snapshot, 'receivedAtUtc', 'ReceivedAtUtc') ||
            readValue(snapshot, 'observedAtUtc', 'ObservedAtUtc'),
            dashboardTimeZone);
        const statusClass = isTradingEnabled ? 'cb-badge-success' : 'cb-badge-warning';

        updateElements('[data-cb-market-price]', symbol, formatDecimal(readValue(snapshot, 'price', 'Price')));
        updateElements('[data-cb-market-status]', symbol, tradingStatus);
        updateElements('[data-cb-market-meta]', symbol, buildMetaText({
            quoteAsset: quoteAsset,
            tickSize: readValue(snapshot, 'tickSize', 'TickSize'),
            stepSize: readValue(snapshot, 'stepSize', 'StepSize')
        }));
        updateElements('[data-cb-market-updated]', symbol, timestampText);
        updateElements('[data-cb-market-source]', symbol, readValue(snapshot, 'source', 'Source') || 'MarketData.Pending');
        updateElements('[data-cb-market-pair]', symbol, pair);

        document.querySelectorAll('[data-cb-market-status][data-cb-symbol="' + symbol + '"]').forEach(function (element) {
            element.className = 'cb-badge ' + statusClass;
        });

        updateFreshness(
            { text: isTradingEnabled ? 'Canlı akış' : 'Kısıtlı', className: statusClass },
            symbol + ' • ' + timestampText);
    }

    function connectMarketDataHub() {
        const page = document.querySelector('[data-cb-market-dashboard]');
        if (!page || !window.signalR || !window.signalR.HubConnectionBuilder) {
            return;
        }

        let symbols = [];
        try {
            symbols = JSON.parse(page.getAttribute('data-cb-market-symbols') || '[]');
        } catch (error) {
            symbols = [];
        }

        if (!Array.isArray(symbols) || symbols.length === 0) {
            return;
        }

        const hubUrl = page.getAttribute('data-cb-market-hub-url');
        const dashboardTimeZone = resolveDashboardTimeZone(page);
        if (!hubUrl) {
            return;
        }

        const connection = new window.signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect()
            .build();

        connection.on('marketPriceUpdated', function (snapshot) {
            applySnapshot(snapshot, dashboardTimeZone);
        });

        connection.onreconnecting(function () {
            updateFreshness({ text: 'Yeniden bağlanıyor', className: 'cb-badge-warning' }, 'Realtime akış tekrar bağlanıyor');
        });

        connection.onclose(function () {
            updateFreshness({ text: 'Bağlantı kapandı', className: 'cb-badge-danger' }, 'Realtime akış durdu');
        });

        function subscribe() {
            return connection.invoke('SubscribeSymbolsAsync', symbols)
                .then(function (snapshots) {
                    if (!Array.isArray(snapshots)) {
                        return;
                    }

                    snapshots.forEach(function (snapshot) {
                        applySnapshot(snapshot, dashboardTimeZone);
                    });
                });
        }

        connection.start()
            .then(subscribe)
            .catch(function () {
                updateFreshness({ text: 'Bağlantı yok', className: 'cb-badge-warning' }, 'Realtime akış henüz bağlanamadı');
            });

        connection.onreconnected(function () {
            subscribe()
                .catch(function () {
                    updateFreshness({ text: 'Bağlantı yok', className: 'cb-badge-warning' }, 'Realtime akış henüz bağlanamadı');
                });
        });
    }

    function writeText(selector, value) {
        const element = document.querySelector(selector);
        if (element) {
            element.textContent = value;
        }
    }

    function applyOperationsSummary(snapshot) {
        if (!snapshot) {
            return;
        }

        writeText('[data-cb-ops-enabled-bots]', String(snapshot.enabledBotCount ?? snapshot.EnabledBotCount ?? '0'));
        writeText('[data-cb-ops-enabled-symbols]', String(snapshot.enabledSymbolCount ?? snapshot.EnabledSymbolCount ?? '0') + ' sembol');
        writeText('[data-cb-ops-conflicts]', String(snapshot.conflictedSymbolCount ?? snapshot.ConflictedSymbolCount ?? '0') + ' conflict');
        writeText('[data-cb-ops-job-state]', snapshot.lastJobStatus ?? snapshot.LastJobStatus ?? 'Idle');
        writeText('[data-cb-ops-job-error]', snapshot.lastJobErrorCode ?? snapshot.LastJobErrorCode ?? 'Worker hata kodu yok');
        writeText('[data-cb-ops-execution-state]', snapshot.lastExecutionState ?? snapshot.LastExecutionState ?? 'N/A');
        writeText('[data-cb-ops-execution-error]', snapshot.lastExecutionFailureCode ?? snapshot.LastExecutionFailureCode ?? 'Execution hata kodu yok');
        writeText('[data-cb-ops-worker-health]', snapshot.workerHealthLabel ?? snapshot.WorkerHealthLabel ?? 'Unknown');
        writeText('[data-cb-ops-stream-health]', snapshot.privateStreamHealthLabel ?? snapshot.PrivateStreamHealthLabel ?? 'Unknown');
        writeText('[data-cb-ops-breaker]', snapshot.breakerLabel ?? snapshot.BreakerLabel ?? 'Closed');
        writeText('[data-cb-ops-daily-loss]', snapshot.dailyLossSummary ?? snapshot.DailyLossSummary ?? 'Risk profili yok');
        writeText('[data-cb-ops-position-limit]', snapshot.positionLimitSummary ?? snapshot.PositionLimitSummary ?? 'Pozisyon limiti yok');
        writeText('[data-cb-ops-cooldown]', snapshot.cooldownSummary ?? snapshot.CooldownSummary ?? 'Cooldown bilgisi yok');
        writeText('[data-cb-ops-drift-summary]', snapshot.driftSummary ?? snapshot.DriftSummary ?? 'Henüz drift snapshot yok');
        writeText('[data-cb-ops-drift-reason]', snapshot.driftReason ?? snapshot.DriftReason ?? 'Clock drift summary monitoring snapshot geldikten sonra görünür.');
    }

    function connectOperationsHub() {
        const summary = document.querySelector('[data-cb-operations-summary]');
        if (!summary || !window.signalR || !window.signalR.HubConnectionBuilder) {
            return;
        }

        const hubUrl = summary.getAttribute('data-cb-operations-hub-url');
        const summaryUrl = summary.getAttribute('data-cb-operations-summary-url');
        if (!hubUrl || !summaryUrl) {
            return;
        }

        const connection = new window.signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect()
            .build();

        function refreshSummary() {
            return fetch(summaryUrl, { credentials: 'same-origin' })
                .then(function (response) {
                    if (!response.ok) {
                        throw new Error('summary fetch failed');
                    }

                    return response.json();
                })
                .then(function (payload) {
                    applyOperationsSummary(payload);
                });
        }

        connection.on('operationsUpdated', function () {
            refreshSummary().catch(function () { });
        });

        connection.start()
            .then(refreshSummary)
            .catch(function () { });

        connection.onreconnected(function () {
            refreshSummary().catch(function () { });
        });
    }

    document.addEventListener('click', function (event) {
        const chip = event.target.closest('[data-cb-toggle-active]');
        if (!chip) {
            return;
        }

        const group = chip.closest('[data-cb-toggle-group]');
        if (!group) {
            return;
        }

        group.querySelectorAll('[data-cb-toggle-active]').forEach(function (item) {
            item.classList.remove('is-active');
        });

        chip.classList.add('is-active');
    });

    connectMarketDataHub();
    connectOperationsHub();
})();
