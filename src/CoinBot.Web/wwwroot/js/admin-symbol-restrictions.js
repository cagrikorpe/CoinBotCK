(function () {
    const forms = document.querySelectorAll('[data-cb-symbol-restriction-editor]');

    const describeEffect = (state) => {
        switch (state) {
            case 'Blocked':
                return 'Tum emirler fail-closed bloklanir.';
            case 'ReviewOnly':
                return 'Emir yolu acik kalir, sonuc review/advisory olarak isaretlenir.';
            case 'ReduceOnly':
                return 'Yalnizca exposure azaltan emirler gecer.';
            case 'CloseOnly':
                return 'Yalnizca pozisyon kapatan emirler gecer.';
            default:
                return 'Policy evaluation uygulanir.';
        }
    };

    forms.forEach((form) => {
        const tbody = form.querySelector('[data-cb-symbol-restriction-rows]');
        const template = form.querySelector('template[data-cb-symbol-restriction-template]');
        const addButton = form.querySelector('[data-cb-symbol-restriction-add]');

        if (!tbody || !template || !addButton) {
            return;
        }

        const emptyMessage = tbody.dataset.cbSymbolRestrictionEmptyMessage || 'Aktif restriction yok.';
        const emptyColspan = tbody.dataset.cbSymbolRestrictionEmptyColspan || '7';

        const removeEmptyRow = () => {
            const emptyRow = tbody.querySelector('[data-cb-symbol-restriction-empty]');
            if (emptyRow) {
                emptyRow.remove();
            }
        };

        const ensureEmptyRow = () => {
            if (tbody.querySelector('[data-cb-symbol-restriction-row]')) {
                return;
            }

            if (tbody.querySelector('[data-cb-symbol-restriction-empty]')) {
                return;
            }

            const row = document.createElement('tr');
            row.setAttribute('data-cb-symbol-restriction-empty', '');
            row.innerHTML = `<td colspan="${emptyColspan}" class="text-muted">${emptyMessage}</td>`;
            tbody.appendChild(row);
        };

        const syncRowState = (row) => {
            const stateInput = row.querySelector('[data-cb-symbol-restriction-state]');
            const effectNode = row.querySelector('[data-cb-symbol-restriction-effect]');

            if (!stateInput || !effectNode) {
                return;
            }

            effectNode.textContent = describeEffect(stateInput.value || 'Blocked');
        };

        const updateIndexes = () => {
            const rows = Array.from(tbody.querySelectorAll('[data-cb-symbol-restriction-row]'));

            rows.forEach((row, index) => {
                const symbolInput = row.querySelector('[data-cb-symbol-restriction-symbol]');
                const stateInput = row.querySelector('[data-cb-symbol-restriction-state]');
                const reasonInput = row.querySelector('[data-cb-symbol-restriction-reason]');

                if (symbolInput) {
                    symbolInput.name = `restrictions[${index}].Symbol`;
                }

                if (stateInput) {
                    stateInput.name = `restrictions[${index}].State`;
                }

                if (reasonInput) {
                    reasonInput.name = `restrictions[${index}].Reason`;
                }

                syncRowState(row);
            });

            ensureEmptyRow();
        };

        const addRow = () => {
            removeEmptyRow();

            const fragment = template.content.cloneNode(true);
            const row = fragment.querySelector('[data-cb-symbol-restriction-row]');

            if (!row) {
                return;
            }

            tbody.appendChild(row);
            updateIndexes();

            const symbolInput = row.querySelector('[data-cb-symbol-restriction-symbol]');
            if (symbolInput instanceof HTMLElement) {
                symbolInput.focus();
            }
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

            updateIndexes();
        });

        tbody.addEventListener('change', (event) => {
            const target = event.target;
            if (!(target instanceof HTMLElement)) {
                return;
            }

            if (!target.matches('[data-cb-symbol-restriction-state]')) {
                return;
            }

            const row = target.closest('[data-cb-symbol-restriction-row]');
            if (row) {
                syncRowState(row);
            }
        });

        addButton.addEventListener('click', () => {
            addRow();
        });

        form.addEventListener('submit', () => {
            updateIndexes();
        });

        updateIndexes();
    });
})();
