export function applySuggestions(prefix, context, url, result) {
    const marker = document.getElementById(prefix + '-import-context');
    const link = document.getElementById(prefix + '-link');
    if (marker?.dataset.importContext !== context || link?.value.trim() !== url) return 0;
    const form = link.closest('form');
    if (!form) return 0;
    let count = 0;
    for (const [suffix, value] of [['name', result.name], ['description', result.description], ['image', result.imageUrl], ['price', result.price]]) {
        if (value === null || value === undefined || value === '') continue;
        const field = document.getElementById(prefix + '-' + suffix);
        if (!field || !form.contains(field) || field.disabled || field.readOnly || field.value.trim() !== '') continue;
        field.value = String(value);
        field.dispatchEvent(new Event('input', { bubbles: true }));
        field.dispatchEvent(new Event('change', { bubbles: true }));
        count++;
    }
    // The dispatched events notify Blazor, navigation guards and automatic draft recovery.
    return count;
}
