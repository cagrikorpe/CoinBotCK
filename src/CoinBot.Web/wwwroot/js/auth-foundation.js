(function () {
    function setButtonLoading(button, isLoading) {
        if (!button) {
            return;
        }

        button.classList.toggle('is-loading', isLoading);
    }

    function updateChoiceCards(input) {
        if (!input || !input.name) {
            return;
        }

        const scope = input.closest('form, [data-cb-wizard], body') || document;
        scope.querySelectorAll('input[name="' + input.name + '"]').forEach(function (item) {
            item.closest('.cb-choice-card, .cb-choice-pill')?.classList.toggle('is-selected', item.checked);
        });
    }

    function updateSummary(input) {
        const key = input?.getAttribute('data-cb-summary-key');
        if (!key) {
            return;
        }

        let value = input.getAttribute('data-cb-summary-text') || input.value || '';
        if (input.tagName === 'SELECT') {
            value = input.options[input.selectedIndex]?.text || value;
        }

        document.querySelectorAll('[data-cb-summary-target="' + key + '"]').forEach(function (target) {
            target.textContent = value;
        });
    }

    function updateModeDependency(value) {
        document.querySelectorAll('[data-cb-mode-dependent="futures"]').forEach(function (element) {
            element.style.display = value === 'futures' ? '' : 'none';
        });
    }

    function activateWizardStep(wizard, stepIndex) {
        if (!wizard) {
            return;
        }

        const totalSteps = parseInt(wizard.getAttribute('data-cb-total-steps') || '1', 10);
        const safeStep = Math.max(1, Math.min(stepIndex, totalSteps));
        wizard.setAttribute('data-cb-current-step', safeStep.toString());

        wizard.querySelectorAll('[data-cb-step-panel]').forEach(function (panel) {
            panel.classList.toggle('is-active', parseInt(panel.getAttribute('data-cb-step-panel') || '0', 10) === safeStep);
        });

        const stepper = wizard.closest('.cb-auth-panel')?.querySelector('[data-cb-stepper]');
        stepper?.querySelectorAll('[data-cb-step]').forEach(function (step) {
            const number = parseInt(step.getAttribute('data-cb-step') || '0', 10);
            step.classList.toggle('is-current', number === safeStep);
            step.classList.toggle('is-complete', number < safeStep);
        });
    }

    function togglePassword(trigger) {
        const selector = trigger.getAttribute('data-cb-password-target');
        const target = selector ? document.querySelector(selector) : null;
        if (!target) {
            return;
        }

        const isPassword = target.getAttribute('type') !== 'text';
        target.setAttribute('type', isPassword ? 'text' : 'password');
        trigger.textContent = isPassword ? 'Gizle' : 'Göster';
    }

    function syncAckGroup(container) {
        const targetSelector = container?.getAttribute('data-cb-ack-target');
        const target = targetSelector ? document.querySelector(targetSelector) : null;
        if (!container || !target) {
            return;
        }

        const items = Array.from(container.querySelectorAll('[data-cb-ack-item]'));
        const allChecked = items.length > 0 && items.every(function (item) { return item.checked; });

        target.classList.toggle('disabled', !allChecked);
        target.setAttribute('aria-disabled', allChecked ? 'false' : 'true');
    }

    function activateConnectionState(state) {
        document.querySelectorAll('[data-cb-connection-state]').forEach(function (button) {
            button.classList.toggle('is-active', button.getAttribute('data-cb-connection-state') === state);
        });

        document.querySelectorAll('[data-cb-connection-panel]').forEach(function (panel) {
            panel.classList.toggle('is-active', panel.getAttribute('data-cb-connection-panel') === state);
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('[data-cb-wizard]').forEach(function (wizard) {
            activateWizardStep(wizard, parseInt(wizard.getAttribute('data-cb-current-step') || '1', 10));
        });

        document.querySelectorAll('input[type="radio"], select[data-cb-summary-key]').forEach(function (input) {
            if ((input.type === 'radio' && input.checked) || input.tagName === 'SELECT') {
                updateChoiceCards(input);
                updateSummary(input);
                if (input.hasAttribute('data-cb-mode-switch')) {
                    updateModeDependency(input.value);
                }
            }
        });

        document.querySelectorAll('[data-cb-ack-group]').forEach(syncAckGroup);
        activateConnectionState('pending');
    });

    document.addEventListener('click', function (event) {
        const passwordToggle = event.target.closest('[data-cb-password-toggle]');
        if (passwordToggle) {
            event.preventDefault();
            togglePassword(passwordToggle);
        }

        const nextButton = event.target.closest('[data-cb-wizard-next]');
        if (nextButton) {
            event.preventDefault();
            const wizard = nextButton.closest('[data-cb-wizard]');
            const currentStep = parseInt(wizard?.getAttribute('data-cb-current-step') || '1', 10);
            setButtonLoading(nextButton, true);
            window.setTimeout(function () {
                setButtonLoading(nextButton, false);
                activateWizardStep(wizard, currentStep + 1);
            }, 250);
        }

        const prevButton = event.target.closest('[data-cb-wizard-prev]');
        if (prevButton) {
            event.preventDefault();
            const wizard = prevButton.closest('[data-cb-wizard]');
            const currentStep = parseInt(wizard?.getAttribute('data-cb-current-step') || '1', 10);
            activateWizardStep(wizard, currentStep - 1);
        }

        const loadingButton = event.target.closest('[data-cb-button-loading]:not([data-cb-wizard-next]):not([data-cb-connection-test])');
        if (loadingButton) {
            event.preventDefault();
            setButtonLoading(loadingButton, true);
            window.setTimeout(function () {
                setButtonLoading(loadingButton, false);
            }, 900);
        }

        const connectionState = event.target.closest('[data-cb-connection-state]');
        if (connectionState) {
            event.preventDefault();
            activateConnectionState(connectionState.getAttribute('data-cb-connection-state') || 'pending');
        }

        const connectionTest = event.target.closest('[data-cb-connection-test]');
        if (connectionTest) {
            event.preventDefault();
            setButtonLoading(connectionTest, true);
            activateConnectionState('pending');
            window.setTimeout(function () {
                setButtonLoading(connectionTest, false);
                activateConnectionState('success');
            }, 900);
        }
    });

    document.addEventListener('change', function (event) {
        const input = event.target;
        if (input.matches('input[type="radio"], select[data-cb-summary-key]')) {
            updateChoiceCards(input);
            updateSummary(input);
            if (input.hasAttribute('data-cb-mode-switch')) {
                updateModeDependency(input.value);
            }
        }

        const ackGroup = input.closest('[data-cb-ack-group]');
        if (ackGroup && input.matches('[data-cb-ack-item]')) {
            syncAckGroup(ackGroup);
        }
    });

    document.addEventListener('input', function (event) {
        const input = event.target;
        if (!input.matches('[data-cb-otp]')) {
            return;
        }

        input.value = input.value.replace(/\D/g, '').slice(0, 1);
        if (!input.value) {
            return;
        }

        const next = input.nextElementSibling;
        if (next && next.matches('[data-cb-otp]')) {
            next.focus();
        }
    });

    document.addEventListener('keydown', function (event) {
        const input = event.target;
        if (!input.matches('[data-cb-otp]')) {
            return;
        }

        if (event.key === 'Backspace' && !input.value) {
            const prev = input.previousElementSibling;
            if (prev && prev.matches('[data-cb-otp]')) {
                prev.focus();
            }
        }
    });

    document.addEventListener('paste', function (event) {
        const input = event.target;
        if (!input.matches('[data-cb-otp]')) {
            return;
        }

        const text = (event.clipboardData || window.clipboardData)?.getData('text') || '';
        const digits = text.replace(/\D/g, '').slice(0, 6).split('');
        if (digits.length === 0) {
            return;
        }

        event.preventDefault();
        const group = input.closest('[data-cb-otp-group]');
        const fields = group ? Array.from(group.querySelectorAll('[data-cb-otp]')) : [];
        fields.forEach(function (field, index) {
            field.value = digits[index] || '';
        });
        fields[Math.min(digits.length, fields.length) - 1]?.focus();
    });
})();
