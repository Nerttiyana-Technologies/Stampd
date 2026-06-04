// Designer PDF render + interactive field placement, move, resize, and delete.
//
// Communication with Blazor:
//   render(host, base64, dotnetRef)           — bootstrap; sets up event handlers
//   syncOverlays(host, fields, selectedIndex) — redraw all field overlays
//
// JS → Blazor invocations on dotnetRef:
//   OnPageClick(pageNo, xPct, yPct)        — empty-page click; place a new field
//   OnFieldSelected(index)                 — user clicked an existing field
//   OnFieldMoved(index, xPct, yPct)        — drag finished; commit new position
//   OnFieldResized(index, xPct, yPct, w, h)— resize finished; commit new geometry
//   OnFieldDeleted(index)                  — × overlay or Delete key
//   OnSelectionCleared()                   — clicked off / pressed Escape

import * as pdfjs from 'pdfjs';

let workerInitialized = false;
async function ensureWorker() {
    if (workerInitialized) return;
    const workerMod = await import('pdfjs-worker');
    pdfjs.GlobalWorkerOptions.workerSrc = workerMod.default || workerMod;
    workerInitialized = true;
}

// Snap to 1% increments. Tweakable; clean coordinates are easier to reason about.
const SNAP = 1;
const MIN_W = 2;     // % minimums to prevent collapse
const MIN_H = 2;

function snap(v) { return Math.round(v / SNAP) * SNAP; }
function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

// Per-host state (one designer page can host multiple PDF pages).
const hostState = new WeakMap();

export async function render(host, base64Pdf, dotnetRef) {
    if (!host || !base64Pdf) return;
    host.innerHTML = '';
    await ensureWorker();

    hostState.set(host, {
        dotnetRef,
        fields: [],
        selectedIndex: null,
        drag: null,    // { mode: 'move'|'resize', index, originX, originY, original }
    });

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

        const overlayLayer = document.createElement('div');
        overlayLayer.className = 'designer-overlay-layer';
        overlayLayer.style.cssText = 'position:absolute;inset:0;';
        pageWrapper.appendChild(overlayLayer);

        // Click on empty page area → place a new field. Anything inside an existing
        // field (body, label, handles, delete button) bubbles up here too, so we walk
        // the DOM path up to the pageWrapper and bail if we cross a .designer-field.
        // We don't trust closest() — some browsers' ES module cache + minification has
        // bitten us; the manual walk is bulletproof.
        pageWrapper.addEventListener('click', async (e) => {
            const state = hostState.get(host);
            if (!state || state.drag) return;

            let node = e.target;
            while (node && node !== pageWrapper) {
                if (node.classList && node.classList.contains('designer-field')) {
                    return; // click landed on a placed field — let the field handlers run
                }
                node = node.parentElement;
            }

            const rect = pageWrapper.getBoundingClientRect();
            const xPct = ((e.clientX - rect.left) / rect.width) * 100;
            const yPct = ((e.clientY - rect.top) / rect.height) * 100;
            await state.dotnetRef.invokeMethodAsync('OnPageClick', pageNo, snap(xPct), snap(yPct));
        });

        host.appendChild(pageWrapper);
        await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
    }

    // Global keyboard: Delete removes selection, Esc deselects, arrows nudge.
    if (!host._stampdKeysBound) {
        host._stampdKeysBound = true;
        document.addEventListener('keydown', async (e) => {
            const state = hostState.get(host);
            if (!state || state.selectedIndex === null) return;
            // Ignore key events when focus is on a text input (e.g. role name).
            const ae = document.activeElement;
            if (ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA' || ae.tagName === 'SELECT')) return;

            const idx = state.selectedIndex;
            const f = state.fields[idx];
            if (!f) return;

            if (e.key === 'Delete' || e.key === 'Backspace') {
                e.preventDefault();
                await state.dotnetRef.invokeMethodAsync('OnFieldDeleted', idx);
            } else if (e.key === 'Escape') {
                e.preventDefault();
                await state.dotnetRef.invokeMethodAsync('OnSelectionCleared');
            } else if (e.key.startsWith('Arrow')) {
                e.preventDefault();
                let dx = 0, dy = 0;
                if (e.key === 'ArrowLeft')  dx = -SNAP;
                if (e.key === 'ArrowRight') dx =  SNAP;
                if (e.key === 'ArrowUp')    dy = -SNAP;
                if (e.key === 'ArrowDown')  dy =  SNAP;
                const newX = clamp(snap(f.x + dx), 0, 100 - f.width);
                const newY = clamp(snap(f.y + dy), 0, 100 - f.height);
                await state.dotnetRef.invokeMethodAsync('OnFieldMoved', idx, newX, newY);
            }
        });
    }
}

export function syncOverlays(host, fields, selectedIndex) {
    if (!host) return;
    const state = hostState.get(host);
    if (state) {
        state.fields = fields;
        state.selectedIndex = (selectedIndex === undefined || selectedIndex === null) ? null : selectedIndex;
    }

    const pages = host.querySelectorAll('.designer-page');
    pages.forEach(page => {
        const overlay = page.querySelector('.designer-overlay-layer');
        if (!overlay) return;
        overlay.innerHTML = '';
        const pageNo = Number(page.dataset.pageNumber);
        fields.forEach((f, idx) => {
            if (f.pageNumber !== pageNo) return;

            const el = document.createElement('div');
            el.className = 'designer-field' + (idx === state?.selectedIndex ? ' designer-field--selected' : '');
            el.style.position = 'absolute';
            el.style.left = f.x + '%';
            el.style.top = f.y + '%';
            el.style.width = f.width + '%';
            el.style.height = Math.max(2.5, f.height) + '%';
            el.style.boxSizing = 'border-box';
            el.dataset.index = String(idx);

            const labelInner = document.createElement('span');
            labelInner.className = 'designer-field-label-inner';
            labelInner.textContent = (f.label || f.kind) + (f.assignedRoleName ? ' · ' + f.assignedRoleName : '');
            el.appendChild(labelInner);

            // Selection-only chrome: delete button + four corner resize handles.
            if (idx === state?.selectedIndex) {
                const delBtn = document.createElement('button');
                delBtn.className = 'designer-field-delete';
                delBtn.type = 'button';
                delBtn.title = 'Delete (or press Delete)';
                delBtn.textContent = '×';
                delBtn.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    e.preventDefault();
                    await state.dotnetRef.invokeMethodAsync('OnFieldDeleted', idx);
                });
                el.appendChild(delBtn);

                ['nw', 'ne', 'sw', 'se'].forEach(corner => {
                    const h = document.createElement('div');
                    h.className = 'designer-field-handle designer-field-handle--' + corner;
                    h.dataset.corner = corner;
                    h.addEventListener('pointerdown', (e) => startResize(host, page, idx, corner, e));
                    el.appendChild(h);
                });
            }

            // Pointer-down on the body starts a move, OR selects if the field wasn't
            // selected yet. Either way we stopPropagation so the empty-page click
            // handler doesn't drop a new field on the user.
            el.addEventListener('pointerdown', (e) => {
                if (e.target.classList.contains('designer-field-handle')) return;
                if (e.target.classList.contains('designer-field-delete')) return;
                e.stopPropagation();
                if (state?.selectedIndex !== idx) {
                    state.dotnetRef.invokeMethodAsync('OnFieldSelected', idx);
                } else {
                    startMove(host, page, idx, e);
                }
            });

            overlay.appendChild(el);
        });
    });
}

function startMove(host, pageEl, idx, downEvent) {
    const state = hostState.get(host);
    if (!state) return;
    const f = state.fields[idx];
    if (!f) return;

    downEvent.preventDefault();
    const rect = pageEl.getBoundingClientRect();
    state.drag = {
        mode: 'move',
        index: idx,
        pageEl,
        rect,
        originX: downEvent.clientX,
        originY: downEvent.clientY,
        original: { x: f.x, y: f.y, width: f.width, height: f.height },
    };

    pageEl.classList.add('designer-page--dragging');
    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('pointerup', onPointerUp);

    function onPointerMove(e) {
        const dx = (e.clientX - downEvent.clientX) / rect.width * 100;
        const dy = (e.clientY - downEvent.clientY) / rect.height * 100;
        const newX = clamp(state.drag.original.x + dx, 0, 100 - state.drag.original.width);
        const newY = clamp(state.drag.original.y + dy, 0, 100 - state.drag.original.height);

        // Live-update the field element directly so the user sees a ghost preview
        // without round-tripping through Blazor on every pixel.
        const el = pageEl.querySelector(`.designer-field[data-index="${idx}"]`);
        if (el) {
            el.style.left = newX + '%';
            el.style.top = newY + '%';
        }
    }

    async function onPointerUp(e) {
        document.removeEventListener('pointermove', onPointerMove);
        document.removeEventListener('pointerup', onPointerUp);
        pageEl.classList.remove('designer-page--dragging');

        const dx = (e.clientX - downEvent.clientX) / rect.width * 100;
        const dy = (e.clientY - downEvent.clientY) / rect.height * 100;
        const newX = snap(clamp(state.drag.original.x + dx, 0, 100 - state.drag.original.width));
        const newY = snap(clamp(state.drag.original.y + dy, 0, 100 - state.drag.original.height));

        const moved = (newX !== state.drag.original.x) || (newY !== state.drag.original.y);
        state.drag = null;

        if (moved) {
            await state.dotnetRef.invokeMethodAsync('OnFieldMoved', idx, newX, newY);
        }
    }
}

function startResize(host, pageEl, idx, corner, downEvent) {
    const state = hostState.get(host);
    if (!state) return;
    const f = state.fields[idx];
    if (!f) return;

    downEvent.preventDefault();
    downEvent.stopPropagation();
    const rect = pageEl.getBoundingClientRect();
    state.drag = {
        mode: 'resize',
        index: idx,
        corner,
        pageEl,
        rect,
        originX: downEvent.clientX,
        originY: downEvent.clientY,
        original: { x: f.x, y: f.y, width: f.width, height: f.height },
    };

    pageEl.classList.add('designer-page--dragging');
    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('pointerup', onPointerUp);

    function computeNext(e) {
        const dx = (e.clientX - downEvent.clientX) / rect.width * 100;
        const dy = (e.clientY - downEvent.clientY) / rect.height * 100;
        let { x, y, width, height } = state.drag.original;

        if (corner.includes('w')) { x = x + dx; width = width - dx; }
        if (corner.includes('e')) { width = width + dx; }
        if (corner.includes('n')) { y = y + dy; height = height - dy; }
        if (corner.includes('s')) { height = height + dy; }

        if (width < MIN_W) { if (corner.includes('w')) x = state.drag.original.x + state.drag.original.width - MIN_W; width = MIN_W; }
        if (height < MIN_H) { if (corner.includes('n')) y = state.drag.original.y + state.drag.original.height - MIN_H; height = MIN_H; }

        x = clamp(x, 0, 100 - width);
        y = clamp(y, 0, 100 - height);
        width = clamp(width, MIN_W, 100 - x);
        height = clamp(height, MIN_H, 100 - y);
        return { x, y, width, height };
    }

    function onPointerMove(e) {
        const next = computeNext(e);
        const el = pageEl.querySelector(`.designer-field[data-index="${idx}"]`);
        if (el) {
            el.style.left = next.x + '%';
            el.style.top = next.y + '%';
            el.style.width = next.width + '%';
            el.style.height = next.height + '%';
        }
    }

    async function onPointerUp(e) {
        document.removeEventListener('pointermove', onPointerMove);
        document.removeEventListener('pointerup', onPointerUp);
        pageEl.classList.remove('designer-page--dragging');

        const next = computeNext(e);
        const snapped = {
            x: snap(next.x),
            y: snap(next.y),
            width: snap(next.width),
            height: snap(next.height),
        };
        const orig = state.drag.original;
        const changed = snapped.x !== orig.x || snapped.y !== orig.y || snapped.width !== orig.width || snapped.height !== orig.height;
        state.drag = null;

        if (changed) {
            await state.dotnetRef.invokeMethodAsync('OnFieldResized', idx, snapped.x, snapped.y, snapped.width, snapped.height);
        }
    }
}
