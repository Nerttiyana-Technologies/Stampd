// PDF.js host module for the signer page. Loads the recipient's PDF, renders each page
// onto a <canvas>, then overlays field rectangles at percentage coordinates so the
// signer can see the document body behind the placement.
//
// Styling lives in wwwroot/css/site.css under .pdf-page / .pdf-page-label /
// .pdf-field-overlay so dark/light themes follow the global tokens.

import * as pdfjs from 'pdfjs';

let workerInitialized = false;
async function ensureWorker() {
    if (workerInitialized) return;
    const workerMod = await import('pdfjs-worker');
    pdfjs.GlobalWorkerOptions.workerSrc = workerMod.default || workerMod;
    workerInitialized = true;
}

/**
 * Renders the recipient's actual source PDF with field overlays on top.
 * @param {HTMLElement} host - Container the pages render into.
 * @param {string} base64Pdf - Source PDF as base64 (no data: prefix).
 * @param {Array} fields - Field placements [{ page, x, y, w, h, label }].
 */
export async function renderPdf(host, base64Pdf, fields) {
    if (!host || !base64Pdf) return;
    host.innerHTML = '';
    await ensureWorker();

    const pdfBytes = Uint8Array.from(atob(base64Pdf), c => c.charCodeAt(0));
    const pdf = await pdfjs.getDocument({ data: pdfBytes }).promise;

    for (let pageNo = 1; pageNo <= pdf.numPages; pageNo++) {
        const page = await pdf.getPage(pageNo);
        const viewport = page.getViewport({ scale: 1.25 });

        const pageWrapper = document.createElement('div');
        pageWrapper.className = 'pdf-page';
        pageWrapper.style.width = viewport.width + 'px';
        pageWrapper.style.height = viewport.height + 'px';

        const canvas = document.createElement('canvas');
        canvas.width = viewport.width;
        canvas.height = viewport.height;
        canvas.style.display = 'block';
        pageWrapper.appendChild(canvas);

        // Field overlays for this page, percentage-positioned over the rendered canvas.
        fields.filter(f => f.page === pageNo).forEach(f => {
            const overlay = document.createElement('div');
            overlay.className = 'pdf-field-overlay';
            overlay.style.left = f.x + '%';
            overlay.style.top = f.y + '%';
            overlay.style.width = f.w + '%';
            overlay.style.height = Math.max(2.5, f.h) + '%';
            overlay.textContent = f.label;
            pageWrapper.appendChild(overlay);
        });

        host.appendChild(pageWrapper);
        await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
    }
}

/**
 * Fallback used when the PDF couldn't be fetched. Renders placeholder pages with the
 * field outlines so the signer still has spatial context.
 */
export function renderPlaceholder(host, fields) {
    if (!host) return;
    host.innerHTML = '';

    const PAGE_WIDTH = 612;
    const PAGE_HEIGHT = 792;

    const pages = new Set(fields.map(f => f.page));
    if (pages.size === 0) pages.add(1);

    Array.from(pages).sort((a, b) => a - b).forEach(pageNo => {
        const pageDiv = document.createElement('div');
        pageDiv.className = 'pdf-page';
        pageDiv.style.width = PAGE_WIDTH + 'px';
        pageDiv.style.height = PAGE_HEIGHT + 'px';

        const heading = document.createElement('div');
        heading.className = 'pdf-page-label';
        heading.textContent = `Page ${pageNo} preview`;
        pageDiv.appendChild(heading);

        fields.filter(f => f.page === pageNo).forEach(f => {
            const overlay = document.createElement('div');
            overlay.className = 'pdf-field-overlay';
            overlay.style.left = (f.x / 100 * PAGE_WIDTH) + 'px';
            overlay.style.top = (f.y / 100 * PAGE_HEIGHT) + 'px';
            overlay.style.width = (f.w / 100 * PAGE_WIDTH) + 'px';
            overlay.style.height = Math.max(20, f.h / 100 * PAGE_HEIGHT) + 'px';
            overlay.textContent = f.label;
            pageDiv.appendChild(overlay);
        });

        host.appendChild(pageDiv);
    });
}
