// Arranque, barra superior, menú y rutas.
import { api, sesion, alCambiarEstado, hayRed, conectarEventos, desconectarEventos, alEvento } from './api.js';
import { estado, cargarMenu, cola, vaciarCola, recordar } from './estado.js';
import { h, montar, aviso, accion } from './ui.js';
import pantallaAcceso from './paginas/acceso.js';

const app = document.getElementById('app');
let limpiar = null;           // función que cierra lo que dejó la pantalla anterior

// ---- Avisos para administración: solicitudes de cambio de PIN de las meseras ----
const zonaSolicitudes = h('div', { class: 'solicitudes', 'aria-live': 'polite' });
let solicitudes = [];
function pintarSolicitudes() {
  const lista = estado.yo?.esAdmin ? solicitudes : [];
  montar(zonaSolicitudes, lista.map((s) => {
    const botones = h('div', { class: 'fila' },
      accion(h('button', { class: 'btn claro chiquito', type: 'button' }, 'Rechazar'), async () => { const r = await api.post(`/api/admin/solicitudes-pin/${s.id}/rechazar`); aviso(r.mensaje); await cargarSolicitudes(); }),
      accion(h('button', { class: 'btn oro chiquito', type: 'button' }, 'Aprobar'), async () => { const r = await api.post(`/api/admin/solicitudes-pin/${s.id}/aprobar`); aviso(r.mensaje); await cargarSolicitudes(); }));
    return h('div', { class: 'solicitud' },
      h('div', { class: 'crece' }, '🔔 ', h('b', null, s.nombre), ' quiere cambiar su PIN. ¿Lo apruebas?'),
      botones);
  }));
}
async function cargarSolicitudes(avisar = false) {
  if (!estado.yo?.esAdmin) { solicitudes = []; pintarSolicitudes(); return; }
  try {
    const nuevas = await api.get('/api/admin/solicitudes-pin');
    const hayNueva = nuevas.some((n) => !solicitudes.some((s) => s.id === n.id));
    solicitudes = nuevas;
    pintarSolicitudes();
    if (avisar && hayNueva) aviso('🔔 Una persona pide cambiar su PIN: revisa el aviso de arriba.', 'ok', 6000);
  } catch { /* se reintenta con el próximo evento */ }
}
alEvento('solicitud_pin', () => cargarSolicitudes(true));
alEvento('reconectado', () => cargarSolicitudes());
let tokenRuta = 0;

const rutas = {
  mesas: () => import('./paginas/mesas.js'),
  cuenta: () => import('./paginas/cuenta.js'),
  pedido: () => import('./paginas/pedido.js'),
  cobro: () => import('./paginas/cobro.js'),
  caja: () => import('./paginas/caja.js'),
  admin: () => import('./paginas/admin.js'),
  yo: () => import('./paginas/yo.js'),
};

function partesRuta() {
  const [nombre = 'mesas', ...resto] = (location.hash.replace(/^#\/?/, '') || 'mesas').split('/');
  return { nombre, params: resto };
}

function barra() {
  const punto = h('span', { class: 'punto', id: 'punto-red', title: 'Conexión con el local' });
  const pendientes = h('span', { class: 'insignia oculto', id: 'pendientes' });
  alCambiarEstado((ok) => punto.classList.toggle('mal', !ok));
  punto.classList.toggle('mal', !hayRed());
  const pintarCola = () => {
    const n = cola.todas().length;
    pendientes.textContent = `${n} sin enviar`;
    pendientes.classList.toggle('oculto', n === 0);
  };
  cola.alCambiar(pintarCola); pintarCola();
  return h('header', { class: 'barra' },
    h('a', { href: '#/mesas', class: 'marca', style: 'text-decoration:none' },
      h('img', { src: '/img/logo.png', alt: '' }),
      h('span', null, 'Dulce Tentación', h('small', null, 'Cafetería'))),
    h('div', { class: 'quien' }, estado.demo?.activo ? h('span', { class: 'etiqueta ocupada', title: 'Versión de demostración' }, 'DEMO') : null, pendientes, punto, h('a', { href: '#/yo', style: 'color:#fff;text-decoration:none;font-weight:600' }, estado.yo.nombre.split(' ')[0])));
}

function menuNav(actual) {
  const yo = estado.yo;
  const items = [['mesas', '🍽️ Mesas']];
  if (yo.puedeCobrar) items.push(['caja', '💵 Caja']);
  if (yo.esAdmin) items.push(['admin', '⚙️ Administración']);
  items.push(['yo', '👤 Mi cuenta']);
  const claseActiva = (n) => (n === actual || (n === 'mesas' && ['cuenta', 'pedido', 'cobro'].includes(actual)) ? 'activo' : '');
  return h('nav', { class: 'menu', 'aria-label': 'Secciones' },
    items.map(([n, t]) => h('a', { href: `#/${n}`, class: claseActiva(n) }, t)));
}

async function pintar() {
  if (!estado.yo) return;
  const mi = ++tokenRuta;
  if (limpiar) { try { limpiar(); } catch { /* nada */ } limpiar = null; }
  const { nombre, params } = partesRuta();
  const cargador = rutas[nombre] || rutas.mesas;
  const host = h('main', null, h('div', { class: 'cargando' }, 'Cargando…'));
  montar(app, barra(), menuNav(nombre), zonaSolicitudes, host);
  window.scrollTo(0, 0);
  try {
    const modulo = await cargador();
    if (mi !== tokenRuta) return;
    const salida = await modulo.default(host, params);
    if (mi !== tokenRuta) { salida?.(); return; }
    limpiar = typeof salida === 'function' ? salida : null;
  } catch (e) {
    if (mi !== tokenRuta) return;
    montar(host, h('div', { class: 'vacio' },
      h('p', null, e.message || 'No se pudo abrir esta pantalla.'),
      h('button', { class: 'btn oro', onclick: () => pintar() }, 'Reintentar')));
  }
}

async function entrarAlSistema(perfil) {
  estado.yo = perfil;
  estado.demo = await api.get('/api/demo').catch(() => ({ activo: false }));
  recordar.guardar('dt_ultimo_usuario', perfil.id);
  conectarEventos();
  await cargarMenu().catch(() => {});
  vaciarCola();
  cargarSolicitudes();
  if (!location.hash || location.hash === '#/') location.hash = '#/mesas';
  pintar();
}

function mostrarAcceso() {
  estado.yo = null;
  solicitudes = [];
  if (limpiar) { try { limpiar(); } catch { /* nada */ } limpiar = null; }
  desconectarEventos();
  pantallaAcceso(app, entrarAlSistema);
}

sesion.alExpirar = () => { if (estado.yo) { aviso('Tu sesión terminó. Entra de nuevo.', 'error'); mostrarAcceso(); } };
window.addEventListener('hashchange', pintar);
window.addEventListener('dt:salir', async () => {
  try { await api.post('/api/auth/salir'); } catch { /* ya no importa */ }
  mostrarAcceso();
});
// Si desactivaron a esta persona o cambió su rol, se refleja de inmediato.
alEvento('reconectado', () => { if (estado.yo) api.get('/api/auth/yo').catch(() => {}); });

(async function iniciar() {
  try {
    const perfil = await api.get('/api/auth/yo');
    await entrarAlSistema(perfil);
  } catch (e) {
    if (e.sinRed) {
      montar(app, h('div', { class: 'acceso' },
        h('img', { class: 'logo', src: '/img/logo.png', alt: '' }),
        h('h1', null, 'Sin conexión'),
        h('p', null, 'No se encuentra el sistema del local. Revisa que estés conectada al WiFi del café.'),
        h('button', { class: 'btn oro', onclick: () => location.reload() }, 'Reintentar')));
    } else mostrarAcceso();
  }
})();
