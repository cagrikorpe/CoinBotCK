(function () {
    const page = document.querySelector('[data-cb-logcenter-page]');
    if (!page) {
        return;
    }

    function setText(id, value) {
        const element = document.getElementById(id);
        if (element) {
            element.textContent = value ?? '';
        }
    }

    function setBadge(id, text, cls) {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }

        element.className = cls;
        element.textContent = text ?? '';
    }

    function resolveSeverityClass(value) {
        switch ((value ?? '').toLowerCase()) {
            case 'critical':
                return 'cb-badge cb-badge-danger';
            case 'warning':
            case 'degraded':
                return 'cb-badge cb-badge-warning';
            case 'healthy':
                return 'cb-badge cb-badge-success';
            case 'info':
                return 'cb-badge cb-badge-info';
            default:
                return 'cb-badge cb-badge-neutral';
        }
    }

    function resolveKindClass(kind) {
        switch ((kind ?? '').toLowerCase()) {
            case 'incident':
            case 'incidentevent':
                return 'cb-badge cb-badge-danger';
            case 'executiontrace':
                return 'cb-badge cb-badge-warning';
            case 'decisiontrace':
                return 'cb-badge cb-badge-info';
            case 'approvalqueue':
            case 'approvalaction':
                return 'cb-badge cb-badge-neutral';
            case 'adminauditlog':
                return 'cb-badge cb-badge-warning';
            default:
                return 'cb-badge cb-badge-neutral';
        }
    }

    function formatUtc(value) {
        if (!value) {
            return 'n/a';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return value;
        }

        return `${date.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
    }

    function buildHref(path, params) {
        const url = new URL(path, window.location.origin);
        Object.entries(params).forEach(function ([key, value]) {
            if (value !== null && value !== undefined && String(value).trim().length > 0) {
                url.searchParams.set(key, String(value));
            }
        });
        return `${url.pathname}${url.search}${url.hash}`;
    }

    function updateLink(id, href) {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }

        if (!href) {
            element.setAttribute('hidden', 'hidden');
            element.setAttribute('href', '#');
            return;
        }

        element.removeAttribute('hidden');
        element.setAttribute('href', href);
    }

    function updateTags(tags) {
        const container = document.getElementById('cb_log_drawer_tags');
        if (!container) {
            return;
        }

        container.innerHTML = '';

        const list = Array.isArray(tags) ? tags.filter(function (tag) { return typeof tag === 'string' && tag.trim().length > 0; }) : [];

        if (list.length === 0) {
            const badge = document.createElement('span');
            badge.className = 'cb-badge cb-badge-neutral';
            badge.textContent = 'No tags';
            container.appendChild(badge);
            return;
        }

        list.slice(0, 8).forEach(function (tag) {
            const badge = document.createElement('span');
            badge.className = 'cb-badge cb-badge-neutral';
            badge.textContent = tag;
            container.appendChild(badge);
        });
    }

    function highlightRow(reference) {
        page.querySelectorAll('[data-cb-log-row]').forEach(function (row) {
            row.classList.toggle('is-selected', row.getAttribute('data-cb-log-row-reference') === reference);
        });
    }

    function updateDrawer(entry) {
        if (!entry) {
            return;
        }

        const severityText = entry.severity || entry.tone || 'Info';
        const kindText = entry.kind || 'Log';
        const referenceText = entry.reference || '-';
        const correlationText = entry.correlationId || referenceText;

        setText('cb_log_drawer_eyebrow', kindText);
        setText('cb_log_drawer_title', entry.title || kindText);
        setBadge('cb_log_drawer_severity', severityText, resolveSeverityClass(severityText));
        setBadge('cb_log_drawer_category', kindText, resolveKindClass(kindText));
        setText('cb_log_drawer_trace', correlationText);
        setText('cb_log_drawer_reference', referenceText);
        setText('cb_log_drawer_correlation', correlationText);
        setText('cb_log_drawer_actor', entry.userId || '-');
        setText('cb_log_drawer_symbol', entry.symbol || entry.source || 'n/a');
        setText('cb_log_drawer_time', formatUtc(entry.createdAtUtc));
        setText('cb_log_drawer_source', entry.source || 'n/a');
        setText('cb_log_drawer_message', entry.summary || '');
        setText('cb_log_drawer_raw_json', entry.rawJson || '{}');
        setText('cb_log_drawer_privacy', 'Masking standardı aktif: API key, secret, signature, auth header ve hassas payload alanları düz metin gösterilmez.');
        setText('cb_log_drawer_action', 'İlgili trace, incident veya approval detayına geçip zinciri devam ettirebilirsin.');
        updateTags(entry.tags);

        updateLink('cb_log_drawer_trace_link', (entry.decisionId || entry.executionAttemptId)
            ? buildHref('/Admin/TraceDetail', {
                correlationId: entry.correlationId || referenceText,
                decisionId: entry.decisionId,
                executionAttemptId: entry.executionAttemptId
            })
            : null);
        updateLink('cb_log_drawer_incident_link', entry.incidentReference
            ? buildHref('/Admin/IncidentDetail', { incidentReference: entry.incidentReference })
            : null);
        updateLink('cb_log_drawer_approval_link', entry.approvalReference
            ? buildHref('/Admin/ApprovalDetail', { approvalReference: entry.approvalReference })
            : null);
        updateLink('cb_log_drawer_audit_link', buildHref('/Admin/Audit', {
            query: entry.correlationId || referenceText
        }));

        highlightRow(referenceText);
        page.setAttribute('data-cb-logcenter-focus-reference', referenceText);
    }

    function tryHydrateFromTrigger(trigger) {
        const payload = trigger.getAttribute('data-cb-log-entry');
        if (!payload) {
            return;
        }

        try {
            updateDrawer(JSON.parse(payload));
        } catch {
            // Fail closed: keep the current drawer content untouched.
        }
    }

    function hydrateInitialSelection() {
        const focusReference = page.getAttribute('data-cb-logcenter-focus-reference');
        const rows = Array.from(page.querySelectorAll('[data-cb-log-row]'));
        const selectedRow = rows.find(function (row) {
            return row.getAttribute('data-cb-log-row-reference') === focusReference;
        }) || rows[0];

        if (!selectedRow) {
            return;
        }

        const trigger = selectedRow.querySelector('[data-cb-log-entry]');
        if (trigger) {
            tryHydrateFromTrigger(trigger);
        }
    }

    page.addEventListener('click', function (event) {
        const detailTrigger = event.target.closest('[data-cb-log-entry]');
        if (detailTrigger) {
            tryHydrateFromTrigger(detailTrigger);
        }
    });

    hydrateInitialSelection();
})();
