import { flushAll } from './draft-recovery.js';
const forms = new Map();
const observers = new Set();
let tracking = false, staleSession = false, skipUnload = false;

function snapshot(form) {
    return JSON.stringify(Array.from(form.querySelectorAll('input, textarea, select'))
        .filter(field => !['hidden', 'submit', 'button'].includes(field.type))
        .map(field => [field.id || field.name, field.type,
            ['checkbox', 'radio'].includes(field.type) ? field.checked
                : field.multiple ? Array.from(field.selectedOptions, option => option.value) : field.value]));
}

function reconcile() {
    for (const form of forms.keys()) if (!form.isConnected) forms.delete(form);
    for (const form of document.querySelectorAll('form[data-draft]')) {
        const revision = form.dataset.draftRevision || '';
        if (!forms.has(form) || forms.get(form).revision !== revision)
            forms.set(form, { revision, value: snapshot(form) });
    }
}

export function hasChanges(formId) {
    reconcile();
    return Array.from(forms, ([form, saved]) => (!formId || form.id === formId) && snapshot(form) !== saved.value).some(Boolean);
}

export function startTracking() {
    if (tracking) return;
    tracking = true;
    reconcile();
    new MutationObserver(reconcile).observe(document.documentElement,
        { childList: true, subtree: true, attributes: true, attributeFilter: ['data-draft-revision'] });
    window.addEventListener('beforeunload', event => {
        if (!skipUnload && hasChanges()) { event.preventDefault(); event.returnValue = ''; }
    });
}

export function accept(formId) {
    reconcile();
    for (const [form, saved] of forms)
        if (!formId || form.id === formId) saved.value = snapshot(form);
}
export function markRestored(formId) {
    reconcile();
    for (const [form, saved] of forms) if (form.id === formId) saved.value = '__recovered_unsaved_draft__';
}

export async function confirmDiscard(message = 'Leave this page with unsaved changes? Available recovery drafts stay in this tab.', formId) {
    if (!hasChanges(formId)) return true;
    if (!window.confirm(message)) return false;
    await flushAll();
    return true;
}

function navigate(action) {
    skipUnload = true;
    action();
    // If navigation is blocked, restore protection without treating any draft as saved.
    setTimeout(() => { skipUnload = false; }, 1000);
}

export async function reloadWithConfirmation(message) {
    if (!await confirmDiscard(message)) return false;
    navigate(() => location.reload());
    return true;
}

export function navigateApproved(url) { navigate(() => location.assign(url)); }

export async function markSessionChanged() {
    staleSession = true;
    // One disconnected subscriber must not prevent the remaining circuit from recovering.
    // Keep failed subscribers until disposal so a later focus check can retry after reconnection.
    await Promise.allSettled(Array.from(observers, receiver => receiver.invokeMethodAsync('SessionChanged')));
}

export async function watch(receiver) {
    startTracking();
    observers.add(receiver);
    try { if (staleSession) await receiver.invokeMethodAsync('SessionChanged'); }
    catch (error) { observers.delete(receiver); throw error; }
    return { dispose() { observers.delete(receiver); } };
}
