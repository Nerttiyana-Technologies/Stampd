// Tiny canvas-based signature pad. No external dependencies (no signature_pad.js,
// no jQuery). Mouse + touch handlers, smooth strokes via quadratic curves, and
// a toBase64 method the C# side calls via IJSRuntime to grab the PNG bytes.
//
// Usage from Blazor:
//   await JS.InvokeVoidAsync("stampdSignaturePad.init", canvasId);
//   var base64 = await JS.InvokeAsync<string>("stampdSignaturePad.toBase64", canvasId);
//   await JS.InvokeVoidAsync("stampdSignaturePad.clear", canvasId);
//
// All state is keyed by canvas id so a page can host multiple pads if needed.

window.stampdSignaturePad = (() => {
    const pads = new Map();

    function init(canvasId) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) return;
        if (pads.has(canvasId)) return; // already initialised

        // Pixel ratio: use device pixel ratio so high-DPI displays draw sharp.
        const dpr = window.devicePixelRatio || 1;
        const rect = canvas.getBoundingClientRect();
        canvas.width = rect.width * dpr;
        canvas.height = rect.height * dpr;
        canvas.style.width = rect.width + 'px';
        canvas.style.height = rect.height + 'px';

        const ctx = canvas.getContext('2d');
        ctx.scale(dpr, dpr);
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        ctx.lineWidth = 2.2;
        ctx.strokeStyle = '#0b1628';
        // Deliberately leave the canvas pixels UNTOUCHED — that keeps the
        // PNG export transparent except where the user has actually drawn.
        // When the engine composites the image onto the PDF, only the strokes
        // appear; the underlying PDF content shows through the rest. The
        // visible white background the user sees while drawing comes from the
        // CSS rule `.sigpad-canvas { background: #ffffff }`, not from painted
        // pixels.

        let isDrawing = false;
        let lastX = 0, lastY = 0;
        let hasInk = false;

        function pos(evt) {
            const r = canvas.getBoundingClientRect();
            if (evt.touches && evt.touches.length > 0) {
                return { x: evt.touches[0].clientX - r.left, y: evt.touches[0].clientY - r.top };
            }
            return { x: evt.clientX - r.left, y: evt.clientY - r.top };
        }

        function start(evt) {
            evt.preventDefault();
            isDrawing = true;
            const p = pos(evt);
            lastX = p.x; lastY = p.y;
            // Place a tiny dot so a single tap leaves a mark.
            ctx.beginPath();
            ctx.arc(p.x, p.y, ctx.lineWidth / 2, 0, Math.PI * 2);
            ctx.fillStyle = ctx.strokeStyle;
            ctx.fill();
            hasInk = true;
        }

        function move(evt) {
            if (!isDrawing) return;
            evt.preventDefault();
            const p = pos(evt);
            const midX = (lastX + p.x) / 2;
            const midY = (lastY + p.y) / 2;
            ctx.beginPath();
            ctx.moveTo(lastX, lastY);
            ctx.quadraticCurveTo(lastX, lastY, midX, midY);
            ctx.stroke();
            lastX = p.x; lastY = p.y;
            hasInk = true;
        }

        function end(evt) {
            if (!isDrawing) return;
            evt.preventDefault();
            isDrawing = false;
        }

        canvas.addEventListener('mousedown', start);
        canvas.addEventListener('mousemove', move);
        canvas.addEventListener('mouseup', end);
        canvas.addEventListener('mouseleave', end);

        canvas.addEventListener('touchstart', start, { passive: false });
        canvas.addEventListener('touchmove', move, { passive: false });
        canvas.addEventListener('touchend', end, { passive: false });

        pads.set(canvasId, { canvas, ctx, dpr, rect, hasInk: () => hasInk });
    }

    function clear(canvasId) {
        const p = pads.get(canvasId);
        if (!p) return;
        // clearRect wipes the canvas back to fully transparent (the alpha
        // channel goes to 0). The CSS background-color still shows the white
        // "paper" to the user, but the PNG export captures nothing — so the
        // next stroke series starts clean and the resulting signature is
        // genuinely transparent except for the ink.
        p.ctx.clearRect(0, 0, p.rect.width, p.rect.height);
        // Reset hasInk flag — replace the snapshot.
        pads.set(canvasId, { ...p, hasInk: () => false });
        // Re-init to reset the closure
        const handlers = p.canvas.cloneNode(false);
        p.canvas.parentNode.replaceChild(handlers, p.canvas);
        pads.delete(canvasId);
        init(canvasId);
    }

    function toBase64(canvasId) {
        const p = pads.get(canvasId);
        if (!p) return null;

        // PdfSharp 6.x's PNG path with alpha was the source of the black
        // rectangle. JPEG sidesteps the entire alpha-handling story because
        // JPEG cannot carry an alpha channel at all — every pixel is opaque.
        // We composite the canvas onto a white-filled offscreen first so the
        // "background" pixels become pure white instead of canvas-default
        // transparent black, then export as a high-quality JPEG.
        const off = document.createElement('canvas');
        off.width = p.canvas.width;
        off.height = p.canvas.height;
        const offCtx = off.getContext('2d');

        // 1. Paint the offscreen canvas pure white.
        offCtx.fillStyle = '#ffffff';
        offCtx.fillRect(0, 0, off.width, off.height);
        // 2. Composite the user's pen strokes on top.
        offCtx.drawImage(p.canvas, 0, 0);

        // 3. Export as JPEG — guaranteed no alpha, guaranteed PdfSharp-friendly.
        //    0.92 quality keeps the strokes crisp without doubling the bytes.
        const dataUrl = off.toDataURL('image/jpeg', 0.92);
        const comma = dataUrl.indexOf(',');
        return comma >= 0 ? dataUrl.substring(comma + 1) : dataUrl;
    }

    function hasInk(canvasId) {
        const p = pads.get(canvasId);
        return !!(p && p.hasInk && p.hasInk());
    }

    return { init, clear, toBase64, hasInk };
})();
