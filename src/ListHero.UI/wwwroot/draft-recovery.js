const watchers = new Set();
export async function flushAll() {
    let timeout;
    try { await Promise.race([Promise.all(Array.from(watchers, watcher => watcher.flush())), new Promise(resolve => { timeout = setTimeout(resolve, 2000); })]); }
    finally { clearTimeout(timeout); }
}
export async function confirmRestore(formId) {
    const navigation = await import('./draft-navigation.js');
    return !navigation.hasChanges(formId) || window.confirm('Replace the unsaved values in this form with the recovery draft?');
}
export function watch(formId, receiver, initialEpoch) {
    let timer, changed = false, epoch = initialEpoch, sequence = 0, disposed = false, queue = Promise.resolve();
    const form = () => document.getElementById(formId);
    const fields = () => Array.from(form()?.querySelectorAll('input, textarea, select') || [])
        .filter(field => field.id && !field.readOnly && !['hidden', 'submit', 'button', 'password', 'file'].includes(field.type))
        .map(field => ({ id: field.id, value: field.value, checked: field.checked === true, type: field.type }));
    const watcher = {
        checkpoint() { changed = true; sequence++; return watcher.flush(); },
        flush() {
            clearTimeout(timer);
            if (disposed || !changed || !form()) return queue;
            changed = false;
            const values = fields(), version = epoch, serial = sequence;
            queue = queue.catch(() => {}).then(() => disposed ? undefined : receiver.invokeMethodAsync('DraftChanged', values, version, serial)).catch(() => {});
            return queue;
        },
        reset(version) { epoch = version; sequence = 0; changed = false; clearTimeout(timer); },
        async restore(values) {
            const target = form(); if (!target) return false;
            for (const value of values) {
                const field = document.getElementById(value.id);
                if (!field || !target.contains(field) || field.readOnly || field.type !== value.type) continue;
                if (['checkbox', 'radio'].includes(field.type)) field.checked = value.checked;
                else field.value = value.value;
                field.dispatchEvent(new Event('input', { bubbles: true }));
                field.dispatchEvent(new Event('change', { bubbles: true }));
            }
            const navigation = await import('./draft-navigation.js'); navigation.markRestored(formId);
            return true;
        },
        dispose() { disposed = true; clearTimeout(timer); watchers.delete(watcher); document.removeEventListener('input', input, true); document.removeEventListener('change', input, true); }
    };
    function input(event) {
        if (!form()?.contains(event.target) || event.target.closest('[data-draft]')?.id !== formId) return;
        sequence++;
        if (!changed) void receiver.invokeMethodAsync('DraftPending', epoch, sequence).catch(() => {});
        changed = true; clearTimeout(timer); timer = setTimeout(() => void watcher.flush(), 400);
    }
    document.addEventListener('input', input, true); document.addEventListener('change', input, true);
    watchers.add(watcher);
    // Capture input made after initial form tracking but before this circuit watcher attached.
    void import('./draft-navigation.js').then(navigation => {
        if (!disposed && navigation.hasChanges(formId)) return watcher.checkpoint();
    }).catch(() => {});
    return watcher;
}

// Only opaque ciphertext and a random anonymous namespace reach browser storage.
globalThis.ListHeroDraftStorage = {
    anonymous() { let value = sessionStorage.getItem('listhero.draft.anonymous.v1'); if (!value) { value = crypto.randomUUID(); sessionStorage.setItem('listhero.draft.anonymous.v1', value); } return value; },
    get(key) { const value = sessionStorage.getItem(key); if (!value) return null; try { const record = JSON.parse(value); if (typeof record?.cipher === 'string') return record.cipher; } catch { } sessionStorage.removeItem(key); return null; },
    keys(prefix) { return Object.keys(sessionStorage).filter(key => key.startsWith(prefix)); },
    prune(prefix, now) { for (const key of Object.keys(sessionStorage).filter(key => key.startsWith(prefix))) { try { const record = JSON.parse(sessionStorage.getItem(key)); if (typeof record?.cipher !== 'string' || !Number.isFinite(record.expires) || record.expires <= now) sessionStorage.removeItem(key); } catch { sessionStorage.removeItem(key); } } },
    set(key, value, prefix, expires) { if (!sessionStorage.getItem(key) && Object.keys(sessionStorage).filter(key => key.startsWith(prefix)).length >= 32) throw new Error('Draft storage full'); sessionStorage.setItem(key, JSON.stringify({ cipher: value, expires })); },
    remove(key) { sessionStorage.removeItem(key); }
};
