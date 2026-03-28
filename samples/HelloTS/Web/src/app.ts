// Type declarations for window.mio.invoke bridge
interface MioInvokeResult<T> {
    then(f: (v: T) => void): this;
    catch(f: (e: Error) => void): this;
}

interface MioApi {
    invoke<T = unknown>(cmd: string, args?: unknown): MioInvokeResult<T>;
}

declare const window: Window & {
    mio: MioApi;
};

interface ServerTime {
    iso: string;
    local: string;
}

interface ComputeResult {
    sum: number;
    product: number;
    power: number;
}

const pages: string[] = ['home', 'compute'];
let currentPage: string = 'home';

function navigate(page: string): void {
    for (let i = 0; i < pages.length; i++) {
        const pageEl = document.getElementById('page-' + pages[i]);
        if (pageEl) pageEl.style.display = 'none';
        const navEl = document.getElementById('nav-' + pages[i]);
        if (navEl) navEl.className = 'nav-item';
    }
    const target = document.getElementById('page-' + page);
    if (target) target.style.display = '';
    const activeNav = document.getElementById('nav-' + page);
    if (activeNav) activeNav.className = 'nav-item active';
    currentPage = page;
}

// Make navigate global so onclick handlers can call it
(window as any).navigate = navigate;

function updateClock(): void {
    window.mio.invoke<ServerTime>('getServerTime')
        .then((data: ServerTime) => {
            const el = document.getElementById('clock');
            if (el) el.textContent = data.local;
        })
        .catch((e: Error) => console.error('clock error:', e));
}

updateClock();
setInterval(updateClock, 1000);

function runCompute(): void {
    const aEl = document.getElementById('input-a') as HTMLInputElement | null;
    const bEl = document.getElementById('input-b') as HTMLInputElement | null;
    if (!aEl || !bEl) return;

    const a: number = parseFloat(aEl.value) || 0;
    const b: number = parseFloat(bEl.value) || 0;

    window.mio.invoke<ComputeResult>('compute', { a, b })
        .then((result: ComputeResult) => {
            const el = document.getElementById('compute-result');
            if (el) {
                el.textContent =
                    `${a} + ${b} = ${result.sum}\n` +
                    `${a} × ${b} = ${result.product}\n` +
                    `${a} ^ ${b} = ${result.power}`;
            }
        })
        .catch((e: Error) => console.error('compute error:', e));
}

(window as any).runCompute = runCompute;
