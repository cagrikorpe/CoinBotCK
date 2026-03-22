(function () {
    const page = document.querySelector('[data-cb-settings-page]');
    if (!page) {
        return;
    }

    const scenarios = {
        default: {
            panel: 'content',
            fullName: 'Operasyon Kullanıcısı',
            publicName: 'Operator',
            note: 'Trading operasyonlarını, risk yüzeylerini ve bildirim akışını tek merkezden izleyen kullanıcı profili.',
            theme: 'dark',
            timezone: 'Europe/Berlin',
            language: 'Türkçe',
            homepage: 'Dashboard',
            dashboardView: 'Operasyon görünümü',
            market: 'Spot',
            direction: 'Long only',
            risk: 'Orta',
            tradeMode: 'Paper',
            leverage: '3',
            channel: 'In-app',
            quietHours: '22:00 - 07:00',
            toggles: { compact: true, tooltips: true, ai: true, risk: true, bots: true, critical: true },
            twoFactor: { badge: ['2FA aktif', 'cb-badge cb-badge-success'], state: ['Aktif', 'cb-badge cb-badge-success'], method: 'Authenticator', notice: 'Authenticator yöntemi önerilir. Email fallback yalnızca geçici güvenlik katmanı olarak düşünülmelidir.' },
            trace: false,
            dirty: false
        },
        customized: {
            panel: 'content',
            fullName: 'Operasyon Kullanıcısı',
            publicName: 'Desk Alpha',
            note: 'Compact dashboard ve AI öncelikli bildirimleri tercih eden özelleştirilmiş kullanıcı görünümü.',
            theme: 'system',
            timezone: 'UTC',
            language: 'English',
            homepage: 'Paper Trading',
            dashboardView: 'AI odaklı görünüm',
            market: 'Futures',
            direction: 'Long + Short',
            risk: 'Yüksek',
            tradeMode: 'Paper',
            leverage: '6',
            channel: 'Telegram placeholder',
            quietHours: '23:00 - 06:30',
            toggles: { compact: true, tooltips: false, ai: true, risk: true, bots: false, critical: true },
            twoFactor: { badge: ['2FA aktif', 'cb-badge cb-badge-success'], state: ['Aktif', 'cb-badge cb-badge-success'], method: 'Authenticator', notice: 'Özelleştirilmiş kullanıcı profili 2FA ve kritik uyarıları görünür tutuyor.' },
            trace: true,
            dirty: false
        },
        security: {
            panel: 'content',
            fullName: 'Operasyon Kullanıcısı',
            publicName: 'Operator',
            note: 'Security review bekleyen kullanıcı görünümü.',
            theme: 'dark',
            timezone: 'Europe/Berlin',
            language: 'Türkçe',
            homepage: 'Dashboard',
            dashboardView: 'Operasyon görünümü',
            market: 'Spot',
            direction: 'Long only',
            risk: 'Orta',
            tradeMode: 'Paper',
            leverage: '2',
            channel: 'In-app',
            quietHours: '—',
            toggles: { compact: false, tooltips: true, ai: true, risk: true, bots: true, critical: true },
            twoFactor: { badge: ['2FA pasif', 'cb-badge cb-badge-danger'], state: ['Pasif', 'cb-badge cb-badge-danger'], method: 'Email placeholder', notice: '2FA pasif görünüyor. Security section daha görünür hale getirilir ve kullanıcıya kontrollü aksiyon CTA\'sı sunulur.' },
            trace: false,
            dirty: false
        },
        dirty: {
            panel: 'content',
            fullName: 'Operasyon Kullanıcısı',
            publicName: 'Operator',
            note: 'Kaydedilmemiş değişiklikler ile çalışan settings yüzeyi.',
            theme: 'light',
            timezone: 'UTC',
            language: 'Deutsch',
            homepage: 'Bildirimler',
            dashboardView: 'Kompakt görünüm',
            market: 'Futures',
            direction: 'Long + Short',
            risk: 'Yüksek',
            tradeMode: 'Live',
            leverage: '8',
            channel: 'Email placeholder',
            quietHours: '21:30 - 07:00',
            toggles: { compact: true, tooltips: false, ai: true, risk: true, bots: true, critical: true },
            twoFactor: { badge: ['2FA aktif', 'cb-badge cb-badge-success'], state: ['Aktif', 'cb-badge cb-badge-success'], method: 'Authenticator', notice: 'Trading ve notification tercihleri değişti. Persist olmadığı için save bar ile subtle warning gösterilir.' },
            trace: true,
            dirty: true
        },
        loading: { panel: 'loading' },
        error: { panel: 'error' }
    };

    function setValue(id, value) {
        const element = document.getElementById(id);
        if (!element) return;
        element.value = value;
    }

    function setText(id, value) {
        const element = document.getElementById(id);
        if (!element) return;
        element.textContent = value;
    }

    function setBadge(id, text, className) {
        const element = document.getElementById(id);
        if (!element) return;
        element.className = className;
        element.textContent = text;
    }

    function setChecked(id, checked) {
        const element = document.getElementById(id);
        if (!element) return;
        element.checked = checked;
    }

    function setPagePanel(name) {
        page.querySelectorAll('[data-cb-settings-page-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-settings-page-panel') !== name);
        });
    }

    function setThemeSelection(name) {
        page.querySelectorAll('[data-cb-settings-theme]').forEach(function (button) {
            button.classList.toggle('is-active', button.getAttribute('data-cb-settings-theme') === name);
        });
    }


    function updateTraceSection(enabled) {
        setBadge('cb_settings_trace_badge', enabled ? 'Açık' : 'Kapalı', enabled ? 'cb-badge cb-badge-success' : 'cb-badge cb-badge-neutral');
        document.getElementById('cb_settings_trace_notice')?.classList.toggle('d-none', !enabled);
        setChecked('cb_settings_trace_toggle', enabled);
    }

    function updateTradingWarning() {
        const market = document.getElementById('cb_settings_market_type')?.value || '';
        const direction = document.getElementById('cb_settings_trade_direction')?.value || '';
        const leverage = parseInt(document.getElementById('cb_settings_leverage')?.value || '0', 10);
        const warning = document.getElementById('cb_settings_trading_warning');
        const badge = document.getElementById('cb_settings_trading_badge');
        const aggressive = market === 'Futures' && direction === 'Long + Short' && leverage >= 6;

        if (warning) {
            warning.classList.toggle('d-none', !aggressive);
        }

        if (badge) {
            badge.className = aggressive ? 'cb-badge cb-badge-danger' : 'cb-badge cb-badge-warning';
            badge.textContent = aggressive ? 'Agresif varsayılanlar' : 'Dengeli varsayılanlar';
        }
    }

    function applyScenario(name) {
        const scenario = scenarios[name];
        if (!scenario) {
            return;
        }

        page.setAttribute('data-cb-settings-scenario', name);
        page.querySelectorAll('[data-cb-settings-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-settings-scenario-trigger') === name);
        });

        setPagePanel(scenario.panel);
        if (scenario.panel !== 'content') {
            return;
        }

        setValue('cb_settings_full_name', scenario.fullName);
        setValue('cb_settings_public_name', scenario.publicName);
        setValue('cb_settings_profile_note', scenario.note);
        setValue('cb_settings_timezone', scenario.timezone);
        setValue('cb_settings_language', scenario.language);
        setValue('cb_settings_homepage', scenario.homepage);
        setValue('cb_settings_dashboard_view', scenario.dashboardView);
        setValue('cb_settings_market_type', scenario.market);
        setValue('cb_settings_trade_direction', scenario.direction);
        setValue('cb_settings_risk_profile', scenario.risk);
        setValue('cb_settings_trade_mode', scenario.tradeMode);
        setValue('cb_settings_leverage', scenario.leverage);
        setValue('cb_settings_channel', scenario.channel);
        setValue('cb_settings_quiet_hours', scenario.quietHours);
        setText('cb_settings_display_name', scenario.publicName);

        setChecked('cb_settings_compact_mode', scenario.toggles.compact);
        setChecked('cb_settings_tooltips', scenario.toggles.tooltips);
        setChecked('cb_settings_notify_ai', scenario.toggles.ai);
        setChecked('cb_settings_notify_risk', scenario.toggles.risk);
        setChecked('cb_settings_notify_bots', scenario.toggles.bots);
        setChecked('cb_settings_notify_critical', scenario.toggles.critical);

        setThemeSelection(scenario.theme);
        setBadge('cb_settings_2fa_badge', scenario.twoFactor.badge[0], scenario.twoFactor.badge[1]);
        setBadge('cb_settings_2fa_state', scenario.twoFactor.state[0], scenario.twoFactor.state[1]);
        setBadge('cb_settings_2fa_method', scenario.twoFactor.method, 'cb-badge cb-badge-info');
        setText('cb_settings_security_notice', scenario.twoFactor.notice);
        updateTraceSection(!!scenario.trace);

        document.getElementById('cb_settings_savebar')?.classList.toggle('d-none', !scenario.dirty);
        updateTradingWarning();
    }

    page.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-settings-scenario-trigger]');
        const navLink = event.target.closest('[data-cb-settings-nav-link]');
        const themeButton = event.target.closest('[data-cb-settings-theme]');
        const saveTrigger = event.target.closest('[data-cb-settings-save]');
        const cancelTrigger = event.target.closest('[data-cb-settings-cancel]');

        if (scenarioTrigger) {
            event.preventDefault();
            applyScenario(scenarioTrigger.getAttribute('data-cb-settings-scenario-trigger'));
            return;
        }

        if (navLink) {
            page.querySelectorAll('[data-cb-settings-nav-link]').forEach(function (link) {
                link.classList.toggle('is-active', link === navLink);
            });
            return;
        }

        if (themeButton) {
            setThemeSelection(themeButton.getAttribute('data-cb-settings-theme'));
            document.getElementById('cb_settings_savebar')?.classList.remove('d-none');
            return;
        }

        if (saveTrigger || cancelTrigger) {
            document.getElementById('cb_settings_savebar')?.classList.add('d-none');
        }
    });

    ['cb_settings_market_type', 'cb_settings_trade_direction', 'cb_settings_leverage', 'cb_settings_trace_toggle'].forEach(function (id) {
        document.getElementById(id)?.addEventListener('change', function () {
            updateTradingWarning();
            document.getElementById('cb_settings_savebar')?.classList.remove('d-none');
        });
    });

    page.querySelectorAll('input, select, textarea').forEach(function (element) {
        element.addEventListener('change', function () {
            if (element.id === 'cb_settings_trace_toggle') {
                updateTraceSection(element.checked);
            }
            document.getElementById('cb_settings_savebar')?.classList.remove('d-none');
        });
    });

    applyScenario(page.getAttribute('data-cb-settings-scenario') || 'default');
})();
