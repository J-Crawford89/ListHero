export function watch(receiver) {
    let disposed = false, refreshing = false, timer;
    const active = () => !document.hidden && navigator.onLine;
    const pause = () => { clearTimeout(timer); timer = undefined; };
    function schedule() {
        pause();
        if (!disposed && active()) timer = setTimeout(refresh, 20000);
    }
    async function refresh() {
        pause();
        if (disposed || refreshing || !active()) return;
        refreshing = true;
        try { await receiver.invokeMethodAsync('RefreshViewer'); }
        catch { /* A disconnected circuit can retry after reconnecting. Never replay writes. */ }
        finally { refreshing = false; schedule(); }
    }
    function activityChanged() {
        pause();
        if (active()) void refresh();
    }
    document.addEventListener('visibilitychange', activityChanged);
    for (const event of ['online', 'offline', 'focus']) window.addEventListener(event, activityChanged);
    schedule();
    return {
        dispose() {
            disposed = true;
            pause();
            document.removeEventListener('visibilitychange', activityChanged);
            for (const event of ['online', 'offline', 'focus']) window.removeEventListener(event, activityChanged);
        }
    };
}
