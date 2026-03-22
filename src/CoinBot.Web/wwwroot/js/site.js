(function () {
    const body = document.body;
    const storage = window.localStorage;
    const asideToggle = document.getElementById('kt_aside_toggle');
    const mobileAsideToggle = document.getElementById('kt_aside_mobile_toggle_desktop');
    const themeToggle = document.getElementById('cb_theme_toggle');
    const drawerBackdrop = document.querySelector('.cb-drawer-backdrop');

    function applyStoredState() {
        if (storage.getItem('coinbot.ui.aside') === 'minimized') {
            body.classList.add('aside-minimize');
        }

        if (storage.getItem('coinbot.ui.theme') === 'light') {
            body.classList.add('theme-light-preview');
            body.setAttribute('data-cb-theme', 'light');
        }
    }

    function toggleAside() {
        body.classList.toggle('aside-minimize');
        storage.setItem('coinbot.ui.aside', body.classList.contains('aside-minimize') ? 'minimized' : 'expanded');
    }

    function toggleTheme() {
        body.classList.toggle('theme-light-preview');
        const isLight = body.classList.contains('theme-light-preview');
        body.setAttribute('data-cb-theme', isLight ? 'light' : 'dark');
        storage.setItem('coinbot.ui.theme', isLight ? 'light' : 'dark');
    }

    let lastDrawerTrigger = null;

    function focusDrawerTarget(drawer) {
        const focusable = drawer.querySelector('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
        if (focusable) {
            focusable.focus();
            return;
        }

        drawer.focus();
    }

    function openDrawer(selector, trigger) {
        const target = document.querySelector(selector);
        if (!target) {
            return;
        }

        document.querySelectorAll('.cb-drawer.is-open').forEach(function (drawer) {
            drawer.classList.remove('is-open');
            drawer.setAttribute('aria-hidden', 'true');
        });

        lastDrawerTrigger = trigger || document.activeElement;
        target.classList.add('is-open');
        target.setAttribute('aria-hidden', 'false');
        target.setAttribute('tabindex', '-1');
        drawerBackdrop?.classList.add('is-open');
        body.classList.add('cb-drawer-open');
        window.setTimeout(function () { focusDrawerTarget(target); }, 30);
    }

    function closeDrawer() {
        document.querySelectorAll('.cb-drawer.is-open').forEach(function (drawer) {
            drawer.classList.remove('is-open');
            drawer.setAttribute('aria-hidden', 'true');
        });
        drawerBackdrop?.classList.remove('is-open');
        body.classList.remove('cb-drawer-open');

        if (lastDrawerTrigger && typeof lastDrawerTrigger.focus === 'function') {
            lastDrawerTrigger.focus();
        }
    }

    function setModalContent(trigger) {
        const targetSelector = trigger.getAttribute('data-target');
        if (!targetSelector) {
            return;
        }

        const modal = document.querySelector(targetSelector);
        if (!modal) {
            return;
        }

        const title = trigger.getAttribute('data-cb-modal-title');
        const message = trigger.getAttribute('data-cb-modal-message');
        const tone = trigger.getAttribute('data-cb-modal-tone');
        const confirmText = trigger.getAttribute('data-cb-modal-confirm');

        const titleEl = modal.querySelector('.modal-title');
        const messageEl = modal.querySelector('[id$="_message"]');
        const toneEl = modal.querySelector('[id$="_tone"]');
        const confirmEl = modal.querySelector('[id$="_confirm"]');

        if (title && titleEl) {
            titleEl.textContent = title;
        }

        if (message && messageEl) {
            messageEl.textContent = message;
        }

        if (confirmText && confirmEl) {
            confirmEl.textContent = confirmText;
        }

        if (toneEl) {
            toneEl.classList.remove('cb-modal-tone-warning', 'cb-modal-tone-danger');
            toneEl.classList.add(tone === 'danger' ? 'cb-modal-tone-danger' : 'cb-modal-tone-warning');
            if (message) {
                toneEl.textContent = tone === 'danger' ? 'Bu aksiyon kontrollü şekilde onay gerektirir.' : 'Bu aksiyon ek doğrulama öncesi kontrollü onay akışı için hazırlanmıştır.';
            }
        }
    }


    document.addEventListener('DOMContentLoaded', function () {
        applyStoredState();
        body.classList.remove('page-loading');
    });

    asideToggle?.addEventListener('click', toggleAside);
    mobileAsideToggle?.addEventListener('click', toggleAside);
    themeToggle?.addEventListener('click', toggleTheme);

    document.addEventListener('click', function (event) {
        const drawerTrigger = event.target.closest('[data-cb-drawer-target]');
        const drawerClose = event.target.closest('[data-cb-drawer-close]');

        if (drawerTrigger) {
            event.preventDefault();
            openDrawer(drawerTrigger.getAttribute('data-cb-drawer-target'), drawerTrigger);
        }

        const modalTrigger = event.target.closest('[data-cb-modal-title]');
        if (modalTrigger) {
            setModalContent(modalTrigger);
        }

        if (drawerClose) {
            event.preventDefault();
            closeDrawer();
        }
    });

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') {
            closeDrawer();
        }
    });
})();
