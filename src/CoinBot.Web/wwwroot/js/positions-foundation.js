(function () {
    const page = document.querySelector('[data-cb-positions-page]');
    if (!page) {
        return;
    }

    const scenarios = {
        empty: {
            panel: 'empty',
            open: 'empty',
            closed: 'empty',
            history: 'empty',
            summary: ['0', '0', '—', '—', '0', '0']
        },
        active: {
            panel: 'content',
            open: 'list',
            closed: 'list',
            history: 'list',
            summary: ['2', '16', '+$210', '+$1,420', '1', '1']
        },
        history: {
            panel: 'content',
            open: 'list',
            closed: 'list',
            history: 'list',
            summary: ['1', '24', '+$42', '+$2,180', '1', '0']
        },
        waiting: {
            panel: 'waiting',
            open: 'empty',
            closed: 'list',
            history: 'list',
            summary: ['0', '12', '—', '+$940', '0', '0']
        },
        loading: {
            panel: 'loading',
            open: 'loading',
            closed: 'loading',
            history: 'loading',
            summary: ['—', '—', '—', '—', '—', '—']
        },
        error: {
            panel: 'error',
            open: 'error',
            closed: 'error',
            history: 'error',
            summary: ['—', '—', '—', '—', '—', '—']
        }
    };

    const details = {
        'btc-open': {
            eyebrow: 'Open Position', title: 'BTCUSDT · Long', status: ['Açık', 'cb-badge cb-badge-success'], type: 'Position', symbol: 'BTCUSDT', direction: ['Long', 'cb-badge cb-badge-success'], orderType: 'Market', leverage: '3x', orderId: 'POS-240321-001', time: '21 Mar · 09:14', bot: 'Alpha Spot Pulse', priceQty: '61,420 / 0.42 BTC', pnl: '+$126 unrealized', reasonOpen: 'EMA cross ve momentum teyidi ile AI confidence band yeterli görüldü; risk filtresi işlem açılmasını onayladı.', reasonClose: 'Pozisyon açık olduğu için kapanış nedeni henüz oluşmadı; close/reduce veya TP/SL sonrası doldurulacak.', riskNote: 'Risk notu: orta kaldıraç, futures context, risk merkezi limiti içinde.', openChips: ['Strategy signal', 'AI recommendation', 'Risk approval'], closeChips: ['Open position', 'Awaiting exit']
        },
        'sol-open': {
            eyebrow: 'Open Position', title: 'SOLUSDT · Short', status: ['Açık', 'cb-badge cb-badge-warning'], type: 'Position', symbol: 'SOLUSDT', direction: ['Short', 'cb-badge cb-badge-danger'], orderType: 'Market', leverage: '8x', orderId: 'POS-240321-002', time: '21 Mar · 08:31', bot: 'Beta Hedge Runner', priceQty: '131.8 / 12 SOL', pnl: '+$84 unrealized', reasonOpen: 'Short bias, hedge setup ve volatility bandı ile açılmış pozisyon. AI tavsiyesi yardımcı sinyal olarak not edilir.', reasonClose: 'Pozisyon açık; exit kararı oluştuğunda reverse signal veya TP/SL notu burada özetlenecek.', riskNote: 'Risk notu: yüksek leverage warning aktif. Risk Merkezi ile kavramsal bağ kurulmuştur.', openChips: ['Strategy signal', 'Risk approval', 'Manual/Auto'], closeChips: ['High leverage', 'Awaiting exit']
        },
        'eth-closed': {
            eyebrow: 'Closed Position', title: 'ETHUSDT · Long', status: ['Kapalı', 'cb-badge cb-badge-info'], type: 'Position', symbol: 'ETHUSDT', direction: ['Long', 'cb-badge cb-badge-success'], orderType: 'Limit', leverage: '—', orderId: 'CLS-240321-004', time: '21 Mar · 08:44', bot: 'Alpha Spot Pulse', priceQty: '3,180 / 1.4 ETH', pnl: '+$96 realized', reasonOpen: 'Mean reversion sonrası trend dönüşü ile long açıldı; AI ve risk tarafı nötr-onay verdi.', reasonClose: 'Take profit hedefi tetiklendi ve realized PnL history’ye işlendi.', riskNote: 'Risk notu: spot işlem, leverage yok, TP ile kapanış.', openChips: ['Strategy signal', 'Risk approval', 'Spot context'], closeChips: ['Take Profit', 'Auto close']
        },
        'btc-closed': {
            eyebrow: 'Closed Position', title: 'BTCUSDT · Short', status: ['Kapalı', 'cb-badge cb-badge-danger'], type: 'Position', symbol: 'BTCUSDT', direction: ['Short', 'cb-badge cb-badge-danger'], orderType: 'Stop', leverage: '4x', orderId: 'CLS-240321-006', time: '21 Mar · 07:52', bot: 'Beta Hedge Runner', priceQty: '62,310 / 0.18 BTC', pnl: '-$58 realized', reasonOpen: 'Breakout fade ve reversal sinyali ile futures short pozisyonu açıldı.', reasonClose: 'Stop loss erken tetiklendi; risk approval kapanış sebebini açık şekilde gösterir.', riskNote: 'Risk notu: futures + 4x leverage. Kapanış nedeni risk paneliyle ilişkilendirilebilir.', openChips: ['Strategy signal', 'AI recommendation', 'Risk approval'], closeChips: ['Stop Loss', 'Risk exit']
        },
        'ord-btc-buy': {
            eyebrow: 'Order Detail', title: 'BTCUSDT · Buy order', status: ['Filled', 'cb-badge cb-badge-success'], type: 'Order', symbol: 'BTCUSDT', direction: ['Long', 'cb-badge cb-badge-success'], orderType: 'Market', leverage: '3x', orderId: 'ORD-240321-141', time: '21 Mar · 09:14', bot: 'Alpha Spot Pulse', priceQty: '61,420 / 0.42 BTC', pnl: 'Pozisyon açıldı', reasonOpen: 'Strategy signal ve AI confidence uygun olduğu için market buy order gönderildi.', reasonClose: 'Emir filled olarak tamamlandı; kapanış nedeni yok, pozisyon açılış order’ı olarak history’de durur.', riskNote: 'AI etkisi ve risk approval özetleri ürün diliyle kısa tutulur.', openChips: ['Filled', 'Strategy signal', 'AI recommendation'], closeChips: ['Open position', 'No close yet']
        },
        'ord-sol-stop': {
            eyebrow: 'Order Detail', title: 'SOLUSDT · Stop order', status: ['Partial', 'cb-badge cb-badge-warning'], type: 'Order', symbol: 'SOLUSDT', direction: ['Short', 'cb-badge cb-badge-danger'], orderType: 'Stop', leverage: '8x', orderId: 'ORD-240321-155', time: '21 Mar · 08:54', bot: 'Beta Hedge Runner', priceQty: '132.4 / 12 SOL', pnl: 'Kısmi gerçekleşme', reasonOpen: 'Short pozisyon koruması için stop order oluşturuldu.', reasonClose: 'Kısmi fill sonrası reduce-only davranışı placeholder olarak history’de işaretlenir.', riskNote: 'Risk notu: high leverage warning, partial fill ve stop/tp ilişkisi.', openChips: ['Stop/TP', 'Risk approval', 'Reduce-only'], closeChips: ['Partial', 'Risk exit']
        },
        'ord-eth-cancel': {
            eyebrow: 'Order Detail', title: 'ETHUSDT · Limit order', status: ['Canceled', 'cb-badge cb-badge-neutral'], type: 'Order', symbol: 'ETHUSDT', direction: ['Long', 'cb-badge cb-badge-info'], orderType: 'Limit', leverage: '—', orderId: 'ORD-240321-101', time: '21 Mar · 07:41', bot: 'Gamma Mean Reversion', priceQty: '3,160 / 1.4 ETH', pnl: 'İşlem açılmadı', reasonOpen: 'Fiyat dönüş senaryosu için limit buy order hazırlandı.', reasonClose: 'Emir iptal edildi; kullanıcı veya runtime kararı nedeniyle açılış gerçekleşmedi.', riskNote: 'Rejection/cancel summary alanı kullanıcıya ham log yerine kısa açıklama sunar.', openChips: ['Limit', 'Pending fill', 'Strategy signal'], closeChips: ['Canceled', 'No execution']
        }
    };

    function text(id, value) {
        const el = document.getElementById(id);
        if (!el) return;
        el.textContent = value;
    }

    function setClassText(id, value, className) {
        const el = document.getElementById(id);
        if (!el) return;
        el.className = className;
        el.textContent = value;
    }

    function setPanel(group, name) {
        page.querySelectorAll('[data-cb-' + group + '-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-' + group + '-panel') !== name);
        });
    }

    function setTab(name) {
        page.querySelectorAll('[data-cb-positions-tab-trigger]').forEach(function (tab) {
            tab.classList.toggle('is-active', tab.getAttribute('data-cb-positions-tab-trigger') === name);
        });
        page.querySelectorAll('[data-cb-positions-tab-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-positions-tab-panel') !== name);
        });
    }

    function applyScenario(name) {
        const scenario = scenarios[name];
        if (!scenario) return;
        page.setAttribute('data-cb-positions-scenario', name);
        page.querySelectorAll('[data-cb-positions-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-positions-scenario-trigger') === name);
        });

        page.querySelectorAll('[data-cb-positions-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-positions-panel') !== scenario.panel);
        });

        if (scenario.panel === 'content') {
            setPanel('open-positions', scenario.open);
            setPanel('closed-positions', scenario.closed);
            setPanel('order-history', scenario.history);
        }

        const openBadge = document.getElementById('cb_positions_open_badge');
        if (openBadge && scenario.panel !== 'content') {
            openBadge.textContent = '0 aktif';
        }
    }

    function fillChips(prefix, values) {
        values.forEach(function (value, index) {
            text(prefix + (index + 1), value);
        });
    }

    function setDetail(key) {
        const item = details[key];
        if (!item) return;
        text('cb_positions_drawer_eyebrow', item.eyebrow);
        text('cb_positions_drawer_title', item.title);
        setClassText('cb_positions_drawer_status', item.status[0], item.status[1]);
        text('cb_positions_drawer_type', item.type);
        text('cb_positions_drawer_symbol', item.symbol);
        setClassText('cb_positions_drawer_direction', item.direction[0], item.direction[1]);
        text('cb_positions_drawer_order_type', item.orderType);
        text('cb_positions_drawer_leverage', item.leverage);
        text('cb_positions_drawer_order_id', item.orderId);
        text('cb_positions_drawer_time', item.time);
        text('cb_positions_drawer_bot', item.bot);
        text('cb_positions_drawer_price_qty', item.priceQty);
        text('cb_positions_drawer_pnl', item.pnl);
        text('cb_positions_drawer_reason_open', item.reasonOpen);
        text('cb_positions_drawer_reason_close', item.reasonClose);
        text('cb_positions_drawer_risk_note', item.riskNote);
        fillChips('cb_positions_drawer_reason_chip_', item.openChips);
        fillChips('cb_positions_drawer_close_chip_', item.closeChips);
    }

    function setButtonLoading(button, isLoading) {
        if (!button) return;
        button.classList.toggle('is-loading', isLoading);
        button.toggleAttribute('disabled', isLoading);
    }

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-positions-scenario-trigger]');
        const tabTrigger = event.target.closest('[data-cb-positions-tab-trigger]');
        const detailTrigger = event.target.closest('[data-cb-position-open-detail]');
        const refreshTrigger = event.target.closest('[data-cb-positions-refresh]');

        if (scenarioTrigger) {
            event.preventDefault();
            applyScenario(scenarioTrigger.getAttribute('data-cb-positions-scenario-trigger'));
        }

        if (tabTrigger) {
            event.preventDefault();
            setTab(tabTrigger.getAttribute('data-cb-positions-tab-trigger'));
        }

        if (detailTrigger) {
            event.preventDefault();
            setDetail(detailTrigger.getAttribute('data-cb-position-open-detail'));
        }

        if (refreshTrigger) {
            event.preventDefault();
            setButtonLoading(refreshTrigger, true);
            applyScenario('loading');
            window.setTimeout(function () {
                setButtonLoading(refreshTrigger, false);
                applyScenario('active');
            }, 650);
        }
    });

    setDetail('btc-open');
    setTab(page.getAttribute('data-cb-positions-default-tab') || 'positions');
    applyScenario('active');
})();
