// Mi cuenta: cambiar el PIN y salir.
import { api } from '../api.js';
import { estado } from '../estado.js';
import { h, montar, aviso, accion } from '../ui.js';

export default async function yo(host) {
  const actual = h('input', { type: 'password', inputmode: 'numeric', autocomplete: 'current-password', maxlength: 8 });
  const nuevo = h('input', { type: 'password', inputmode: 'numeric', autocomplete: 'new-password', maxlength: 8 });
  const repetir = h('input', { type: 'password', inputmode: 'numeric', autocomplete: 'new-password', maxlength: 8 });
  const boton = h('button', { class: 'btn oro grande', type: 'button' }, 'Cambiar mi PIN');
  accion(boton, async () => {
    if (nuevo.value !== repetir.value) throw new Error('El PIN nuevo no coincide en las dos casillas.');
    const r = await api.post('/api/auth/cambiar-pin', { actual: actual.value, nuevo: nuevo.value });
    actual.value = nuevo.value = repetir.value = '';
    aviso(r.mensaje, 'ok', r.pendiente ? 8000 : 3200);
  });
  montar(host, h('div', { class: 'pagina-estrecha pila' },
    h('h1', null, estado.yo.nombre),
    h('div', { class: 'suave' }, estado.yo.esAdmin ? 'Administrador/a' : estado.yo.puedeCobrar ? 'Cajero/a' : 'Mesero/a'),
    h('div', { class: 'tarjeta pila' },
      h('h2', null, 'Cambiar mi PIN'),
      h('label', { class: 'campo' }, 'PIN actual', actual),
      h('label', { class: 'campo' }, 'PIN nuevo (4 a 8 números)', nuevo),
      h('label', { class: 'campo' }, 'Repite el PIN nuevo', repetir),
      estado.yo.esAdmin ? null : h('div', { class: 'chico suave' }, 'El cambio de PIN lo aprueba un administrador. Mientras tanto sigue valiendo tu PIN actual.'),
      boton),
    h('button', { class: 'btn peligro grande', type: 'button', onclick: () => window.dispatchEvent(new Event('dt:salir')) }, 'Salir')));
}
