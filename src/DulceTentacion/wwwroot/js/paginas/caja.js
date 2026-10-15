// Caja: gastos del día y cierre. Los números salen solo de lo que cuenta quien cierra y de los gastos.
import { api } from '../api.js';
import { estado } from '../estado.js';
import { h, montar, dinero, hoja, confirmar, aviso, accion, fechaBonita, horaCorta } from '../ui.js';

const DENOMINACIONES = [
  [100000, 'Billete $100.000'], [50000, 'Billete $50.000'], [20000, 'Billete $20.000'], [10000, 'Billete $10.000'],
  [5000, 'Billete $5.000'], [2000, 'Billete $2.000'], [1000, 'Moneda $1.000'], [500, 'Moneda $500'], [200, 'Moneda $200'], [100, 'Moneda $100'], [50, 'Moneda $50'],
];

export default async function caja(host, params) {
  if (!estado.yo.puedeCobrar) { location.hash = '#/mesas'; return; }
  let pestana = params[0] === 'historial' ? 'historial' : 'hoy';
  let hoy = null;
  let hist = null;
  let rango = { desde: '', hasta: '' };
  // lo que se va escribiendo en el cierre se conserva mientras se agregan gastos
  const form = { base: '', efectivo: '', otros: '', obs: '', desglose: null };

  async function cargar() {
    if (pestana === 'hoy') hoy = await api.get('/api/caja/hoy');
    else hist = await api.get(`/api/caja/cierres${rango.desde ? `?desde=${rango.desde}&hasta=${rango.hasta}` : ''}`);
    pintar();
  }

  function contador() {
    const hj = hoja('Contar el efectivo');
    const cant = Object.fromEntries(DENOMINACIONES.map(([v]) => [v, form.desglose?.[v] || 0]));
    const total = () => DENOMINACIONES.reduce((s, [v]) => s + v * cant[v], 0);
    const totalEl = h('b', { style: 'font-size:1.6rem' });
    const cuerpo = h('div', { class: 'pila' });
    const refrescar = () => { totalEl.textContent = dinero(total()); };
    DENOMINACIONES.forEach(([v, t]) => {
      const num = h('b', null, String(cant[v]));
      const sub = h('span', { class: 'chico suave num' }, dinero(v * cant[v]));
      const cambiar = (d) => { cant[v] = Math.max(0, Math.min(9999, cant[v] + d)); num.textContent = String(cant[v]); sub.textContent = dinero(v * cant[v]); refrescar(); };
      cuerpo.append(h('div', { class: 'fila sep' },
        h('div', { class: 'crece' }, h('div', { style: 'font-weight:700' }, t), sub),
        h('div', { class: 'paso' }, h('button', { type: 'button', onclick: () => cambiar(-1) }, '−'), num, h('button', { type: 'button', onclick: () => cambiar(1) }, '+'))));
    });
    refrescar();
    hj.cuerpo.append(cuerpo,
      h('div', { class: 'fila sep tarjeta' }, h('span', { style: 'font-weight:700' }, 'Total contado'), totalEl),
      h('button', { class: 'btn oro grande', type: 'button', onclick: () => {
        form.efectivo = String(total());
        form.desglose = Object.fromEntries(DENOMINACIONES.filter(([v]) => cant[v] > 0).map(([v]) => [v, cant[v]]));
        hj.cerrar(); pintar();
      } }, 'Usar este total'));
  }

  function tarjetaGastos() {
    const concepto = h('input', { type: 'text', placeholder: 'Ej: Hielo, domicilio, servilletas', maxlength: 80 });
    const valor = h('input', { type: 'number', inputmode: 'numeric', placeholder: 'Valor', min: 1 });
    const boton = h('button', { class: 'btn oro', type: 'button' }, 'Agregar gasto');
    accion(boton, async () => {
      await api.post('/api/caja/gastos', { concepto: concepto.value, monto: Number(valor.value) });
      aviso('Gasto registrado');
      await cargar();
    });
    const total = hoy.gastos.reduce((s, g) => s + g.monto, 0);
    return h('div', { class: 'tarjeta pila' },
      h('div', { class: 'fila sep' }, h('h2', null, 'Gastos pendientes de cierre'), h('b', { class: 'num' }, dinero(total))),
      hoy.gastos.length ? hoy.gastos.map((g) => h('div', { class: 'fila sep', style: 'border-top:1px solid var(--linea);padding-top:8px' },
        h('div', null, h('b', null, g.concepto), h('div', { class: 'chico suave' }, `${g.usuario} · ${horaCorta(g.creadoEn)}`)),
        h('div', { class: 'fila' }, h('b', { class: 'num' }, dinero(g.monto)),
          h('button', { class: 'btn peligro chiquito', type: 'button', 'aria-label': 'Borrar gasto', onclick: async () => {
            if (!(await confirmar('¿Borrar el gasto?', `${g.concepto} por ${dinero(g.monto)}`, { si: 'Sí, borrar', peligro: true }))) return;
            try { await api.del(`/api/caja/gastos/${g.id}`); await cargar(); } catch (e) { aviso(e.message, 'error'); }
          } }, '🗑'))))
        : h('div', { class: 'suave' }, 'Aún no hay gastos registrados.'),
      h('div', { class: 'pila', style: 'border-top:2px solid var(--linea);padding-top:12px' },
        h('label', { class: 'campo' }, 'Concepto', concepto), h('label', { class: 'campo' }, 'Valor', valor), boton));
  }

  function tarjetaCierre() {
    const gastos = hoy.gastos.reduce((s, g) => s + g.monto, 0);
    const resultado = h('div', { class: 'kpi' });
    const pintarRes = () => {
      const n = (v) => Number(v) || 0;
      const ingreso = n(form.efectivo) + n(form.otros) + gastos - n(form.base);
      montar(resultado, h('small', null, 'Ingreso del día'),
        h('b', { class: 'num' }, dinero(ingreso)),
        h('div', { class: 'chico suave' }, `Efectivo ${dinero(n(form.efectivo))} + otros ${dinero(n(form.otros))} + gastos ${dinero(gastos)} − base ${dinero(n(form.base))}`));
    };
    const campo = (etiqueta, clave, ayuda) => h('label', { class: 'campo' }, etiqueta,
      h('input', { type: 'number', inputmode: 'numeric', min: 0, value: form[clave], placeholder: '0',
        oninput: (e) => { form[clave] = e.target.value; if (clave === 'efectivo') form.desglose = null; pintarRes(); } }),
      ayuda ? h('span', { class: 'chico suave', style: 'font-weight:400' }, ayuda) : null);
    const obs = h('textarea', { rows: 2, placeholder: 'Observaciones (opcional)', maxlength: 400, oninput: (e) => { form.obs = e.target.value; } }, form.obs);
    const boton = h('button', { class: 'btn rojo grande', type: 'button' }, 'Cerrar caja');
    accion(boton, async () => {
      if (form.efectivo === '') { aviso('Escribe el efectivo que contaste (o cuéntalo por billetes).', 'error'); return; }
      const ok = await confirmar('¿Cerrar la caja?', `Efectivo ${dinero(form.efectivo)} · otros ${dinero(form.otros || 0)} · gastos ${dinero(gastos)}. Se guardará como cierre del día.`, { si: 'Sí, cerrar caja' });
      if (!ok) return;
      const r = await api.post('/api/caja/cierre', {
        baseInicial: Number(form.base) || 0, efectivoContado: Number(form.efectivo), otrosReportados: Number(form.otros) || 0,
        desglose: form.desglose || undefined, observaciones: form.obs || undefined,
      });
      Object.assign(form, { base: '', efectivo: '', otros: '', obs: '', desglose: null });
      aviso(`Caja cerrada. Ingreso del día: ${dinero(r.cierre.ingreso)}`, 'ok', 6000);
      await cargar();
    });
    const caja = h('div', { class: 'tarjeta pila' },
      h('h2', null, 'Cerrar caja'),
      h('div', { class: 'chico suave' }, 'Escribe lo que contaste. El sistema no mira los pedidos: solo suma lo que reportas y los gastos.'),
      campo('Base inicial (con lo que se abrió la caja)', 'base'),
      campo('Efectivo contado', 'efectivo'),
      h('button', { class: 'btn claro', type: 'button', onclick: contador }, '🧮 Contar billetes y monedas'),
      campo('Otros ingresos que reportas (transferencias, tarjeta…)', 'otros'),
      obs, resultado, boton);
    pintarRes();
    return caja;
  }

  function vistaHoy() {
    return h('div', { class: 'dos-col' },
      h('div', { class: 'pila' }, tarjetaGastos(),
        hoy.cierres.length ? h('div', { class: 'tarjeta pila' }, h('h2', null, 'Cierres de hoy'),
          hoy.cierres.map((c) => h('div', { class: 'fila sep' },
            h('div', null, h('b', null, `${c.usuario} · ${horaCorta(c.creadoEn)}`), h('div', { class: 'chico suave' }, `Efectivo ${dinero(c.efectivoContado)} · gastos ${dinero(c.gastos)}`)),
            h('b', { class: 'num' }, dinero(c.ingreso))))) : null),
      tarjetaCierre());
  }

  function vistaHistorial() {
    const t = hist.totales;
    const desde = h('input', { type: 'date', value: hist.desde });
    const hasta = h('input', { type: 'date', value: hist.hasta });
    const ver = h('button', { class: 'btn oro', type: 'button' }, 'Ver');
    ver.addEventListener('click', () => { rango = { desde: desde.value, hasta: hasta.value }; cargar().catch((e) => aviso(e.message, 'error')); });
    return h('div', { class: 'pila' },
      h('div', { class: 'fila envolver' }, h('label', { class: 'campo' }, 'Desde', desde), h('label', { class: 'campo' }, 'Hasta', hasta), ver,
        h('a', { class: 'btn verde', href: `/api/caja/excel?desde=${hist.desde}&hasta=${hist.hasta}`, download: '' }, '⬇ Descargar Excel')),
      h('div', { class: 'kpis' },
        h('div', { class: 'kpi' }, h('small', null, 'Efectivo contado'), h('b', { class: 'num' }, dinero(t.efectivo))),
        h('div', { class: 'kpi' }, h('small', null, 'Otros reportados'), h('b', { class: 'num' }, dinero(t.otros))),
        h('div', { class: 'kpi' }, h('small', null, 'Gastos'), h('b', { class: 'num' }, dinero(t.gastos))),
        h('div', { class: 'kpi' }, h('small', null, 'Ingreso del periodo'), h('b', { class: 'num' }, dinero(t.ingreso)))),
      hist.cierres.length
        ? h('div', { class: 'tarjeta', style: 'overflow-x:auto' }, h('table', { class: 'tabla' },
          h('thead', null, h('tr', null, ['Fecha', 'Cerró', 'Base', 'Efectivo', 'Otros', 'Gastos', 'Ingreso', ''].map((x, i) => h('th', { class: i > 1 ? 'd' : '' }, x)))),
          h('tbody', null, hist.cierres.map((c) => h('tr', null,
            h('td', null, fechaBonita(c.fecha)), h('td', null, c.usuario),
            h('td', { class: 'd num' }, dinero(c.baseInicial)), h('td', { class: 'd num' }, dinero(c.efectivoContado)),
            h('td', { class: 'd num' }, dinero(c.otrosReportados)), h('td', { class: 'd num' }, dinero(c.gastos)),
            h('td', { class: 'd num' }, h('b', null, dinero(c.ingreso))),
            h('td', { class: 'd' }, estado.yo.esAdmin ? h('button', { class: 'btn peligro chiquito', type: 'button', 'aria-label': 'Borrar cierre', onclick: async () => {
              if (!(await confirmar('¿Borrar este cierre?', 'Sus gastos quedarán pendientes para el siguiente cierre.', { si: 'Sí, borrar', peligro: true }))) return;
              try { await api.del(`/api/caja/cierres/${c.id}`); await cargar(); } catch (e) { aviso(e.message, 'error'); }
            } }, '🗑') : null))))))
        : h('div', { class: 'vacio' }, 'No hay cierres en esas fechas.'));
  }

  function pintar() {
    montar(host,
      h('div', { class: 'fila sep envolver' },
        h('h1', null, 'Caja'),
        h('div', { class: 'chips' },
          h('button', { class: 'chip' + (pestana === 'hoy' ? ' activo' : ''), type: 'button', onclick: () => { pestana = 'hoy'; cargar(); } }, 'Hoy'),
          h('button', { class: 'chip' + (pestana === 'historial' ? ' activo' : ''), type: 'button', onclick: () => { pestana = 'historial'; cargar(); } }, 'Historial'))),
      pestana === 'hoy' ? vistaHoy() : vistaHistorial());
  }

  await cargar();
}
