// Comunicación con el servidor del local y eventos en vivo.

const oyentes = new Set();
export const alCambiarEstado = (f) => { oyentes.add(f); return () => oyentes.delete(f); };
let enLinea = true;
export const hayRed = () => enLinea;
function marcarRed(v) {
  if (v === enLinea) return;
  enLinea = v;
  oyentes.forEach((f) => f(v));
}

export const sesion = { alExpirar: () => {} };

async function pedir(metodo, ruta, cuerpo) {
  let resp;
  try {
    resp = await fetch(ruta, {
      method: metodo,
      headers: cuerpo !== undefined ? { 'Content-Type': 'application/json' } : undefined,
      body: cuerpo !== undefined ? JSON.stringify(cuerpo) : undefined,
      credentials: 'same-origin',
    });
  } catch {
    marcarRed(false);
    const e = new Error('No hay conexión con el sistema del local. Revisa el WiFi.');
    e.sinRed = true;
    throw e;
  }
  marcarRed(true);
  if (resp.status === 401 && !ruta.startsWith('/api/auth/entrar')) {
    sesion.alExpirar();
    const e = new Error('Tu sesión terminó. Entra de nuevo.');
    e.estado = 401;
    throw e;
  }
  const texto = await resp.text();
  let datos = null;
  try { datos = texto ? JSON.parse(texto) : null; } catch { /* respuesta sin json */ }
  if (!resp.ok) {
    const e = new Error(datos?.error || 'No se pudo completar la acción.');
    e.estado = resp.status;
    throw e;
  }
  return datos;
}

export const api = {
  get: (r) => pedir('GET', r),
  post: (r, c = {}) => pedir('POST', r, c),
  put: (r, c = {}) => pedir('PUT', r, c),
  del: (r) => pedir('DELETE', r),
};

// ---------- Eventos en vivo ----------
const suscriptores = new Map();   // tipo -> Set(fn)
let fuente = null;

export function alEvento(tipo, fn) {
  if (!suscriptores.has(tipo)) suscriptores.set(tipo, new Set());
  suscriptores.get(tipo).add(fn);
  return () => suscriptores.get(tipo)?.delete(fn);
}

export function conectarEventos() {
  if (fuente) return;
  fuente = new EventSource('/api/eventos');
  fuente.onmessage = (e) => {
    marcarRed(true);
    try {
      const m = JSON.parse(e.data);
      suscriptores.get(m.t)?.forEach((f) => f(m.d));
      suscriptores.get('*')?.forEach((f) => f(m));
    } catch { /* mensaje ilegible */ }
  };
  fuente.onopen = () => { marcarRed(true); suscriptores.get('reconectado')?.forEach((f) => f()); };
  fuente.onerror = () => { marcarRed(false); };   // el navegador reintenta solo
}

export function desconectarEventos() {
  fuente?.close();
  fuente = null;
}
