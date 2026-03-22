(function () {
    const page = document.querySelector('[data-cb-backtest]');
    if (!page) {
        return;
    }

    const scenarios = {
        idle: {
            panel: 'idle',
            mode: 'Spot context',
            range: 'Son 90 gün',
            cost: 'Fee + slippage etkin',
            validation: false,
            summary: ['+18.4%', '61%', '-8.6%', '48', '$10,000', '$11,840'],
            curve: ['Sonuç hazır', 'Backtest motoru bağlandığında çizgi gerçek equity, zoom ve drawdown metrikleriyle güncellenecek.', '$ 10,000.00', '$ 11,840.20', '+18.40%'],
            metrics: ['-8.6%', 'Risk profili ve stop mantığı drawdown davranışını belirgin şekilde etkiler.', '61%', '39%', '+2.1%', '-1.3%', '29', '19']
        },
        running: {
            panel: 'running',
            mode: 'Çalışıyor',
            range: 'Özel aralık',
            cost: 'Sonuç hesaplanıyor',
            validation: false,
            summary: ['—', '—', '—', '—', '$10,000', 'Hesaplanıyor'],
            curve: ['Çalışıyor', 'Veri hazırlanıyor, sonuç hesaplanıyor ve trade listesi oluşturuluyor.', '$ 10,000.00', 'Hesaplanıyor', '—'],
            metrics: ['—', 'Risk baskısı hesaplanıyor.', '—', '—', '—', '—', '—', '—']
        },
        success: {
            panel: 'content',
            mode: 'Futures context',
            range: 'Özel aralık',
            cost: 'Fee + slippage etkin',
            validation: false,
            summary: ['+24.1%', '64%', '-10.2%', '62', '$15,000', '$18,615'],
            curve: ['Sonuç hazır', 'Futures ağırlıklı senaryoda sonuç eğrisi, maliyet varsayımlarıyla birlikte özetleniyor.', '$ 15,000.00', '$ 18,615.34', '+24.10%'],
            metrics: ['-10.2%', 'Drawdown artmış olsa da toplam getiri korunuyor.', '64%', '36%', '+2.8%', '-1.6%', '40', '22']
        },
        limited: {
            panel: 'limited',
            mode: 'Spot context',
            range: 'Son 7 gün',
            cost: 'Kısa aralık seçili',
            validation: true,
            summary: ['+2.1%', '52%', '-5.1%', '8', '$10,000', '$10,210'],
            curve: ['Sonuç sınırlı', 'Kısa aralık nedeniyle eğri fazla gürültülü görünebilir; yorumlarken dikkatli olun.', '$ 10,000.00', '$ 10,210.00', '+2.10%'],
            metrics: ['-5.1%', 'Dar veri kümesi risk okumasını sınırlı hale getirir.', '52%', '48%', '+1.0%', '-0.9%', '4', '4']
        },
        error: {
            panel: 'error',
            mode: 'Doğrulanamadı',
            range: 'Özel aralık',
            cost: 'Parametreler gözden geçirilmeli',
            validation: true,
            summary: ['—', '—', '—', '—', '$10,000', '—'],
            curve: ['Başarısız', 'Sonuç eğrisi üretilemedi; kullanıcıyı çıkmaza sokmayan retry yüzeyi aktif.', '$ 10,000.00', '—', '—'],
            metrics: ['—', 'Hata durumu metrik panelinde nötr görünümle karşılanır.', '—', '—', '—', '—', '—', '—']
        }
    };

    const trades = {
        'btc-long': { title: 'BTCUSDT · Long', symbol: 'BTCUSDT', direction: 'Long', pnl: '+3.4%', setup: 'EMA Cross', reason: 'Trend teyidi ve hacim desteği ile uzun yön açıldı.' },
        'eth-short': { title: 'ETHUSDT · Short', symbol: 'ETHUSDT', direction: 'Short', pnl: '-1.1%', setup: 'Breakout Fade', reason: 'Breakout teyidi zayıfladı; stop seviyesine erken dönüldü.' },
        'sol-long': { title: 'SOLUSDT · Long', symbol: 'SOLUSDT', direction: 'Long', pnl: '+1.8%', setup: 'RSI Reversal', reason: 'RSI aşırı satım dönüşü ile long sinyal oluştu.' }
    };

    function text(id, value) {
        const el = document.getElementById(id);
        if (!el) return;
        if ('value' in el && (el.tagName === 'INPUT' || el.tagName === 'SELECT' || el.tagName === 'TEXTAREA')) {
            el.value = value;
            return;
        }
        el.textContent = value;
    }

    function setScenario(name) {
        const scenario = scenarios[name];
        if (!scenario) return;

        page.setAttribute('data-cb-backtest-scenario', name);
        page.querySelectorAll('[data-cb-backtest-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-backtest-scenario-trigger') === name);
        });

        page.querySelectorAll('[data-cb-backtest-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-backtest-panel') !== scenario.panel);
        });

        document.querySelectorAll('[data-cb-backtest-panel="content"]').forEach(function (panel) {
            panel.classList.toggle('d-none', !(name === 'success' || name === 'limited'));
        });

        text('cb_backtest_mode_badge', scenario.mode);
        text('cb_backtest_range_badge', scenario.range);
        text('cb_backtest_cost_badge', scenario.cost);
        document.getElementById('cb_backtest_validation_alert')?.classList.toggle('d-none', !scenario.validation);

        const s = scenario.summary;
        text('cb_backtest_summary_return', s[0]);
        text('cb_backtest_summary_win_rate', s[1]);
        text('cb_backtest_summary_drawdown', s[2]);
        text('cb_backtest_summary_trades', s[3]);
        text('cb_backtest_summary_initial', s[4]);
        text('cb_backtest_summary_ending', s[5]);

        const c = scenario.curve;
        text('cb_backtest_curve_badge', c[0]);
        text('cb_backtest_curve_note', c[1]);
        text('cb_backtest_curve_initial', c[2]);
        text('cb_backtest_curve_end', c[3]);
        text('cb_backtest_curve_change', c[4]);

        const m = scenario.metrics;
        text('cb_backtest_drawdown_value', m[0]);
        text('cb_backtest_drawdown_note', m[1]);
        text('cb_backtest_metric_win', m[2]);
        text('cb_backtest_metric_loss', m[3]);
        text('cb_backtest_metric_avg_win', m[4]);
        text('cb_backtest_metric_avg_loss', m[5]);
        text('cb_backtest_metric_wins', m[6]);
        text('cb_backtest_metric_losses', m[7]);
    }

    function setTrade(id) {
        const trade = trades[id];
        if (!trade) return;
        text('cb_backtest_drawer_title', trade.title);
        text('cb_backtest_drawer_symbol', trade.symbol);
        text('cb_backtest_drawer_direction', trade.direction);
        text('cb_backtest_drawer_pnl', trade.pnl);
        text('cb_backtest_drawer_setup', trade.setup);
        text('cb_backtest_drawer_reason', trade.reason);
    }

    function setButtonLoading(button, isLoading) {
        if (!button) return;
        button.classList.toggle('is-loading', isLoading);
        button.toggleAttribute('disabled', isLoading);
    }

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-backtest-scenario-trigger]');
        const runTrigger = event.target.closest('[data-cb-backtest-run]');
        const resetTrigger = event.target.closest('[data-cb-backtest-reset]');
        const rangeTrigger = event.target.closest('[data-cb-backtest-range]');
        const tradeDetail = event.target.closest('[data-cb-backtest-trade-detail]');

        if (scenarioTrigger) {
            event.preventDefault();
            setScenario(scenarioTrigger.getAttribute('data-cb-backtest-scenario-trigger'));
        }

        if (runTrigger) {
            event.preventDefault();
            setButtonLoading(runTrigger, true);
            setScenario('running');
            window.setTimeout(function () {
                setButtonLoading(runTrigger, false);
                setScenario('success');
            }, 700);
        }

        if (resetTrigger) {
            event.preventDefault();
            setScenario('idle');
            text('cb_backtest_initial_balance', '10000');
            text('cb_backtest_fee', '0.10');
            text('cb_backtest_slippage', '0.03');
        }

        if (rangeTrigger) {
            event.preventDefault();
            const key = rangeTrigger.getAttribute('data-cb-backtest-range');
            const map = { '7d': 'Son 7 gün', '30d': 'Son 30 gün', '90d': 'Son 90 gün', 'custom': 'Özel aralık' };
            text('cb_backtest_range_badge', map[key] || 'Özel aralık');
        }

        if (tradeDetail) {
            event.preventDefault();
            setTrade(tradeDetail.getAttribute('data-cb-backtest-trade-detail'));
        }
    });

    document.addEventListener('change', function (event) {
        if (event.target && event.target.id === 'cb_backtest_market') {
            const value = event.target.value;
            text('cb_backtest_mode_badge', value === 'futures' ? 'Futures context' : 'Spot context');
        }
    });

    setTrade('btc-long');
    setScenario('idle');
})();
