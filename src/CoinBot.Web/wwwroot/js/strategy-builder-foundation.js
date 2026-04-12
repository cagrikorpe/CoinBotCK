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
    let selectedTemplate = null;

    function syncForms() {
        const hasSelection = !!selectedTemplate;
        const hasTarget = !!(draftTarget && draftTarget.value);

        templateInputs.forEach(function (input) {
            input.value = hasSelection ? selectedTemplate : '';
        });

        if (startSubmit) {
            startSubmit.disabled = !hasSelection;
        }

        if (draftSubmit) {
            draftSubmit.disabled = !(hasSelection && hasTarget);
        }

        if (selectionSummary) {
            selectionSummary.textContent = hasSelection
                ? 'Şablon seçildi. Strateji taslağını oluşturabilirsiniz.'
                : 'Önce bir şablon seçin.';
        }

        if (targetSummary) {
            const selectedTarget = draftTarget && draftTarget.selectedOptions.length > 0
                ? draftTarget.selectedOptions[0].textContent
                : 'Bot oluştururken strateji seçimi bu listeden yapılır.';
            targetSummary.textContent = hasTarget
                ? 'Mevcut strateji: ' + selectedTarget
                : 'Bot oluştururken strateji seçimi bu listeden yapılır.';
        }
    }

    function selectTemplate(card) {
        selectedTemplate = card ? card.getAttribute('data-cb-template-key') : null;
        templateCards.forEach(function (item) {
            item.classList.toggle('is-selected', item === card);
        });
        syncForms();
    }

    document.addEventListener('click', function (event) {
        const templateCard = event.target.closest('[data-cb-template-card]');
        if (!templateCard || !page.contains(templateCard)) {
            return;
        }

        event.preventDefault();
        selectTemplate(templateCard);
    });

    document.addEventListener('change', function (event) {
        if (event.target && event.target === draftTarget) {
            syncForms();
        }
    });

    syncForms();
})();
