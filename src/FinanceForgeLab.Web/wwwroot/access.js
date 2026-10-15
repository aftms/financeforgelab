(() => {
    const password = document.getElementById('password');
    const confirm = document.getElementById('confirm');
    const status = document.getElementById('match-status');
    const caps = document.getElementById('caps-warning');
    document.querySelectorAll('[data-reveal]').forEach(button => {
        const input = document.getElementById(button.dataset.reveal);
        const originalLabel = button.getAttribute('aria-label');
        button.hidden = false;
        button.addEventListener('click', () => {
            const visible = input.type === 'password';
            input.type = visible ? 'text' : 'password';
            button.setAttribute('aria-pressed', String(visible));
            button.setAttribute('aria-label', visible ? originalLabel.replace('Mostrar', 'Ocultar') : originalLabel);
        });
    });
    [password, confirm].filter(Boolean).forEach(input => {
        for (const event of ['keyup', 'keydown']) {
            input.addEventListener(event, e => { caps.hidden = !e.getModifierState('CapsLock'); });
        }
        input.addEventListener('blur', () => { caps.hidden = true; });
        input.addEventListener('input', () => {
            if (!confirm) return;
            const matches = password.value === confirm.value;
            const hasConfirmation = confirm.value.length > 0;
            confirm.setCustomValidity(hasConfirmation && !matches ? 'As palavras-passe devem ser iguais.' : '');
            status.textContent = !hasConfirmation ? '' : matches ? 'As palavras-passe coincidem.' : 'As palavras-passe ainda não coincidem.';
            status.dataset.match = hasConfirmation ? String(matches) : '';
        });
    });
})();
