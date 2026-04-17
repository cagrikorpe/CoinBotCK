(function () {
    const page = document.querySelector('[data-cb-strategy-builder]');
    if (!page) {
        return;
    }

    const templateCards = Array.from(page.querySelectorAll('[data-cb-template-card]'));
    const templateInputs = Array.from(page.querySelectorAll('[data-cb-template-start-template], [data-cb-template-draft-template]'));
    const startSubmit = page.querySelector('[data-cb-template-start-submit]');
    const draftSubmit = page.querySelector('[data-cb-template-draft-submit]');
    const draftTarget = page.querySelector('[data-cb-template-draft-target]');
    const selectionSummary = page.querySelector('[data-cb-template-selection-summary]');
    const targetSummary = page.querySelector('[data-cb-template-target-summary]');
    const strategyNameInput = page.querySelector('[data-cb-template-strategy-name]');
    const templateNameInput = page.querySelector('[data-cb-builder-template-name]');
    const schemaVersionInput = page.querySelector('[data-cb-builder-schema-version]');
    const templateKeyInput = page.querySelector('[data-cb-builder-template-key]');
    const sectionCountInput = page.querySelector('[data-cb-builder-section-count]');
    const builderRoot = page.querySelector('[data-cb-builder-form-root]');
    const previewStatus = page.querySelector('[data-cb-builder-preview-status]');
    const jsonPreview = page.querySelector('[data-cb-builder-json-preview]');
    const definitionInputs = Array.from(page.querySelectorAll('[data-cb-builder-definition-json]'));
    const validationSummary = page.querySelector('[data-cb-builder-validation-summary]');
    const validationBadge = page.querySelector('[data-cb-builder-validation-badge]');
    const validationList = page.querySelector('[data-cb-builder-validation-list]');
    const explainabilitySummary = page.querySelector('[data-cb-builder-explainability-summary]');
    const explainabilityBadge = page.querySelector('[data-cb-builder-explainability-badge]');
    const explainabilityList = page.querySelector('[data-cb-builder-explainability-list]');
    const parityBody = page.querySelector('[data-cb-builder-parity-body]');
    const runtimeThresholdSummary = page.querySelector('[data-cb-builder-runtime-threshold-summary]');
    const advancedToggle = page.querySelector('[data-cb-builder-advanced-toggle]');
    const advancedPanel = page.querySelector('[data-cb-builder-advanced-panel]');
    const advancedJson = page.querySelector('[data-cb-builder-advanced-json]');
    const advancedApply = page.querySelector('[data-cb-builder-advanced-apply]');
    const advancedStatus = page.querySelector('[data-cb-builder-advanced-status]');

    let selectedTemplate = null;
    let selectedTemplateCard = null;
    let builderState = null;
    let builderValidationErrors = [];
    let currentDefinitionJson = '';
    let advancedModeEnabled = false;
    const runtimeConfig = readRuntimeConfig();

    function cloneValue(value) {
        return value == null
            ? value
            : JSON.parse(JSON.stringify(value));
    }


    function readRuntimeConfig() {
        const raw = page.getAttribute('data-cb-runtime-config');
        if (!raw) {
            return null;
        }

        try {
            return JSON.parse(raw);
        }
        catch (error) {
            return null;
        }
    }

    function escapeHtml(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function toText(value, fallback) {
        if (typeof value === 'string') {
            return value;
        }

        if (typeof value === 'number' || typeof value === 'boolean') {
            return String(value);
        }

        return fallback || '';
    }

    function toNumberOrText(value, fallback) {
        if (typeof value === 'number') {
            return value;
        }

        if (typeof value === 'string' && value.trim() !== '') {
            const parsed = Number(value);
            return Number.isFinite(parsed)
                ? parsed
                : value;
        }

        return fallback;
    }

    function toScalarInput(value) {
        if (value == null) {
            return '';
        }

        if (typeof value === 'string') {
            return value;
        }

        if (typeof value === 'number' || typeof value === 'boolean') {
            return String(value);
        }

        return JSON.stringify(value);
    }

    function parseScalarInput(value) {
        const normalized = String(value ?? '').trim();
        if (!normalized) {
            return undefined;
        }

        if (normalized === 'true') {
            return true;
        }

        if (normalized === 'false') {
            return false;
        }

        if (/^-?\d+(\.\d+)?$/.test(normalized)) {
            return Number(normalized);
        }

        return normalized;
    }

    function setDefinitionInputsValue(value) {
        const normalized = value || '';
        definitionInputs.forEach(function (input) {
            input.value = normalized;
        });

        currentDefinitionJson = normalized;
    }

    function tryParseNumber(value) {
        const normalized = String(value ?? '').trim();
        if (!normalized) {
            return null;
        }

        const parsed = Number(normalized);
        return Number.isFinite(parsed)
            ? parsed
            : null;
    }

    function addValidationError(errors, message, seen) {
        if (!message) {
            return;
        }

        if (seen.has(message)) {
            return;
        }

        seen.add(message);
        errors.push(message);
    }

    function validateBuilderDefinition(definition) {
        const errors = [];
        const seen = new Set();

        if (!selectedTemplate) {
            addValidationError(errors, 'Önce bir şablon seçin.', seen);
            return errors;
        }

        if (!definition || typeof definition !== 'object' || Array.isArray(definition)) {
            addValidationError(errors, 'Builder JSON üretilemedi.', seen);
            return errors;
        }

        const sectionNames = Object.keys(definition).filter(function (key) {
            return ['schemaVersion', 'metadata', 'direction'].indexOf(key) === -1;
        });

        if (sectionNames.length === 0) {
            addValidationError(errors, 'En az bir entry/exit/risk kural kökü gerekli.', seen);
            return errors;
        }

        let hasRuleGroup = false;

        sectionNames.forEach(function (sectionName) {
            const section = definition[sectionName];
            if (!section || typeof section !== 'object' || Array.isArray(section)) {
                addValidationError(errors, getSectionLabel(sectionName) + ' bölümü boş veya hatalı.', seen);
                return;
            }

            const rules = Array.isArray(section.rules)
                ? section.rules
                : [section];
            hasRuleGroup = hasRuleGroup || Array.isArray(section.rules);
            const operator = Array.isArray(section.rules)
                ? String(section.operator || '').trim().toLowerCase()
                : '';

            if (Array.isArray(section.rules) && !operator) {
                addValidationError(errors, getSectionLabel(sectionName) + ' bölümü için operator gerekli.', seen);
            }

            if (rules.length === 0) {
                addValidationError(errors, getSectionLabel(sectionName) + ' bölümü en az bir kural içermeli.', seen);
                return;
            }

            const equalityByPath = {};
            const ruleIds = new Set();

            rules.forEach(function (rule, ruleIndex) {
                const ruleLabel = getSectionLabel(sectionName) + ' / Kural #' + (ruleIndex + 1);
                const enabled = !rule || rule.enabled !== false;
                if (!enabled) {
                    return;
                }

                const ruleId = String(rule.ruleId || '').trim();
                if (ruleId) {
                    const normalizedRuleId = ruleId.toLowerCase();
                    if (ruleIds.has(normalizedRuleId)) {
                        addValidationError(errors, ruleLabel + ' için tekrar eden rule id var.', seen);
                    }
                    else {
                        ruleIds.add(normalizedRuleId);
                    }
                }

                const path = String(rule.path || '').trim();
                const comparison = String(rule.comparison || '').trim();
                const hasValue = Object.prototype.hasOwnProperty.call(rule, 'value');
                const valuePath = String(rule.valuePath || '').trim();
                const hasValuePath = valuePath !== '';

                if (!path) {
                    addValidationError(errors, ruleLabel + ' için path gerekli.', seen);
                }

                if (!comparison) {
                    addValidationError(errors, ruleLabel + ' için comparison gerekli.', seen);
                }

                if (hasValue === hasValuePath) {
                    addValidationError(errors, ruleLabel + ' için value veya valuePath alanlarından yalnız biri dolu olmalı.', seen);
                }

                const weight = tryParseNumber(rule.weight);
                if (String(rule.weight || '').trim() !== '' && (weight === null || weight <= 0 || weight > 100)) {
                    addValidationError(errors, ruleLabel + ' için weight 0 ile 100 arasında olmalı.', seen);
                }

                if (path.toLowerCase() === 'indicator.rsi.value' && hasValue) {
                    const rsiThreshold = tryParseNumber(rule.value);
                    if (rsiThreshold === null || rsiThreshold < 0 || rsiThreshold > 100) {
                        addValidationError(errors, ruleLabel + ' için RSI eşiği 0 ile 100 arasında olmalı.', seen);
                    }
                }

                if (operator === 'all' && comparison.toLowerCase() === 'equals' && hasValue && !hasValuePath && path) {
                    const normalizedPath = path.toLowerCase();
                    const nextValue = JSON.stringify(rule.value);
                    if (Object.prototype.hasOwnProperty.call(equalityByPath, normalizedPath) && equalityByPath[normalizedPath] !== nextValue) {
                        addValidationError(errors, getSectionLabel(sectionName) + ' bölümünde aynı path için çelişkili equals kuralı var.', seen);
                    }
                    else {
                        equalityByPath[normalizedPath] = nextValue;
                    }
                }
            });
        });

        if (!hasRuleGroup) {
            addValidationError(errors, 'En az bir group root gerekli.', seen);
        }

        return errors;
    }

    function renderValidationSummary(errors) {
        if (!validationSummary || !validationBadge || !validationList) {
            return;
        }

        if (!selectedTemplate) {
            validationSummary.classList.add('d-none');
            validationSummary.classList.remove('cb-validation-summary-warning', 'cb-validation-summary-danger', 'cb-validation-summary-success');
            validationSummary.classList.add('cb-validation-summary-info');
            validationBadge.textContent = 'Validation bekleniyor';
            validationList.innerHTML = '';
            return;
        }

        if (!errors || errors.length === 0) {
            validationSummary.classList.remove('d-none', 'cb-validation-summary-warning', 'cb-validation-summary-danger', 'cb-validation-summary-info');
            validationSummary.classList.add('cb-validation-summary-success');
            validationBadge.textContent = 'Validation geçti';
            validationList.innerHTML = '<li>Builder JSON kayda hazır. Save sırasında canonical JSON üretilecek.</li>';
            return;
        }

        validationSummary.classList.remove('d-none', 'cb-validation-summary-success', 'cb-validation-summary-info');
        validationSummary.classList.add('cb-validation-summary-warning');
        validationBadge.textContent = 'Validation başarısız';
        validationList.innerHTML = errors.map(function (error) {
            return '<li>' + escapeHtml(error) + '</li>';
        }).join('');
    }



    function getPathLabel(path) {
        const normalized = String(path || '').trim().toLowerCase();
        switch (normalized) {
            case 'indicator.rsi.value':
                return 'RSI';
            case 'indicator.macd.histogram':
                return 'MACD histogram';
            case 'indicator.bollinger.bandwidth':
                return 'Bollinger width %';
            default:
                return path || 'Path';
        }
    }

    function getComparisonSymbol(comparison) {
        const normalized = String(comparison || '').trim().toLowerCase();
        switch (normalized) {
            case 'greaterthan':
                return '>';
            case 'greaterthanorequal':
                return '>=';
            case 'lessthan':
                return '<';
            case 'lessthanorequal':
                return '<=';
            case 'equals':
                return '=';
            default:
                return comparison || '?';
        }
    }

    function formatNumber(value, digits) {
        return Number(value).toFixed(digits).replace(/0+$/, '').replace(/\.$/, '');
    }

    function describeRuleThreshold(ruleRef) {
        if (!ruleRef) {
            return 'Tanımsız';
        }

        const digits = ruleRef.metricLabel === 'RSI'
            ? 2
            : 4;
        const suffix = ruleRef.metricLabel === 'Bollinger width %'
            ? '%'
            : '';
        return getComparisonSymbol(ruleRef.comparison) + ' ' + formatNumber(ruleRef.numericValue, digits) + suffix;
    }

    function getRuntimeMetricDisplay(directionKey, metricLabel) {
        const direction = runtimeConfig && runtimeConfig[directionKey];
        if (!direction) {
            return 'n/a';
        }

        if (!direction.Enabled) {
            return 'Disabled';
        }

        if (metricLabel === 'RSI') {
            return directionKey === 'Long'
                ? 'RSI < ' + formatNumber(direction.RsiThreshold, 2)
                : 'RSI > ' + formatNumber(direction.RsiThreshold, 2);
        }

        if (metricLabel === 'MACD histogram') {
            return directionKey === 'Long'
                ? 'MACD hist >= ' + formatNumber(direction.MacdThreshold, 4)
                : 'MACD hist <= ' + formatNumber(direction.MacdThreshold, 4);
        }

        return 'Bollinger width >= ' + formatNumber(direction.BollingerWidthThreshold, 4) + '%';
    }

    function collectRuleRefsFromNode(node, label, collector) {
        if (!node || typeof node !== 'object' || Array.isArray(node)) {
            return;
        }

        if (node.enabled === false) {
            return;
        }

        if (Array.isArray(node.rules)) {
            let activeIndex = 0;
            node.rules.forEach(function (child) {
                if (!child || typeof child !== 'object' || Array.isArray(child) || child.enabled === false) {
                    return;
                }

                activeIndex += 1;
                collectRuleRefsFromNode(child, label + ' / Kural #' + activeIndex, collector);
            });
            return;
        }

        const path = String(node.path || '').trim();
        const comparison = String(node.comparison || '').trim();
        const numericValue = tryParseNumber(node.value);
        if (!path || !comparison || numericValue === null) {
            return;
        }

        const metricLabel = getPathLabel(path);
        collector.push({
            path: path.toLowerCase(),
            comparison: comparison.toLowerCase(),
            numericValue: numericValue,
            metricLabel: metricLabel,
            inputLabel: label + ' / Value (' + metricLabel + ')',
            strategyThreshold: describeRuleThreshold({ comparison: comparison, numericValue: numericValue, metricLabel: metricLabel })
        });
    }

    function collectRuleRefs(definition) {
        const refs = [];
        collectRuleRefsFromNode(definition && definition.entry, 'Entry', refs);
        collectRuleRefsFromNode(definition && definition.longEntry, 'Long Entry', refs);
        collectRuleRefsFromNode(definition && definition.shortEntry, 'Short Entry', refs);
        return refs;
    }

    function findMetricRule(ruleRefs, directionKey, metricLabel) {
        return ruleRefs.find(function (ruleRef) {
            if (ruleRef.metricLabel !== metricLabel) {
                return false;
            }

            if (directionKey === 'Long' && metricLabel === 'RSI') {
                return ruleRef.comparison === 'lessthan' || ruleRef.comparison === 'lessthanorequal';
            }

            if (directionKey === 'Short' && metricLabel === 'RSI') {
                return ruleRef.comparison === 'greaterthan' || ruleRef.comparison === 'greaterthanorequal';
            }

            if (directionKey === 'Long' && metricLabel === 'MACD histogram') {
                return ruleRef.comparison === 'greaterthan' || ruleRef.comparison === 'greaterthanorequal';
            }

            if (directionKey === 'Short' && metricLabel === 'MACD histogram') {
                return ruleRef.comparison === 'lessthan' || ruleRef.comparison === 'lessthanorequal';
            }

            return ruleRef.comparison === 'greaterthan' || ruleRef.comparison === 'greaterthanorequal';
        }) || null;
    }

    function compareThresholds(directionKey, metricLabel, strategyValue, runtimeValue) {
        if (strategyValue === runtimeValue) {
            return 'Aligned';
        }

        if (metricLabel === 'RSI' && directionKey === 'Long') {
            return strategyValue < runtimeValue ? 'Strategy stricter' : 'Runtime stricter';
        }

        if (metricLabel === 'RSI' && directionKey === 'Short') {
            return strategyValue > runtimeValue ? 'Strategy stricter' : 'Runtime stricter';
        }

        if (metricLabel === 'MACD histogram' && directionKey === 'Short') {
            return strategyValue < runtimeValue ? 'Strategy stricter' : 'Runtime stricter';
        }

        return strategyValue > runtimeValue ? 'Strategy stricter' : 'Runtime stricter';
    }

    function getRuntimeMetricValue(directionKey, metricLabel) {
        const direction = runtimeConfig && runtimeConfig[directionKey];
        if (!direction) {
            return null;
        }

        if (metricLabel === 'RSI') {
            return direction.RsiThreshold;
        }

        if (metricLabel === 'MACD histogram') {
            return direction.MacdThreshold;
        }

        return direction.BollingerWidthThreshold;
    }

    function buildParityRow(directionKey, metricLabel, ruleRef) {
        const direction = runtimeConfig && runtimeConfig[directionKey];
        const runtimeDisplay = getRuntimeMetricDisplay(directionKey, metricLabel);
        if (!direction) {
            return {
                metric: directionKey + ' · ' + metricLabel,
                strategyInputLabel: ruleRef ? ruleRef.inputLabel : directionKey + ' / ' + metricLabel + ' inputu',
                strategyThreshold: ruleRef ? ruleRef.strategyThreshold : 'Tanımsız',
                runtimeThreshold: 'n/a',
                status: 'n/a',
                summary: 'Runtime config bulunamadı.'
            };
        }

        if (!direction.Enabled) {
            return {
                metric: directionKey + ' · ' + metricLabel,
                strategyInputLabel: ruleRef ? ruleRef.inputLabel : directionKey + ' / ' + metricLabel + ' inputu',
                strategyThreshold: ruleRef ? ruleRef.strategyThreshold : 'Tanımsız',
                runtimeThreshold: runtimeDisplay,
                status: 'Runtime disabled',
                summary: directionKey + ' runtime gate kapalı; bu metric save sonrası runtime block üretmez.'
            };
        }

        if (!ruleRef) {
            return {
                metric: directionKey + ' · ' + metricLabel,
                strategyInputLabel: directionKey + ' / ' + metricLabel + ' inputu',
                strategyThreshold: 'Tanımsız',
                runtimeThreshold: runtimeDisplay,
                status: 'Eksik',
                summary: 'Strategy threshold tanımlı değil. Runtime gate ' + runtimeDisplay + ' ile yine block üretebilir.'
            };
        }

        const comparison = compareThresholds(directionKey, metricLabel, ruleRef.numericValue, getRuntimeMetricValue(directionKey, metricLabel));
        return {
            metric: directionKey + ' · ' + metricLabel,
            strategyInputLabel: ruleRef.inputLabel,
            strategyThreshold: ruleRef.strategyThreshold,
            runtimeThreshold: runtimeDisplay,
            status: comparison,
            summary: comparison === 'Runtime stricter'
                ? ruleRef.inputLabel + ' runtime gate\'ten daha gevşek. Strategy ' + ruleRef.strategyThreshold + '; runtime ' + runtimeDisplay + ' ister.'
                : comparison === 'Strategy stricter'
                    ? ruleRef.inputLabel + ' runtime gate\'ten daha sıkı. Strategy ' + ruleRef.strategyThreshold + '; runtime ' + runtimeDisplay + ' isteğini zaten kapsar.'
                    : ruleRef.inputLabel + ' ile runtime gate aynı yöne bakıyor. Strategy ' + ruleRef.strategyThreshold + '; runtime ' + runtimeDisplay + '.'
        };
    }

    function renderParityRows(rows) {
        if (!parityBody) {
            return;
        }

        if (!rows || rows.length === 0) {
            parityBody.innerHTML = '<tr><td colspan="4" class="text-muted font-size-sm">Parity tablosu için aktif metric bulunamadı.</td></tr>';
            return;
        }

        parityBody.innerHTML = rows.map(function (row) {
            return ''
                + '<tr>'
                + '  <td><div class="font-weight-bolder">' + escapeHtml(row.metric) + '</div><div class="text-muted font-size-sm">' + escapeHtml(row.summary) + '</div></td>'
                + '  <td><div class="font-weight-bolder">' + escapeHtml(row.strategyThreshold) + '</div><div class="text-muted font-size-sm">' + escapeHtml(row.strategyInputLabel) + '</div></td>'
                + '  <td>' + escapeHtml(row.runtimeThreshold) + '</td>'
                + '  <td>' + escapeHtml(row.status) + '</td>'
                + '</tr>';
        }).join('');
    }

    function renderExplainability(messages, rows) {
        if (runtimeThresholdSummary && runtimeConfig) {
            runtimeThresholdSummary.textContent = 'Long runtime: ' + (runtimeConfig.Long ? runtimeConfig.Long.Summary : 'n/a') + ' | Short runtime: ' + (runtimeConfig.Short ? runtimeConfig.Short.Summary : 'n/a');
        }

        if (!explainabilitySummary || !explainabilityBadge || !explainabilityList) {
            renderParityRows(rows);
            return;
        }

        explainabilityBadge.textContent = 'Neden entry yok / neden blocked?';
        explainabilityList.innerHTML = (messages && messages.length > 0 ? messages : ['Strategy input ve runtime gate parity özeti hazır.']).map(function (message) {
            return '<li>' + escapeHtml(message) + '</li>';
        }).join('');
        renderParityRows(rows);
    }

    function buildExplainabilityAnalysis(definition) {
        const messages = [];
        const rows = [];
        const ruleRefs = collectRuleRefs(definition);
        const hasEntryRoot = !!(definition && (definition.entry || definition.longEntry || definition.shortEntry));

        if (!hasEntryRoot) {
            messages.push('Entry kökü bulunamadı. Bu durumda neden entry yok cevabı doğrudan strategy giriş tarafındadır.');
        }

        if (ruleRefs.length === 0) {
            messages.push('Aktif ve numeric entry kuralı bulunamadı. Runtime gate block özetleri strategy inputlarından bağımsız görünebilir.');
        }

        ['RSI', 'MACD histogram', 'Bollinger width %'].forEach(function (metricLabel) {
            const longRow = buildParityRow('Long', metricLabel, findMetricRule(ruleRefs, 'Long', metricLabel));
            const shortRow = buildParityRow('Short', metricLabel, findMetricRule(ruleRefs, 'Short', metricLabel));
            rows.push(longRow, shortRow);

            [longRow, shortRow].forEach(function (row) {
                if (row.status === 'Runtime stricter') {
                    messages.push(row.strategyInputLabel + ' runtime gate ile çakışıyor. Strategy ' + row.strategyThreshold + ' olsa da runtime ' + row.runtimeThreshold + ' nedeniyle blocked olabilir.');
                }
                else if (row.status === 'Eksik') {
                    messages.push(row.strategyInputLabel + ' tanımlı değil. Runtime gate yine de ' + row.runtimeThreshold + ' ister; bu yüzden neden blocked görebilirsiniz.');
                }
            });
        });

        if (messages.length === 0) {
            messages.push('Strategy threshold\'ları ile runtime gate threshold\'ları hizalı görünüyor. Bariz bir parity çakışması bulunmadı.');
        }

        return { messages: messages, rows: rows };
    }

    function syncAdvancedEditorFromPreview(previewDefinition) {
        if (!advancedJson) {
            return;
        }

        advancedJson.value = JSON.stringify(previewDefinition, null, 2);
        if (advancedStatus) {
            advancedStatus.textContent = advancedModeEnabled
                ? 'Advanced JSON editörü açık. Değişiklikten sonra "JSON\'u forma uygula" ile güvenli hydrate yapın.'
                : 'Advanced mode pasif.';
        }
    }

    function toggleAdvancedMode() {
        advancedModeEnabled = !advancedModeEnabled;
        if (advancedPanel) {
            advancedPanel.classList.toggle('d-none', !advancedModeEnabled);
        }

        if (advancedToggle) {
            advancedToggle.textContent = advancedModeEnabled ? 'Advanced mode kapat' : 'Advanced mode';
        }

        if (advancedModeEnabled && advancedJson) {
            advancedJson.value = jsonPreview ? jsonPreview.textContent : advancedJson.value;
        }

        if (advancedStatus) {
            advancedStatus.textContent = advancedModeEnabled
                ? 'Advanced JSON editörü açık. Değişiklikten sonra "JSON\'u forma uygula" ile güvenli hydrate yapın.'
                : 'Advanced mode pasif.';
        }
    }

    function applyAdvancedJsonToBuilder() {
        if (!advancedJson) {
            return;
        }

        const raw = advancedJson.value.trim();
        if (!raw) {
            if (advancedStatus) {
                advancedStatus.textContent = 'Advanced JSON boş bırakılamaz.';
            }
            return;
        }

        try {
            const parsed = JSON.parse(raw);
            builderState = buildStateFromDefinition(parsed);
            if (templateNameInput) {
                templateNameInput.value = builderState.metadata.templateName || templateNameInput.value;
            }
            if (schemaVersionInput) {
                schemaVersionInput.value = builderState.schemaVersion || '2';
            }
            if (templateKeyInput) {
                templateKeyInput.value = builderState.metadata.templateKey || selectedTemplate || '';
            }
            if (sectionCountInput) {
                sectionCountInput.value = String(builderState.sections.length);
            }
            renderBuilder();
            updateBuilderPreview();
            if (advancedStatus) {
                advancedStatus.textContent = 'Advanced JSON forma uygulandı. Preview ve save payload güncellendi.';
            }
        }
        catch (error) {
            if (advancedStatus) {
                advancedStatus.textContent = 'Advanced JSON çözümlenemedi. Önce geçerli JSON girin.';
            }
        }
    }

    function getSectionLabel(sectionName) {
        if (sectionName === 'entry') {
            return 'Entry';
        }

        if (sectionName === 'risk') {
            return 'Risk';
        }

        if (sectionName === 'exit') {
            return 'Exit';
        }

        return sectionName.charAt(0).toUpperCase() + sectionName.slice(1);
    }

    function readSelectedTargetLabel() {
        if (!draftTarget || !draftTarget.selectedOptions || draftTarget.selectedOptions.length === 0) {
            return 'Bot oluştururken strateji seçimi bu listeden yapılır.';
        }

        return draftTarget.selectedOptions[0].textContent || 'Bot oluştururken strateji seçimi bu listeden yapılır.';
    }

    function readDefinition(card) {
        if (!card) {
            return null;
        }

        const definitionJson = card.getAttribute('data-cb-template-definition');
        if (!definitionJson) {
            return null;
        }

        try {
            return JSON.parse(definitionJson);
        }
        catch (error) {
            return null;
        }
    }

    function normalizeRule(rule, sectionName, ruleIndex) {
        const source = rule && typeof rule === 'object'
            ? cloneValue(rule)
            : {};

        return {
            original: source,
            ruleId: toText(source.ruleId, sectionName + '-rule-' + (ruleIndex + 1)),
            ruleType: toText(source.ruleType, ''),
            path: toText(source.path, ''),
            comparison: toText(source.comparison, ''),
            value: toScalarInput(Object.prototype.hasOwnProperty.call(source, 'value') ? source.value : ''),
            valuePath: toText(source.valuePath, ''),
            timeframe: toText(source.timeframe, ''),
            weight: Object.prototype.hasOwnProperty.call(source, 'weight')
                ? toText(source.weight, '')
                : '',
            enabled: source.enabled !== false,
            group: toText(source.group, sectionName)
        };
    }

    function normalizeSection(sectionName, sourceSection) {
        if (!sourceSection || typeof sourceSection !== 'object' || Array.isArray(sourceSection)) {
            return null;
        }

        const section = cloneValue(sourceSection);
        const isGroup = Array.isArray(section.rules);
        const ruleSourceList = isGroup
            ? section.rules
            : [section];

        return {
            original: section,
            sectionName: sectionName,
            isGroup: isGroup,
            operator: toText(section.operator, isGroup ? 'all' : ''),
            timeframe: toText(section.timeframe, ''),
            enabled: section.enabled !== false,
            groupRuleId: toText(section.ruleId, sectionName + '-root'),
            rules: ruleSourceList.map(function (rule, ruleIndex) {
                return normalizeRule(rule, sectionName, ruleIndex);
            })
        };
    }

    function buildStateFromDefinition(definition) {
        const sourceDefinition = definition && typeof definition === 'object' && !Array.isArray(definition)
            ? cloneValue(definition)
            : {};
        const metadata = sourceDefinition.metadata && typeof sourceDefinition.metadata === 'object'
            ? sourceDefinition.metadata
            : {};
        const sectionNames = Object.keys(sourceDefinition).filter(function (key) {
            return key !== 'schemaVersion'
                && key !== 'metadata'
                && sourceDefinition[key]
                && typeof sourceDefinition[key] === 'object'
                && !Array.isArray(sourceDefinition[key]);
        });

        return {
            sourceDefinition: sourceDefinition,
            schemaVersion: toText(sourceDefinition.schemaVersion, '2'),
            metadata: {
                templateKey: toText(metadata.templateKey, selectedTemplate || ''),
                templateName: toText(metadata.templateName, selectedTemplateCard ? selectedTemplateCard.getAttribute('data-cb-template-name') : '')
            },
            sections: sectionNames.map(function (sectionName) {
                return normalizeSection(sectionName, sourceDefinition[sectionName]);
            }).filter(function (section) {
                return !!section;
            })
        };
    }

    function renderRule(sectionIndex, ruleIndex, rule) {
        return ''
            + '<div class="border rounded p-4 mb-4" data-cb-builder-rule="' + ruleIndex + '">'
            + '    <div class="d-flex align-items-center justify-content-between mb-4">'
            + '        <div class="font-weight-bolder">Kural #' + (ruleIndex + 1) + '</div>'
            + '        <span class="text-muted font-size-sm">Preview only</span>'
            + '    </div>'
            + '    <div class="form-row">'
            + '        <div class="form-group col-md-6">'
            + '            <label class="text-muted font-size-sm mb-2">Rule id</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.ruleId) + '" data-cb-builder-rule-id="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '        <div class="form-group col-md-6">'
            + '            <label class="text-muted font-size-sm mb-2">Rule type</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.ruleType) + '" data-cb-builder-rule-type="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '    </div>'
            + '    <div class="form-row">'
            + '        <div class="form-group col-md-6">'
            + '            <label class="text-muted font-size-sm mb-2">Path</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.path) + '" data-cb-builder-rule-path="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '        <div class="form-group col-md-6">'
            + '            <label class="text-muted font-size-sm mb-2">Comparison</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.comparison) + '" data-cb-builder-rule-comparison="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '    </div>'
            + '    <div class="form-row">'
            + '        <div class="form-group col-md-6">'
            + '            <label class="text-muted font-size-sm mb-2">Value</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.value) + '" data-cb-builder-rule-value="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '        <div class="form-group col-md-6">'
            + '            <label class="text-muted font-size-sm mb-2">Value path</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.valuePath) + '" data-cb-builder-rule-value-path="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '    </div>'
            + '    <div class="form-row mb-0">'
            + '        <div class="form-group col-md-3">'
            + '            <label class="text-muted font-size-sm mb-2">Timeframe</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.timeframe) + '" data-cb-builder-rule-timeframe="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '        <div class="form-group col-md-3">'
            + '            <label class="text-muted font-size-sm mb-2">Weight</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.weight) + '" data-cb-builder-rule-weight="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '        <div class="form-group col-md-3">'
            + '            <label class="text-muted font-size-sm mb-2">Group</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(rule.group) + '" data-cb-builder-rule-group="' + sectionIndex + ':' + ruleIndex + '" />'
            + '        </div>'
            + '        <div class="form-group col-md-3">'
            + '            <label class="text-muted font-size-sm mb-2">Enabled</label>'
            + '            <select class="form-control" data-cb-builder-rule-enabled="' + sectionIndex + ':' + ruleIndex + '">'
            + '                <option value="true"' + (rule.enabled ? ' selected' : '') + '>True</option>'
            + '                <option value="false"' + (!rule.enabled ? ' selected' : '') + '>False</option>'
            + '            </select>'
            + '        </div>'
            + '    </div>'
            + '</div>';
    }

    function renderSection(section, sectionIndex) {
        const sectionRules = section.rules.map(function (rule, ruleIndex) {
            return renderRule(sectionIndex, ruleIndex, rule);
        }).join('');

        return ''
            + '<div class="card card-custom gutter-b cb-card border" data-cb-builder-section-card="' + sectionIndex + '">'
            + '    <div class="card-body">'
            + '        <div class="d-flex align-items-start justify-content-between flex-wrap gap-3 mb-4">'
            + '            <div>'
            + '                <div class="cb-section-title">' + escapeHtml(getSectionLabel(section.sectionName)) + ' form alanları</div>'
            + '                <p class="cb-page-description mt-2 mb-0">Mevcut section JSON’u form alanlarına açıldı. Değişiklikler preview JSON üzerinde anlık izlenir.</p>'
            + '            </div>'
            + '            <span class="text-muted font-size-sm">' + (section.isGroup ? 'Group root' : 'Single rule root') + '</span>'
            + '        </div>'
            + '        <div class="form-row">'
            + '            <div class="form-group col-md-4">'
            + '                <label class="text-muted font-size-sm mb-2">Operator</label>'
            + '                <input class="form-control" type="text" value="' + escapeHtml(section.operator) + '" data-cb-builder-section-operator="' + sectionIndex + '" ' + (section.isGroup ? '' : 'readonly') + ' />'
            + '            </div>'
            + '            <div class="form-group col-md-4">'
            + '                <label class="text-muted font-size-sm mb-2">Timeframe</label>'
            + '                <input class="form-control" type="text" value="' + escapeHtml(section.timeframe) + '" data-cb-builder-section-timeframe="' + sectionIndex + '" />'
            + '            </div>'
            + '            <div class="form-group col-md-4">'
            + '                <label class="text-muted font-size-sm mb-2">Enabled</label>'
            + '                <select class="form-control" data-cb-builder-section-enabled="' + sectionIndex + '">'
            + '                    <option value="true"' + (section.enabled ? ' selected' : '') + '>True</option>'
            + '                    <option value="false"' + (!section.enabled ? ' selected' : '') + '>False</option>'
            + '                </select>'
            + '            </div>'
            + '        </div>'
            + '        <div class="form-group">'
            + '            <label class="text-muted font-size-sm mb-2">Root rule id</label>'
            + '            <input class="form-control" type="text" value="' + escapeHtml(section.groupRuleId) + '" data-cb-builder-section-rule-id="' + sectionIndex + '" ' + (section.isGroup ? '' : 'readonly') + ' />'
            + '        </div>'
            +          sectionRules
            + '    </div>'
            + '</div>';
    }

    function renderBuilder() {
        if (!builderRoot) {
            return;
        }

        if (!selectedTemplate) {
            builderRoot.innerHTML = '<div class="cb-validation-summary cb-validation-summary-info">Önce bir şablon seçin.</div>';
            return;
        }

        if (!builderState || builderState.sections.length === 0) {
            builderRoot.innerHTML = '<div class="cb-validation-summary cb-validation-summary-info">Bu şablonda forma açılabilir section bulunamadı.</div>';
            return;
        }

        builderRoot.innerHTML = builderState.sections.map(function (section, sectionIndex) {
            return renderSection(section, sectionIndex);
        }).join('');
    }

    function applyString(target, key, value) {
        const normalized = String(value ?? '').trim();
        if (!normalized) {
            delete target[key];
            return;
        }

        target[key] = normalized;
    }

    function applyNumberOrString(target, key, value) {
        const normalized = String(value ?? '').trim();
        if (!normalized) {
            delete target[key];
            return;
        }

        const parsed = Number(normalized);
        target[key] = Number.isFinite(parsed)
            ? parsed
            : normalized;
    }

    function buildRuleFromState(sectionState, ruleState) {
        const nextRule = cloneValue(ruleState.original) || {};
        applyString(nextRule, 'ruleId', ruleState.ruleId);
        applyString(nextRule, 'ruleType', ruleState.ruleType);
        applyString(nextRule, 'path', ruleState.path);
        applyString(nextRule, 'comparison', ruleState.comparison);
        applyString(nextRule, 'group', ruleState.group || sectionState.sectionName);
        applyString(nextRule, 'timeframe', ruleState.timeframe || sectionState.timeframe);
        applyNumberOrString(nextRule, 'weight', ruleState.weight);
        nextRule.enabled = ruleState.enabled;

        const valuePath = String(ruleState.valuePath ?? '').trim();
        if (valuePath) {
            nextRule.valuePath = valuePath;
        }
        else {
            delete nextRule.valuePath;
        }

        const value = parseScalarInput(ruleState.value);
        if (value === undefined) {
            delete nextRule.value;
        }
        else {
            nextRule.value = value;
        }

        return nextRule;
    }

    function buildPreviewDefinition() {
        if (!builderState) {
            return {};
        }

        const nextDefinition = cloneValue(builderState.sourceDefinition) || {};
        nextDefinition.schemaVersion = toNumberOrText(builderState.schemaVersion, 2);
        nextDefinition.metadata = cloneValue(nextDefinition.metadata) || {};
        nextDefinition.metadata.templateKey = builderState.metadata.templateKey;
        nextDefinition.metadata.templateName = builderState.metadata.templateName;

        builderState.sections.forEach(function (sectionState) {
            if (sectionState.isGroup) {
                const nextSection = cloneValue(sectionState.original) || {};
                nextSection.ruleType = 'group';
                nextSection.group = sectionState.sectionName;
                nextSection.enabled = sectionState.enabled;
                applyString(nextSection, 'ruleId', sectionState.groupRuleId || (sectionState.sectionName + '-root'));
                applyString(nextSection, 'operator', sectionState.operator || 'all');
                applyString(nextSection, 'timeframe', sectionState.timeframe);
                nextSection.rules = sectionState.rules.map(function (ruleState) {
                    return buildRuleFromState(sectionState, ruleState);
                });
                nextDefinition[sectionState.sectionName] = nextSection;
                return;
            }

            const singleRule = buildRuleFromState(sectionState, sectionState.rules[0]);
            singleRule.enabled = sectionState.enabled;
            applyString(singleRule, 'timeframe', sectionState.timeframe || sectionState.rules[0].timeframe);
            nextDefinition[sectionState.sectionName] = singleRule;
        });

        return nextDefinition;
    }

    function readInputValue(selector, fallback) {
        const element = page.querySelector(selector);
        return element
            ? element.value
            : fallback;
    }

    function collectBuilderStateFromDom() {
        if (!builderState) {
            return;
        }

        builderState.schemaVersion = schemaVersionInput
            ? schemaVersionInput.value.trim()
            : builderState.schemaVersion;
        builderState.metadata.templateKey = templateKeyInput
            ? templateKeyInput.value.trim()
            : builderState.metadata.templateKey;
        builderState.metadata.templateName = templateNameInput
            ? templateNameInput.value.trim()
            : builderState.metadata.templateName;

        builderState.sections = builderState.sections.map(function (sectionState, sectionIndex) {
            const nextSection = Object.assign({}, sectionState);
            nextSection.operator = readInputValue('[data-cb-builder-section-operator="' + sectionIndex + '"]', sectionState.operator).trim();
            nextSection.timeframe = readInputValue('[data-cb-builder-section-timeframe="' + sectionIndex + '"]', sectionState.timeframe).trim();
            nextSection.enabled = readInputValue('[data-cb-builder-section-enabled="' + sectionIndex + '"]', sectionState.enabled ? 'true' : 'false') === 'true';
            nextSection.groupRuleId = readInputValue('[data-cb-builder-section-rule-id="' + sectionIndex + '"]', sectionState.groupRuleId).trim();
            nextSection.rules = sectionState.rules.map(function (ruleState, ruleIndex) {
                return Object.assign({}, ruleState, {
                    ruleId: readInputValue('[data-cb-builder-rule-id="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.ruleId).trim(),
                    ruleType: readInputValue('[data-cb-builder-rule-type="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.ruleType).trim(),
                    path: readInputValue('[data-cb-builder-rule-path="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.path).trim(),
                    comparison: readInputValue('[data-cb-builder-rule-comparison="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.comparison).trim(),
                    value: readInputValue('[data-cb-builder-rule-value="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.value),
                    valuePath: readInputValue('[data-cb-builder-rule-value-path="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.valuePath).trim(),
                    timeframe: readInputValue('[data-cb-builder-rule-timeframe="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.timeframe).trim(),
                    weight: readInputValue('[data-cb-builder-rule-weight="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.weight).trim(),
                    group: readInputValue('[data-cb-builder-rule-group="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.group).trim(),
                    enabled: readInputValue('[data-cb-builder-rule-enabled="' + sectionIndex + ':' + ruleIndex + '"]', ruleState.enabled ? 'true' : 'false') === 'true'
                });
            });

            return nextSection;
        });
    }

    function updateBuilderPreview() {
        if (!jsonPreview) {
            return;
        }

        collectBuilderStateFromDom();
        const previewDefinition = buildPreviewDefinition();
        builderValidationErrors = validateBuilderDefinition(previewDefinition);
        jsonPreview.textContent = JSON.stringify(previewDefinition, null, 2);
        setDefinitionInputsValue(builderValidationErrors.length === 0 ? JSON.stringify(previewDefinition) : '');
        renderValidationSummary(builderValidationErrors);
        const explainability = buildExplainabilityAnalysis(previewDefinition);
        renderExplainability(explainability.messages, explainability.rows);
        syncAdvancedEditorFromPreview(previewDefinition);
        syncForms();
    }

    function syncBuilderFromSelection() {
        builderState = buildStateFromDefinition(readDefinition(selectedTemplateCard));

        if (templateNameInput) {
            templateNameInput.value = builderState.metadata.templateName || '';
        }

        if (schemaVersionInput) {
            schemaVersionInput.value = builderState.schemaVersion || '2';
        }

        if (templateKeyInput) {
            templateKeyInput.value = builderState.metadata.templateKey || selectedTemplate || '';
        }

        if (sectionCountInput) {
            sectionCountInput.value = String(builderState.sections.length);
        }

        if (strategyNameInput && !strategyNameInput.value.trim() && builderState.metadata.templateName) {
            strategyNameInput.value = builderState.metadata.templateName;
        }

        renderBuilder();

        if (previewStatus) {
            previewStatus.textContent = selectedTemplate
                ? 'Form alanları mevcut JSON ile dolduruldu. Geçerli durumda save canonical JSON üreterek strategy version zincirine yazılır.'
                : 'Bir şablon seçtiğinizde form alanları mevcut JSON ile doldurulur.';
        }

        updateBuilderPreview();
    }

    function syncForms() {
        const hasSelection = !!selectedTemplate;
        const hasTarget = !!(draftTarget && draftTarget.value);
        const isValid = hasSelection && builderValidationErrors.length === 0 && !!currentDefinitionJson;

        templateInputs.forEach(function (input) {
            input.value = hasSelection ? selectedTemplate : '';
        });

        if (!hasSelection) {
            setDefinitionInputsValue('');
        }

        if (startSubmit) {
            startSubmit.disabled = !isValid;
        }

        if (draftSubmit) {
            draftSubmit.disabled = !(isValid && hasTarget);
        }

        if (selectionSummary) {
            selectionSummary.textContent = !hasSelection
                ? 'Önce bir şablon seçin.'
                : builderValidationErrors.length === 0
                    ? 'Şablon seçildi. Builder doğrulandı ve save canonical JSON ile yapılacak.'
                    : 'Şablon seçildi fakat validation hataları nedeniyle save kilitlendi.';
        }

        if (targetSummary) {
            const selectedTarget = readSelectedTargetLabel();
            targetSummary.textContent = hasTarget
                ? 'Mevcut strateji: ' + selectedTarget
                : 'Bot oluştururken strateji seçimi bu listeden yapılır.';
        }
    }

    function selectTemplate(card) {
        selectedTemplateCard = card || null;
        selectedTemplate = selectedTemplateCard
            ? selectedTemplateCard.getAttribute('data-cb-template-key')
            : null;

        templateCards.forEach(function (item) {
            item.classList.toggle('is-selected', item === selectedTemplateCard);
        });

        syncForms();
        syncBuilderFromSelection();
    }

    page.addEventListener('click', function (event) {
        const templateCard = event.target.closest('[data-cb-template-card]');
        if (!templateCard || !page.contains(templateCard)) {
            return;
        }

        event.preventDefault();
        selectTemplate(templateCard);
    });

    page.addEventListener('change', function (event) {
        if (event.target && event.target === draftTarget) {
            syncForms();
            return;
        }

        if (event.target && (event.target.closest('[data-cb-builder-form-root]') || event.target === templateNameInput || event.target === schemaVersionInput)) {
            updateBuilderPreview();
        }
    });

    page.addEventListener('input', function (event) {
        if (event.target && (event.target.closest('[data-cb-builder-form-root]') || event.target === templateNameInput || event.target === schemaVersionInput)) {
            updateBuilderPreview();
        }
    });

    if (advancedToggle) {
        advancedToggle.addEventListener('click', function () {
            toggleAdvancedMode();
        });
    }

    if (advancedApply) {
        advancedApply.addEventListener('click', function () {
            applyAdvancedJsonToBuilder();
        });
    }

    page.addEventListener('submit', function (event) {
        const form = event.target;
        if (!form || !page.contains(form)) {
            return;
        }

        if (!form.matches('[data-cb-template-start-form], [data-cb-template-draft-form]')) {
            return;
        }

        updateBuilderPreview();
        if (builderValidationErrors.length > 0 || !currentDefinitionJson) {
            event.preventDefault();
            if (previewStatus) {
                previewStatus.textContent = 'Validation hataları çözülmeden güvenli save başlatılamaz.';
            }
        }
    });

    syncForms();
    syncBuilderFromSelection();
})();
