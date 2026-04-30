(function () {
    const page = document.querySelector('[data-cb-positions-page]');
    if (!page) {
        return;
    }

    function setTab(name) {
        page.querySelectorAll('[data-cb-positions-tab-trigger]').forEach(function (tab) {
            tab.classList.toggle('is-active', tab.getAttribute('data-cb-positions-tab-trigger') === name);
        });
        page.querySelectorAll('[data-cb-positions-tab-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-positions-tab-panel') !== name);
        });
    }

    function setButtonLoading(button, isLoading) {
        if (!button) {
            return;
        }

        button.classList.toggle('is-loading', isLoading);
        button.toggleAttribute('disabled', isLoading);
    }

    document.addEventListener('click', function (event) {
        const tabTrigger = event.target.closest('[data-cb-positions-tab-trigger]');
        const refreshTrigger = event.target.closest('[data-cb-positions-refresh]');
        const manualCloseTrigger = event.target.closest('[data-cb-user-manual-close-button]');

        if (tabTrigger) {
            event.preventDefault();
            setTab(tabTrigger.getAttribute('data-cb-positions-tab-trigger'));
        }

        if (refreshTrigger) {
            event.preventDefault();
            setButtonLoading(refreshTrigger, true);
            window.location.reload();
        }

        if (manualCloseTrigger) {
            const panel = manualCloseTrigger.closest('[data-cb-user-manual-close-panel]');
            const form = panel ? panel.querySelector('[data-cb-user-manual-close-form]') : null;
            const checkbox = panel ? panel.querySelector('[data-cb-user-manual-close-checkbox]') : null;

            if (!panel || !form || !checkbox) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();

            if (!checkbox.checked) {
                checkbox.focus();
                return;
            }

            const symbol = form.querySelector('input[name="symbol"]')?.value?.trim();
            const message = symbol
                ? symbol + ' reduce-only close emri gonderilsin mi?'
                : 'Reduce-only close emri gonderilsin mi?';

            if (!window.confirm(message)) {
                return;
            }

            setButtonLoading(manualCloseTrigger, true);
            if (typeof form.requestSubmit === 'function') {
                form.requestSubmit(manualCloseTrigger);
                return;
            }

            form.submit();
        }
    });

    setTab(page.getAttribute('data-cb-positions-default-tab') || 'positions');
})();
