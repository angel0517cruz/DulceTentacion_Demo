// Utilidades de interfaz: crear elementos, avisos, hojas (ventanas inferiores) y formato.

export function h(tag, props, ...hijos) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'value') el.value = v;
    else if (k === 'dataset') Object.assign(el.dataset, v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }
  agregar(el, hijos);
  return el;
}
function agregar(el, hijos) {
  for (const hijo of hijos.flat(Infinity)) {
    if (hijo === null || hijo === undefined || hijo === false) continue;
    el.append(hijo instanceof Node ? hijo : document.createTextNode(String(hijo)));
  }
}
export function vaciar(el) { while (el.firstChild) el.removeChild(el.firstChild); return el; }
export function montar(el, ...hijos) { vaciar(el); agregar(el, hijos); return el; }

const pesos = new Intl.NumberFormat('es-CO');
export const dinero = (n) => '$' + pesos.format(Math.round(Number(n) || 0));
export const numero = (n) => pesos.format(Number(n) || 0);
export const quitarTildes = (t) => (t || '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
export const uid = () => (crypto.randomUUID ? crypto.randomUUID() : 'u' + Date.now() + Math.random().toString(16).slice(2));
export const iniciales = (n) => n.split(/\s+/).slice(0, 2).map((p) => p[0]).join('').toUpperCase();

export function hace(fecha) {
  if (!fecha) return '';
  const t = new Date(fecha.replace(' ', 'T'));
  const min = Math.max(0, Math.round((Date.now() - t.getTime()) / 60000));
  if (min < 1) return 'ahora';
  if (min < 60) return `${min} min`;
  return `${Math.floor(min / 60)} h ${min % 60} min`;
}
export function horaCorta(fecha) {
  return fecha ? fecha.slice(11, 16) : '';
}
export function fechaBonita(f) {
  const d = new Date(f + 'T12:00:00');
  return d.toLocaleDateString('es-CO', { weekday: 'short', day: 'numeric', month: 'short' });
}

export function debounce(fn, ms = 160) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

export function vibrar(ms = 15) { try { navigator.vibrate?.(ms); } catch { /* sin vibración */ } }

// ---------- Avisos ----------
export function aviso(texto, tipo = 'ok', ms = 3200) {
  let caja = document.getElementById('avisos');
  if (!caja) { caja = h('div', { id: 'avisos', 'aria-live': 'polite' }); document.body.append(caja); }
  const el = h('div', { class: 'aviso ' + (tipo === 'error' ? 'error' : '') }, texto);
  caja.append(el);
  setTimeout(() => el.remove(), tipo === 'error' ? Math.max(ms, 5000) : ms);
}

// ---------- Hojas ----------
// Devuelve { cuerpo, cerrar }. La hoja se cierra con la X, tocando el fondo o la tecla Esc.
export function hoja(titulo, { ancho } = {}) {
  const cuerpo = h('div', { class: 'pila' });
  const cerrarBtn = h('button', { class: 'cerrar', 'aria-label': 'Cerrar', type: 'button' }, '✕');
  const caja = h('div', { class: 'hoja', style: ancho ? `max-width:${ancho}px` : null },
    h('div', { class: 'cabeza' }, h('h2', null, titulo), cerrarBtn), cuerpo);
  const fondo = h('div', { class: 'fondo' }, caja);
  let cerrada = false;
  const alCerrar = [];
  const cerrar = () => {
    if (cerrada) return;
    cerrada = true; fondo.remove(); document.removeEventListener('keydown', tecla);
    alCerrar.forEach((f) => f());
  };
  const tecla = (e) => { if (e.key === 'Escape') cerrar(); };
  fondo.addEventListener('mousedown', (e) => { if (e.target === fondo) cerrar(); });
  cerrarBtn.addEventListener('click', cerrar);
  document.addEventListener('keydown', tecla);
  document.body.append(fondo);
  return { cuerpo, cerrar, alCerrar: (f) => alCerrar.push(f), caja };
}

export function confirmar(titulo, mensaje, { si = 'Sí, continuar', no = 'Cancelar', peligro = false } = {}) {
  return new Promise((resolver) => {
    const hj = hoja(titulo);
    let respondido = false;
    const responder = (v) => { if (!respondido) { respondido = true; resolver(v); } hj.cerrar(); };
    hj.alCerrar(() => { if (!respondido) { respondido = true; resolver(false); } });
    hj.cuerpo.append(
      h('p', { style: 'margin:0' }, mensaje),
      h('div', { class: 'fila' },
        h('button', { class: 'btn claro crece', type: 'button', onclick: () => responder(false) }, no),
        h('button', { class: 'btn crece ' + (peligro ? 'rojo' : 'oro'), type: 'button', onclick: () => responder(true) }, si)));
  });
}

export function pedirTexto(titulo, { etiqueta, valor = '', placeholder = '', boton = 'Guardar', tipo = 'text', obligatorio = true } = {}) {
  return new Promise((resolver) => {
    const hj = hoja(titulo);
    let respondido = false;
    const responder = (v) => { if (!respondido) { respondido = true; resolver(v); } hj.cerrar(); };
    hj.alCerrar(() => { if (!respondido) { respondido = true; resolver(null); } });
    const entrada = h('input', { type: tipo, value: valor, placeholder, maxlength: 120, inputmode: tipo === 'number' ? 'numeric' : null });
    const error = h('div', { class: 'error-campo' });
    const enviar = () => {
      const t = entrada.value.trim();
      if (obligatorio && !t) { error.textContent = 'Escribe algo para continuar.'; return; }
      responder(t);
    };
    entrada.addEventListener('keydown', (e) => { if (e.key === 'Enter') enviar(); });
    hj.cuerpo.append(
      h('label', { class: 'campo' }, etiqueta || '', entrada), error,
      h('button', { class: 'btn oro grande', type: 'button', onclick: enviar }, boton));
    setTimeout(() => entrada.focus(), 50);
  });
}

// Botón que se desactiva mientras trabaja y evita el doble toque (clave con prisa y mala señal).
export function accion(boton, fn) {
  boton.addEventListener('click', async () => {
    if (boton.disabled) return;
    boton.disabled = true;
    try { await fn(); } catch (e) { aviso(e.message || 'No se pudo completar.', 'error'); } finally { boton.disabled = false; }
  });
  return boton;
}

export function estrellas(valor, alCambiar) {
  const caja = h('div', { class: 'estrellas' });
  const pintar = (v) => {
    vaciar(caja);
    for (let i = 1; i <= 5; i++) {
      caja.append(h('button', {
        type: 'button', class: i <= v ? 'on' : '', 'aria-label': `${i} de 5`,
        onclick: () => { alCambiar(i); pintar(i); },
      }, '★'));
    }
  };
  pintar(valor || 0);
  return caja;
}
