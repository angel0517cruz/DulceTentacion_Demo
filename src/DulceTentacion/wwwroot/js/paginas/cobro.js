// Cobro de una cuenta única. Dos formas habituales:
//  · "Paga uno solo": se cobra todo lo que falta.
//  · "Pagan individual": la cajera marca de la misma lista lo que pidió cada persona, cobra, y eso se descuenta;
//    sigue con la siguiente persona hasta que Falta llegue a $0.
import { api, alEvento } from '../api.js';
import { estado } from '../estado.js';
import { h, montar, dinero, aviso, accion, uid, debounce } from '../ui.js';
import { abrirEncuesta, encuestaHecha } from '../encuesta-ui.js';

const BILLETES = [2000, 5000, 10000, 20000, 50000, 100000];

export default async function cobro(host, params) {
  const id = Number(params[0]);
  if (!estado.yo.puedeCobrar) { location.hash = `#/cuenta/${id}`; return; }
  let c = await api.get(`/api/cuentas/${id}`);
  if (c.cuenta.estado !== 'abierta') { location.hash = `#/cuenta/${id}`; return; }

  let modo = c.pagos.some((p) => !p.anulado) ? 'lineas' : 'todo';
  const selLineas = new Set();
  let montoLibre = '';
  let metodo = 'efectivo';
  let recibido = '';
  let uidCobro = uid();            // si se reintenta el mismo cobro por mala señal, no se cobra dos veces
  let partes = 0;

  const pendientes = () => c.lineas.filter((l) => !l.pagada && !l.anulada);
  const sumaSeleccion = () => {
    if (modo === 'todo') return c.totales.falta;
    if (modo === 'lineas') return pendientes().filter((l) => selLineas.has(l.id)).reduce((s, l) => s + l.subtotal, 0);
    return Number(montoLibre) || 0;
  };
  const numeroPago = () => c.pagos.filter((p) => !p.anulado).length + 1;

  function pintar() {
    const falta = c.totales.falta;
    const aCobrar = sumaSeleccion();
    const titulo = c.cuenta.mesaNombre || `Para llevar · ${c.cuenta.etiqueta}`;
    const exceso = aCobrar > falta;

    let contenidoModo;
    if (modo === 'lineas') {
      const lista = pendientes();
      contenidoModo = h('div', { class: 'pila' },
        h('div', { class: 'banner' }, 'Marca lo que pidió la persona que va a pagar y cóbrale. Lo cobrado se descuenta de la cuenta y sigues con la siguiente.'),
        lista.length ? lista.map((l) => {
          const sel = selLineas.has(l.id);
          return h('button', { class: 'opcion' + (sel ? ' sel' : ''), type: 'button', onclick: () => { sel ? selLineas.delete(l.id) : selLineas.add(l.id); pintar(); } },
            h('span', { class: 'marca' }, sel ? '✓' : ''),
            h('span', { class: 'crece' }, `${l.cantidad > 1 ? l.cantidad + ' × ' : ''}${l.nombre}`,
              l.detalle ? h('div', { class: 'chico suave' }, l.detalle) : null),
            h('b', { class: 'num' }, dinero(l.subtotal)));
        }) : h('div', { class: 'vacio' }, 'No quedan productos por cobrar.'),
        lista.length > 1 ? h('div', { class: 'fila envolver' },
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: () => { lista.forEach((l) => selLineas.add(l.id)); pintar(); } }, 'Marcar todos'),
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: () => { selLineas.clear(); pintar(); } }, 'Quitar marcas')) : null);
    } else if (modo === 'monto') {
      const dividir = [2, 3, 4, 5, 6, 8, 10];
      contenidoModo = h('div', { class: 'pila' },
        h('div', { class: 'chico suave' }, 'Para un abono o para dividir lo que falta en partes iguales.'),
        h('label', { class: 'campo' }, 'Valor a cobrar',
          h('input', { type: 'number', inputmode: 'numeric', min: 0, value: montoLibre, placeholder: '0',
            oninput: (e) => { montoLibre = e.target.value; partes = 0; refrescarPie(); } })),
        h('div', { class: 'chico suave' }, 'O divide lo que falta entre:'),
        h('div', { class: 'rapidos' }, dividir.map((n) =>
          h('button', { class: 'chip' + (partes === n ? ' activo' : ''), type: 'button',
            onclick: () => { partes = n; const v = Math.ceil(falta / n / 50) * 50; montoLibre = String(v > falta ? falta : v); pintar(); } }, `${n} partes`))),
        partes ? h('div', { class: 'chico suave' }, `Cada parte: ${dinero(Number(montoLibre))}. Después de cobrar, la cuenta recuerda lo que falta.`) : null);
    } else {
      contenidoModo = h('div', { class: 'banner' }, `Una sola persona paga todo lo que falta: ${dinero(falta)}.`);
    }

    const pie = h('div', { id: 'pie-cobro', class: 'pila' });
    function refrescarPie() {
      const aC = sumaSeleccion();
      const rec = Number(recibido) || 0;
      const camb = metodo === 'efectivo' && rec ? rec - aC : 0;
      const sobra = aC > falta;
      montar(pie,
        h('div', { class: 'metodos' },
          [['efectivo', '💵 Efectivo'], ['transferencia', '📲 Transferencia'], ['tarjeta', '💳 Tarjeta']].map(([m, t]) =>
            h('button', { class: 'modo' + (metodo === m ? ' activo' : ''), type: 'button', onclick: () => { metodo = m; recibido = ''; pintar(); } }, t))),
        metodo === 'efectivo' && aC > 0 ? h('div', { class: 'pila' },
          h('label', { class: 'campo' }, '¿Con cuánto paga?',
            h('input', { type: 'number', inputmode: 'numeric', min: 0, value: recibido, placeholder: dinero(aC), oninput: (e) => { recibido = e.target.value; refrescarPie(); } })),
          h('div', { class: 'rapidos' },
            h('button', { class: 'chip', type: 'button', onclick: () => { recibido = String(aC); pintar(); } }, 'Exacto'),
            BILLETES.filter((b) => b >= aC || b >= 20000).map((b) => h('button', { class: 'chip', type: 'button', onclick: () => { recibido = String(b); pintar(); } }, dinero(b)))),
          rec ? h('div', { class: 'cambio' + (camb < 0 ? ' mal' : '') }, camb < 0 ? `Faltan ${dinero(-camb)}` : `Devolver ${dinero(camb)}`) : null) : null,
        sobra ? h('div', { class: 'banner rojo' }, `Eso supera lo que falta (${dinero(falta)}).`) : null,
        boton());
    }

    function boton() {
      const aC = sumaSeleccion();
      const rec = Number(recibido) || 0;
      const b = h('button', { class: 'btn rojo grande',
        type: 'button', disabled: aC <= 0 || aC > falta || (metodo === 'efectivo' && rec > 0 && rec < aC) },
      aC > 0 ? `Cobrar ${dinero(aC)}` : 'Marca qué cobrar');
      accion(b, cobrar);
      return b;
    }

    async function cobrar() {
      const aC = sumaSeleccion();
      const cuerpo = { uid: uidCobro, modo, metodo };
      if (modo === 'lineas') { cuerpo.lineas = [...selLineas]; cuerpo.etiqueta = `Pago ${numeroPago()}`; }
      if (modo === 'monto') { cuerpo.monto = aC; cuerpo.etiqueta = partes ? `Parte de ${partes}` : 'Abono'; }
      if (metodo === 'efectivo' && Number(recibido) > 0) cuerpo.recibido = Number(recibido);
      const r = await api.post(`/api/cuentas/${id}/cobrar`, cuerpo);
      uidCobro = uid();
      c = r.detalle;
      const ultimo = c.pagos.filter((p) => !p.anulado).at(-1);
      selLineas.clear(); montoLibre = ''; recibido = ''; partes = 0;
      if (c.cuenta.estado === 'pagada') {
        aviso(ultimo?.cambio ? `Cuenta pagada. Devolver ${dinero(ultimo.cambio)}` : '✓ Cuenta pagada', 'ok', 5000);
        location.hash = `#/cuenta/${id}`;
        return;
      }
      aviso(`✓ Cobrado ${dinero(aC)}${ultimo?.cambio ? ` · devolver ${dinero(ultimo.cambio)}` : ''}. Falta ${dinero(c.totales.falta)}`, 'ok', 5000);
      if (modo === 'todo') modo = 'lineas';
      pintar();
    }

    montar(host, h('div', { class: 'pagina-estrecha pila' },
      h('div', { class: 'fila' }, h('a', { class: 'btn claro chiquito', href: `#/cuenta/${id}` }, '← Cuenta'), h('h1', null, `Cobrar · ${titulo}`)),
      h('div', { class: 'tarjeta fila sep envolver' },
        h('div', null, h('div', { class: 'chico suave' }, 'Atendió'), h('b', { style: 'font-size:1.15rem' }, c.cuenta.mesero)),
        encuestaHecha(id)
          ? h('span', { class: 'etiqueta pagada' }, '✓ Encuesta hecha')
          : h('button', { class: 'btn claro', type: 'button', onclick: () => abrirEncuesta({ cuentaId: id, mesero: c.cuenta.mesero, mesa: titulo, alTerminar: pintar }) }, '⭐ Hacer encuesta')),
      h('div', { class: 'totales' },
        h('div', null, h('small', null, 'Total'), h('b', { class: 'num' }, dinero(c.totales.total))),
        h('div', null, h('small', null, 'Pagado'), h('b', { class: 'num' }, dinero(c.totales.pagado))),
        h('div', { class: 'falta' }, h('small', null, 'Falta'), h('b', { class: 'num' }, dinero(falta)))),
      h('div', { class: 'modos' },
        [['todo', 'Paga uno solo', 'Todo lo que falta'], ['lineas', 'Pagan individual', 'Marcar lo de cada uno'], ['monto', 'Valor libre', 'Abono o partes iguales']]
          .map(([m, t, s]) => h('button', { class: 'modo' + (modo === m ? ' activo' : ''), type: 'button', onclick: () => { modo = m; selLineas.clear(); pintar(); } }, t, h('small', null, s)))),
      contenidoModo,
      h('div', { class: 'tarjeta' }, h('div', { class: 'fila sep' }, h('span', { style: 'font-weight:700' }, 'A cobrar ahora'), h('b', { class: 'num', style: 'font-size:1.5rem' + (exceso ? ';color:var(--rojo)' : '') }, dinero(aCobrar)))),
      pie));
    refrescarPie();
  }

  pintar();
  // Si cambia la cuenta por otro lado (otro cajero cobró, la mesera agregó algo), se actualiza sin perder el cobro en curso.
  const refrescar = debounce(async () => {
    try {
      const nueva = await api.get(`/api/cuentas/${id}`);
      if (nueva.cuenta.estado !== 'abierta') { location.hash = `#/cuenta/${id}`; return; }
      if (JSON.stringify(nueva.totales) !== JSON.stringify(c.totales)) {
        c = nueva;
        for (const l of [...selLineas]) if (!pendientes().some((p) => p.id === l)) selLineas.delete(l);
        aviso('La cuenta cambió: revisa los valores.'); pintar();
      }
    } catch { /* se reintenta */ }
  }, 300);
  const quitar = [alEvento('cuenta', (d) => { if (d?.id === id) refrescar(); })];
  return () => quitar.forEach((q) => q());
}
