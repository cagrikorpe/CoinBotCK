(function () {
    const page = document.querySelector('[data-cb-paper-trading]');
    if (!page) {
        return;
    }

    const scenarios = {
        ready: {
            panel: 'content',
            modeBadge: ['Paper Mode', 'is-paper'],
            modeNote: 'İşlemler gerçek piyasaya gönderilmez. Sonuçlar simülasyondur ve canlı sonuç garantisi vermez.',
            liveState: 'Live geçiş öneri aşamasında',
            summary: ['$25,000', '$25,000', '—', '$0', '0', '0'],
            positionsPanel: 'empty',
            botsPanel: 'empty',
            tradesPanel: 'empty',
            session: ['Hazır', 'cb-badge-info', 'Henüz paper akış başlamadı', 'Veri bekleniyor', 'Bot tetiklenmedi', 'İşlem yok', 'Paper hesap hazır; akış başlayınca meta alanları güncellenecek.'],
            daily: ['0', '0', '$0', '—', '—', '', ''],
            readiness: ['İzlemeye devam et', 'cb-badge-warning', 'Önce sonuç üret', 'Paper akış henüz sinyal üretmedi. Canlı moda geçiş öncesi biraz daha gözlem verisi toplanmalı.', 'Önce paper işlemler oluşsun', 'Risk merkezini kontrol et', 'Exchange ayarlarını doğrula']
        },
        active: {
            panel: 'content',
            modeBadge: ['Paper Mode', 'is-paper'],
            modeNote: 'Canlı piyasayı etkilemeyen simülasyon modunda açık paper pozisyonlar ve botlar operasyonel olarak izleniyor.',
            liveState: 'Live Mode placeholder',
            summary: ['$25,684', '$18,420', '+2.3%', '+$184', '2', '3'],
            positionsPanel: 'list',
            botsPanel: 'list',
            tradesPanel: 'list',
            session: ['Aktif', 'cb-badge-success', 'Az önce', '2 sn önce', '5 sn önce', '09:42', 'Paper session aktif; canlı akış simülasyonu normal görünüyor.'],
            daily: ['6', '4', '+$184', 'BTCUSDT', 'ETHUSDT', '+1.9%', '-0.7%'],
            readiness: ['Kontrollü ilerle', 'cb-badge-info', 'Bazı sinyaller umut verici', 'Paper sonuçları olumlu görünüyor; yine de live geçişten önce risk limitleri ve drawdown davranışı izlenmeli.', 'Paper sonuçlarını biraz daha izle', 'Risk ayarlarını kontrol et', 'Win rate kalıcılığı ölçülmeli']
        },
        waiting: {
            panel: 'content',
            modeBadge: ['Paper Mode', 'is-paper'],
            modeNote: 'Paper session veri bekliyor; bu ara durum normaldir ve canlı emir gönderilmez.',
            liveState: 'Live geçiş için erken',
            summary: ['$25,120', '$24,910', '+0.2%', '+$18', '0', '1'],
            positionsPanel: 'empty',
            botsPanel: 'list',
            tradesPanel: 'empty',
            session: ['Veri bekleniyor', 'cb-badge-warning', '15 dk önce', 'Akış bekleniyor', '12 dk önce', '08:31', 'Kısa süreli fiyat/veri bekleme durumu normal karşılanır; kullanıcıyı paniğe sevk etmez.'],
            daily: ['1', '0', '+$18', 'BTCUSDT', '—', '+0.2%', '—'],
            readiness: ['Sabırlı ol', 'cb-badge-neutral', 'Önce daha fazla akış gözle', 'Veri beklenirken canlı readiness kararı verilmemeli.', 'Akışın dengelenmesini bekle', 'Exchange bağlantısını kontrol et', 'Risk merkezini tekrar gözden geçir']
        },
        caution: {
            panel: 'content',
            modeBadge: ['Paper Mode', 'is-paper'],
            modeNote: 'Paper mod aktif ancak sonuç kalitesi ve risk sinyalleri canlı geçiş öncesi dikkat gerektiriyor.',
            liveState: 'Live geçiş için dikkat',
            summary: ['$24,860', '$17,940', '-1.8%', '-$142', '3', '2'],
            positionsPanel: 'list',
            botsPanel: 'list',
            tradesPanel: 'list',
            session: ['Aktif', 'cb-badge-warning', '1 dk önce', '6 sn önce', '11 sn önce', '09:28', 'Paper session çalışıyor fakat kalite ve risk notları daha dikkatli okunmalı.'],
            daily: ['7', '5', '-$142', 'SOLUSDT', 'BTCUSDT', '+0.8%', '-1.3%'],
            readiness: ['Canlıya geçme', 'cb-badge-danger', 'Risk merkezi kontrolü gerekli', 'Drawdown ve sonuç kalitesi hâlâ zayıf görünüyor. Canlı moda geçmeden önce risk limitlerini sıkılaştırmak daha doğru olur.', 'Drawdown yüksek olabilir', 'Risk merkezine git', 'Exchange ayarlarını doğrula']
        },
        loading: {
            panel: 'loading'
        },
        error: {
            panel: 'error'
        }
    };

    const activities = {
        'btc-position': { eyebrow: 'Paper Position', title: 'BTCUSDT · Açık paper pozisyon', kind: 'Position', symbol: 'BTCUSDT', direction: 'Long', mode: 'Paper Mode', note: 'Momentum template üzerinden açılmış sanal long pozisyon. Gerçek emir gönderilmez, yalnızca çalışma simülasyonu izlenir.' },
        'sol-position': { eyebrow: 'Paper Position', title: 'SOLUSDT · Açık paper short', kind: 'Position', symbol: 'SOLUSDT', direction: 'Short', mode: 'Paper Mode', note: 'Futures benzeri paper short simülasyonu; leverage ve risk notları sonradan runtime verisiyle genişletilebilir.' },
        'alpha-bot': { eyebrow: 'Paper Bot', title: 'Alpha Spot Pulse · Bot detayı', kind: 'Bot', symbol: 'Alpha Spot Pulse', direction: 'Aktif', mode: 'Paper Mode', note: 'Momentum template ile paper modda çalışan bot. Son sinyal ve açık işlem sayısı operasyonel özet olarak gösterilir.' },
        'beta-bot': { eyebrow: 'Paper Bot', title: 'Beta Hedge Runner · Bot detayı', kind: 'Bot', symbol: 'Beta Hedge Runner', direction: 'Beklemede', mode: 'Paper Mode', note: 'Hedge template için bekleme modundaki paper bot. Live geçişe hazır değil; daha fazla gözlem gerekir.' },
        'btc-trade': { eyebrow: 'Paper Trade', title: 'BTCUSDT · Kapanan paper işlem', kind: 'Trade', symbol: 'BTCUSDT', direction: 'Long', mode: 'Paper Mode', note: 'Kapanan paper işlem detayında giriş/çıkış, setup etiketi ve sonuç placeholder olarak özetlenir.' },
        'eth-trade': { eyebrow: 'Paper Trade', title: 'ETHUSDT · Açılan paper işlem', kind: 'Trade', symbol: 'ETHUSDT', direction: 'Short', mode: 'Paper Mode', note: 'Henüz açık olan paper işlem, canlı emir göndermeden simüle edilir ve sonradan order events ile genişletilebilir.' }
    };

    function text(id, value) {
        const el = document.getElementById(id);
        if (!el) return;
        el.textContent = value;
    }

    function setBadge(id, value, classes) {
        const el = document.getElementById(id);
        if (!el) return;
        el.textContent = value;
        el.className = classes;
    }

    function setPanel(group, name) {
        page.querySelectorAll('[data-cb-paper-' + group + '-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-paper-' + group + '-panel') !== name);
        });
    }

    function applyScenario(name) {
        const scenario = scenarios[name];
        if (!scenario) return;
        page.setAttribute('data-cb-paper-scenario', name);
        page.querySelectorAll('[data-cb-paper-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-paper-scenario-trigger') === name);
        });
        page.querySelectorAll('[data-cb-paper-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-paper-panel') !== scenario.panel);
        });
        if (scenario.panel !== 'content') return;

        text('cb_paper_mode_badge', scenario.modeBadge[0]);
        const modeBadge = document.getElementById('cb_paper_mode_badge');
        if (modeBadge) {
            modeBadge.className = 'cb-paper-mode-badge ' + scenario.modeBadge[1];
        }
        text('cb_paper_mode_note', scenario.modeNote);
        text('cb_paper_mode_live_state', scenario.liveState);

        const s = scenario.summary;
        text('cb_paper_summary_total', s[0]);
        text('cb_paper_summary_available', s[1]);
        text('cb_paper_summary_open_pnl', s[2]);
        text('cb_paper_summary_day', s[3]);
        text('cb_paper_summary_positions', s[4]);
        text('cb_paper_summary_bots', s[5]);

        setPanel('positions', scenario.positionsPanel);
        setPanel('bots', scenario.botsPanel);
        setPanel('trades', scenario.tradesPanel);

        const session = scenario.session;
        setBadge('cb_paper_session_status', session[0], session[1]);
        text('cb_paper_session_updated', session[2]);
        text('cb_paper_session_price', session[3]);
        text('cb_paper_session_bot_tick', session[4]);
        text('cb_paper_session_trade', session[5]);
        text('cb_paper_session_alert', session[6]);

        const d = scenario.daily;
        text('cb_paper_daily_opened', d[0]);
        text('cb_paper_daily_closed', d[1]);
        text('cb_paper_daily_net', d[2]);
        text('cb_paper_daily_best', d[3]);
        text('cb_paper_daily_worst', d[4]);
        text('cb_paper_daily_best_note', d[5]);
        text('cb_paper_daily_worst_note', d[6]);

        const r = scenario.readiness;
        setBadge('cb_paper_readiness_badge', r[0], r[1]);
        text('cb_paper_readiness_state', r[2]);
        text('cb_paper_readiness_note', r[3]);
        text('cb_paper_readiness_hint_1', r[4]);
        text('cb_paper_readiness_hint_2', r[5]);
        text('cb_paper_readiness_hint_3', r[6]);
    }

    function setActivity(id) {
        const item = activities[id];
        if (!item) return;
        text('cb_paper_drawer_eyebrow', item.eyebrow);
        text('cb_paper_drawer_title', item.title);
        text('cb_paper_drawer_kind', item.kind);
        text('cb_paper_drawer_symbol', item.symbol);
        text('cb_paper_drawer_direction', item.direction);
        text('cb_paper_drawer_mode', item.mode);
        text('cb_paper_drawer_note', item.note);
    }

    function setButtonLoading(button, isLoading) {
        if (!button) return;
        button.classList.toggle('is-loading', isLoading);
        button.toggleAttribute('disabled', isLoading);
    }

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-paper-scenario-trigger]');
        const refreshTrigger = event.target.closest('[data-cb-paper-refresh]');
        const activityTrigger = event.target.closest('[data-cb-paper-activity-detail]');

        if (scenarioTrigger) {
            event.preventDefault();
            applyScenario(scenarioTrigger.getAttribute('data-cb-paper-scenario-trigger'));
        }

        if (refreshTrigger) {
            event.preventDefault();
            setButtonLoading(refreshTrigger, true);
            applyScenario('loading');
            window.setTimeout(function () {
                setButtonLoading(refreshTrigger, false);
                applyScenario('active');
            }, 700);
        }

        if (activityTrigger) {
            setActivity(activityTrigger.getAttribute('data-cb-paper-activity-detail'));
        }
    });

    setActivity('btc-position');
    applyScenario('ready');
})();
