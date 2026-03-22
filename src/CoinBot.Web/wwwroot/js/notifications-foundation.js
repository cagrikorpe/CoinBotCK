(function () {
    const page = document.querySelector('[data-cb-notifications-page]');
    if (!page) {
        return;
    }

    const filters = {
        severity: 'all',
        read: 'all',
        category: 'all'
    };

    const scenarios = {
        empty: {
            panel: 'content',
            list: 'empty',
            counts: ['0', '0', '0', '—', 'Sessiz akış', 'Henüz AI, risk veya bot durum bildirimi oluşmadı.'],
            defaults: { severity: 'all', read: 'all', category: 'all' }
        },
        mixed: {
            panel: 'content',
            list: 'list',
            counts: ['6', '3', '2', '09:42', 'Karışık akış aktif', 'AI, risk ve bot durum uyarıları aynı listede birlikte görünüyor.'],
            defaults: { severity: 'all', read: 'all', category: 'all' }
        },
        critical: {
            panel: 'content',
            list: 'list',
            counts: ['2', '2', '2', '09:42', 'Kritik yoğunluk', 'Risk odaklı yüksek öncelikli bildirimler filtrelenmiş durumda.'],
            defaults: { severity: 'critical', read: 'all', category: 'all' }
        },
        unread: {
            panel: 'content',
            list: 'list',
            counts: ['3', '3', '2', '09:42', 'Sadece okunmamış', 'Kullanıcının henüz gözden geçirmediği satırlar önceliklendirildi.'],
            defaults: { severity: 'all', read: 'unread', category: 'all' }
        },
        waiting: {
            panel: 'waiting',
            list: 'empty',
            counts: ['—', '—', '—', 'Bekleniyor', 'Veri bekleniyor', 'Notification producer katmanları geçici olarak veri bekliyor olabilir.'],
            defaults: { severity: 'all', read: 'all', category: 'all' }
        },
        loading: {
            panel: 'loading',
            list: 'loading',
            counts: ['—', '—', '—', 'Yükleniyor', 'Yükleniyor', 'Liste, severity ve meta alanları skeleton olarak hazırlanıyor.'],
            defaults: { severity: 'all', read: 'all', category: 'all' }
        },
        error: {
            panel: 'error',
            list: 'error',
            counts: ['—', '—', '—', '—', 'Akış alınamadı', 'Kullanıcıyı retry ve yardım akışına taşıyan hata yüzeyi gösterilir.'],
            defaults: { severity: 'all', read: 'all', category: 'all' }
        }
    };

    const details = {
        'risk-daily': {
            eyebrow: 'Risk Alert',
            title: 'Günlük zarar limitine yaklaşıldı',
            severity: ['Kritik', 'cb-badge cb-badge-danger'],
            category: ['Risk', 'cb-badge cb-badge-warning'],
            read: ['Okunmamış', 'cb-badge cb-badge-neutral'],
            time: '21 Mar · 09:42',
            module: 'Risk Merkezi',
            message: 'Günlük zarar limiti placeholder olarak %85 kullanım eşiğine yaklaştı. Kullanıcıya panik değil, kontrollü aksiyon öneren kısa ürün dili gösterilir.',
            tags: ['BTCUSDT', 'Daily limit', 'Risk approval'],
            action: 'Risk Merkezi ekranını açıp günlük limit davranışı ve max leverage ayarlarını tekrar gözden geçir.'
        },
        'ai-veto': {
            eyebrow: 'AI Alert',
            title: 'AVAXUSDT önerisi veto edildi',
            severity: ['Uyarı', 'cb-badge cb-badge-warning'],
            category: ['AI', 'cb-badge cb-badge-info'],
            read: ['Okunmamış', 'cb-badge cb-badge-neutral'],
            time: '21 Mar · 09:35',
            module: 'AI Robot',
            message: 'Düşük confidence ve yüksek risk notu sebebiyle öneri yalnızca watch düzeyinde bırakıldı. Ham log yerine açıklanabilir kısa özet verilir.',
            tags: ['AVAXUSDT', 'Confidence 41%', 'Veto'],
            action: 'AI Robot ekranını açıp explainability ve veto notlarını incele; risk merkezi ile bağlamı birlikte değerlendir.'
        },
        'bot-paused': {
            eyebrow: 'Bot Status',
            title: 'Beta Hedge Runner duraklatıldı',
            severity: ['Bilgi', 'cb-badge cb-badge-info'],
            category: ['Bot durumu', 'cb-badge cb-badge-success'],
            read: ['Okunmuş', 'cb-badge cb-badge-neutral'],
            time: '21 Mar · 08:58',
            module: 'Botlarım',
            message: 'Bot yeni sinyal bekleme moduna geçti. Duraklatma hata değilse kullanıcıya sade ve operasyonel bilgi tonu verilir.',
            tags: ['Beta Hedge Runner', 'Paused', 'Paper bot'],
            action: 'Botlarım ekranını açıp bot durumunu ve son sinyal alanını kontrol et.'
        },
        'risk-leverage': {
            eyebrow: 'Risk Alert',
            title: 'Yüksek leverage uyarısı',
            severity: ['Kritik', 'cb-badge cb-badge-danger'],
            category: ['Risk', 'cb-badge cb-badge-warning'],
            read: ['Okunmamış', 'cb-badge cb-badge-neutral'],
            time: '21 Mar · 08:41',
            module: 'Risk Merkezi',
            message: 'SOLUSDT tarafında yüksek leverage warning oluştu placeholder. Severity, modül ve coin etiketleriyle taranabilir bir detail yüzeyi sunulur.',
            tags: ['SOLUSDT', '8x leverage', 'Futures'],
            action: 'Risk Merkezi' + ' ekranında leverage limitini sıkılaştırmayı ve ilgili pozisyonu yeniden gözden geçirmeyi değerlendir.'
        },
        'ai-collecting': {
            eyebrow: 'AI Status',
            title: 'AI veri topluyor',
            severity: ['Bilgi', 'cb-badge cb-badge-info'],
            category: ['AI', 'cb-badge cb-badge-info'],
            read: ['Okunmuş', 'cb-badge cb-badge-neutral'],
            time: '21 Mar · 08:12',
            module: 'AI Robot',
            message: 'Model warming-up ve data collection aşamasındayken öneri akışının sınırlı olması normaldir. Kullanıcıya bunun geçici ara durum olduğu net anlatılır.',
            tags: ['Model v0.9.7', 'Collecting data', 'Low signal volume'],
            action: 'AI Robot ekranından model health ve son skor zamanlarını kontrol et; hemen aksiyon alma baskısı yaratma.'
        },
        'bot-signal': {
            eyebrow: 'Bot Status',
            title: 'Alpha Spot Pulse yeni sinyal üretti',
            severity: ['Uyarı', 'cb-badge cb-badge-warning'],
            category: ['Bot durumu', 'cb-badge cb-badge-success'],
            read: ['Okunmuş', 'cb-badge cb-badge-neutral'],
            time: '21 Mar · 07:54',
            module: 'Botlarım',
            message: 'Yeni setup üretildi placeholder. Execution veya live order detayı yerine bot durumu ve kısa neden açıklaması öne çıkarılır.',
            tags: ['Alpha Spot Pulse', 'BTCUSDT', 'New signal'],
            action: 'Botlarım ekranından bot detayını açıp strategy ve risk bağlamını kontrol et.'
        }
    };

    function text(id, value) {
        const el = document.getElementById(id);
        if (el) {
            el.textContent = value;
        }
    }

    function setClassText(id, value, className) {
        const el = document.getElementById(id);
        if (el) {
            el.className = className;
            el.textContent = value;
        }
    }

    function setPagePanel(name) {
        page.querySelectorAll('[data-cb-notifications-page-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-notifications-page-panel') !== name);
        });
    }

    function setListPanel(name) {
        page.querySelectorAll('[data-cb-notification-list-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-notification-list-panel') !== name);
        });
    }

    function updateFilterChips() {
        const severityMap = { all: 'Seviye: Tümü', critical: 'Seviye: Kritik', warning: 'Seviye: Uyarı', info: 'Seviye: Bilgi' };
        const readMap = { all: 'Okunma: Tümü', unread: 'Okunma: Okunmamış', read: 'Okunma: Okunmuş' };
        const categoryMap = { all: 'Kategori: Tümü', ai: 'Kategori: AI', risk: 'Kategori: Risk', bot: 'Kategori: Bot durumu' };
        text('cb_notifications_filter_chip_severity', severityMap[filters.severity]);
        text('cb_notifications_filter_chip_read', readMap[filters.read]);
        text('cb_notifications_filter_chip_category', categoryMap[filters.category]);
    }

    function applyFilters() {
        const items = Array.from(page.querySelectorAll('[data-cb-notification-item]'));
        let visibleCount = 0;
        let unreadCount = 0;
        let totalCount = 0;
        let criticalCount = 0;

        items.forEach(function (item) {
            totalCount += 1;
            if (item.getAttribute('data-cb-severity') === 'critical') {
                criticalCount += 1;
            }
            const matchesSeverity = filters.severity === 'all' || item.getAttribute('data-cb-severity') === filters.severity;
            const matchesRead = filters.read === 'all' || item.getAttribute('data-cb-read') === filters.read;
            const matchesCategory = filters.category === 'all' || item.getAttribute('data-cb-category') === filters.category;
            const isVisible = matchesSeverity && matchesRead && matchesCategory;
            item.classList.toggle('d-none', !isVisible);
            if (isVisible) {
                visibleCount += 1;
                if (item.getAttribute('data-cb-read') === 'unread') {
                    unreadCount += 1;
                }
            }
        });

        if (page.getAttribute('data-cb-notifications-scenario') === 'empty') {
            setListPanel('empty');
        } else if (page.getAttribute('data-cb-notifications-scenario') === 'loading') {
            setListPanel('loading');
        } else if (page.getAttribute('data-cb-notifications-scenario') === 'error') {
            setListPanel('error');
        } else {
            setListPanel(visibleCount === 0 ? 'empty' : 'list');
        }

        text('cb_notifications_list_badge', unreadCount + ' okunmamış');
        text('cb_notifications_total', String(totalCount));
        text('cb_notifications_unread', String(items.filter(function (i) { return i.getAttribute('data-cb-read') === 'unread'; }).length));
        text('cb_notifications_critical', String(criticalCount));
        updateFilterChips();
    }

    function applyScenario(name) {
        const scenario = scenarios[name];
        if (!scenario) {
            return;
        }

        page.setAttribute('data-cb-notifications-scenario', name);
        page.querySelectorAll('[data-cb-notifications-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-notifications-scenario-trigger') === name);
        });

        setPagePanel(scenario.panel);
        setListPanel(scenario.list);

        filters.severity = scenario.defaults.severity;
        filters.read = scenario.defaults.read;
        filters.category = scenario.defaults.category;

        page.querySelector('[data-cb-notifications-filter-group="severity"]').value = filters.severity;
        page.querySelector('[data-cb-notifications-filter-group="read"]').value = filters.read;
        page.querySelector('[data-cb-notifications-filter-group="category"]').value = filters.category;

        text('cb_notifications_total', scenario.counts[0]);
        text('cb_notifications_unread', scenario.counts[1]);
        text('cb_notifications_critical', scenario.counts[2]);
        text('cb_notifications_last_time', scenario.counts[3]);
        text('cb_notifications_status', scenario.counts[4]);
        text('cb_notifications_status_note', scenario.counts[5]);

        applyFilters();
    }

    function setDetail(key) {
        const detail = details[key];
        if (!detail) {
            return;
        }

        text('cb_notifications_drawer_eyebrow', detail.eyebrow);
        text('cb_notifications_drawer_title', detail.title);
        setClassText('cb_notifications_drawer_severity', detail.severity[0], detail.severity[1]);
        setClassText('cb_notifications_drawer_category', detail.category[0], detail.category[1]);
        setClassText('cb_notifications_drawer_read', detail.read[0], detail.read[1]);
        text('cb_notifications_drawer_time', detail.time);
        text('cb_notifications_drawer_module', detail.module);
        text('cb_notifications_drawer_message', detail.message);
        text('cb_notifications_drawer_tag1', detail.tags[0]);
        text('cb_notifications_drawer_tag2', detail.tags[1]);
        text('cb_notifications_drawer_tag3', detail.tags[2]);
        text('cb_notifications_drawer_action', detail.action);
        text('cb_notifications_drawer_cta', detail.module + ' ekranına git');
    }

    function markRead(key) {
        const item = page.querySelector('[data-cb-id="' + key + '"]');
        if (!item) {
            return;
        }

        item.setAttribute('data-cb-read', 'read');
        item.classList.remove('is-unread');
        const readBadge = item.querySelector('.cb-inline-stack .cb-badge:last-child');
        if (readBadge) {
            readBadge.textContent = 'Okunmuş';
        }
        applyFilters();
    }

    function markAllRead() {
        page.querySelectorAll('[data-cb-notification-item]').forEach(function (item) {
            item.setAttribute('data-cb-read', 'read');
            item.classList.remove('is-unread');
            const readBadge = item.querySelector('.cb-inline-stack .cb-badge:last-child');
            if (readBadge) {
                readBadge.textContent = 'Okunmuş';
            }
        });
        applyFilters();
    }

    function setButtonLoading(button, active) {
        if (!button) return;
        button.classList.toggle('is-loading', !!active);
        button.disabled = !!active;
    }

    document.addEventListener('change', function (event) {
        const select = event.target.closest('[data-cb-notifications-filter-group]');
        if (!select) {
            return;
        }

        filters[select.getAttribute('data-cb-notifications-filter-group')] = select.value;
        applyFilters();
    });

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-notifications-scenario-trigger]');
        const detailTrigger = event.target.closest('[data-cb-notification-detail]');
        const markReadTrigger = event.target.closest('[data-cb-notification-mark-read]');
        const markAllTrigger = event.target.closest('[data-cb-notifications-mark-all]');
        const clearFiltersTrigger = event.target.closest('[data-cb-notifications-clear-filters]');
        const refreshTrigger = event.target.closest('[data-cb-notifications-refresh]');

        if (scenarioTrigger) {
            event.preventDefault();
            applyScenario(scenarioTrigger.getAttribute('data-cb-notifications-scenario-trigger'));
        }

        if (detailTrigger) {
            setDetail(detailTrigger.getAttribute('data-cb-notification-detail'));
        }

        if (markReadTrigger) {
            event.preventDefault();
            markRead(markReadTrigger.getAttribute('data-cb-notification-mark-read'));
        }

        if (markAllTrigger) {
            event.preventDefault();
            markAllRead();
        }

        if (clearFiltersTrigger) {
            event.preventDefault();
            filters.severity = 'all';
            filters.read = 'all';
            filters.category = 'all';
            page.querySelector('[data-cb-notifications-filter-group="severity"]').value = 'all';
            page.querySelector('[data-cb-notifications-filter-group="read"]').value = 'all';
            page.querySelector('[data-cb-notifications-filter-group="category"]').value = 'all';
            applyFilters();
        }

        if (refreshTrigger) {
            event.preventDefault();
            setButtonLoading(refreshTrigger, true);
            applyScenario('loading');
            window.setTimeout(function () {
                setButtonLoading(refreshTrigger, false);
                applyScenario('mixed');
            }, 700);
        }
    });

    setDetail('risk-daily');
    applyScenario('mixed');
})();
