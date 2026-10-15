// Tablero de mesas: lo primero que ve la mesera. Se actualiza solo cuando cualquiera toma un pedido o cobra.
import { api, alEvento } from '../api.js';
import { estado } from '../estado.js';
import { h, montar, dinero, hace, hoja, pedirTexto, aviso, uid, accion, debounce } from '../ui.js';

const TEXTO_ESTADO = { libre: 'Libre', ocupada: 'Ocupada', por_cobrar: 'Pide la cuenta' };

export default async function mesas(host) {
  let datos = null;
  const yo = estado.yo;

  const tarjeta = (m, esLlevar) => {
    const clases = ['mesa', m.estado, m.meseroId === yo.id ? 'mia' : ''].join(' ');
    const pendiente = Math.max((m.total || 0) - (m.pagado || 0), 0);
    return h('button', {
      class: clases, type: 'button',
      onclick: () => {
        if (m.estado === 'libre') abrirMesa(m);
        else location.hash = `#/cuenta/${m.cuentaId}`;
      },
    },
      h('div', { class: 'fila sep' },
        h('span', { class: 'nom' }, esLlevar ? `🥡 ${m.nombre}` : m.nombre),
        h('span', { class: 'etiqueta ' + m.estado }, TEXTO_ESTADO[m.estado])),
      m.estado === 'libre'
        ? h('span', { class: 'suave chico' }, 'Toca para tomar pedido')
        : h('div', null,
          h('div', { class: 'num', style: 'font-weight:800;font-size:1.15rem' }, dinero(pendiente)),
          h('div', { class: 'chico suave' }, `${m.mesero.split(' ')[0]} · ${hace(m.desde)}`)));
  };

  async function abrirMesa(m) {
    try {
      const c = await api.post(`/api/mesas/${m.id}/abrir`, { uid: uid(), personas: 1 });
      location.hash = `#/pedido/${c.cuenta.id}`;
    } catch (e) { aviso(e.message, 'error'); }
  }

  async function paraLlevar() {
    const nombre = await pedirTexto('Pedido para llevar', { etiqueta: 'Nombre del cliente', placeholder: 'Ej: Sra. Marta', boton: 'Crear pedido' });
    if (!nombre) return;
    try {
      const c = await api.post('/api/llevar', { uid: uid(), etiqueta: nombre });
      location.hash = `#/pedido/${c.cuenta.id}`;
    } catch (e) { aviso(e.message, 'error'); }
  }

  function pintar() {
    if (!datos) return;
    const todas = [...datos.mesas, ...datos.llevar];
    const mias = todas.filter((m) => m.cuentaId && m.meseroId === yo.id);
    const porCobrar = todas.filter((m) => m.estado === 'por_cobrar');
    const ocupadas = datos.mesas.filter((m) => m.estado !== 'libre').length;

    montar(host,
      h('div', { class: 'fila sep envolver' },
        h('div', null, h('h1', null, 'Mesas'), h('div', { class: 'suave' }, `${ocupadas} ocupadas · ${datos.mesas.length - ocupadas} libres`)),
        h('button', { class: 'btn oro', type: 'button', onclick: paraLlevar }, '🥡 Pedido para llevar')),
      porCobrar.length && yo.puedeCobrar
        ? h('div', { class: 'banner rojo', style: 'margin-top:12px' },
          `💵 Piden la cuenta: ${porCobrar.map((m) => m.nombre).join(', ')}`)
        : null,
      mias.length
        ? [h('div', { class: 'titulo-seccion' }, h('h2', null, 'Mis mesas')),
          h('div', { class: 'rejilla' }, mias.map((m) => tarjeta(m, !m.id)))]
        : null,
      h('div', { class: 'titulo-seccion' }, h('h2', null, 'Todas las mesas')),
      h('div', { class: 'rejilla' }, datos.mesas.map((m) => tarjeta(m, false))),
      datos.llevar.length
        ? [h('div', { class: 'titulo-seccion' }, h('h2', null, 'Para llevar')),
          h('div', { class: 'rejilla' }, datos.llevar.map((m) => tarjeta(m, true)))]
        : null);
  }

  async function cargar() {
    try { datos = await api.get('/api/mesas'); pintar(); } catch (e) {
      if (!datos) montar(host, h('div', { class: 'vacio' }, e.message));
    }
  }
  await cargar();
  const recargar = debounce(cargar, 250);
  const quitar = [alEvento('mesas', recargar), alEvento('reconectado', recargar)];
  const reloj = setInterval(pintar, 30000);
  return () => { quitar.forEach((q) => q()); clearInterval(reloj); };
}
