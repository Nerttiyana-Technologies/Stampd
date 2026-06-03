// Designer PDF render + click-to-place handler. Renders the uploaded PDF via PDF.js and
// captures click coordinates on each page (translated to percentages so the backend can
// reproduce the position regardless of zoom level).

import * as pdfjs from 'pdfjs';

let workerInitialized = false;
async function ensureWorker() {
    if (workerInitialized) return;
    const workerMod = await import('pdfjs-worker');
    pdfjs.GlobalWorkerOptions.workerSrc = workerMod.default || workerMod;
    workerInitialized = true;
}

export async function render(host, base64Pdf, dotnetRef) {
    if (!host || !base64Pdf) return;
    host.innerHTML = '';
    await ensureWorker();

    const pdfBytes = Uint8Array.from(atob(base64Pdf), c => c.charCodeAt(0));
    const pdf = await pdfjs.getDocument({ data: pdfBytes }).promise;

    for (let pageNo = 1; pageNo <= pdf.numPages; pageNo++) {
        const page = await pdf.getPage(pageNo);
        const viewport = page.getViewport({ scale: 1.25 });

        const pageWrapper = document.createElement('div');
        pageWrapper.className = 'designer-page';
        pageWrapper.style.width = viewport.width + 'px';
        pageWrapper.style.height = viewport.height + 'px';
        pageWrapper.dataset.pageNumber = String(pageNo);

        const canvas = document.createElement('canvas');
        canvas.width = viewport.width;
        canvas.height = viewport.height;
        pageWrapper.appendChild(canvas);

        // Field overlay layer; absolutely positioned children get added by syncOverlays().
        const overlayLayer = document.createElement('div');
        overlayLayer.className = 'designer-overlay-layer';
        overlayLayer.style.cssText = 'position:absolute;inset:0;';
        pageWrapper.appendChild(overlayLayer);

        // Click-to-place: percentage coords relative to the page.
        pageWrapper.addEventListener('click', async (e) => {
            // Ignore clicks that landed on existing field overlays (those handle their
            // own selection).
            if (e.target.classList && e.target.classList.contains('designer-field')) return;
            const rect = pageWrapper.getBoundingClientRect();
            const xPct = ((e.clientX - rect.left) / rect.width) * 100;
            const yPct = ((e.clientY - rect.top) / rect.height) * 100;
            await dotnetRef.invokeMethodAsync('OnPageClick', pageNo, xPct, yPct);
        });

        host.appendChild(pageWrapper);
        await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
    }
}

export function syncOverlays(host, fields) {
    if (!host) return;
    const pages = host.querySelectorAll('.designer-page');
    pages.forEach(page => {
        const overlay = page.querySelector('.designer-overlay-layer');
        if (!overlay) return;
        overlay.innerHTML = '';
        const pageNo = Number(page.dataset.pageNumber);
        fields
            .filter(f => f.pageNumber === pageNo)
            .forEach(f => {
                const el = document.createElement('div');
                el.className = 'designer-field';
                el.style.position = 'absolute';
                el.style.left = f.x + '%';
                el.style.top = f.y + '%';
                el.style.width = f.width + '%';
                el.style.height = Math.max(2.5, f.height) + '%';
                el.style.boxSizing = 'border-box';
                el.textContent = (f.label || f.kind) + (f.assignedRoleName ? ' · ' + f.assignedRoleName : '');
                overlay.appendChild(el);
            });
    });
}
