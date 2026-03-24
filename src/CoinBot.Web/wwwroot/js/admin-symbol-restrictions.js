(function () {
    const forms = document.querySelectorAll('[data-cb-symbol-restriction-editor]');

    forms.forEach((form) => {
        const policyField = form.querySelector('[data-cb-policy-json-field]');
        const tbody = form.querySelector('[data-cb-symbol-restriction-rows]');
        const template = form.querySelector('template[data-cb-symbol-restriction-template]');
        const addButton = form.querySelector('[data-cb-symbol-restriction-add]');

        if (!policyField || !tbody || !template || !addButton) {
            return;
        }

        const ensureEditableRow = () => {
            const emptyRow = tbody.querySelector('[data-cb-symbol-restriction-empty]');
            if (emptyRow) {
                emptyRow.remove();
            }
        };

        const createRow = (values) => {
            ensureEditableRow();

            const fragment = template.content.cloneNode(true);
            const row = fragment.querySelector('[data-cb-symbol-restriction-row]');

            if (!row) {
                return null;
            }

            const symbolInput = row.querySelector('[data-cb-symbol-restriction-symbol]');
            const stateInput = row.querySelector('[data-cb-symbol-restriction-state]');
            const reasonInput = row.querySelector('[data-cb-symbol-restriction-reason]');
            const updatedAtInput = row.querySelector('[data-cb-symbol-restriction-updated-at]');
            const updatedByInput = row.querySelector('[data-cb-symbol-restriction-updated-by]');

            if (symbolInput) {
                symbolInput.value = values?.symbol ?? '';
            }

            if (stateInput) {
                stateInput.value = values?.state ?? 'Blocked';
            }

            if (reasonInput) {
                reasonInput.value = values?.reason ?? '';
            }

            if (updatedAtInput) {
                updatedAtInput.value = values?.updatedAtUtc ?? '';
            }

            if (updatedByInput) {
                updatedByInput.value = values?.updatedByUserId ?? '';
            }

            return row;
        };

        const readRows = () => {
            return Array.from(tbody.querySelectorAll('[data-cb-symbol-restriction-row]'))
                .map((row) => {
                    const symbol = row.querySelector('[data-cb-symbol-restriction-symbol]')?.value?.trim();
                    if (!symbol) {
                        return null;
                    }

                    return {
                        symbol,
                        state: row.querySelector('[data-cb-symbol-restriction-state]')?.value ?? 'Blocked',
                        reason: row.querySelector('[data-cb-symbol-restriction-reason]')?.value?.trim() || null,
                        updatedAtUtc: row.querySelector('[data-cb-symbol-restriction-updated-at]')?.value?.trim() || null,
                        updatedByUserId: row.querySelector('[data-cb-symbol-restriction-updated-by]')?.value?.trim() || null
                    };
                })
                .filter((item) => item !== null);
        };

        const syncPolicyJson = () => {
            let policy;

            try {
                policy = JSON.parse(policyField.value || '{}');
            } catch {
                return;
            }

            policy.symbolRestrictions = readRows();
            policyField.value = JSON.stringify(policy, null, 2);
        };

        const addRow = (values) => {
            const row = createRow(values);
            if (!row) {
                return;
            }

            tbody.appendChild(row);
        };

        tbody.addEventListener('click', (event) => {
            const target = event.target;

            if (!(target instanceof HTMLElement)) {
                return;
            }

            const removeButton = target.closest('[data-cb-symbol-restriction-remove]');
            if (!removeButton) {
                return;
            }

            const row = removeButton.closest('[data-cb-symbol-restriction-row]');
            if (row) {
                row.remove();
            }

            if (!tbody.querySelector('[data-cb-symbol-restriction-row]')) {
                tbody.appendChild(createRow({}));
            }
        });

        addButton.addEventListener('click', () => {
            addRow({});
        });

        form.addEventListener('submit', () => {
            syncPolicyJson();
        });
    });
})();
