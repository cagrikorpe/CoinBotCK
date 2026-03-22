(function () {
    const scenarios = {
        first: {
            result: 'pending',
            summary: ['Henüz test edilmedi', 'neutral'],
            permissions: {
                spot: ['Doğrulanmadı', 'neutral'],
                futures: ['Pasif', 'neutral'],
                read: ['Doğrulanmadı', 'neutral'],
                withdrawal: ['Kapalı olmalı', 'warning'],
                ip: ['Önerilir', 'info'],
                secret: ['Henüz test edilmedi', 'neutral']
            },
            freshness: {
                test: ['Henüz test yok', 'Bekliyor', 'neutral'],
                account: ['Henüz senkron yok', 'Bekliyor', 'neutral'],
                permissions: ['Henüz kontrol yok', 'Bekliyor', 'neutral'],
                market: ['Henüz veri yok', 'Bekliyor', 'neutral']
            }
        },
        success: {
            result: 'success',
            summary: ['Uygun', 'success'],
            permissions: {
                spot: ['Uygun', 'success'],
                futures: ['Uygun', 'success'],
                read: ['Uygun', 'success'],
                withdrawal: ['Kapalı', 'success'],
                ip: ['Whitelist açık', 'info'],
                secret: ['Doğrulandı', 'success']
            },
            freshness: {
                test: ['Az önce', 'Aktif', 'success'],
                account: ['1 dk önce', 'Aktif', 'success'],
                permissions: ['Az önce', 'Aktif', 'success'],
                market: ['30 sn önce', 'Aktif', 'success']
            }
        },
        missing: {
            result: 'partial',
            summary: ['Eksik permission', 'warning'],
            permissions: {
                spot: ['Uygun', 'success'],
                futures: ['Eksik', 'warning'],
                read: ['Uygun', 'success'],
                withdrawal: ['Kapalı', 'success'],
                ip: ['Önerilir', 'info'],
                secret: ['Doğrulandı', 'success']
            },
            freshness: {
                test: ['Az önce', 'Kontrol edildi', 'info'],
                account: ['2 dk önce', 'Aktif', 'success'],
                permissions: ['Az önce', 'Warning', 'warning'],
                market: ['1 dk önce', 'Aktif', 'success']
            }
        },
        risky: {
            result: 'partial',
            summary: ['Riskli yapı', 'danger'],
            permissions: {
                spot: ['Uygun', 'success'],
                futures: ['Uygun', 'success'],
                read: ['Uygun', 'success'],
                withdrawal: ['Riskli', 'danger'],
                ip: ['Whitelist yok', 'warning'],
                secret: ['Doğrulandı', 'success']
            },
            freshness: {
                test: ['Az önce', 'Kontrol edildi', 'info'],
                account: ['1 dk önce', 'Aktif', 'success'],
                permissions: ['Az önce', 'Risk uyarısı', 'danger'],
                market: ['45 sn önce', 'Aktif', 'success']
            }
        },
        stale: {
            result: 'error',
            summary: ['Stale bağlantı', 'warning'],
            permissions: {
                spot: ['Son durum bilinmiyor', 'warning'],
                futures: ['Son durum bilinmiyor', 'warning'],
                read: ['Son durum bilinmiyor', 'warning'],
                withdrawal: ['Kontrol edilmeli', 'warning'],
                ip: ['Tekrar gözden geçir', 'info'],
                secret: ['Yeniden test et', 'neutral']
            },
            freshness: {
                test: ['3 saat önce', 'Stale', 'warning'],
                account: ['2 saat önce', 'Stale', 'warning'],
                permissions: ['3 saat önce', 'Stale', 'warning'],
                market: ['47 dk önce', 'Eski', 'warning']
            }
        }
    };

    function setButtonLoading(button, isLoading) {
        if (!button) return;
        button.classList.toggle('is-loading', isLoading);
        button.toggleAttribute('disabled', isLoading);
    }

    function updateChoiceState(input) {
        if (!input || !input.name) return;
        const scope = input.closest('form') || document;
        scope.querySelectorAll('input[name="' + input.name + '"]').forEach(function (item) {
            item.closest('.cb-choice-pill')?.classList.toggle('is-selected', item.checked);
        });
    }

    function setBadgeTone(element, tone) {
        if (!element) return;
        element.classList.remove('cb-badge-success', 'cb-badge-warning', 'cb-badge-danger', 'cb-badge-info', 'cb-badge-neutral');
        element.classList.add('cb-badge-' + (tone || 'neutral'));
    }

    function activateResultState(root, state) {
        root.querySelectorAll('[data-cb-result-panel]').forEach(function (panel) {
            panel.classList.toggle('is-active', panel.getAttribute('data-cb-result-panel') === state);
        });

        root.querySelectorAll('[data-cb-exchange-result-trigger]').forEach(function (trigger) {
            trigger.classList.toggle('is-active', trigger.getAttribute('data-cb-exchange-result-trigger') === state);
        });
    }

    function applyTradeModeVisibility(root) {
        const selected = root.querySelector('input[name="cb_trade_mode"]:checked')?.value || 'spot';
        const futuresRow = root.querySelector('[data-cb-permission="futures"]');
        if (!futuresRow) return;
        futuresRow.classList.toggle('is-secondary', selected === 'spot');
    }

    function syncScenarioChips(root, key) {
        root.querySelectorAll('[data-cb-exchange-scenario-trigger]').forEach(function (trigger) {
            trigger.classList.toggle('is-active', trigger.getAttribute('data-cb-exchange-scenario-trigger') === key);
        });
    }

    function applyScenario(root, key) {
        const scenario = scenarios[key] || scenarios.first;
        root.setAttribute('data-cb-exchange-scenario', key);
        syncScenarioChips(root, key);

        const summary = root.querySelector('[data-cb-permission-summary]');
        if (summary) {
            summary.textContent = scenario.summary[0];
            setBadgeTone(summary, scenario.summary[1]);
        }

        Object.entries(scenario.permissions).forEach(function ([name, value]) {
            const row = root.querySelector('[data-cb-permission="' + name + '"]');
            const badge = row?.querySelector('[data-cb-permission-badge]');
            if (!badge) return;
            badge.textContent = value[0];
            setBadgeTone(badge, value[1]);
            row.classList.toggle('is-risk', value[1] === 'danger');
        });

        Object.entries(scenario.freshness).forEach(function ([name, value]) {
            const item = root.querySelector('[data-cb-freshness-item="' + name + '"]');
            const time = item?.querySelector('[data-cb-freshness-time]');
            const badge = item?.querySelector('[data-cb-freshness-badge]');
            if (time) time.textContent = value[0];
            if (badge) {
                badge.textContent = value[1];
                setBadgeTone(badge, value[2]);
            }
            item?.classList.toggle('is-warning', value[2] === 'warning' || value[2] === 'danger');
        });

        activateResultState(root, scenario.result);
        applyTradeModeVisibility(root);
    }

    function toggleSecret(trigger) {
        const selector = trigger.getAttribute('data-cb-password-target');
        const target = selector ? document.querySelector(selector) : null;
        if (!target) return;
        const show = target.type === 'password';
        target.type = show ? 'text' : 'password';
        trigger.textContent = show ? 'Gizle' : 'Göster';
    }

    function updateModeHelper(root) {
        const mode = root.querySelector('input[name="cb_mode"]:checked')?.value || 'paper';
        const helper = root.querySelector('[data-cb-mode-helper]');
        if (!helper) return;
        helper.textContent = mode === 'live'
            ? 'Live mod daha dikkatli doğrulama gerektirir; permission check ve IP whitelist tamamlanmadan ilerlemeyin.'
            : 'Paper mode ile başlayıp izinleri doğruladıktan sonra live moda geçin.';
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('[data-cb-exchange-connect]').forEach(function (root) {
            applyScenario(root, root.getAttribute('data-cb-exchange-scenario') || 'first');
            updateModeHelper(root);
            root.querySelectorAll('input[type="radio"]').forEach(updateChoiceState);
        });
    });

    document.addEventListener('click', function (event) {
        const root = event.target.closest('[data-cb-exchange-connect]');
        if (!root) return;

        const scenarioTrigger = event.target.closest('[data-cb-exchange-scenario-trigger]');
        if (scenarioTrigger) {
            event.preventDefault();
            applyScenario(root, scenarioTrigger.getAttribute('data-cb-exchange-scenario-trigger'));
            return;
        }

        const resultTrigger = event.target.closest('[data-cb-exchange-result-trigger]');
        if (resultTrigger) {
            event.preventDefault();
            activateResultState(root, resultTrigger.getAttribute('data-cb-exchange-result-trigger'));
            return;
        }

        const secretToggle = event.target.closest('[data-cb-exchange-secret-toggle]');
        if (secretToggle) {
            event.preventDefault();
            toggleSecret(secretToggle);
            return;
        }

        const testButton = event.target.closest('[data-cb-exchange-test], [data-cb-exchange-test-inline]');
        if (testButton) {
            event.preventDefault();
            setButtonLoading(testButton, true);
            window.setTimeout(function () {
                setButtonLoading(testButton, false);
                applyScenario(root, 'success');
            }, 850);
            return;
        }

        const saveButton = event.target.closest('[data-cb-exchange-save]');
        if (saveButton) {
            event.preventDefault();
            setButtonLoading(saveButton, true);
            window.setTimeout(function () {
                setButtonLoading(saveButton, false);
                activateResultState(root, 'success');
            }, 650);
            return;
        }

        const paperContinue = event.target.closest('[data-cb-paper-continue]');
        if (paperContinue) {
            event.preventDefault();
            applyScenario(root, 'first');
        }
    });

    document.addEventListener('change', function (event) {
        const input = event.target;
        const root = input.closest('[data-cb-exchange-connect]');
        if (!root) return;

        if (input.matches('input[name="cb_mode"], input[name="cb_trade_mode"]')) {
            updateChoiceState(input);
            updateModeHelper(root);
            applyTradeModeVisibility(root);
        }
    });
})();
