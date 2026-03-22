(function () {
    const page = document.querySelector('[data-cb-logcenter-page]');
    if (!page) return;

    const scenarios = {
        'empty': { panel: 'content', total: '0', today: '0', critical: '0', fresh: '0', trace: false, last: '—', badge: '0 yeni olay', list: 'empty', notice: 'Detaylı davranış izleme kapalı veya henüz log akışı başlamamış olabilir.' },
        'mixed': { panel: 'content', total: '184', today: '28', critical: '3', fresh: '7', trace: true, last: '09:42', badge: '7 yeni olay', list: 'list', notice: 'Detaylı davranış izleme açıkken kullanıcı davranışı olayları ayrı badge ile görünür.' },
        'critical': { panel: 'content', total: '41', today: '12', critical: '6', fresh: '6', trace: true, last: '09:48', badge: 'Kritik yoğunluk', list: 'list', notice: 'Kritik security ve risk kayıtları öne çıkarıldı.' },
        'trace-off': { panel: 'content', total: '129', today: '18', critical: '2', fresh: '4', trace: false, last: '09:15', badge: 'Trace kapalı', list: 'list', notice: 'Detaylı davranış izleme kapalı. User Activity kategorisi sınırlı veya gizli görünür.' },
        'trace-on': { panel: 'content', total: '236', today: '36', critical: '3', fresh: '9', trace: true, last: '09:53', badge: 'Trace aktif', list: 'list', notice: 'Davranış olayları daha ayrıntılı görünür, ancak hassas alanlar yine maskelenir.' },
        'loading': { panel: 'loading' },
        'error': { panel: 'error' }
    };

    const detailMap = {
        'risk-limit': { eyebrow: 'Risk Event', title: 'Günlük zarar limiti eşiği aşılıyor', severity: ['Critical', 'cb-badge cb-badge-danger'], category: ['Risk', 'cb-badge cb-badge-warning'], trace: 'Özet olay', time: '21 Mar · 09:42', source: 'Risk Merkezi', message: 'Günlük zarar limiti bandı %85 kullanım seviyesine ulaştı. Kullanıcıya daha sıkı risk kontrolü ve gerekirse emergency action yüzeyi önerilir.', tags: ['Risk', 'Alpha Desk', 'BTCUSDT'], action: 'Risk Merkezi’ne giderek limit davranışı ve emergency policy alanını gözden geçir.' },
        'security-2fa': { eyebrow: 'Security Event', title: '2FA yönetim yüzeyi görüntülendi', severity: ['Warning', 'cb-badge cb-badge-warning'], category: ['Security', 'cb-badge cb-badge-danger'], trace: 'Özet olay', time: '21 Mar · 09:31', source: 'Settings · Güvenlik', message: 'Kullanıcı 2FA yönetim yüzeyini açtı. Kritik alanlar görünür, hassas bilgiler maskeli kalır ve yalnızca ürün diliyle özetlenir.', tags: ['Settings', '2FA', 'Security review'], action: 'Ayarlar ekranında security section üzerinden 2FA ve oturum alanlarını kontrollü biçimde gözden geçir.' },
        'ai-veto': { eyebrow: 'AI Event', title: 'AI önerisi veto edildi', severity: ['Info', 'cb-badge cb-badge-info'], category: ['AI', 'cb-badge cb-badge-info'], trace: 'Özet olay', time: '21 Mar · 09:18', source: 'AI Robot', message: 'Confidence bandı düşük kaldığı için öneri watch seviyesinde tutuldu. Bu log ham model trace yerine kullanıcı dostu özet sunar.', tags: ['AI Robot', 'AVAXUSDT', 'Confidence 41%'], action: 'AI Robot ekranında explainability ve veto panelini açarak ilgili setup özetini gözden geçir.' },
        'activity-builder': { eyebrow: 'User Activity', title: 'Strategy Builder içinde yeni blok eklendi', severity: ['Trace', 'cb-badge cb-badge-neutral'], category: ['User Activity', 'cb-badge cb-badge-success'], trace: 'Detaylı izleme', time: '21 Mar · 09:11', source: 'Strategy Builder', message: 'Kullanıcı RSI koşulu ekledi ve builder canvas üzerinde satırı güncelledi. Bu olay yalnızca davranış özeti verir; request body veya hassas alan içermez.', tags: ['Builder', 'RSI block', 'Condition edit'], action: 'Strategy Builder ekranına geçerek ilgili blok grubunu ve strategy summary alanını gözden geçir.' },
        'trading-sync': { eyebrow: 'Trading Event', title: 'Paper trade senkronizasyonu gecikti', severity: ['Error', 'cb-badge cb-badge-danger'], category: ['Trading', 'cb-badge cb-badge-success'], trace: 'Özet olay', time: '21 Mar · 08:54', source: 'Paper Trading', message: 'Simülasyon verisi gecikiyor olabilir. Bu yüzey gerçek sync backend olmadan yalnızca operasyonel summary gösterir.', tags: ['Paper Trading', 'Sync queue', 'Waiting data'], action: 'Paper Trading ekranında session meta ve günlük sonuç alanlarını kontrol et.' },
        'audit-settings': { eyebrow: 'Audit Event', title: 'Bildirim tercihleri güncelleme girişimi', severity: ['Info', 'cb-badge cb-badge-info'], category: ['Audit', 'cb-badge cb-badge-warning'], trace: 'Özet olay', time: '21 Mar · 08:32', source: 'Settings · Bildirimler', message: 'Kullanıcı notification preferences bölümünde toggle değiştirdi. Persist olmadığı için olay ürün diliyle summary olarak tutulur.', tags: ['Audit', 'Notifications', 'Toggle change'], action: 'Ayarlar ekranında bildirim tercihleri ve ilgili kanal seçimlerini kontrollü şekilde güncelle.' }
    };

    function setText(id, value) { const el = document.getElementById(id); if (el) el.textContent = value; }
    function setBadge(id, text, cls) { const el = document.getElementById(id); if (el) { el.className = cls; el.textContent = text; } }
    function setPagePanel(name) {
        page.querySelectorAll('[data-cb-logcenter-page-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-logcenter-page-panel') !== name);
        });
    }
    function setListPanel(name) {
        page.querySelectorAll('[data-cb-log-list-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-log-list-panel') !== name);
        });
    }
    function applyScenario(name) {
        const scenario = scenarios[name]; if (!scenario) return;
        page.setAttribute('data-cb-logcenter-scenario', name);
        page.querySelectorAll('[data-cb-logcenter-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-logcenter-scenario-trigger') === name);
        });
        setPagePanel(scenario.panel);
        if (scenario.panel !== 'content') return;
        setText('cb_log_total', scenario.total);
        setText('cb_log_today', scenario.today);
        setText('cb_log_critical', scenario.critical);
        setText('cb_log_new', scenario.fresh);
        setText('cb_log_last', scenario.last);
        setText('cb_log_list_badge', scenario.badge);
        setBadge('cb_log_trace_badge', scenario.trace ? 'Açık' : 'Kapalı', scenario.trace ? 'cb-badge cb-badge-success' : 'cb-badge cb-badge-neutral');
        setBadge('cb_log_trace_status_side', scenario.trace ? 'Açık' : 'Kapalı', scenario.trace ? 'cb-badge cb-badge-success' : 'cb-badge cb-badge-neutral');
        setText('cb_log_trace_notice', scenario.notice);
        setListPanel(scenario.list);
        page.querySelectorAll('[data-cb-log-item]').forEach(function (item) {
            const isTrace = item.getAttribute('data-cb-trace') === 'true';
            const severity = (item.getAttribute('data-cb-severity') || '').toLowerCase();
            item.classList.toggle('d-none', !scenario.trace && isTrace);
            if (name === 'critical') {
                item.classList.toggle('d-none', severity !== 'critical' && severity !== 'error');
            }
            if (name === 'mixed' || name === 'trace-on') {
                item.classList.remove('d-none');
            }
            if (name === 'trace-off' && isTrace) {
                item.classList.add('d-none');
            }
        });
    }

    function hydrateDrawer(id) {
        const d = detailMap[id]; if (!d) return;
        setText('cb_log_drawer_eyebrow', d.eyebrow);
        setText('cb_log_drawer_title', d.title);
        setBadge('cb_log_drawer_severity', d.severity[0], d.severity[1]);
        setBadge('cb_log_drawer_category', d.category[0], d.category[1]);
        setText('cb_log_drawer_trace', d.trace);
        setText('cb_log_drawer_time', d.time);
        setText('cb_log_drawer_source', d.source);
        setText('cb_log_drawer_message', d.message);
        setText('cb_log_drawer_tag1', d.tags[0]);
        setText('cb_log_drawer_tag2', d.tags[1]);
        setText('cb_log_drawer_tag3', d.tags[2]);
        setText('cb_log_drawer_action', d.action);
    }

    page.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-logcenter-scenario-trigger]');
        const detailTrigger = event.target.closest('[data-cb-log-detail]');
        const clearTrigger = event.target.closest('[data-cb-logcenter-filter-clear]');
        const refreshTrigger = event.target.closest('[data-cb-logcenter-refresh]');
        if (scenarioTrigger) { event.preventDefault(); applyScenario(scenarioTrigger.getAttribute('data-cb-logcenter-scenario-trigger')); return; }
        if (detailTrigger) { hydrateDrawer(detailTrigger.getAttribute('data-cb-log-detail')); return; }
        if (clearTrigger) {
            page.querySelectorAll('#cb_log_filter_category, #cb_log_filter_severity, #cb_log_filter_trace').forEach(function (select) { select.selectedIndex = 0; });
            return;
        }
        if (refreshTrigger) {
            applyScenario('loading');
            window.setTimeout(function(){ applyScenario('mixed'); }, 650);
        }
    });

    applyScenario(page.getAttribute('data-cb-logcenter-scenario') || 'mixed');
})();
