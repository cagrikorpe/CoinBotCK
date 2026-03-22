(function () {
    const page = document.querySelector('[data-cb-bot-management]');
    if (!page) {
        return;
    }

    const bots = {
        alpha: {
            name: 'Alpha Spot Pulse',
            status: 'Aktif',
            statusClass: 'cb-badge-success',
            exchange: 'Binance',
            market: 'Spot',
            direction: 'Long only',
            risk: 'Düşük',
            leverage: '1x',
            mode: 'Paper',
            note: 'Spot piyasada daha kontrollü ilerleyen, düşük risk profilinde çalışan foundation bot örneği.',
            created: '21 Mar · 08:30',
            updated: '21 Mar · 09:42',
            lastRun: '09:42',
            strategy: 'Momentum Template',
            entryExit: 'Trend devam / ATR çıkış placeholder',
            ai: 'İsteğe bağlı',
            guard: 'Daily loss guard placeholder',
            positions: '1',
            signal: 'BTCUSDT · Long',
            action: 'Pozisyon artırma veto edildi',
            daily: '+1.8%',
            riskScore: '24 / 100',
            stop: 'Normal',
            warning: 'Bot sağlıklı görünür; risk kartı şu anda uyarı gerektirmiyor.',
            primaryActionTitle: 'Botu durdur',
            primaryActionMessage: 'Aktif bot placeholder olarak durdurma onayı ister. Gerçek runtime aksiyonu bu fazda bağlı değildir.',
            primaryActionConfirm: 'Botu durdur',
            form: {
                eyebrow: 'Edit Bot', title: 'Botu düzenle', name: 'Alpha Spot Pulse', note: 'Spot odaklı momentum denemesi',
                market: 'spot', direction: 'long', strategy: 'Momentum Template', risk: 'low', leverage: '1', mode: 'paper', start: true
            }
        },
        beta: {
            name: 'Beta Hedge Runner', status: 'Durduruldu', statusClass: 'cb-badge-neutral', exchange: 'Binance', market: 'Futures', direction: 'Long + Short', risk: 'Orta', leverage: '8x', mode: 'Paper',
            note: 'Futures hedge senaryolarını denemek için kullanılan, gerektiğinde yeniden başlatılabilecek foundation örnek bot.',
            created: '20 Mar · 18:05', updated: '21 Mar · 07:10', lastRun: '07:10', strategy: 'Hedge Template', entryExit: 'Dual side hedge / risk exit placeholder', ai: 'Düşük güven filtreli', guard: 'Futures exposure guard', positions: '0', signal: 'ETHUSDT · Watch', action: 'Bot manuel olarak durduruldu', daily: '-0.6%', riskScore: '47 / 100', stop: 'Manual stop', warning: 'Futures modunda olduğu için leverage ve direction kontrolleri tekrar gözden geçirilmeli.',
            primaryActionTitle: 'Botu başlat', primaryActionMessage: 'Durdurulmuş bot placeholder olarak yeniden başlatma onayı ister. Gerçek start işlemi bu fazda yoktur.', primaryActionConfirm: 'Botu başlat',
            form: {
                eyebrow: 'Edit Bot', title: 'Botu düzenle', name: 'Beta Hedge Runner', note: 'Futures hedge denemesi', market: 'futures', direction: 'both', strategy: 'Breakout Template', risk: 'medium', leverage: '8', mode: 'paper', start: false
            }
        },
        gamma: {
            name: 'Gamma Draft Scout', status: 'Taslak', statusClass: 'cb-badge-neutral', exchange: 'Binance', market: 'Spot', direction: 'Long only', risk: 'Seçilmedi', leverage: '1x', mode: 'Paper',
            note: 'Kurulum tamamlanmamış taslak bot. Strategy, risk ve çalışma notları henüz netleşmedi.', created: '21 Mar · 10:12', updated: '21 Mar · 10:12', lastRun: '--:--', strategy: 'Placeholder seçimi bekleniyor', entryExit: 'Kurulum tamamlanınca belirlenecek', ai: 'Henüz kapalı', guard: 'Henüz seçilmedi', positions: '0', signal: 'Yok', action: 'Kurulum bekleniyor', daily: '--', riskScore: '--', stop: 'Taslak', warning: 'Taslak bot çalıştırılamaz; önce strategy ve risk tercihi tamamlanmalıdır.',
            primaryActionTitle: 'Taslağı tamamla', primaryActionMessage: 'Taslak botu çalıştırmadan önce düzenleme akışı üzerinden alanları tamamlamalısın.', primaryActionConfirm: 'Düzenle',
            form: {
                eyebrow: 'Edit Draft', title: 'Taslak botu düzenle', name: 'Gamma Draft Scout', note: 'Kurulum eksik', market: 'spot', direction: 'long', strategy: 'Placeholder seçiniz', risk: 'low', leverage: '1', mode: 'paper', start: false
            }
        },
        delta: {
            name: 'Delta Futures Surge', status: 'Dikkat', statusClass: 'cb-badge-danger', exchange: 'Binance', market: 'Futures', direction: 'Long + Short', risk: 'Yüksek', leverage: '18x', mode: 'Live',
            note: 'Yüksek leverage nedeniyle riskli görünen, warning tonuna sahip foundation örnek bot.', created: '19 Mar · 12:20', updated: '21 Mar · 09:31', lastRun: '09:31', strategy: 'Breakout Template', entryExit: 'Breakout / volatility exit placeholder', ai: 'Yüksek güven veto kontrollü', guard: 'Exposure guard tetiklenmeye yakın', positions: '2', signal: 'SOLUSDT · Short', action: 'Leverage azalt önerisi üretildi', daily: '-2.4%', riskScore: '81 / 100', stop: 'Risk limitine yakın', warning: 'Futures + yüksek leverage kombinasyonu dikkat ister. Live modda devam etmeden önce risk merkezi ve exchange izinleri gözden geçirilmelidir.',
            primaryActionTitle: 'Botu durdur', primaryActionMessage: 'Riskli bot için placeholder stop onayı gösterilir. Gerçek runtime işlemi bu fazda bağlı değildir.', primaryActionConfirm: 'Botu durdur',
            form: {
                eyebrow: 'Edit Bot', title: 'Botu düzenle', name: 'Delta Futures Surge', note: 'Agresif breakout denemesi', market: 'futures', direction: 'both', strategy: 'Breakout Template', risk: 'high', leverage: '18', mode: 'live', start: true
            }
        }
    };

    const formModal = document.getElementById('cb_bot_form_modal');
    const detailDrawer = document.getElementById('cb_bot_detail_drawer');
    const statusEl = document.getElementById('cb_bot_drawer_status');

    function setScenario(scenario) {
        page.setAttribute('data-cb-bot-scenario', scenario);
        page.querySelectorAll('[data-cb-bot-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-bot-panel') !== scenario);
        });
        page.querySelectorAll('[data-cb-bot-scenario-trigger]').forEach(function (trigger) {
            trigger.classList.toggle('is-active', trigger.getAttribute('data-cb-bot-scenario-trigger') === scenario);
        });
    }

    function applyModeChoices(selected) {
        document.querySelectorAll('[data-cb-bot-mode-choice]').forEach(function (choice) {
            const input = choice.querySelector('input');
            choice.classList.toggle('is-active', input && input.value === selected);
        });
    }

    function applyFormState() {
        const market = document.getElementById('cb_bot_form_market')?.value;
        const risk = document.getElementById('cb_bot_form_risk')?.value;
        const leverageInput = document.getElementById('cb_bot_form_leverage');
        const leverageHelp = document.getElementById('cb_bot_form_leverage_help');
        const warning = document.getElementById('cb_bot_form_warning');
        const leverage = parseInt(leverageInput?.value || '1', 10);

        if (market === 'spot') {
            leverageInput?.setAttribute('disabled', 'disabled');
            if (leverageInput) leverageInput.value = '1';
            leverageHelp.textContent = 'Spot modunda leverage alanı pasif tutulur; futures seçildiğinde aktifleşir.';
        } else {
            leverageInput?.removeAttribute('disabled');
            leverageHelp.textContent = 'Futures modunda leverage alanı daha kritik görünür; yüksek değerler warning gösterebilir.';
        }

        if (warning) {
            warning.classList.toggle('d-none', !(market === 'futures' && (risk === 'high' || leverage >= 10)));
        }
    }

    function hydrateForm(mode, botId) {
        const modalEyebrow = document.getElementById('cb_bot_form_modal_eyebrow');
        const modalTitle = document.getElementById('cb_bot_form_modal_title');
        const info = document.getElementById('cb_bot_form_info');
        const submit = document.getElementById('cb_bot_form_submit');
        const preset = botId && bots[botId] ? bots[botId].form : null;

        if (mode === 'create') {
            modalEyebrow.textContent = 'Create Bot';
            modalTitle.textContent = 'Yeni bot oluştur';
            info.textContent = 'Gerçek persist akışı olmadan, yeni bot oluşturma foundation formu aynı create/edit standardını kullanır.';
            submit.textContent = 'Botu hazırla';
            document.getElementById('cb_bot_form_name').value = '';
            document.getElementById('cb_bot_form_note').value = '';
            document.getElementById('cb_bot_form_market').value = 'spot';
            document.getElementById('cb_bot_form_direction').value = 'long';
            document.getElementById('cb_bot_form_strategy').value = 'Momentum Template';
            document.getElementById('cb_bot_form_risk').value = 'low';
            document.getElementById('cb_bot_form_leverage').value = '1';
            document.getElementById('cb_bot_form_start_enabled').checked = false;
            document.querySelectorAll('input[name="cb_bot_form_mode"]').forEach(function (radio) {
                radio.checked = radio.value === 'paper';
            });
            applyModeChoices('paper');
        } else if (preset) {
            modalEyebrow.textContent = preset.eyebrow;
            modalTitle.textContent = preset.title;
            info.textContent = 'Mevcut bot özeti üstte tutulur; create ve edit aynı form standardını paylaşır.';
            submit.textContent = 'Değişiklikleri kaydet';
            document.getElementById('cb_bot_form_name').value = preset.name;
            document.getElementById('cb_bot_form_note').value = preset.note;
            document.getElementById('cb_bot_form_market').value = preset.market;
            document.getElementById('cb_bot_form_direction').value = preset.direction;
            document.getElementById('cb_bot_form_strategy').value = preset.strategy;
            document.getElementById('cb_bot_form_risk').value = preset.risk;
            document.getElementById('cb_bot_form_leverage').value = preset.leverage;
            document.getElementById('cb_bot_form_start_enabled').checked = preset.start;
            document.querySelectorAll('input[name="cb_bot_form_mode"]').forEach(function (radio) {
                radio.checked = radio.value === preset.mode;
            });
            applyModeChoices(preset.mode);
        }

        applyFormState();
    }

    function hydrateDrawer(botId) {
        const bot = bots[botId];
        if (!bot || !detailDrawer) {
            return;
        }

        document.getElementById('cb_bot_drawer_title').textContent = bot.name;
        statusEl.textContent = bot.status;
        statusEl.className = 'cb-badge ' + bot.statusClass;
        document.getElementById('cb_bot_drawer_exchange').textContent = bot.exchange;
        document.getElementById('cb_bot_drawer_market').textContent = bot.market;
        document.getElementById('cb_bot_drawer_direction').textContent = bot.direction;
        document.getElementById('cb_bot_drawer_risk').textContent = bot.risk;
        document.getElementById('cb_bot_drawer_leverage').textContent = bot.leverage;
        document.getElementById('cb_bot_drawer_mode').textContent = bot.mode;
        document.getElementById('cb_bot_drawer_note').textContent = bot.note;
        document.getElementById('cb_bot_drawer_created').textContent = bot.created;
        document.getElementById('cb_bot_drawer_updated').textContent = bot.updated;
        document.getElementById('cb_bot_drawer_last_run').textContent = bot.lastRun;
        document.getElementById('cb_bot_drawer_strategy').textContent = bot.strategy;
        document.getElementById('cb_bot_drawer_entry_exit').textContent = bot.entryExit;
        document.getElementById('cb_bot_drawer_ai').textContent = bot.ai;
        document.getElementById('cb_bot_drawer_guard').textContent = bot.guard;
        document.getElementById('cb_bot_drawer_positions').textContent = bot.positions;
        document.getElementById('cb_bot_drawer_signal').textContent = bot.signal;
        document.getElementById('cb_bot_drawer_action').textContent = bot.action;
        document.getElementById('cb_bot_drawer_daily').textContent = bot.daily;
        document.getElementById('cb_bot_drawer_risk_score').textContent = bot.riskScore;
        document.getElementById('cb_bot_drawer_risk_leverage').textContent = bot.leverage;
        document.getElementById('cb_bot_drawer_stop').textContent = bot.stop;
        document.getElementById('cb_bot_drawer_warning').textContent = bot.warning;

        const editBtn = document.getElementById('cb_bot_drawer_edit');
        const quickEditBtn = document.getElementById('cb_bot_drawer_quick_edit');
        editBtn.setAttribute('data-cb-bot-id', botId);
        editBtn.setAttribute('data-cb-bot-form', 'edit');
        quickEditBtn.setAttribute('data-cb-bot-id', botId);
        quickEditBtn.setAttribute('data-cb-bot-form', 'edit');

        const primaryBtn = document.getElementById('cb_bot_drawer_primary');
        const quickToggleBtn = document.getElementById('cb_bot_drawer_quick_toggle');
        [primaryBtn, quickToggleBtn].forEach(function (button) {
            button.setAttribute('data-cb-modal-title', bot.primaryActionTitle);
            button.setAttribute('data-cb-modal-message', bot.primaryActionMessage);
            button.setAttribute('data-cb-modal-confirm', bot.primaryActionConfirm);
        });
    }

    function resetDrawerTabs() {
        page.querySelectorAll('[data-cb-bot-tab-trigger]').forEach(function (trigger, index) {
            trigger.classList.toggle('is-active', index === 0);
        });
        page.querySelectorAll('[data-cb-bot-tab-panel]').forEach(function (panel, index) {
            panel.classList.toggle('d-none', index !== 0);
        });
    }

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-bot-scenario-trigger]');
        const formTrigger = event.target.closest('[data-cb-bot-form]');
        const detailTrigger = event.target.closest('[data-cb-bot-detail]');
        const tabTrigger = event.target.closest('[data-cb-bot-tab-trigger]');
        const modeChoice = event.target.closest('[data-cb-bot-mode-choice]');

        if (scenarioTrigger) {
            event.preventDefault();
            setScenario(scenarioTrigger.getAttribute('data-cb-bot-scenario-trigger'));
        }

        if (formTrigger) {
            const mode = formTrigger.getAttribute('data-cb-bot-form');
            const botId = formTrigger.getAttribute('data-cb-bot-id');
            hydrateForm(mode, botId);
        }

        if (detailTrigger) {
            hydrateDrawer(detailTrigger.getAttribute('data-cb-bot-detail'));
            resetDrawerTabs();
        }

        if (tabTrigger) {
            const tab = tabTrigger.getAttribute('data-cb-bot-tab-trigger');
            page.querySelectorAll('[data-cb-bot-tab-trigger]').forEach(function (item) {
                item.classList.toggle('is-active', item === tabTrigger);
            });
            page.querySelectorAll('[data-cb-bot-tab-panel]').forEach(function (panel) {
                panel.classList.toggle('d-none', panel.getAttribute('data-cb-bot-tab-panel') !== tab);
            });
        }

        if (modeChoice) {
            const input = modeChoice.querySelector('input');
            if (input) {
                input.checked = true;
                applyModeChoices(input.value);
            }
        }
    });

    ['change', 'input'].forEach(function (eventName) {
        document.addEventListener(eventName, function (event) {
            if (event.target && ['cb_bot_form_market', 'cb_bot_form_risk', 'cb_bot_form_leverage'].includes(event.target.id)) {
                applyFormState();
            }
        });
    });

    setScenario(page.getAttribute('data-cb-bot-scenario') || 'list');
    hydrateForm('create');
    hydrateDrawer('alpha');
    resetDrawerTabs();
})();
