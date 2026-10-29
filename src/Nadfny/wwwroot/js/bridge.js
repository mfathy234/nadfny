// Request/response + event bridge to the native host (WebView2 postMessage).
const webview = window.chrome?.webview;
const pending = new Map();
const listeners = new Map();
let seq = 0;

if (webview) {
  webview.addEventListener('message', (e) => {
    const msg = e.data;
    if (msg?.type === 'result') {
      const p = pending.get(msg.id);
      if (!p) return;
      pending.delete(msg.id);
      if (msg.ok) p.resolve(msg.result);
      else p.reject(Object.assign(new Error(msg.error || 'Something went wrong.'), { cancelled: !!msg.cancelled }));
    } else if (msg?.type === 'event') {
      for (const fn of listeners.get(msg.name) ?? []) fn(msg.data);
    }
  });
}

export function call(method, params = {}) {
  if (!webview) return Promise.reject(new Error('Nadfny must run inside its desktop window.'));
  const id = String(++seq);
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    webview.postMessage(JSON.stringify({ id, method, params }));
  });
}

export function on(name, fn) {
  if (!listeners.has(name)) listeners.set(name, new Set());
  listeners.get(name).add(fn);
  return () => listeners.get(name).delete(fn);
}
