// Encuesta rápida de la mesa. Siempre se carga a la persona que atendió esa cuenta.
import { api } from './api.js';
import { h, hoja, aviso, accion, estrellas } from './ui.js';

const clave = (cuentaId) => `dt_encuesta_${cuentaId}`;
export function encuestaHecha(cuentaId) {
  try { return !!localStorage.getItem(clave(cuentaId)); } catch { return false; }
}
function marcarHecha(cuentaId) {
  try { localStorage.setItem(clave(cuentaId), '1'); } catch { /* sin almacenamiento */ }
}

export function abrirEncuesta({ cuentaId, mesero, mesa, alTerminar }) {
  const hj = hoja('⭐ Encuesta');
  let atencion = 0, comida = 0;
  const comentario = h('input', { type: 'text', placeholder: 'Comentario (opcional)', maxlength: 300 });
  const boton = h('button', { class: 'btn oro grande', type: 'button' }, 'Guardar opinión');
  accion(boton, async () => {
    if (!atencion && !comida && !comentario.value.trim()) { aviso('Toca las estrellas o escribe un comentario.', 'error'); return; }
    try {
      await api.post(`/api/cuentas/${cuentaId}/encuesta`, { atencion: atencion || null, comida: comida || null, comentario: comentario.value });
    } catch (e) { if (e.estado !== 409) throw e; }
    marcarHecha(cuentaId);
    hj.cerrar();
    aviso(`¡Gracias! Quedó guardada para ${mesero}.`);
    alTerminar?.();
  });
  hj.cuerpo.append(
    h('div', { class: 'banner' }, `${mesa} · Atendió: `, h('b', null, mesero)),
    h('div', null, h('div', { class: 'chico suave' }, '¿Cómo estuvo la atención?'), estrellas(0, (v) => { atencion = v; })),
    h('div', null, h('div', { class: 'chico suave' }, '¿Cómo estuvo la comida?'), estrellas(0, (v) => { comida = v; })),
    comentario,
    h('div', { class: 'fila' },
      h('button', { class: 'btn claro crece', type: 'button', onclick: () => hj.cerrar() }, 'Ahora no'),
      boton));
}
