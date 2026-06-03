// Designer-side JWT bearer token persistence. We keep the token in sessionStorage so it
// survives navigation between /designer pages but is cleared when the browser tab closes.
// Blazor reads/writes via JSInterop.

const KEY = 'stampd-designer-jwt';

export function get() {
    try { return sessionStorage.getItem(KEY) || ''; }
    catch (_) { return ''; }
}

export function set(token) {
    try { sessionStorage.setItem(KEY, token || ''); }
    catch (_) { /* ignore */ }
}

export function clear() {
    try { sessionStorage.removeItem(KEY); }
    catch (_) { /* ignore */ }
}

// File picker → base64 helper. Reads the first PDF the user selects and resolves with
// just the base64 payload (no "data:application/pdf;base64," prefix) so the WebApi can
// consume it as-is.
export function pickPdfAsBase64(inputElement) {
    return new Promise((resolve, reject) => {
        if (!inputElement || !inputElement.files || inputElement.files.length === 0) {
            reject(new Error('No file selected'));
            return;
        }
        const file = inputElement.files[0];
        if (!file.type || file.type !== 'application/pdf') {
            reject(new Error('Selected file is not a PDF'));
            return;
        }
        const reader = new FileReader();
        reader.onload = () => {
            const dataUrl = reader.result;
            const comma = dataUrl.indexOf(',');
            resolve({
                fileName: file.name,
                base64: comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl,
            });
        };
        reader.onerror = () => reject(reader.error || new Error('Failed to read file'));
        reader.readAsDataURL(file);
    });
}
