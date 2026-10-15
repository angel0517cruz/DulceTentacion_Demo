// Detalle de la cuenta de una mesa: todo lo pedido en una sola lista, cuánto falta y qué se puede hacer.
import { api, alEvento } from '../api.js';
import { estado } from '../estado.js';
import { h, montar, dinero, hace, horaCorta, confirmar, pedirTexto, aviso, accion, debounce } from '../ui.js';
import { abrirEncuesta, encuestaHecha } from '../encuesta-ui.js';

export default async function cuenta(host, params) {
  const id = Number(params[0]);
  let c = await api.get(`/api/cuentas/${id}`);
  const yo = estado.yo;

  const recargar = async () => { c = await api.get(`/api/cuentas/${id}`); pintar(); };
  const abierta = () => c.cuenta.estado === 'abierta';

  async function anularLinea(l) {
    const motivo = await pedirTexto(`Anular ${l.nombre}`, { etiqueta: '¿Por qué se anula?', placeholder: 'Ej: Se equivocó, no llegó…', boton: 'Anular producto' });
    if (!motivo) return;
    try { c = await api.post(`/api/lineas/${l.id}/anular`, { motivo }); pintar(); aviso('Producto anulado'); } catch (e) { aviso(e.message, 'error'); }
  }

  function filaLinea(l) {
    const editable = abierta() && !l.pagada && !l.anulada;
    return h('div', { class: 'linea-fila' + (l.pagada ? ' pagada' : '') + (l.anulada ? ' anulada' : '') },
      h('div', { class: 'crece' },
        h('div', { style: 'font-weight:700' }, `${l.cantidad > 1 ? l.cantidad + ' × ' : ''}${l.nombre}`),
        l.detalle ? h('div', { class: 'det' }, l.detalle) : null,
        l.nota ? h('div', { class: 'det' }, `📝 ${l.nota}`) : null,
        l.anulada ? h('div', { class: 'det' }, `Anulado: ${l.motivo || ''}`) : null,
        l.pagada ? h('div', { class: 'det', style: 'color:var(--verde);font-weight:700' }, '✓ Pagado') : null),
      h('div', { class: 'der num' }, dinero(l.subtotal)),
      editable ? h('button', { class: 'btn peligro chiquito', type: 'button', 'aria-label': 'Anular producto', onclick: () => anularLinea(l) }, '✕') : null);
  }

  function encuestaTarjeta() {
    if (c.cuenta.estado === 'anulada' || (c.cuenta.estado === 'abierta' && !yo.puedeCobrar)) return null;
    if (encuestaHecha(id)) return null;
    const titulo = c.cuenta.mesaNombre || `Para llevar · ${c.cuenta.etiqueta}`;
    return h('div', { class: 'tarjeta fila sep envolver' },
      h('div', null, h('div', { class: 'chico suave' }, 'Atendió'), h('b', { style: 'font-size:1.15rem' }, c.cuenta.mesero)),
      h('button', { class: 'btn oro', type: 'button', onclick: () => abrirEncuesta({ cuentaId: id, mesero: c.cuenta.mesero, mesa: titulo, alTerminar: pintar }) }, '⭐ Hacer encuesta'));
  }

  function imprimir() {
    const t = document.getElementById('ticket');
    const filas = c.lineas.filter((l) => !l.anulada);
    montar(t,
      h('div', { style: 'text-align:center;font-weight:bold' }, 'DULCE TENTACIÓN'),
      h('div', { style: 'text-align:center' }, 'RETEN EL VINO'),
      h('div', { style: 'text-align:center' }, 'KM 31 AUTOPISTA MEDELLIN'),
      h('div', { style: 'text-align:center' }, 'NIT: 81754819-5'),
      h('div', { style: 'text-align:center' }, c.cuenta.mesaNombre || `Para llevar: ${c.cuenta.etiqueta}`),
      h('div', { style: 'text-align:center' }, new Date().toLocaleString('es-CO')),
      h('hr'),
      filas.map((l) => h('div', null, `${l.cantidad} x ${l.nombre}${l.detalle ? ' (' + l.detalle + ')' : ''}`, h('span', { style: 'float:right' }, dinero(l.subtotal)))),
      h('hr'),
      h('div', { style: 'font-weight:bold' }, 'TOTAL', h('span', { style: 'float:right' }, dinero(c.totales.total))),
      c.totales.pagado ? h('div', null, 'Pagado', h('span', { style: 'float:right' }, dinero(c.totales.pagado))) : null,
      h('div', null, 'Falta', h('span', { style: 'float:right' }, dinero(c.totales.falta))),
      h('hr'), h('div', { style: 'text-align:center' }, '¡Gracias por visitarnos!'));
    window.print();
  }

  function pintar() {
    const x = c.cuenta;
    const titulo = x.mesaNombre || `Para llevar · ${x.etiqueta}`;
    const falta = c.totales.falta;
    montar(host, h('div', { class: 'pagina-estrecha pila' },
      h('div', { class: 'fila sep envolver' },
        h('div', null,
          h('div', { class: 'fila' }, h('a', { class: 'btn claro chiquito', href: '#/mesas' }, '← Mesas'), h('h1', null, titulo)),
          h('div', { class: 'suave chico', style: 'margin-top:6px' }, `Atiende ${x.mesero} · abierta hace ${hace(x.abiertaEn)} (${horaCorta(x.abiertaEn)})`)),
        h('span', { class: 'etiqueta ' + (x.estado === 'abierta' && x.porCobrar ? 'por_cobrar' : x.estado) },
          x.estado === 'abierta' ? (x.porCobrar ? 'Pide la cuenta' : 'Abierta') : x.estado === 'pagada' ? 'Pagada' : 'Anulada')),

      h('div', { class: 'totales' },
        h('div', null, h('small', null, 'Total'), h('b', { class: 'num' }, dinero(c.totales.total))),
        h('div', null, h('small', null, 'Pagado'), h('b', { class: 'num' }, dinero(c.totales.pagado))),
        h('div', { class: 'falta' + (falta === 0 ? ' cero' : '') }, h('small', null, 'Falta'), h('b', { class: 'num' }, dinero(falta)))),

      x.estado === 'anulada' ? h('div', { class: 'banner rojo' }, `Cuenta anulada: ${x.motivoAnulacion || ''}`) : null,

      abierta() ? h('div', { class: 'fila envolver' },
        h('a', { class: 'btn oro crece', href: `#/pedido/${id}` }, '＋ Agregar pedido'),
        yo.puedeCobrar && falta > 0 ? h('a', { class: 'btn rojo crece', href: `#/cobro/${id}` }, '💵 Cobrar') : null,
        !yo.puedeCobrar && !x.porCobrar && c.totales.total > 0
          ? accion(h('button', { class: 'btn rojo crece', type: 'button' }, '🧾 Pedir la cuenta'), async () => {
            c = await api.post(`/api/cuentas/${id}/pedir-cuenta`); pintar(); aviso('Listo, caja ya sabe que piden la cuenta.');
          }) : null) : null,

      encuestaTarjeta(),

      c.lineas.length === 0 && abierta() ? h('div', { class: 'vacio' }, 'Todavía no hay pedidos en esta cuenta.') : null,
      c.lineas.length ? h('div', { class: 'persona-bloque' },
        h('header', null, h('span', { class: 'crece' }, 'Lo que pidió la mesa'), h('span', { class: 'num' }, dinero(c.totales.total))),
        c.lineas.map(filaLinea)) : null,

      c.pagos.length ? h('div', { class: 'tarjeta pila' },
        h('h3', null, 'Cobros'),
        c.pagos.map((p) => h('div', { class: 'fila sep', style: p.anulado ? 'text-decoration:line-through;opacity:.5' : '' },
          h('div', null, h('b', null, `${dinero(p.monto)} · ${p.metodo}`), h('div', { class: 'chico suave' }, `${p.etiqueta || ''} · ${horaCorta(p.creadoEn)} · ${p.cajero}`)),
          yo.esAdmin && !p.anulado
            ? h('button', { class: 'btn peligro chiquito', type: 'button', onclick: () => anularPago(p) }, 'Anular cobro') : null))) : null,

      h('div', { class: 'fila envolver' },
        c.totales.total > 0 ? h('button', { class: 'btn claro', type: 'button', onclick: imprimir }, '🖨️ Imprimir cuenta') : null,
        yo.esAdmin && abierta() ? h('button', { class: 'btn peligro', type: 'button', onclick: anularCuenta }, 'Anular cuenta') : null)));
  }

  async function anularPago(p) {
    const motivo = await pedirTexto('Anular cobro', { etiqueta: `Cobro de ${dinero(p.monto)}: ¿por qué se anula?`, boton: 'Anular cobro' });
    if (!motivo) return;
    try { c = await api.post(`/api/pagos/${p.id}/anular`, { motivo }); pintar(); aviso('Cobro anulado'); } catch (e) { aviso(e.message, 'error'); }
  }
  async function anularCuenta() {
    const motivo = await pedirTexto('Anular toda la cuenta', { etiqueta: '¿Por qué se anula?', placeholder: 'Ej: El cliente se fue', boton: 'Anular cuenta' });
    if (!motivo) return;
    if (!(await confirmar('¿Anular la cuenta?', 'La mesa quedará libre y esta cuenta no sumará.', { si: 'Sí, anular', peligro: true }))) return;
    try { c = await api.post(`/api/cuentas/${id}/anular`, { motivo }); location.hash = '#/mesas'; } catch (e) { aviso(e.message, 'error'); }
  }

  pintar();
  const refrescar = debounce(() => recargar().catch(() => {}), 200);
  const quitar = [alEvento('cuenta', (d) => { if (d?.id === id) refrescar(); }), alEvento('reconectado', refrescar)];
  return () => quitar.forEach((q) => q());
}
