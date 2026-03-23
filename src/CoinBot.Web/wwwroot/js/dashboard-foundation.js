(function () {
    function formatDecimal(value) {
        if (value === null || value === undefined || Number.isNaN(value)) {
            return 'Veri bekleniyor';
        }

        return Number(value).toLocaleString('en-US', {
            minimumFractionDigits: 0,
            maximumFractionDigits: 8
        });
    }

    function formatTimestamp(value) {
        if (!value) {
            return 'Henüz veri yok';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return 'Henüz veri yok';
        }

        return date.toLocaleTimeString('tr-TR', {
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit',
            timeZone: 'UTC'
        }) + ' UTC';
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

    function applySnapshot(snapshot) {
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
            readValue(snapshot, 'observedAtUtc', 'ObservedAtUtc'));
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
        if (!hubUrl) {
            return;
        }

        const connection = new window.signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect()
            .build();

        connection.on('marketPriceUpdated', function (snapshot) {
            applySnapshot(snapshot);
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
                        applySnapshot(snapshot);
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
})();
