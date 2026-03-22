(function () {
    document.addEventListener('click', function (event) {
        const chip = event.target.closest('[data-cb-toggle-active]');
        if (!chip) {
            return;
        }

        const group = chip.closest('[data-cb-toggle-group]');
        if (!group) {
            return;
        }

        group.querySelectorAll('[data-cb-toggle-active]').forEach(function (item) {
            item.classList.remove('is-active');
        });

        chip.classList.add('is-active');
    });
})();
