(function () {
    const page = document.querySelector('[data-cb-risk-center]');
    if (!page) {
        return;
    }

    const scenarios = {
        stable: {
            summary: { leverage: '2x', leverageNote: 'Koruyucu futures limiti', daily: '1.8%', dailyNote: 'Yeni girişleri durdur', weekly: '4.5%', weeklyNote: 'Haftalık koruma bandı', open: '3', openNote: 'Sınırlı eş zamanlı pozisyon', coin: '2', coinNote: 'BTC ve SOL için ek kural', status: 'Stabil', statusNote: 'Koruyucu profil aktif', statusTone: 'success' },
            usage: { daily: 42, weekly: 31 },
            policy: { state: ['Kontrollü profil', 'warning'], market: 'spot', leverage: '2', daily: '1.8', weekly: '4.5', open: '3', perTrade: '0.8', noTrade: true, summary: '22:00 - 01:30 arası yeni giriş kapalı' },
            warnings: [
                ['No-trade hours', 'info', 'Haber akışı ve düşük likidite saatleri için işlem penceresi kısıtlı tutuluyor.'],
                ['Risk limiti', 'success', 'Günlük ve haftalık limitler koruyucu bandın içinde görünüyor.']
            ],
            lastAction: ['Henüz kritik aksiyon yok', 'Sistem stabil görünüyor'],
            panel: 'content', surface: false
        },
        balanced: {
            summary: { leverage: '4x', leverageNote: 'Orta agresif futures limiti', daily: '3.0%', dailyNote: 'İlk eşikte uyarı ver', weekly: '7.0%', weeklyNote: 'Orta bant koruma', open: '5', openNote: 'Daha esnek pozisyon limiti', coin: '1', coinNote: 'Majör coin için tek kural', status: 'Dengeli', statusNote: 'Birkaç uyarı görünüyor', statusTone: 'warning' },
            usage: { daily: 57, weekly: 48 },
            policy: { state: ['Dengeli profil', 'info'], market: 'both', leverage: '4', daily: '3.0', weekly: '7.0', open: '5', perTrade: '1.2', noTrade: false, summary: 'No-trade hours kapalı' },
            warnings: [
                ['No-trade hours kapalı', 'warning', 'Likidite düşük saatlerde sistem yeni giriş arayabilir.'],
                ['AI düşük güven', 'info', 'AI düşük güven moduna girerse risk paneli ile birlikte değerlendirilmelidir.']
            ],
            lastAction: ['Pause New Entries', 'Dün · 22:14 placeholder'],
            panel: 'content', surface: false
        },
        aggressive: {
            summary: { leverage: '10x', leverageNote: 'Agresif futures limiti', daily: '6.5%', dailyNote: 'Sadece uyarı modu seçili', weekly: '14%', weeklyNote: 'Gevşek koruma bandı', open: '9', openNote: 'Yüksek eş zamanlı pozisyon', coin: '0', coinNote: 'Coin bazlı kural yok', status: 'Yüksek risk', statusNote: 'Agresif kombinasyon aktif', statusTone: 'danger' },
            usage: { daily: 78, weekly: 69 },
            policy: { state: ['Agresif profil', 'danger'], market: 'futures', leverage: '10', daily: '6.5', weekly: '14', open: '9', perTrade: '2.5', noTrade: false, summary: 'No-trade hours tanımlı değil' },
            warnings: [
                ['Futures + yüksek leverage', 'danger', 'Liquidation ve margin baskısı artar; emergency actions daha kritik hale gelir.'],
                ['Zarar limitleri gevşek', 'warning', 'Günlük ve haftalık limitler koruma mekanizmasını zayıflatabilir.'],
                ['Max açık işlem yüksek', 'warning', 'Aynı anda çok sayıda sinyal açılabilir; risk dağılımı dikkat ister.']
            ],
            lastAction: ['Stop All Bots', '2 saat önce placeholder'],
            panel: 'content', surface: true
        },
        emergency: {
            summary: { leverage: '4x', leverageNote: 'Emergency sonrası kontrollü bant', daily: '2.2%', dailyNote: 'Yeni girişler pause modunda', weekly: '5.5%', weeklyNote: 'Koruma yeniden sıkılaştırıldı', open: '2', openNote: 'Sadece kritik pozisyonlar', coin: '2', coinNote: 'Koruyucu coin limitleri geri alındı', status: 'Emergency mod', statusNote: 'Son aksiyon sonrası gözlem', statusTone: 'danger' },
            usage: { daily: 64, weekly: 52 },
            policy: { state: ['Emergency gözlem', 'danger'], market: 'futures', leverage: '4', daily: '2.2', weekly: '5.5', open: '2', perTrade: '0.7', noTrade: true, summary: 'Acil modda yeni girişler sınırlı' },
            warnings: [
                ['Emergency mod aktif', 'danger', 'Close All / Stop All sonrası yeni girişler kontrollü tutulmalıdır.'],
                ['Futures tarafı kısıtlı', 'warning', 'Futures aksiyonları geçici olarak daha sıkı politika altında gözleniyor.']
            ],
            lastAction: ['Close All placeholder', 'Az önce · kontrollü aksiyon kaydı'],
            panel: 'content', surface: true
        },
        empty: {
            panel: 'empty'
        },
        loading: {
            panel: 'loading'
        },
        error: {
            panel: 'error'
        }
    };

    function text(id, value) {
        const el = document.getElementById(id);
        if (!el) return;
        if ('value' in el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA')) {
            el.value = value;
            return;
        }
        el.textContent = value;
    }

    function setBadgeTone(el, tone) {
        if (!el) return;
        el.classList.remove('cb-badge-success', 'cb-badge-warning', 'cb-badge-danger', 'cb-badge-info', 'cb-badge-neutral');
        el.classList.add('cb-badge-' + (tone || 'neutral'));
    }

    function setProgress(id, value, toneClass) {
        const bar = document.getElementById(id);
        if (!bar) return;
        bar.style.width = value + '%';
        bar.className = 'progress-bar ' + toneClass;
    }

    function renderWarnings(items) {
        const wrap = document.getElementById('cb_risk_warning_list');
        if (!wrap) return;
        wrap.innerHTML = items.map(function (item) {
            return '<div class="cb-rule-setting-item"><div class="cb-rule-setting-header"><strong>' + item[0] + '</strong><span class="cb-badge cb-badge-' + item[1] + '">' + (item[1] === 'danger' ? 'Kritik' : item[1] === 'warning' ? 'Dikkat' : item[1] === 'success' ? 'Stabil' : 'Bilgi') + '</span></div><div class="text-muted font-size-sm mt-2">' + item[2] + '</div></div>';
        }).join('');
    }

    function applyNoTradeState(enabled, summary) {
        const body = document.getElementById('cb_risk_notrade_body');
        const toggle = document.getElementById('cb_risk_notrade_toggle');
        if (toggle) toggle.checked = enabled;
        if (body) body.classList.toggle('is-disabled', !enabled);
        text('cb_risk_notrade_summary', summary);
    }

    function applyScenario(name) {
        const scenario = scenarios[name];
        if (!scenario) return;
        page.setAttribute('data-cb-risk-scenario', name);

        page.querySelectorAll('[data-cb-risk-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-risk-scenario-trigger') === name);
        });

        page.querySelectorAll('[data-cb-risk-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-risk-panel') !== scenario.panel);
        });

        if (scenario.panel !== 'content') {
            return;
        }

        const s = scenario.summary;
        text('cb_risk_summary_leverage', s.leverage);
        text('cb_risk_summary_leverage_note', s.leverageNote);
        text('cb_risk_summary_daily', s.daily);
        text('cb_risk_summary_daily_note', s.dailyNote);
        text('cb_risk_summary_weekly', s.weekly);
        text('cb_risk_summary_weekly_note', s.weeklyNote);
        text('cb_risk_summary_open_positions', s.open);
        text('cb_risk_summary_open_note', s.openNote);
        text('cb_risk_summary_coin_limits', s.coin);
        text('cb_risk_summary_coin_note', s.coinNote);
        text('cb_risk_summary_status', s.status);
        text('cb_risk_summary_status_note', s.statusNote);

        const statusWrap = document.getElementById('cb_risk_summary_status_wrap');
        if (statusWrap) {
            statusWrap.classList.remove('is-success', 'is-warning', 'is-danger');
            statusWrap.classList.add('is-' + s.statusTone);
        }

        const policyState = document.getElementById('cb_risk_policy_state');
        if (policyState) {
            policyState.textContent = scenario.policy.state[0];
            setBadgeTone(policyState, scenario.policy.state[1]);
        }

        const market = document.getElementById('cb_risk_market_mode');
        const leverage = document.getElementById('cb_risk_max_leverage');
        const daily = document.getElementById('cb_risk_daily_limit');
        const weekly = document.getElementById('cb_risk_weekly_limit');
        const open = document.getElementById('cb_risk_max_open_positions');
        const perTrade = document.getElementById('cb_risk_per_trade');

        if (market) market.value = scenario.policy.market;
        if (leverage) leverage.value = scenario.policy.leverage;
        if (daily) daily.value = scenario.policy.daily;
        if (weekly) weekly.value = scenario.policy.weekly;
        if (open) open.value = scenario.policy.open;
        if (perTrade) perTrade.value = scenario.policy.perTrade;

        applyNoTradeState(scenario.policy.noTrade, scenario.policy.summary);
        text('cb_risk_leverage_help', Number(scenario.policy.leverage) >= 8 ? 'Yüksek kaldıraç bandı seçildi. Risk motoru bağlandığında daha sıkı guard'lar önerilir.' : scenario.policy.market === 'spot' ? 'Spot ağırlıklı modda leverage alanı bilgilendirme amaçlı daha pasif düşünülebilir.' : 'Futures politikası aktif; leverage limiti yeni girişler için üst sınırı temsil eder.');
        text('cb_risk_position_note', Number(scenario.policy.open) >= 8 ? 'Yüksek açık işlem sayısı, aynı anda çok sayıda sinyalin yürütülmesine zemin bırakır.' : 'Pozisyon limiti kontrollü tutulduğunda bot ve AI önerileri daha okunabilir kalır.');

        text('cb_risk_daily_usage_badge', '%' + scenario.usage.daily + ' kullanıldı');
        text('cb_risk_weekly_usage_badge', '%' + scenario.usage.weekly + ' kullanıldı');
        setBadgeTone(document.getElementById('cb_risk_daily_usage_badge'), scenario.usage.daily >= 70 ? 'warning' : 'info');
        setBadgeTone(document.getElementById('cb_risk_weekly_usage_badge'), scenario.usage.weekly >= 60 ? 'warning' : 'success');
        setProgress('cb_risk_daily_usage_bar', scenario.usage.daily, scenario.usage.daily >= 70 ? 'bg-danger' : 'bg-warning');
        setProgress('cb_risk_weekly_usage_bar', scenario.usage.weekly, scenario.usage.weekly >= 60 ? 'bg-warning' : 'bg-info');

        renderWarnings(scenario.warnings);
        document.getElementById('cb_risk_warning_surface')?.classList.toggle('d-none', !scenario.surface);
        document.getElementById('cb_risk_leverage_warning')?.classList.toggle('d-none', Number(scenario.policy.leverage) < 8 && scenario.policy.market !== 'futures');

        text('cb_risk_last_action', scenario.lastAction[0]);
        text('cb_risk_last_action_note', scenario.lastAction[1]);
    }

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-risk-scenario-trigger]');
        if (scenarioTrigger) {
            event.preventDefault();
            applyScenario(scenarioTrigger.getAttribute('data-cb-risk-scenario-trigger'));
        }

        const coinLimitTrigger = event.target.closest('[data-cb-coin-limit-mode]');
        if (coinLimitTrigger) {
            const mode = coinLimitTrigger.getAttribute('data-cb-coin-limit-mode');
            text('cb_coin_limit_modal_eyebrow', mode === 'edit' ? 'Edit Coin Limit' : 'Create Coin Limit');
            text('cb_coin_limit_modal_title', mode === 'edit' ? 'Coin limitini düzenle' : 'Coin limiti ekle');
            text('cb_coin_limit_symbol', coinLimitTrigger.getAttribute('data-cb-coin-symbol') || 'BTCUSDT');
        }
    });

    document.addEventListener('change', function (event) {
        const noTradeToggle = event.target.closest('[data-cb-risk-notrade-toggle]');
        if (noTradeToggle) {
            applyNoTradeState(noTradeToggle.checked, noTradeToggle.checked ? '22:00 - 01:30 arası yeni giriş kapalı' : 'No-trade hours kapalı');
        }

    });

    applyScenario('stable');
})();
