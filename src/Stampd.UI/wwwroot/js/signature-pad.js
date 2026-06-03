// Minimal pointer-based signature pad. No deps. Supports mouse + touch + pen.
// Auto-commits to Blazor on each pointer-up via the supplied DotNetObjectReference,
// so the recipient just draws — no separate "Use signature" button.

const attached = new WeakMap();

export function attach(canvas, dotnetRef) {
    if (!canvas || attached.has(canvas)) return;

    const ctx = canvas.getContext('2d');
    ctx.lineWidth = 2;
    ctx.lineCap = 'round';
    ctx.strokeStyle = '#111';

    let drawing = false;
    let last = null;
    let dirty = false;

    function pointerPos(e) {
        const rect = canvas.getBoundingClientRect();
        const scaleX = canvas.width / rect.width;
        const scaleY = canvas.height / rect.height;
        return {
            x: (e.clientX - rect.left) * scaleX,
            y: (e.clientY - rect.top) * scaleY,
        };
    }

    function start(e) {
        drawing = true;
        last = pointerPos(e);
        e.preventDefault();
    }

    function move(e) {
        if (!drawing) return;
        const p = pointerPos(e);
        ctx.beginPath();
        ctx.moveTo(last.x, last.y);
        ctx.lineTo(p.x, p.y);
        ctx.stroke();
        last = p;
        dirty = true;
    }

    async function end() {
        const wasDrawing = drawing;
        drawing = false;
        last = null;
        if (wasDrawing && dirty && dotnetRef) {
            try {
                await dotnetRef.invokeMethodAsync('OnSignatureStroke');
            } catch (_) { /* circuit may be reconnecting */ }
        }
    }

    canvas.addEventListener('pointerdown', start);
    canvas.addEventListener('pointermove', move);
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointerleave', end);

    attached.set(canvas, { ctx });
}

export function clear(canvas) {
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    ctx.clearRect(0, 0, canvas.width, canvas.height);
}

export function toBase64(canvas) {
    if (!canvas) return '';
    return canvas.toDataURL('image/png');
}
