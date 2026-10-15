// Pantalla de entrada: se toca el nombre y se escribe el PIN en un teclado grande.
import { api } from '../api.js';
import { recordar } from '../estado.js';
import { h, montar, iniciales, aviso } from '../ui.js';

export default async function acceso(app, alEntrar) {
  montar(app, h('div', { class: 'acceso' }, h('div', { class: 'cargando', style: 'color:#cfc8b6' }, 'Cargando…')));
  let personal;
  try { personal = await api.get('/api/personal'); } catch (e) {
    montar(app, h('div', { class: 'acceso' },
      h('img', { class: 'logo', src: '/img/logo.png', alt: '' }),
      h('h1', null, 'Sin conexión'),
      h('p', null, e.message),
      h('button', { class: 'btn oro', onclick: () => location.reload() }, 'Reintentar')));
    return;
  }
  const demo = await api.get('/api/demo').catch(() => ({ activo: false }));
  const ultimo = recordar.leer('dt_ultimo_usuario');
  if (ultimo) personal.sort((a, b) => (b.id === ultimo) - (a.id === ultimo));

  const lista = () => {
    montar(app, h('div', { class: 'acceso' },
      h('img', { class: 'logo', src: '/img/logo.png', alt: '' }),
      h('h1', null, 'Dulce Tentación'),
      h('p', null, '¿Quién eres? Toca tu nombre'),
      demo.activo ? h('div', { class: 'banner', style: 'max-width:760px;margin-bottom:16px;color:var(--carbon)' }, '🎓 Versión de demostración con datos de ejemplo. El PIN de todos es ', h('b', null, demo.pin), '.') : null,
      h('div', { class: 'nombres' }, personal.map((u) =>
        h('button', { class: 'nombre-btn', type: 'button', onclick: () => teclado(u) },
          h('span', { class: 'avatar' }, iniciales(u.nombre)),
          h('span', null, u.nombre),
          h('small', null, u.rol === 'administrador' ? 'Administración' : u.rol === 'cajero' ? 'Caja' : 'Mesera/o'))))));
  };

  const teclado = (u) => {
    let pin = '';
    const puntos = h('div', { class: 'puntos' });
    const error = h('div', { class: 'error-campo', style: 'color:#ff9aa2;min-height:24px;margin-top:8px;text-align:center' });
    const pintar = () => montar(puntos, Array.from({ length: Math.max(4, pin.length) }, (_, i) => h('i', { class: i < pin.length ? 'lleno' : '' })));
    const entrar = async (btn) => {
      if (pin.length < 4) { error.textContent = 'El PIN tiene mínimo 4 números.'; return; }
      btn.disabled = true; error.textContent = '';
      try {
        const perfil = await api.post('/api/auth/entrar', { usuarioId: u.id, pin });
        alEntrar(perfil);
      } catch (e) {
        pin = ''; pintar();
        error.textContent = e.message;
        btn.disabled = false;
      }
    };
    const boton = h('button', { class: 'btn oro grande', type: 'button', style: 'max-width:340px;margin-top:14px' }, 'Entrar');
    boton.addEventListener('click', () => entrar(boton));
    const tecla = (n) => h('button', {
      class: 'tecla', type: 'button',
      onclick: () => { if (pin.length < 8) { pin += n; pintar(); error.textContent = ''; } },
    }, n);
    const alTeclado = (e) => {
      if (/^\d$/.test(e.key) && pin.length < 8) { pin += e.key; pintar(); }
      else if (e.key === 'Backspace') { pin = pin.slice(0, -1); pintar(); }
      else if (e.key === 'Enter') entrar(boton);
    };
    document.addEventListener('keydown', alTeclado);
    const volver = () => { document.removeEventListener('keydown', alTeclado); lista(); };
    pintar();
    montar(app, h('div', { class: 'acceso' },
      h('span', { class: 'nombre-btn', style: 'min-height:0;border:0;background:none' }, h('span', { class: 'avatar' }, iniciales(u.nombre))),
      h('h1', null, u.nombre),
      h('p', null, 'Escribe tu PIN'),
      puntos, error,
      h('div', { class: 'teclado' },
        ['1', '2', '3', '4', '5', '6', '7', '8', '9'].map(tecla),
        h('button', { class: 'tecla', type: 'button', onclick: volver, 'aria-label': 'Volver', style: 'font-size:1.1rem' }, '← Atrás'),
        tecla('0'),
        h('button', { class: 'tecla', type: 'button', 'aria-label': 'Borrar', onclick: () => { pin = pin.slice(0, -1); pintar(); } }, '⌫')),
      boton));
    // al entrar se retira el oyente del teclado físico
    const observador = new MutationObserver(() => {
      if (!document.body.contains(puntos)) { document.removeEventListener('keydown', alTeclado); observador.disconnect(); }
    });
    observador.observe(app, { childList: true, subtree: true });
  };

  lista();
  if (!personal.length) aviso('No hay personal registrado.', 'error');
}
