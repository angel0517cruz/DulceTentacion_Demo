// Administración: estadísticas, productos, personal, mesas, precios y sistema.
import { api } from '../api.js';
import { estado, cargarMenu } from '../estado.js';
import { h, montar, vaciar, dinero, numero, hoja, confirmar, pedirTexto, aviso, accion, quitarTildes, fechaBonita, debounce } from '../ui.js';

const PESTANAS = [['estadisticas', '📊 Estadísticas'], ['productos', '🍰 Productos'], ['personal', '👥 Personal'], ['mesas', '🪑 Mesas'], ['precios', '🏷️ Precios'], ['sistema', '🛠️ Sistema']];

export default async function admin(host, params) {
  if (!estado.yo.esAdmin) { location.hash = '#/mesas'; return; }
  const actual = PESTANAS.some(([k]) => k === params[0]) ? params[0] : 'estadisticas';
  const contenido = h('div', { class: 'pila' }, h('div', { class: 'cargando' }, 'Cargando…'));
  montar(host,
    h('h1', null, 'Administración'),
    h('div', { class: 'chips', style: 'margin-top:8px' }, PESTANAS.map(([k, t]) => h('a', { class: 'chip' + (k === actual ? ' activo' : ''), href: `#/admin/${k}`, style: 'text-decoration:none;color:inherit' }, t))),
    contenido);
  const vistas = { estadisticas, productos, personal, mesas, precios, sistema };
  try { await vistas[actual](contenido); } catch (e) { montar(contenido, h('div', { class: 'vacio' }, e.message)); }
}

// ======================================================================= ESTADÍSTICAS
async function estadisticas(el) {
  const hoyF = new Date();
  const fmt = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const hace = (n) => { const d = new Date(); d.setDate(d.getDate() - n); return fmt(d); };
  let rango = { desde: hace(6), hasta: fmt(hoyF), preset: '7' };

  async function pintar() {
    const e = await api.get(`/api/estadisticas?desde=${rango.desde}&hasta=${rango.hasta}`);
    const preset = (k, t, d, hs) => h('button', { class: 'chip' + (rango.preset === k ? ' activo' : ''), type: 'button',
      onclick: () => { rango = { desde: d, hasta: hs, preset: k }; pintar().catch((x) => aviso(x.message, 'error')); } }, t);
    const desde = h('input', { type: 'date', value: rango.desde });
    const hasta = h('input', { type: 'date', value: rango.hasta });
    const maxProd = Math.max(1, ...e.productos.map((p) => p.cantidad));
    const maxCat = Math.max(1, ...e.categorias.map((p) => p.valor));
    const maxHora = Math.max(1, ...e.porHora.map((p) => p.cantidad));
    const maxMes = Math.max(1, ...e.porMesero.map((p) => p.valor));
    const barras = (filas, max, valor, texto) => filas.map((f) => h('div', { class: 'barra-h' },
      h('span', { class: 'chico' }, texto(f)), h('div', { class: 'pista' }, h('div', { class: 'relleno', style: `width:${Math.max(2, (valor(f) / max) * 100)}%` })), h('b', { class: 'num' }, numero(valor(f)))));

    montar(el,
      h('div', { class: 'fila envolver' },
        preset('hoy', 'Hoy', hace(0), hace(0)), preset('7', 'Últimos 7 días', hace(6), hace(0)), preset('30', 'Últimos 30 días', hace(29), hace(0)),
        h('label', { class: 'fila chico' }, 'Del', desde), h('label', { class: 'fila chico' }, 'al', hasta),
        h('button', { class: 'btn oro chiquito', type: 'button', onclick: () => { rango = { desde: desde.value, hasta: hasta.value, preset: 'x' }; pintar().catch((x) => aviso(x.message, 'error')); } }, 'Ver'),
        h('a', { class: 'btn verde chiquito', href: `/api/admin/excel?desde=${rango.desde}&hasta=${rango.hasta}`, download: '' }, '⬇ Descargar Excel')),

      h('div', { class: 'kpis' },
        h('div', { class: 'kpi' }, h('small', null, 'Cuentas atendidas'), h('b', null, numero(e.resumen.cuentas))),
        h('div', { class: 'kpi' }, h('small', null, 'Productos pedidos'), h('b', null, numero(e.resumen.unidades))),
        h('div', { class: 'kpi' }, h('small', null, 'Pedido total'), h('b', { class: 'num' }, dinero(e.resumen.pedido))),
        h('div', { class: 'kpi' }, h('small', null, 'Cuenta promedio'), h('b', { class: 'num' }, dinero(e.resumen.ticketPromedio)))),

      h('div', { class: 'tarjeta' },
        h('h2', null, '🏆 Productos más pedidos'),
        e.productos.length ? barras(e.productos, maxProd, (p) => p.cantidad, (p) => p.nombre) : h('div', { class: 'vacio' }, 'Aún no hay pedidos en este periodo.')),

      h('div', { class: 'dos-col' },
        h('div', { class: 'tarjeta' }, h('h2', null, 'Por categoría (valor pedido)'),
          barras(e.categorias, maxCat, (p) => p.valor, (p) => `${p.icono} ${p.nombre}`)),
        h('div', { class: 'tarjeta' }, h('h2', null, 'Por mesera/o'),
          e.porMesero.length ? barras(e.porMesero, maxMes, (p) => p.valor, (p) => `${p.nombre.split(' ')[0]} (${p.cuentas})`) : h('div', { class: 'suave' }, 'Sin datos.'))),

      h('div', { class: 'tarjeta' }, h('h2', null, '🕒 Horas de más movimiento (productos pedidos)'),
        e.porHora.length ? h('div', { class: 'columnas' }, e.porHora.map((x) =>
          h('div', null, h('span', { class: 'num' }, String(x.cantidad)), h('div', { class: 'col', style: `height:${Math.max(3, (x.cantidad / maxHora) * 100)}%` }), h('span', null, `${x.hora}h`)))) : h('div', { class: 'suave' }, 'Sin datos.')),

      e.porDia.length > 1 ? h('div', { class: 'tarjeta' }, h('h2', null, 'Por día'),
        h('table', { class: 'tabla' }, h('thead', null, h('tr', null, h('th', null, 'Día'), h('th', { class: 'd' }, 'Cuentas'), h('th', { class: 'd' }, 'Productos'), h('th', { class: 'd' }, 'Pedido'))),
          h('tbody', null, e.porDia.map((d) => h('tr', null, h('td', null, fechaBonita(d.fecha)), h('td', { class: 'd' }, numero(d.cuentas)), h('td', { class: 'd' }, numero(d.cantidad)), h('td', { class: 'd num' }, dinero(d.valor))))))) : null,

      h('div', { class: 'tarjeta pila' },
        h('h2', null, '💵 Caja (solo lo que se reportó en los cierres)'),
        h('div', { class: 'kpis' },
          h('div', { class: 'kpi' }, h('small', null, 'Cierres'), h('b', null, numero(e.caja.cierres))),
          h('div', { class: 'kpi' }, h('small', null, 'Efectivo contado'), h('b', { class: 'num' }, dinero(e.caja.efectivo))),
          h('div', { class: 'kpi' }, h('small', null, 'Otros reportados'), h('b', { class: 'num' }, dinero(e.caja.otros))),
          h('div', { class: 'kpi' }, h('small', null, 'Gastos'), h('b', { class: 'num' }, dinero(e.caja.gastos))),
          h('div', { class: 'kpi' }, h('small', null, 'Ingreso'), h('b', { class: 'num' }, dinero(e.caja.ingreso)))),
        h('div', { class: 'chico suave' }, 'Ingreso = efectivo contado + otros reportados + gastos − base inicial. No usa los pedidos.'),
        e.porMetodo.length ? h('div', { class: 'chico suave' }, 'Cobros registrados por método: ' + e.porMetodo.map((m) => `${m.metodo} ${dinero(m.valor)}`).join(' · ')) : null),

      h('div', { class: 'tarjeta pila' },
        h('h2', null, '⭐ Encuestas por mesera'),
        h('div', { class: 'chico suave' }, 'Cada encuesta se carga a la persona que atendió esa mesa. Promedio de 1 a 5.'),
        h('table', { class: 'tabla' },
          h('thead', null, h('tr', null, h('th', null, 'Mesera/o'), h('th', { class: 'd' }, 'Encuestas'), h('th', { class: 'd' }, 'Atención'), h('th', { class: 'd' }, 'Comida'))),
          h('tbody', null, e.encuestas.porMesera.map((m) => h('tr', null,
            h('td', null, m.nombre), h('td', { class: 'd' }, numero(m.total)),
            h('td', { class: 'd' }, m.atencion ? `${m.atencion} ★` : '—'), h('td', { class: 'd' }, m.comida ? `${m.comida} ★` : '—'))))),
        e.encuestas.comentarios.length
          ? [h('h3', null, 'Comentarios recientes'),
            e.encuestas.comentarios.map((c) => h('div', { style: 'border-top:1px solid var(--linea);padding-top:8px' },
              h('div', null, `“${c.comentario}”`), h('div', { class: 'chico suave' }, `Para ${c.mesero || '—'} · ${c.fecha.slice(0, 10)}${c.atencion ? ` · atención ${c.atencion}★` : ''}${c.comida ? ` · comida ${c.comida}★` : ''}`)))]
          : null));
  }
  await pintar();
}

// ======================================================================= PRODUCTOS
async function productos(el) {
  let menu = await api.get('/api/admin/menu');
  let cat = 'todas';
  let texto = '';
  const lista = h('div', { class: 'pila' });
  const buscador = h('input', { type: 'search', placeholder: 'Buscar producto…' });
  const zonaCats = h('div', { class: 'chips' });
  const recargar = async () => { menu = await api.get('/api/admin/menu'); cargarMenu().catch(() => {}); pintar(); };

  function editarProducto(p) {
    const nuevo = !p;
    const d = p ? JSON.parse(JSON.stringify(p)) : { nombre: '', categoriaId: cat === 'todas' ? menu.categorias[0].id : cat, seccion: '', precio: '', activo: 1, variantes: [], grupos: [] };
    const hj = hoja(nuevo ? 'Nuevo producto' : 'Editar producto');
    const zona = h('div', { class: 'pila' });
    const nombre = h('input', { type: 'text', value: d.nombre, maxlength: 80 });
    const selCat = h('select', null, menu.categorias.map((c) => h('option', { value: c.id, selected: c.id === d.categoriaId }, `${c.icono} ${c.nombre}`)));
    const secciones = [...new Set(menu.productos.map((x) => x.seccion).filter(Boolean))];
    const sec = h('input', { type: 'text', value: d.seccion || '', maxlength: 40, list: 'lista-secciones', placeholder: 'Ej: Caldos, Gaseosas (opcional)' });
    const precio = h('input', { type: 'number', inputmode: 'numeric', min: 0, value: d.variantes.length ? '' : d.precio });
    const activo = h('input', { type: 'checkbox', checked: d.activo === 1 || d.activo === true });
    const error = h('div', { class: 'error-campo' });

    function pintarHijos() {
      montar(zona,
        h('div', { class: 'tarjeta pila' },
          h('h3', null, 'Presentaciones con precio propio'),
          h('div', { class: 'chico suave' }, 'Ej: Sin arroz / Con arroz, o 350 ml / 1,5 L. Si agregas presentaciones, el precio de arriba no se usa.'),
          d.variantes.map((v, i) => h('div', { class: 'fila' },
            h('input', { type: 'text', value: v.nombre, placeholder: 'Nombre', oninput: (e) => { v.nombre = e.target.value; } }),
            h('input', { type: 'number', inputmode: 'numeric', value: v.precio, placeholder: 'Precio', style: 'max-width:130px', oninput: (e) => { v.precio = Number(e.target.value); } }),
            h('button', { class: 'btn peligro chiquito', type: 'button', onclick: () => { d.variantes.splice(i, 1); pintarHijos(); } }, '✕'))),
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: () => { d.variantes.push({ nombre: '', precio: '' }); pintarHijos(); } }, '＋ Presentación')),
        h('div', { class: 'tarjeta pila' },
          h('h3', null, 'Opciones para elegir (sin costo)'),
          h('div', { class: 'chico suave' }, 'Ej: tipo de huevo, con o sin arroz, bebida. Una opción por línea. Si cuesta más, escribe el recargo: Rancheros +2000'),
          d.grupos.map((g, i) => h('div', { class: 'pila', style: 'border-top:1px solid var(--linea);padding-top:10px' },
            h('div', { class: 'fila' },
              h('input', { type: 'text', value: g.nombre, placeholder: 'Ej: Bebida', oninput: (e) => { g.nombre = e.target.value; } }),
              h('button', { class: 'btn peligro chiquito', type: 'button', onclick: () => { d.grupos.splice(i, 1); pintarHijos(); } }, '✕')),
            h('div', { class: 'fila' },
              h('label', { class: 'campo crece' }, 'Mínimo a elegir', h('input', { type: 'number', min: 0, value: g.min, oninput: (e) => { g.min = Number(e.target.value); } })),
              h('label', { class: 'campo crece' }, 'Máximo', h('input', { type: 'number', min: 1, value: g.max, oninput: (e) => { g.max = Number(e.target.value); } }))),
            h('textarea', { rows: 4, placeholder: 'Una opción por línea', oninput: (e) => { g.opciones = e.target.value.split('\n').map((x) => x.trim()).filter(Boolean); } }, (g.opciones || []).join('\n')))),
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: () => { d.grupos.push({ nombre: '', min: 1, max: 1, opciones: [] }); pintarHijos(); } }, '＋ Grupo de opciones')));
    }
    // normaliza grupos existentes (opciones vienen como objetos)
    d.grupos.forEach((g) => { g.opciones = (g.opciones || []).map((o) => (o.nombre === undefined ? o : (o.extra ? `${o.nombre} +${o.extra}` : o.nombre))); });
    pintarHijos();

    const guardarBtn = h('button', { class: 'btn oro grande', type: 'button' }, 'Guardar');
    accion(guardarBtn, async () => {
      const cuerpo = {
        categoriaId: Number(selCat.value), nombre: nombre.value, seccion: sec.value, precio: Number(precio.value) || 0, activo: activo.checked,
        variantes: d.variantes.filter((v) => v.nombre || v.precio).map((v) => ({ nombre: v.nombre, precio: Number(v.precio) })),
        grupos: d.grupos.map((g) => ({ nombre: g.nombre, min: g.min, max: g.max, opciones: g.opciones })),
      };
      if (nuevo) await api.post('/api/admin/productos', cuerpo); else await api.put(`/api/admin/productos/${p.id}`, cuerpo);
      hj.cerrar(); aviso('Producto guardado'); await recargar();
    });
    hj.cuerpo.append(
      h('datalist', { id: 'lista-secciones' }, secciones.map((s) => h('option', { value: s }))),
      h('label', { class: 'campo' }, 'Nombre', nombre), h('label', { class: 'campo' }, 'Categoría', selCat),
      h('label', { class: 'campo' }, 'Sección dentro de la categoría', sec),
      h('label', { class: 'campo' }, 'Precio (si no tiene presentaciones)', precio),
      h('label', { class: 'fila' }, activo, 'Disponible para pedir'),
      zona, error, guardarBtn,
      nuevo ? null : h('button', { class: 'btn peligro', type: 'button', onclick: async () => {
        if (!(await confirmar('¿Eliminar producto?', 'Si ya se vendió, solo se oculta para conservar las estadísticas.', { si: 'Sí, eliminar', peligro: true }))) return;
        try { const r = await api.del(`/api/admin/productos/${p.id}`); hj.cerrar(); aviso(r.desactivado ? 'El producto se ocultó (ya tenía ventas).' : 'Producto eliminado'); await recargar(); } catch (e) { aviso(e.message, 'error'); }
      } }, 'Eliminar producto'));
  }

  async function categorias() {
    const hj = hoja('Categorías');
    const pintar = () => montar(hj.cuerpo,
      menu.categorias.map((c) => h('div', { class: 'fila sep tarjeta' },
        h('b', null, `${c.icono} ${c.nombre}`),
        h('div', { class: 'fila' },
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: async () => {
            const n = await pedirTexto('Renombrar categoría', { etiqueta: 'Nombre', valor: c.nombre });
            if (!n) return;
            await api.put(`/api/admin/categorias/${c.id}`, { nombre: n, activa: c.activa === 1 }); await recargar(); hj.cerrar(); categorias();
          } }, '✎'),
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: async () => {
            await api.put(`/api/admin/categorias/${c.id}`, { nombre: c.nombre, activa: c.activa !== 1 }); await recargar(); hj.cerrar(); categorias();
          } }, c.activa === 1 ? 'Ocultar' : 'Mostrar')))),
      h('button', { class: 'btn oro', type: 'button', onclick: async () => {
        const n = await pedirTexto('Nueva categoría', { etiqueta: 'Nombre' });
        if (!n) return;
        try { await api.post('/api/admin/categorias', { nombre: n }); await recargar(); hj.cerrar(); categorias(); } catch (e) { aviso(e.message, 'error'); }
      } }, '＋ Nueva categoría'));
    pintar();
  }

  function pintar() {
    montar(zonaCats,
      h('button', { class: 'chip' + (cat === 'todas' ? ' activo' : ''), type: 'button', onclick: () => { cat = 'todas'; pintar(); } }, 'Todas'),
      menu.categorias.map((c) => h('button', { class: 'chip' + (cat === c.id ? ' activo' : ''), type: 'button', onclick: () => { cat = c.id; pintar(); } }, `${c.icono} ${c.nombre}`)));
    const q = quitarTildes(texto);
    const filas = menu.productos.filter((p) => (cat === 'todas' || p.categoriaId === cat) && (!q || quitarTildes(`${p.nombre} ${p.seccion || ''}`).includes(q)));
    vaciar(lista);
    if (!filas.length) lista.append(h('div', { class: 'vacio' }, 'No hay productos con ese filtro.'));
    filas.forEach((p) => lista.append(h('div', { class: 'tarjeta fila', style: p.activo ? '' : 'opacity:.55' },
      h('div', { class: 'crece' }, h('b', null, p.nombre),
        h('div', { class: 'chico suave' }, [menu.categorias.find((c) => c.id === p.categoriaId)?.nombre, p.seccion, p.variantes.length ? `${p.variantes.length} presentaciones` : null, p.activo ? null : 'OCULTO'].filter(Boolean).join(' · '))),
      h('b', { class: 'num' }, (p.variantes.length ? 'desde ' : '') + dinero(p.precio)),
      p.variantes.length ? null : h('button', { class: 'btn claro chiquito', type: 'button', onclick: async () => {
        const v = await pedirTexto(`Precio de ${p.nombre}`, { etiqueta: 'Nuevo precio', valor: String(p.precio), tipo: 'number', boton: 'Cambiar precio' });
        if (!v) return;
        try { await api.put(`/api/admin/productos/${p.id}/precio`, { precio: Number(v) }); aviso('Precio actualizado'); await recargar(); } catch (e) { aviso(e.message, 'error'); }
      } }, '$'),
      h('button', { class: 'btn claro chiquito', type: 'button', onclick: async () => { await api.post(`/api/admin/productos/${p.id}/activo`, { activo: !p.activo }); await recargar(); } }, p.activo ? 'Ocultar' : 'Mostrar'),
      h('button', { class: 'btn oro chiquito', type: 'button', onclick: () => editarProducto(p) }, 'Editar'))));
  }

  buscador.addEventListener('input', debounce(() => { texto = buscador.value; pintar(); }, 120));
  montar(el,
    h('div', { class: 'fila envolver' },
      h('div', { class: 'buscador crece', style: 'min-width:200px' }, buscador),
      h('button', { class: 'btn oro', type: 'button', onclick: () => editarProducto(null) }, '＋ Producto'),
      h('button', { class: 'btn claro', type: 'button', onclick: categorias }, 'Categorías')),
    zonaCats, lista);
  pintar();
}

// ======================================================================= PERSONAL
async function personal(el) {
  const ROLES = { administrador: 'Administrador/a', cajero: 'Cajero/a', mesero: 'Mesero/a' };
  let lista = [];
  async function pintar() {
    lista = await api.get('/api/admin/usuarios');
    montar(el,
      h('div', { class: 'fila sep' }, h('div', { class: 'suave' }, 'Los administradores también pueden tomar pedidos. Los cajeros cobran y cierran caja.'),
        h('button', { class: 'btn oro', type: 'button', onclick: () => editar(null) }, '＋ Persona')),
      lista.map((u) => h('div', { class: 'tarjeta fila', style: u.activo ? '' : 'opacity:.55' },
        h('div', { class: 'crece' }, h('b', null, u.nombre), h('div', { class: 'chico suave' }, `${ROLES[u.rol]}${u.activo ? '' : ' · inactivo'}`)),
        h('button', { class: 'btn claro chiquito', type: 'button', onclick: () => cambiarPin(u) }, '🔑 PIN'),
        h('button', { class: 'btn oro chiquito', type: 'button', onclick: () => editar(u) }, 'Editar'))));
  }
  function editar(u) {
    const nuevo = !u;
    const hj = hoja(nuevo ? 'Nueva persona' : 'Editar persona');
    const nombre = h('input', { type: 'text', value: u?.nombre || '', maxlength: 60 });
    const rol = h('select', null, Object.entries(ROLES).map(([k, t]) => h('option', { value: k, selected: (u?.rol || 'mesero') === k }, t)));
    const pin = h('input', { type: 'password', inputmode: 'numeric', maxlength: 8, autocomplete: 'new-password' });
    const activo = h('input', { type: 'checkbox', checked: u ? u.activo === 1 : true });
    const guardar = h('button', { class: 'btn oro grande', type: 'button' }, 'Guardar');
    accion(guardar, async () => {
      if (nuevo) await api.post('/api/admin/usuarios', { nombre: nombre.value, rol: rol.value, pin: pin.value });
      else await api.put(`/api/admin/usuarios/${u.id}`, { nombre: nombre.value, rol: rol.value, activo: activo.checked });
      hj.cerrar(); aviso('Guardado'); await pintar();
    });
    hj.cuerpo.append(h('label', { class: 'campo' }, 'Nombre', nombre), h('label', { class: 'campo' }, 'Rol', rol),
      nuevo ? h('label', { class: 'campo' }, 'PIN inicial (4 a 8 números)', pin) : h('label', { class: 'fila' }, activo, 'Puede entrar al sistema'),
      guardar);
  }
  async function cambiarPin(u) {
    const v = await pedirTexto(`Nuevo PIN para ${u.nombre}`, { etiqueta: 'PIN (4 a 8 números)', tipo: 'password', boton: 'Cambiar PIN' });
    if (!v) return;
    try { await api.post(`/api/admin/usuarios/${u.id}/pin`, { pin: v }); aviso('PIN cambiado'); } catch (e) { aviso(e.message, 'error'); }
  }
  await pintar();
}

// ======================================================================= MESAS
async function mesas(el) {
  async function pintar() {
    const lista = await api.get('/api/admin/mesas');
    montar(el,
      h('div', { class: 'fila sep' }, h('div', { class: 'suave' }, `${lista.filter((m) => m.activa).length} mesas activas`),
        h('button', { class: 'btn oro', type: 'button', onclick: async () => {
          const n = await pedirTexto('Nueva mesa', { etiqueta: 'Nombre', placeholder: `Mesa ${lista.length + 1}` });
          if (!n) return;
          try { await api.post('/api/admin/mesas', { nombre: n }); await pintar(); } catch (e) { aviso(e.message, 'error'); }
        } }, '＋ Mesa')),
      h('div', { class: 'rejilla' }, lista.map((m) => h('div', { class: 'tarjeta pila', style: m.activa ? '' : 'opacity:.55' },
        h('b', null, m.nombre),
        h('div', { class: 'fila' },
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: async () => {
            const n = await pedirTexto('Renombrar mesa', { etiqueta: 'Nombre', valor: m.nombre });
            if (!n) return;
            try { await api.put(`/api/admin/mesas/${m.id}`, { nombre: n }); await pintar(); } catch (e) { aviso(e.message, 'error'); }
          } }, '✎'),
          h('button', { class: 'btn claro chiquito', type: 'button', onclick: async () => {
            try { await api.put(`/api/admin/mesas/${m.id}`, { nombre: m.nombre, activa: !m.activa }); await pintar(); } catch (e) { aviso(e.message, 'error'); }
          } }, m.activa ? 'Quitar' : 'Activar'))))));
  }
  await pintar();
}

// ======================================================================= PRECIOS
async function precios(el) {
  const menu = await api.get('/api/admin/menu');
  const cat = h('select', null, h('option', { value: '' }, 'Todo el menú'), menu.categorias.map((c) => h('option', { value: c.id }, `${c.icono} ${c.nombre}`)));
  const pct = h('input', { type: 'number', step: '0.5', placeholder: 'Ej: 8 para subir 8%, -5 para bajar' });
  const red = h('select', null, [[100, 'Al $100 más cercano'], [50, 'Al $50 más cercano'], [500, 'Al $500 más cercano'], [1000, 'Al $1.000 más cercano'], [0, 'Sin redondear']].map(([v, t]) => h('option', { value: v }, t)));
  const vista = h('div', { class: 'pila' });
  const cuerpo = () => ({ categoriaId: cat.value ? Number(cat.value) : null, porcentaje: Number(pct.value), redondeo: Number(red.value) });
  const ver = h('button', { class: 'btn oro', type: 'button' }, 'Ver cómo quedarían');
  accion(ver, async () => {
    const r = await api.post('/api/admin/precios/vista', cuerpo());
    montar(vista,
      h('div', { class: 'banner' }, `Se cambiarían ${r.total} precios. Algunos ejemplos:`),
      h('table', { class: 'tabla' }, h('tbody', null, r.ejemplos.map((x) => h('tr', null, h('td', null, x.nombre), h('td', { class: 'd num' }, dinero(x.antes)), h('td', null, '→'), h('td', { class: 'd num' }, h('b', null, dinero(x.nuevo))))))),
      accion(h('button', { class: 'btn rojo grande', type: 'button' }, `Aplicar a ${r.total} precios`), async () => {
        if (!(await confirmar('¿Aplicar el cambio?', 'Los pedidos ya tomados conservan el precio con el que se pidieron.', { si: 'Sí, aplicar', peligro: true }))) return;
        const a = await api.post('/api/admin/precios/aplicar', cuerpo());
        aviso(`Listo: ${a.cambios} precios actualizados`);
        vaciar(vista); cargarMenu().catch(() => {});
      }));
  });
  montar(el, h('div', { class: 'tarjeta pila pagina-estrecha' },
    h('h2', null, 'Subir o bajar precios por porcentaje'),
    h('div', { class: 'chico suave' }, 'Para cambiar el precio de un solo producto, usa Productos → botón $.'),
    h('label', { class: 'campo' }, '¿A qué?', cat), h('label', { class: 'campo' }, 'Porcentaje', pct), h('label', { class: 'campo' }, 'Redondeo', red), ver, vista));
}

// ======================================================================= SISTEMA
async function sistema(el) {
  async function pintar() {
    const s = await api.get('/api/admin/sistema');
    const mb = (b) => (b / 1048576).toFixed(1) + ' MB';
    montar(el, h('div', { class: 'pila pagina-estrecha' },
      h('div', { class: 'tarjeta pila' },
        h('h2', null, '📱 Cómo entrar desde los celulares'),
        h('div', { class: 'chico suave' }, 'Con el celular conectado al WiFi del café, abre esta dirección en el navegador:'),
        s.ips.length ? s.ips.map((ip) => h('div', { style: 'font-size:1.3rem;font-weight:800;color:var(--oro-osc)' }, s.puerto === 80 ? `http://${ip}` : `http://${ip}:${s.puerto}`)) : h('div', { class: 'banner rojo' }, 'Este computador no aparece conectado a una red.'),
        h('div', { class: 'chico suave' }, 'Consejo: en el celular, menú del navegador → “Agregar a la pantalla de inicio”.')),
      h('div', { class: 'tarjeta pila' },
        h('h2', null, '💾 Copias de seguridad'),
        h('div', { class: 'chico suave' }, `Base de datos: ${mb(s.baseDeDatos.bytes)}. Se hace una copia automática cada día y se guardan las de los últimos 30 días.`),
        h('div', { class: 'chico suave' }, `Ubicación: ${s.baseDeDatos.ruta}`),
        accion(h('button', { class: 'btn oro', type: 'button' }, 'Crear copia ahora'), async () => { await api.post('/api/admin/respaldos'); aviso('Copia creada'); await pintar(); }),
        s.respaldos.length ? s.respaldos.map((r) => h('div', { class: 'fila sep', style: 'border-top:1px solid var(--linea);padding-top:8px' },
          h('div', null, h('b', null, r.fecha), h('div', { class: 'chico suave' }, `${r.nombre} · ${mb(r.bytes)}`)),
          h('a', { class: 'btn claro chiquito', href: `/api/admin/respaldos/${encodeURIComponent(r.nombre)}` }, 'Descargar'))) : null)));
  }
  await pintar();
}
