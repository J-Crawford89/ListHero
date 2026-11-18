import { startTracking, confirmDiscard, reloadWithConfirmation, navigateApproved, markSessionChanged } from './_content/ListHero.UI/draft-navigation.js';

(() => {
    startTracking();
    let activity = Date.now(), lastCheck = 0, checking = false;
    const session = document.querySelector('#session-context')?.dataset.sessionId || null;
    async function reloadSession() {
        await markSessionChanged();
        reloadWithConfirmation('Your session changed. Reloading discards unsaved form changes. Copy them first if needed, then continue.');
    }
    const token = () => document.querySelector('#session-context input[name="__RequestVerificationToken"]')?.value ?? '';
    async function check() {
        if (checking || document.hidden || Date.now() - activity > 60000 || Date.now() - lastCheck < 300000) return;
        checking = true; lastCheck = Date.now();
        try {
            const response = await fetch('/account/session', { method: 'POST', credentials: 'same-origin', body: new URLSearchParams({ __RequestVerificationToken: token() }) });
            if (response.status === 401 || response.status === 400) { await reloadSession(); return; }
            // Keep the original baseline when reload is declined so later checks still detect the change.
            if (response.ok) { const result = await response.json(); if (session !== result.session) await reloadSession(); }
        } catch { /* Offline: normal page/API recovery remains available. */ }
        finally { checking = false; }
    }
    for (const event of ['pointerdown', 'keydown']) document.addEventListener(event, () => { activity = Date.now(); void check(); }, { passive: true });
    window.addEventListener('focus', () => { activity = Date.now(); lastCheck = 0; void check(); });
    setInterval(check, 60000);
    document.addEventListener('click', async event => {
        const anchor = event.target.closest?.('a');
        if (!anchor || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button !== 0) return;
        const url = new URL(anchor.href, location.href);
        if (url.origin !== location.origin || !['/sign-in', '/account/sign-in'].includes(url.pathname)) return;
        event.preventDefault(); event.stopImmediatePropagation();
        if (!confirmDiscard('Signing in reloads this page. Unsaved changes will not be restored. Copy them first if needed. Continue?')) return;
        if (['/','/welcome','/sign-in'].includes(location.pathname)) { navigateApproved(url.href); return; }
        try {
            const response = await fetch('/account/pending-return', { method: 'POST', credentials: 'same-origin', body: new URLSearchParams({ __RequestVerificationToken: token(), destination: location.pathname + location.search + location.hash }) });
            if (!response.ok) throw new Error('return context');
            navigateApproved(url.href);
        } catch { alert('We could not preserve your return location. Please retry signing in.'); }
    }, true);
})();
