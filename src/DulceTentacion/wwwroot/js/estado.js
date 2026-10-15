// Estado compartido: usuario, menú en memoria (y guardado en el celular) y cola de pedidos sin enviar.
import { api, alEvento, hayRed } from './api.js';
import { uid, aviso } from './ui.js';

export const estado = { yo: null, menu: null };

const guardar = (k, v) => { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* sin espacio */ } };
const leer = (k, def = null) => { try { const t = localStorage.getItem(k); return t ? JSON.parse(t) : def; } catch { return def; } };
export const recordar = { guardar, leer };

// ---------- Menú ----------
export async function cargarMenu(forzar = false) {
  try {
    const m = await api.get('/api/menu');
    estado.menu = indexar(m);
    guardar('dt_menu', m);
  } catch (e) {
    if (!estado.menu) {
      const copia = leer('dt_menu');
      if (copia) estado.menu = indexar(copia);
      else if (forzar) throw e;
    }
  }
  return estado.menu;
}
function indexar(m) {
  const porId = new Map(m.productos.map((p) => [p.id, p]));
  const porCategoria = new Map();
  for (const p of m.productos) {
    if (!porCategoria.has(p.categoriaId)) porCategoria.set(p.categoriaId, []);
    porCategoria.get(p.categoriaId).push(p);
  }
  return { ...m, porId, porCategoria };
}
alEvento('menu', () => cargarMenu().catch(() => {}));

// ---------- Borrador del pedido (queda en el celular aunque se cierre la página) ----------
const claveBorrador = (cuentaId) => `dt_borrador_${cuentaId}`;
export function leerBorrador(cuentaId) {
  return leer(claveBorrador(cuentaId), { uid: uid(), lineas: [], nota: '' });
}
export function guardarBorrador(cuentaId, b) {
  if (!b.lineas.length && !b.nota) { try { localStorage.removeItem(claveBorrador(cuentaId)); } catch { /* nada */ } } else guardar(claveBorrador(cuentaId), b);
}
export function borrarBorrador(cuentaId) { try { localStorage.removeItem(claveBorrador(cuentaId)); } catch { /* nada */ } }

// ---------- Cola de envíos pendientes (cuando se cae el WiFi) ----------
const oyentesCola = new Set();
export const cola = {
  todas: () => leer('dt_cola', []),
  alCambiar: (f) => { oyentesCola.add(f); return () => oyentesCola.delete(f); },
  agregar(item) { const c = cola.todas(); c.push(item); guardar('dt_cola', c); oyentesCola.forEach((f) => f()); },
  quitar(id) { guardar('dt_cola', cola.todas().filter((x) => x.uid !== id)); oyentesCola.forEach((f) => f()); },
};

let enviando = false;
export async function vaciarCola() {
  if (enviando) return;
  enviando = true;
  try {
    for (const item of cola.todas()) {
      try {
        await api.post(`/api/cuentas/${item.cuentaId}/pedido`, { uid: item.uid, nota: item.nota, lineas: item.lineas });
        cola.quitar(item.uid);
        aviso(`Se envió el pedido pendiente de ${item.titulo || 'una mesa'}.`);
      } catch (e) {
        if (e.sinRed) break;                       // sigue sin red: se intenta más tarde
        if (e.estado === 401) break;
        cola.quitar(item.uid);                     // el servidor lo rechazó: no tiene caso reintentar
        aviso(`No se pudo enviar el pedido de ${item.titulo || 'una mesa'}: ${e.message}`, 'error');
      }
    }
  } finally { enviando = false; }
}
setInterval(() => { if (cola.todas().length) vaciarCola(); }, 6000);
window.addEventListener('online', () => vaciarCola());
alEvento('reconectado', () => vaciarCola());
export const hayPendientes = () => cola.todas().length;
export { hayRed };
