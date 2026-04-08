(function () {
    const page = document.querySelector('[data-cb-strategy-builder]');
    if (!page) {
        return;
    }

    const templateCards = Array.from(page.querySelectorAll('[data-cb-template-card]'));
    const templateDraftInput = page.querySelector('[data-cb-template-draft-template]');
    const templateDraftSubmit = page.querySelector('[data-cb-template-draft-submit]');
    const templateDraftTarget = page.querySelector('[data-cb-template-draft-target]');
    const templateSelectionSummary = page.querySelector('[data-cb-template-selection-summary]');
    const templateTargetSummary = page.querySelector('[data-cb-template-target-summary]');
    const templates = Object.fromEntries(templateCards.map(function (card) {
        return [card.getAttribute('data-cb-template-key'), {
            key: card.getAttribute('data-cb-template-key'),
            name: card.getAttribute('data-cb-template-name'),
            risk: card.getAttribute('data-cb-template-risk'),
            market: card.getAttribute('data-cb-template-market'),
            tag: card.getAttribute('data-cb-template-tag'),
            validation: card.getAttribute('data-cb-template-validation') || 'Unknown',
            source: card.getAttribute('data-cb-template-source') || 'Unknown',
            currentRevision: card.getAttribute('data-cb-template-current-revision') || '0',
            latestRevision: card.getAttribute('data-cb-template-latest-revision') || '0',
            publishedRevision: card.getAttribute('data-cb-template-published-revision') || '0',
            description: card.getAttribute('data-cb-template-description') || ''
        }];
    }));

    let selectedTemplate = null;

    function text(id, value) {
        const el = document.getElementById(id);
        if (el) {
            el.textContent = value;
        }
    }

    function syncCloneForm() {
        const hasSelection = !!selectedTemplate && !!templates[selectedTemplate];
        const hasTarget = !!(templateDraftTarget && templateDraftTarget.value);

        if (templateDraftInput) {
            templateDraftInput.value = hasSelection ? selectedTemplate : '';
        }

        if (templateDraftSubmit) {
            templateDraftSubmit.disabled = !(hasSelection && hasTarget);
        }

        if (templateSelectionSummary) {
            templateSelectionSummary.textContent = hasSelection
                ? "Secilen template published revision'dan hedef strategy altinda bagimsiz draft version uretir."
                : 'Once bir template sec. Clone sonucu hedef strategy altinda bagimsiz draft version olusur.';
        }

        if (templateTargetSummary) {
            const selectedTarget = templateDraftTarget && templateDraftTarget.selectedOptions.length > 0
                ? templateDraftTarget.selectedOptions[0].textContent
                : 'Hedef strategy secilmedi';
            templateTargetSummary.textContent = "Hedef strategy: " + selectedTarget + ". Clone yalniz kullanici scope'undaki strategy kayitlarina yazilir.";
        }
    }

    function setTemplate(key) {
        if (!key || !templates[key]) {
            selectedTemplate = null;
            templateCards.forEach(function (card) {
                card.classList.remove('is-selected');
            });
            text('cb_strategy_preview_template', 'Template secilmedi');
            text('cb_strategy_drawer_title', 'Template secilmedi');
            text('cb_strategy_drawer_description', 'Published template secildiginde hedef strategy altinda bagimsiz draft version uretilir. Template ile canli referans kurulmaz.');
            text('cb_strategy_drawer_market', 'Spot / Futures');
            text('cb_strategy_drawer_risk', 'Secim bekleniyor');
            text('cb_strategy_drawer_tag', 'Catalog');
            text('cb_strategy_drawer_current_revision', 'Current revision: n/a');
            text('cb_strategy_drawer_published_revision', 'Published revision: n/a');
            text('cb_strategy_drawer_clone_surface', 'Clone source: latest published revision only');
            text('cb_strategy_name', 'Bagimsiz Draft');
            text('cb_strategy_preview_description', 'Template secilince source revision ve validation bilgisi korunarak yeni bir draft version olusturulur.');
            syncCloneForm();
            return;
        }

        selectedTemplate = key;
        templateCards.forEach(function (card) {
            card.classList.toggle('is-selected', card.getAttribute('data-cb-template-key') === key);
        });

        const template = templates[key];
        text('cb_strategy_preview_template', template.name);
        text('cb_strategy_drawer_title', template.name);
        text('cb_strategy_drawer_description', template.description);
        text('cb_strategy_drawer_market', template.market);
        text('cb_strategy_drawer_risk', template.risk);
        text('cb_strategy_drawer_tag', template.tag);
        text('cb_strategy_drawer_current_revision', 'Current revision: r' + template.currentRevision);
        text('cb_strategy_drawer_published_revision', 'Published revision: r' + template.publishedRevision);
        text('cb_strategy_drawer_clone_surface', 'Clone source: published r' + template.publishedRevision + ' · Validation=' + template.validation + ' · Source=' + template.source);
        text('cb_strategy_name', template.name + ' Draft');
        text('cb_strategy_preview_description', template.description + ' Secili target strategy altinda bagimsiz bir draft version olusturulur.');

        syncCloneForm();
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
        text('cb_strategy_status_summary', selectedTemplate
            ? (isIncomplete || isRisky ? 'Dikkat gerekiyor' : 'Clone icin hazir')
            : 'Template secimi bekleniyor');
        text('cb_strategy_preview_entry', selectedTemplate ? 'Template preset' : 'Secim bekleniyor');
        text('cb_strategy_preview_exit', selectedTemplate ? 'Template preset' : 'Secim bekleniyor');
        text('cb_strategy_preview_risk', selectedTemplate ? (isRisky ? 'Agresif' : isFutures ? 'Dinamik' : 'Dengeli') : 'Secim bekleniyor');

        document.getElementById('cb_strategy_preview_warning')?.classList.toggle('d-none', !(isIncomplete || isRisky || !selectedTemplate));
        document.getElementById('cb_strategy_exit_warning')?.classList.toggle('d-none', !isRisky && !isIncomplete);
        document.getElementById('cb_strategy_risk_warning')?.classList.toggle('d-none', !isRisky && !isFutures);
    }

    function resetBuilder() {
        setTemplate(null);
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
            setScenario('template');
        }

        if (newStrategyTrigger) {
            event.preventDefault();
            resetBuilder();
        }

        if (applyTemplate) {
            event.preventDefault();
            setScenario(selectedTemplate ? 'template' : 'blank');
        }
    });

    ['change', 'input'].forEach(function (eventName) {
        document.addEventListener(eventName, function (event) {
            if (event.target && event.target.id === 'cb_strategy_template_draft_target') {
                syncCloneForm();
            }

            if (event.target && event.target.id === 'cb_strategy_leverage') {
                const value = parseInt(event.target.value || '1', 10);
                const risky = value >= 10 || page.getAttribute('data-cb-strategy-scenario') === 'risky';
                document.getElementById('cb_strategy_risk_warning')?.classList.toggle('d-none', !risky);
                text('cb_strategy_preview_risk', selectedTemplate ? (risky ? 'Agresif' : page.getAttribute('data-cb-market-mode') === 'futures' ? 'Dinamik' : 'Dengeli') : 'Secim bekleniyor');
            }

            if (event.target && event.target.id === 'cb_strategy_stop_loss') {
                const raw = (event.target.value || '').replace('%', '').replace(',', '.');
                const value = parseFloat(raw || '0');
                document.getElementById('cb_strategy_exit_warning')?.classList.toggle('d-none', !(value === 0 || value >= 4));
            }
        });
    });

    setTemplate(null);
    setScenario('blank');
    syncCloneForm();
})();

