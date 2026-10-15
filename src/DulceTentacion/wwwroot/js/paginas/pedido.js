// Toma de pedido: rápida, con buscador, categorías, "más pedidos" y la persona de la mesa que lo pidió.
import { api, alEvento } from '../api.js';
import { estado, cargarMenu, leerBorrador, guardarBorrador, borrarBorrador, cola, vaciarCola } from '../estado.js';
import { h, montar, vaciar, dinero, hoja, aviso, accion, debounce, quitarTildes, vibrar, uid } from '../ui.js';

export default async function pedido(host, params) {
  const cuentaId = Number(params[0]);
  if (!estado.menu) await cargarMenu(true);
  const menu = estado.menu;
  let c = (await api.get(`/api/cuentas/${cuentaId}`));
  if (c.cuenta.estado !== 'abierta') { location.hash = `#/cuenta/${cuentaId}`; return; }

  const borrador = leerBorrador(cuentaId);
  const persona = 0;            // una sola cuenta: el reparto por persona se hace al cobrar
  let categoria = menu.populares.length ? 'populares' : (menu.categorias[0]?.id ?? 'todo');
  let texto = '';

  const titulo = c.cuenta.mesaNombre || `Para llevar · ${c.cuenta.etiqueta}`;
  const guardar = () => { guardarBorrador(cuentaId, borrador); };
  const totalBorrador = () => borrador.lineas.reduce((s, l) => s + l.precio * l.cantidad, 0);
  const cantidadBorrador = () => borrador.lineas.reduce((s, l) => s + l.cantidad, 0);
  const enBorrador = (productoId) => borrador.lineas.filter((l) => l.productoId === productoId).reduce((s, l) => s + l.cantidad, 0);

  // ---------- Estructura fija ----------
  const entrada = h('input', { type: 'search', placeholder: 'Buscar producto… (ej: tinto, caldo, pan)', autocomplete: 'off', enterkeyhint: 'search' });
  const zonaCategorias = h('div', { class: 'chips' });
  const zonaLista = h('div');
  const barraInferior = h('div', { class: 'barra-pedido' });
  montar(host,
    h('div', { class: 'fila sep' },
      h('div', { class: 'fila' },
        h('a', { class: 'btn claro chiquito', href: `#/cuenta/${cuentaId}` }, '← Cuenta'),
        h('h1', null, titulo)),
      h('span', { class: 'suave chico' }, 'Nuevo pedido')),
    h('div', { class: 'buscador', style: 'margin:6px 0 6px' }, entrada),
    zonaCategorias, zonaLista, barraInferior);

  // ---------- Categorías y lista ----------
  function pintarCategorias() {
    const chips = [];
    if (menu.populares.length) chips.push(['populares', '⭐ Más pedidos']);
    menu.categorias.forEach((cat) => chips.push([cat.id, `${cat.icono} ${cat.nombre}`]));
    montar(zonaCategorias, chips.map(([id, t]) =>
      h('button', { class: 'chip' + (categoria === id && !texto ? ' activo' : ''), type: 'button',
        onclick: () => { categoria = id; entrada.value = ''; texto = ''; pintarCategorias(); pintarLista(); } }, t)));
  }

  function tarjetaProducto(p) {
    const cnt = enBorrador(p.id);
    const conVariantes = p.variantes.length > 0;
    const complejo = conVariantes || p.grupos.length > 0;
    const extra = conVariantes ? ` · ${p.variantes.length} presentaciones` : p.grupos.length ? ' · elegir opciones' : '';
    return h('button', { class: 'producto', type: 'button', onclick: () => tocar(p) },
      h('div', { class: 'crece' },
        h('div', { class: 'nom' }, p.nombre),
        h('div', { class: 'sub' }, h('span', { class: 'precio' }, (conVariantes ? 'desde ' : '') + dinero(p.precio)), complejo ? h('span', { class: 'suave chico' }, extra) : null)),
      cnt ? h('span', { class: 'en-pedido' }, `× ${cnt}`) : null,
      h('span', { class: 'mas', 'aria-hidden': 'true' }, '+'));
  }

  function pintarLista() {
    const q = quitarTildes(texto.trim());
    let lista = [];
    let encabezado = null;
    if (q) {
      const palabras = q.split(/\s+/);
      lista = menu.productos.filter((p) => {
        const alvo = quitarTildes(`${p.nombre} ${p.seccion || ''}`);
        return palabras.every((w) => alvo.includes(w));
      });
      encabezado = `${lista.length} resultado(s) para “${texto.trim()}”`;
    } else if (categoria === 'populares') {
      lista = menu.populares.map((id) => menu.porId.get(id)).filter(Boolean);
      encabezado = 'Lo que más se pide';
    } else {
      lista = menu.porCategoria.get(categoria) || [];
    }

    vaciar(zonaLista);
    if (encabezado) zonaLista.append(h('div', { class: 'seccion-titulo' }, encabezado));
    if (!lista.length) {
      zonaLista.append(h('div', { class: 'vacio' }, q ? 'No encontramos ese producto. Prueba con otra palabra.' : 'Esta categoría no tiene productos.'));
      return;
    }
    if (q || categoria === 'populares') {
      zonaLista.append(h('div', { class: 'productos' }, lista.map(tarjetaProducto)));
      return;
    }
    // agrupado por sección (Caldos, Huevos, Gaseosas…)
    const grupos = new Map();
    for (const p of lista) {
      const k = p.seccion || '';
      if (!grupos.has(k)) grupos.set(k, []);
      grupos.get(k).push(p);
    }
    for (const [sec, items] of grupos) {
      if (sec) zonaLista.append(h('div', { class: 'seccion-titulo' }, sec));
      zonaLista.append(h('div', { class: 'productos' }, items.map(tarjetaProducto)));
    }
  }

  // ---------- Agregar ----------
  function agregarLinea(l) {
    const clave = JSON.stringify([l.productoId, l.varianteId ?? null, [...(l.opciones || [])].sort(), l.persona, l.nota || '']);
    const igual = borrador.lineas.find((x) => x.clave === clave);
    if (igual) igual.cantidad = Math.min(50, igual.cantidad + l.cantidad);
    else borrador.lineas.push({ ...l, clave });
    guardar();
    vibrar();
    pintarLista(); pintarBarra();
  }

  function tocar(p) {
    if (!p.variantes.length && !p.grupos.length) {
      agregarLinea({ productoId: p.id, nombre: p.nombre, detalle: '', precio: p.precio, cantidad: 1, persona });
      aviso(`+ ${p.nombre}`, 'ok', 900);
      return;
    }
    configurar(p);
  }

  function configurar(p) {
    const hj = hoja(p.nombre);
    let variante = p.variantes.length === 1 ? p.variantes[0] : null;
    const elegidas = new Map(p.grupos.map((g) => [g.id, new Set()]));
    let cantidad = 1;
    const zona = h('div', { class: 'pila' });
    const notaEl = h('input', { type: 'text', placeholder: 'Nota para cocina (opcional): sin azúcar, bien caliente…', maxlength: 120 });
    const error = h('div', { class: 'error-campo' });
    const extras = () => p.grupos.reduce((s, g) => s + g.opciones.filter((o) => elegidas.get(g.id).has(o.id)).reduce((a, o) => a + (o.extra || 0), 0), 0);
    const total = () => ((variante ? variante.precio : p.precio) + extras()) * cantidad;

    const boton = h('button', { class: 'btn oro grande pegado', type: 'button' });
    const refrescarBoton = () => { boton.textContent = `Agregar · ${dinero(total())}`; };

    function pintar() {
      montar(zona,
        p.variantes.length > 1
          ? h('div', { class: 'pila' }, h('h3', null, 'Elige la presentación'),
            p.variantes.map((v) => h('button', { class: 'opcion' + (variante?.id === v.id ? ' sel' : ''), type: 'button', onclick: () => { variante = v; error.textContent = ''; pintar(); } },
              h('span', { class: 'marca' }, variante?.id === v.id ? '✓' : ''), h('span', { class: 'crece' }, v.nombre), h('b', null, dinero(v.precio)))))
          : null,
        p.grupos.map((g) => h('div', { class: 'pila' },
          h('h3', null, g.nombre, h('span', { class: 'suave chico' }, g.max > 1 ? ` (hasta ${g.max})` : g.min === 0 ? ' (opcional)' : '')),
          g.opciones.map((o) => {
            const sel = elegidas.get(g.id).has(o.id);
            return h('button', { class: 'opcion' + (sel ? ' sel' : ''), type: 'button',
              onclick: () => {
                const s = elegidas.get(g.id);
                if (s.has(o.id)) s.delete(o.id);
                else { if (g.max === 1) s.clear(); if (s.size < g.max) s.add(o.id); }
                error.textContent = ''; pintar();
              } }, h('span', { class: 'marca' }, sel ? '✓' : ''), h('span', { class: 'crece' }, o.nombre), o.extra ? h('b', { class: 'num' }, `+${dinero(o.extra)}`) : null);
          }))));
      refrescarBoton();
    }
    pintar();

    accion(boton, async () => {
      if (p.variantes.length && !variante) { error.textContent = 'Elige la presentación.'; return; }
      for (const g of p.grupos) {
        if (elegidas.get(g.id).size < g.min) { error.textContent = `Falta elegir: ${g.nombre}.`; return; }
      }
      const ops = [];
      const partes = [];
      if (variante && p.variantes.length) partes.push(variante.nombre);
      for (const g of p.grupos) {
        const sel = g.opciones.filter((o) => elegidas.get(g.id).has(o.id));
        sel.forEach((o) => ops.push(o.id));
        if (sel.length) partes.push(sel.map((o) => (o.extra ? `${o.nombre} (+${dinero(o.extra)})` : o.nombre)).join(' + '));
      }
      agregarLinea({
        productoId: p.id, varianteId: variante?.id, opciones: ops, nombre: p.nombre, detalle: partes.join(' · '),
        precio: (variante ? variante.precio : p.precio) + extras(), cantidad, persona, nota: notaEl.value.trim() || undefined,
      });
      hj.cerrar();
    });

    const valor = h('b', null, '1');
    const paso = (d) => { cantidad = Math.min(50, Math.max(1, cantidad + d)); valor.textContent = String(cantidad); refrescarBoton(); };
    hj.cuerpo.append(zona, h('label', { class: 'campo' }, 'Nota', notaEl), error,
      h('div', { class: 'fila sep' }, h('span', { style: 'font-weight:700' }, 'Cantidad'),
        h('div', { class: 'paso' }, h('button', { type: 'button', onclick: () => paso(-1) }, '−'), valor, h('button', { type: 'button', onclick: () => paso(1) }, '+'))),
      boton);
  }

  // ---------- Revisar y enviar ----------
  function pintarBarra() {
    const n = cantidadBorrador();
    montar(barraInferior,
      h('div', { class: 'resumen' },
        n ? h('div', null, h('b', null, dinero(totalBorrador())), h('span', { class: 'suave', style: 'color:#d8d2c2' }, ` · ${n} producto${n === 1 ? '' : 's'} sin enviar`))
          : h('div', { style: 'color:#d8d2c2' }, 'Toca los productos para agregarlos')),
      h('button', { class: 'btn oro', type: 'button', disabled: n === 0, onclick: revisar }, 'Revisar y enviar'));
  }

  function revisar() {
    const hj = hoja('Pedido por enviar');
    const lista = h('div', { class: 'pila' });
    const notaEl = h('input', { type: 'text', placeholder: 'Nota general del pedido (opcional)', maxlength: 200, value: borrador.nota || '' });
    notaEl.addEventListener('input', () => { borrador.nota = notaEl.value; guardar(); });
    const boton = h('button', { class: 'btn rojo grande pegado', type: 'button' });
    function pintar() {
      if (!borrador.lineas.length) { hj.cerrar(); pintarBarra(); pintarLista(); return; }
      montar(lista, h('div', { class: 'persona-bloque' },
        borrador.lineas.map((l) => h('div', { class: 'linea-fila' },
          h('div', { class: 'crece' },
            h('div', { style: 'font-weight:700' }, l.nombre),
            l.detalle ? h('div', { class: 'det' }, l.detalle) : null,
            l.nota ? h('div', { class: 'det' }, `📝 ${l.nota}`) : null),
          h('div', { class: 'paso' },
            h('button', { type: 'button', onclick: () => { l.cantidad -= 1; if (l.cantidad <= 0) borrador.lineas.splice(borrador.lineas.indexOf(l), 1); guardar(); pintar(); pintarLista(); pintarBarra(); } }, l.cantidad === 1 ? '🗑' : '−'),
            h('b', null, String(l.cantidad)),
            h('button', { type: 'button', onclick: () => { l.cantidad = Math.min(50, l.cantidad + 1); guardar(); pintar(); pintarBarra(); } }, '+'))))));
      boton.textContent = `Enviar pedido · ${dinero(totalBorrador())}`;
    }
    pintar();

    accion(boton, async () => {
      if (!borrador.lineas.length) return;
      const cuerpo = {
        uid: borrador.uid, nota: borrador.nota?.trim() || undefined,
        lineas: borrador.lineas.map((l) => ({ productoId: l.productoId, varianteId: l.varianteId, opciones: l.opciones, cantidad: l.cantidad, persona: 0, nota: l.nota })),
      };
      try {
        await api.post(`/api/cuentas/${cuentaId}/pedido`, cuerpo);
        borrarBorrador(cuentaId);
        hj.cerrar();
        aviso('✓ Pedido enviado');
        location.hash = '#/mesas';
      } catch (e) {
        if (e.sinRed) {
          // Sin WiFi: se guarda en el celular y se manda solo cuando vuelva la conexión.
          cola.agregar({ uid: borrador.uid, cuentaId, titulo, ...cuerpo });
          borrarBorrador(cuentaId);
          hj.cerrar();
          aviso('Sin conexión: el pedido quedó guardado y se enviará solo cuando vuelva el WiFi.', 'ok', 6000);
          location.hash = '#/mesas';
          vaciarCola();
        } else throw e;
      }
    });

    hj.cuerpo.append(lista, h('label', { class: 'campo' }, 'Nota general', notaEl), boton,
      h('button', { class: 'btn peligro', type: 'button', onclick: () => { borrador.lineas = []; borrador.nota = ''; guardar(); hj.cerrar(); pintarLista();  pintarBarra(); } }, 'Vaciar pedido'));
  }

  entrada.addEventListener('input', debounce(() => { texto = entrada.value; pintarCategorias(); pintarLista(); }, 120));

  pintarCategorias(); pintarLista(); pintarBarra();

  // Si otra persona modifica la cuenta o el menú, se refresca sin perder lo que está armando.
  const quitar = [alEvento('cuenta', async (d) => {
    if (d?.id !== cuentaId) return;
    try {
      c = await api.get(`/api/cuentas/${cuentaId}`);
      if (c.cuenta.estado !== 'abierta') { aviso('Esta cuenta ya fue cerrada.', 'error'); location.hash = '#/mesas'; return; }
    } catch { /* se reintenta con el siguiente evento */ }
  })];
  return () => quitar.forEach((q) => q());
}
