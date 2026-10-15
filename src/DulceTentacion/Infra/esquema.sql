-- Dulce Tentación · esquema de la base de datos (SQLite).
-- Se puede ejecutar varias veces: solo crea lo que no existe.
-- Dinero: enteros en pesos colombianos. Fechas: texto 'AAAA-MM-DD HH:MM:SS' en hora local del local.

CREATE TABLE IF NOT EXISTS usuario (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    nombre             TEXT    NOT NULL,
    rol                TEXT    NOT NULL CHECK (rol IN ('administrador', 'cajero', 'mesero')),
    pin_hash           TEXT    NOT NULL,
    activo             INTEGER NOT NULL DEFAULT 1 CHECK (activo IN (0, 1)),
    orden              INTEGER NOT NULL DEFAULT 0,
    intentos_fallidos  INTEGER NOT NULL DEFAULT 0,
    bloqueado_hasta    TEXT,
    creado             TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);

CREATE TABLE IF NOT EXISTS categoria (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    nombre  TEXT    NOT NULL,
    icono   TEXT    NOT NULL DEFAULT '🍽️',
    orden   INTEGER NOT NULL DEFAULT 0,
    activa  INTEGER NOT NULL DEFAULT 1 CHECK (activa IN (0, 1))
);

CREATE TABLE IF NOT EXISTS producto (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    categoria_id  INTEGER NOT NULL REFERENCES categoria (id),
    seccion       TEXT,                                   -- subgrupo dentro de la categoría (Caldos, Huevos, Gaseosas…)
    nombre        TEXT    NOT NULL,
    precio        INTEGER NOT NULL CHECK (precio >= 0),   -- precio base; si tiene variantes, es el menor
    activo        INTEGER NOT NULL DEFAULT 1 CHECK (activo IN (0, 1)),
    orden         INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_producto_categoria ON producto (categoria_id, orden);

-- Tamaños o presentaciones con precio propio (Sin arroz / Con arroz, 350 ml / 1,5 L…)
CREATE TABLE IF NOT EXISTS variante (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    producto_id  INTEGER NOT NULL REFERENCES producto (id) ON DELETE CASCADE,
    nombre       TEXT    NOT NULL,
    precio       INTEGER NOT NULL CHECK (precio >= 0),
    orden        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_variante_producto ON variante (producto_id, orden);

-- Elecciones sin costo (tipo de huevo, bebida del desayuno, carne del sándwich…)
CREATE TABLE IF NOT EXISTS grupo_opcion (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    producto_id  INTEGER NOT NULL REFERENCES producto (id) ON DELETE CASCADE,
    nombre       TEXT    NOT NULL,
    min_sel      INTEGER NOT NULL DEFAULT 1,
    max_sel      INTEGER NOT NULL DEFAULT 1,
    orden        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_grupo_producto ON grupo_opcion (producto_id, orden);

CREATE TABLE IF NOT EXISTS opcion (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    grupo_id  INTEGER NOT NULL REFERENCES grupo_opcion (id) ON DELETE CASCADE,
    nombre    TEXT    NOT NULL,
    precio_extra INTEGER NOT NULL DEFAULT 0 CHECK (precio_extra >= 0),   -- recargo (ej: huevos rancheros +2.000)
    orden     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_opcion_grupo ON opcion (grupo_id, orden);

CREATE TABLE IF NOT EXISTS mesa (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    nombre  TEXT    NOT NULL,
    orden   INTEGER NOT NULL DEFAULT 0,
    activa  INTEGER NOT NULL DEFAULT 1 CHECK (activa IN (0, 1))
);

-- Una cuenta es todo lo que consume una mesa (o un cliente "para llevar") hasta que se paga.
CREATE TABLE IF NOT EXISTS cuenta (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    uid          TEXT    UNIQUE,                       -- identificador que genera el celular (evita duplicados al reintentar)
    mesa_id      INTEGER REFERENCES mesa (id),         -- NULL = para llevar
    etiqueta     TEXT,                                 -- nombre del cliente en pedidos para llevar
    mesero_id    INTEGER NOT NULL REFERENCES usuario (id),
    estado       TEXT    NOT NULL DEFAULT 'abierta' CHECK (estado IN ('abierta', 'pagada', 'anulada')),
    por_cobrar   INTEGER NOT NULL DEFAULT 0,           -- la mesa pidió la cuenta
    abierta_en   TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    cerrada_en   TEXT,
    motivo_anulacion TEXT,
    anulada_por  INTEGER REFERENCES usuario (id)
);
CREATE INDEX IF NOT EXISTS ix_cuenta_estado ON cuenta (estado, mesa_id);
CREATE INDEX IF NOT EXISTS ix_cuenta_fecha ON cuenta (abierta_en);

-- Las personas de la mesa: así cada consumo se asigna a quien lo pidió.
CREATE TABLE IF NOT EXISTS comensal (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    cuenta_id  INTEGER NOT NULL REFERENCES cuenta (id) ON DELETE CASCADE,
    numero     INTEGER NOT NULL,                       -- 1, 2, 3… dentro de la cuenta
    nombre     TEXT,
    UNIQUE (cuenta_id, numero)
);

-- Cada envío de pedido (ronda) al local.
CREATE TABLE IF NOT EXISTS pedido (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    uid        TEXT    UNIQUE,
    cuenta_id  INTEGER NOT NULL REFERENCES cuenta (id) ON DELETE CASCADE,
    mesero_id  INTEGER NOT NULL REFERENCES usuario (id),
    nota       TEXT,
    creado_en  TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);

CREATE TABLE IF NOT EXISTS pago (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    uid         TEXT    UNIQUE,
    cuenta_id   INTEGER NOT NULL REFERENCES cuenta (id) ON DELETE CASCADE,
    monto       INTEGER NOT NULL CHECK (monto > 0),
    metodo      TEXT    NOT NULL CHECK (metodo IN ('efectivo', 'transferencia', 'tarjeta')),
    recibido    INTEGER,
    cambio      INTEGER,
    etiqueta    TEXT,                                   -- "Persona 2", "Parte 1 de 4"…
    cajero_id   INTEGER NOT NULL REFERENCES usuario (id),
    creado_en   TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    anulado     INTEGER NOT NULL DEFAULT 0 CHECK (anulado IN (0, 1)),
    anulado_por INTEGER REFERENCES usuario (id),
    motivo_anulacion TEXT
);
CREATE INDEX IF NOT EXISTS ix_pago_cuenta ON pago (cuenta_id);

CREATE TABLE IF NOT EXISTS linea (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    cuenta_id    INTEGER NOT NULL REFERENCES cuenta (id) ON DELETE CASCADE,
    pedido_id    INTEGER NOT NULL REFERENCES pedido (id) ON DELETE CASCADE,
    producto_id  INTEGER REFERENCES producto (id),
    nombre       TEXT    NOT NULL,                      -- copia del nombre al momento del pedido
    detalle      TEXT,                                  -- "Con arroz · Chocolate · Fritos"
    precio       INTEGER NOT NULL CHECK (precio >= 0),  -- copia del precio al momento del pedido
    cantidad     INTEGER NOT NULL CHECK (cantidad > 0),
    comensal_id  INTEGER REFERENCES comensal (id),
    nota         TEXT,
    pago_id      INTEGER REFERENCES pago (id),          -- si ya la cubre un pago por productos o personas
    anulada      INTEGER NOT NULL DEFAULT 0 CHECK (anulada IN (0, 1)),
    anulada_por  INTEGER REFERENCES usuario (id),
    motivo       TEXT,
    creada_en    TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE INDEX IF NOT EXISTS ix_linea_cuenta ON linea (cuenta_id);
CREATE INDEX IF NOT EXISTS ix_linea_producto ON linea (producto_id);

-- Cierre de caja: lo que cuenta quien cierra y los gastos del día. No se calcula desde los pedidos.
CREATE TABLE IF NOT EXISTS cierre_caja (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    fecha            TEXT    NOT NULL,                  -- AAAA-MM-DD
    usuario_id       INTEGER NOT NULL REFERENCES usuario (id),
    base_inicial     INTEGER NOT NULL DEFAULT 0,
    efectivo_contado INTEGER NOT NULL DEFAULT 0,
    otros_reportados INTEGER NOT NULL DEFAULT 0,        -- transferencias u otros que quien cierra reporta
    desglose         TEXT,                              -- JSON con el conteo por billetes y monedas
    observaciones    TEXT,
    creado_en        TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE INDEX IF NOT EXISTS ix_cierre_fecha ON cierre_caja (fecha);

CREATE TABLE IF NOT EXISTS gasto (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    fecha       TEXT    NOT NULL,
    usuario_id  INTEGER NOT NULL REFERENCES usuario (id),
    concepto    TEXT    NOT NULL,
    monto       INTEGER NOT NULL CHECK (monto > 0),
    cierre_id   INTEGER REFERENCES cierre_caja (id),    -- NULL = todavía sin cierre
    creado_en   TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE INDEX IF NOT EXISTS ix_gasto_fecha ON gasto (fecha);

CREATE TABLE IF NOT EXISTS encuesta (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    cuenta_id    INTEGER REFERENCES cuenta (id),
    mesero_id    INTEGER REFERENCES usuario (id),
    atencion     INTEGER CHECK (atencion BETWEEN 1 AND 5),
    comida       INTEGER CHECK (comida BETWEEN 1 AND 5),
    comentario   TEXT,
    creada_en    TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);

CREATE TABLE IF NOT EXISTS auditoria (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    usuario_id  INTEGER REFERENCES usuario (id),
    accion      TEXT    NOT NULL,
    detalle     TEXT,
    creada_en   TEXT    NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE INDEX IF NOT EXISTS ix_auditoria_fecha ON auditoria (creada_en);

CREATE TABLE IF NOT EXISTS meta (
    clave  TEXT PRIMARY KEY,
    valor  TEXT
);

-- Cambios de PIN que pide una mesera: no se aplican hasta que un administrador los aprueba.
CREATE TABLE IF NOT EXISTS solicitud_pin (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    usuario_id   INTEGER NOT NULL REFERENCES usuario (id),
    pin_hash     TEXT    NOT NULL,
    estado       TEXT    NOT NULL DEFAULT 'pendiente' CHECK (estado IN ('pendiente', 'aprobada', 'rechazada')),
    creada_en    TEXT    NOT NULL DEFAULT (datetime('now', 'localtime')),
    resuelta_por INTEGER REFERENCES usuario (id),
    resuelta_en  TEXT
);
CREATE INDEX IF NOT EXISTS ix_solicitud_pin_estado ON solicitud_pin (estado);
