(function () {
    const page = document.querySelector('[data-cb-strategy-builder]');
    if (!page) {
        return;
    }

    const templateCards = Array.from(page.querySelectorAll('[data-cb-template-card]'));
    const templateDraftInput = page.querySelector('[data-cb-template-draft-template]');
    const templates = Object.fromEntries(templateCards.map(function (card) {
        return [card.getAttribute('data-cb-template-key'), {
            key: card.getAttribute('data-cb-template-key'),
            name: card.getAttribute('data-cb-template-name'),
            risk: card.getAttribute('data-cb-template-risk'),
            market: card.getAttribute('data-cb-template-market'),
            tag: card.getAttribute('data-cb-template-tag'),
            description: card.getAttribute('data-cb-template-description')
        }];
    }));

    let selectedTemplate = 'blank';

    function text(id, value) {
        const el = document.getElementById(id);
        if (el) {
            el.textContent = value;
        }
    }

    function setTemplate(key) {
        if (!templates[key]) {
            return;
        }

        selectedTemplate = key;
        templateCards.forEach(function (card) {
            card.classList.toggle('is-selected', card.getAttribute('data-cb-template-key') === key);
        });

        const template = templates[key];
        if (templateDraftInput) {
            templateDraftInput.value = key;
        }
        text('cb_strategy_preview_template', template.name);
        text('cb_strategy_drawer_title', template.name);
        text('cb_strategy_drawer_description', template.description);
        text('cb_strategy_drawer_market', template.market);
        text('cb_strategy_drawer_risk', template.risk);
        text('cb_strategy_drawer_tag', template.tag);

        if (key === 'blank') {
            text('cb_strategy_name', 'Yeni Strategy Draft');
            text('cb_strategy_preview_description', 'Bu strateji için henüz template seçilmedi. İlk kuralını ekleyerek giriş/çıkış/risk mantığını doldurabilirsin.');
        } else {
            text('cb_strategy_name', template.name + ' Strategy');
            text('cb_strategy_preview_description', template.description + ' Builder alanı template seçimine göre örnek bloklarla ön doldurulmuş gibi davranır.');
        }
    }

    function setPanel(panel) {
        page.querySelectorAll('[data-cb-strategy-panel]').forEach(function (item) {
            item.classList.toggle('d-none', item.getAttribute('data-cb-strategy-panel') !== panel);
        });
    }

    function setScenario(scenario) {
        page.setAttribute('data-cb-strategy-scenario', scenario);
        page.querySelectorAll('[data-cb-strategy-scenario-trigger]').forEach(function (trigger) {
            trigger.classList.toggle('is-active', trigger.getAttribute('data-cb-strategy-scenario-trigger') === scenario);
        });

        const effectivePanel = ['blank', 'loading', 'error'].includes(scenario) ? scenario : 'template';
        setPanel(effectivePanel);

        const isFutures = scenario === 'futures' || scenario === 'risky';
        const isIncomplete = scenario === 'incomplete';
        const isRisky = scenario === 'risky';

        page.setAttribute('data-cb-market-mode', isFutures ? 'futures' : 'spot');
        document.getElementById('cb_short_rules_section')?.classList.toggle('is-secondary', !isFutures);
        const shortToggle = document.getElementById('cb_short_rules_toggle');
        if (shortToggle) {
            shortToggle.checked = isFutures;
        }

        const leverageInput = document.getElementById('cb_strategy_leverage');
        const stopLossInput = document.getElementById('cb_strategy_stop_loss');

        if (leverageInput) {
            leverageInput.value = isRisky ? '12' : isFutures ? '6' : '1';
        }

        if (stopLossInput) {
            stopLossInput.value = isRisky ? '4.8%' : isIncomplete ? '' : '1.1%';
        }

        text('cb_strategy_market_summary', isFutures ? 'Futures · Long + Short' : 'Spot · Long only');
        text('cb_strategy_preview_market', isFutures ? 'Futures' : 'Spot');
        text('cb_strategy_preview_direction', isFutures ? 'Long + Short' : 'Long only');
        text('cb_strategy_status_summary', isIncomplete || isRisky ? 'Dikkat gerekiyor' : scenario === 'template' || scenario === 'futures' ? 'Hazır' : 'Taslak');
        text('cb_strategy_preview_entry', isIncomplete ? '0 placeholder' : isFutures ? '4 placeholder' : selectedTemplate === 'blank' ? '0 placeholder' : '3 placeholder');
        text('cb_strategy_preview_exit', isIncomplete ? '0 placeholder' : isRisky ? '1 placeholder' : '2 placeholder');
        text('cb_strategy_preview_risk', isRisky ? 'Agresif' : isFutures ? 'Dinamik' : 'Dengeli');

        document.getElementById('cb_strategy_preview_warning')?.classList.toggle('d-none', !(isIncomplete || isRisky));
        document.getElementById('cb_strategy_exit_warning')?.classList.toggle('d-none', !isRisky && !isIncomplete);
        document.getElementById('cb_strategy_risk_warning')?.classList.toggle('d-none', !isRisky && !isFutures);

        if (scenario === 'template' && selectedTemplate === 'blank') {
            setTemplate('ema');
        }
        if (scenario === 'futures') {
            setTemplate('trend');
        }
        if (scenario === 'risky') {
            setTemplate('breakout');
        }
        if (scenario === 'blank') {
            setTemplate('blank');
        }
        if (scenario === 'incomplete') {
            setTemplate(selectedTemplate === 'blank' ? 'ema' : selectedTemplate);
        }
    }

    function resetBuilder() {
        setTemplate('blank');
        setScenario('blank');
    }

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-strategy-scenario-trigger]');
        const templateCard = event.target.closest('[data-cb-template-card]');
        const newStrategyTrigger = event.target.closest('[data-cb-strategy-action="new"]');
        const applyTemplate = event.target.closest('[data-cb-strategy-apply-template]');

        if (scenarioTrigger) {
            event.preventDefault();
            setScenario(scenarioTrigger.getAttribute('data-cb-strategy-scenario-trigger'));
        }

        if (templateCard) {
            event.preventDefault();
            setTemplate(templateCard.getAttribute('data-cb-template-key'));
            if (templateCard.getAttribute('data-cb-template-key') === 'blank') {
                setScenario('blank');
            } else {
                setScenario('template');
            }
        }

        if (newStrategyTrigger) {
            event.preventDefault();
            resetBuilder();
        }

        if (applyTemplate) {
            event.preventDefault();
            if (selectedTemplate === 'blank') {
                setScenario('blank');
            } else {
                setScenario('template');
            }
        }
    });

    ['change', 'input'].forEach(function (eventName) {
        document.addEventListener(eventName, function (event) {
            if (event.target && event.target.id === 'cb_strategy_leverage') {
                const value = parseInt(event.target.value || '1', 10);
                const risky = value >= 10 || page.getAttribute('data-cb-strategy-scenario') === 'risky';
                document.getElementById('cb_strategy_risk_warning')?.classList.toggle('d-none', !risky);
                text('cb_strategy_preview_risk', risky ? 'Agresif' : page.getAttribute('data-cb-market-mode') === 'futures' ? 'Dinamik' : 'Dengeli');
            }

            if (event.target && event.target.id === 'cb_strategy_stop_loss') {
                const raw = (event.target.value || '').replace('%', '').replace(',', '.');
                const value = parseFloat(raw || '0');
                document.getElementById('cb_strategy_exit_warning')?.classList.toggle('d-none', !(value === 0 || value >= 4));
            }
        });
    });

    setTemplate('blank');
    setScenario('blank');
})();
