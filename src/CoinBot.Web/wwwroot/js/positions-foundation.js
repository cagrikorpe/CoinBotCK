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

        if (tabTrigger) {
            event.preventDefault();
            setTab(tabTrigger.getAttribute('data-cb-positions-tab-trigger'));
        }

        if (refreshTrigger) {
            event.preventDefault();
            setButtonLoading(refreshTrigger, true);
            window.location.reload();
        }
    });

    setTab(page.getAttribute('data-cb-positions-default-tab') || 'positions');
})();
