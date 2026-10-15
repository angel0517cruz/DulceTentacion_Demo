# Pruebas de la API de Dulce Tentación. Uso: .\Probar-Api.ps1 -Url http://127.0.0.1:8099 -Pins ..\src\DulceTentacion\datos\PINS_INICIALES.txt
param([string]$Url = "http://127.0.0.1:8099", [string]$Pins = "..\src\DulceTentacion\datos\PINS_INICIALES.txt")
$ErrorActionPreference = "Stop"
$ok = 0; $mal = 0
function Check($nombre, $cond) { if ($cond) { $script:ok++; Write-Host "  OK   $nombre" -ForegroundColor Green } else { $script:mal++; Write-Host "  FALLA $nombre" -ForegroundColor Red } }

function Pin($nombre) { (Get-Content $Pins -Encoding UTF8 | Where-Object { $_ -like "$nombre*" } | Select-Object -First 1) -replace '.*PIN:\s*', '' | ForEach-Object { $_.Trim() } }
function Sesion($id, $nombre) {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Invoke-RestMethod "$Url/api/auth/entrar" -Method Post -WebSession $s -ContentType "application/json" -Body (@{ usuarioId = $id; pin = (Pin $nombre) } | ConvertTo-Json) | Out-Null
    $s
}
function Api($s, $metodo, $ruta, $cuerpo = $null) {
    $p = @{ Uri = "$Url$ruta"; Method = $metodo; WebSession = $s; ContentType = "application/json" }
    if ($null -ne $cuerpo) { $p.Body = ($cuerpo | ConvertTo-Json -Depth 8) }
    Invoke-RestMethod @p
}
function Falla($s, $metodo, $ruta, $cuerpo = $null) {   # devuelve el código HTTP de un error esperado
    try { Api $s $metodo $ruta $cuerpo | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
}

Write-Host "`n== Acceso"
$sinSesion = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Check "menú sin sesión da 401" ((Falla $sinSesion Get "/api/menu") -eq 401)
$mal1 = try { Invoke-RestMethod "$Url/api/auth/entrar" -Method Post -ContentType "application/json" -Body '{"usuarioId":4,"pin":"9999"}' | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
Check "PIN incorrecto da 401" ($mal1 -eq 401)
$admin = Sesion 1 "Norberto"
$mesera = Sesion 4 "Liliana"
$mesera2 = Sesion 5 "Mariana"
Check "mesera no puede ver estadísticas (403)" ((Falla $mesera Get "/api/estadisticas") -eq 403)
Check "mesera no puede cobrar (403)" ((Falla $mesera Post "/api/cuentas/1/cobrar" @{ modo = "todo"; metodo = "efectivo" }) -eq 403)
Check "mesera no entra a caja (403)" ((Falla $mesera Get "/api/caja/hoy") -eq 403)

Write-Host "`n== Menú"
$menu = Api $mesera Get "/api/menu"
Check "11 categorías" ($menu.categorias.Count -eq 11)
Check "más de 150 productos" ($menu.productos.Count -gt 150)
$pan = $menu.productos | Where-Object { $_.nombre -like "Pan rollo*" } | Select-Object -First 1
Check "pan rollo con variantes" ($pan.variantes.Count -ge 20)
$desayuno = $menu.productos | Where-Object { $_.nombre -eq "Desayuno completo de costilla" } | Select-Object -First 1
Check "desayuno con grupos de opciones" ($null -ne $desayuno)
$simple = $menu.productos | Where-Object { $_.variantes.Count -eq 0 -and $_.grupos.Count -eq 0 } | Select-Object -First 2

Write-Host "`n== Mesas y pedido"
$mesas = Api $mesera Get "/api/mesas"
Check "12 mesas libres" ($mesas.mesas.Count -eq 12 -and ($mesas.mesas | Where-Object estado -ne "libre").Count -eq 0)
$uidMesa = [guid]::NewGuid().ToString()
$c = Api $mesera Post "/api/mesas/1/abrir" @{ uid = $uidMesa; personas = 3 }
$cuentaId = $c.cuenta.id
Check "cuenta con 3 personas" ($c.comensales.Count -eq 3)
$c2 = Api $mesera2 Post "/api/mesas/1/abrir" @{ personas = 5 }
Check "otra mesera recibe la misma cuenta" ($c2.cuenta.id -eq $cuentaId)

$uidPedido = [guid]::NewGuid().ToString()
$pedido = @{ uid = $uidPedido; lineas = @(
    @{ productoId = $simple[0].id; cantidad = 2; persona = 1 },
    @{ productoId = $simple[1].id; cantidad = 1; persona = 2 },
    @{ productoId = $simple[1].id; cantidad = 1; persona = 3 },
    @{ productoId = $simple[0].id; cantidad = 1; persona = 0 }) }
$c = Api $mesera Post "/api/cuentas/$cuentaId/pedido" $pedido
$esperado = 3 * $simple[0].precio + 2 * $simple[1].precio
Check "total correcto" ($c.totales.total -eq $esperado)
$c = Api $mesera Post "/api/cuentas/$cuentaId/pedido" $pedido
Check "reintento con el mismo uid no duplica" ($c.totales.total -eq $esperado -and $c.lineas.Count -eq 4)
Check "pedido sin elegir presentación falla (400)" ((Falla $mesera Post "/api/cuentas/$cuentaId/pedido" @{ lineas = @(@{ productoId = $pan.id; cantidad = 1; persona = 1 }) }) -eq 400)
$sinOpc = Falla $mesera Post "/api/cuentas/$cuentaId/pedido" @{ lineas = @(@{ productoId = $desayuno.id; cantidad = 1; persona = 1 }) }
Check "desayuno sin elegir opciones falla (400)" ($sinOpc -eq 400)

# Desayuno completo: con/sin arroz y recargo de huevos rancheros (+2.000)
function Op($prod, $grupo, $nombre) { (($prod.grupos | Where-Object nombre -eq $grupo).opciones | Where-Object nombre -eq $nombre).id }
Check "desayuno tiene grupo Arroz (con/sin)" (@($desayuno.grupos | Where-Object nombre -eq "Arroz").Count -eq 1)
$ops1 = @((Op $desayuno "Huevos" "Pericos"), (Op $desayuno "Arroz" "Sin arroz"), (Op $desayuno "Bebida" "Tinto"))
$ops2 = @((Op $desayuno "Huevos" "Rancheros"), (Op $desayuno "Arroz" "Con arroz"), (Op $desayuno "Bebida" "Tinto"))
$antes = (Api $mesera Get "/api/cuentas/$cuentaId").totales.total
$c = Api $mesera Post "/api/cuentas/$cuentaId/pedido" @{ lineas = @(@{ productoId = $desayuno.id; opciones = $ops1; cantidad = 1; persona = 0 }) }
Check "desayuno sin arroz cuesta el precio base" ($c.totales.total -eq $antes + $desayuno.precio)
$c = Api $mesera Post "/api/cuentas/$cuentaId/pedido" @{ lineas = @(@{ productoId = $desayuno.id; opciones = $ops2; cantidad = 1; persona = 0 }) }
Check "huevos rancheros suman +2.000" ($c.totales.total -eq $antes + 2 * $desayuno.precio + 2000)
Check "el detalle muestra el recargo" ((@($c.lineas | Where-Object { $_.detalle -like "*Rancheros (+*" })).Count -eq 1)
$v = $pan.variantes[2]
$antesVar = (Api $mesera Get "/api/cuentas/$cuentaId").totales.total
$c = Api $mesera Post "/api/cuentas/$cuentaId/pedido" @{ lineas = @(@{ productoId = $pan.id; varianteId = $v.id; cantidad = 1; persona = 1 }) }
Check "variante usa su propio precio" ($c.totales.total -eq $antesVar + $v.precio)
$total = $c.totales.total

Write-Host "`n== Cobro compartido"
$caja = Sesion 2 "Lina"
$c = Api $mesera Post "/api/cuentas/$cuentaId/pedir-cuenta"
Check "queda por cobrar" ($c.cuenta.porCobrar -eq 1)
$p1 = ($c.comensales | Where-Object numero -eq 1).pendiente
$r = Api $caja Post "/api/cuentas/$cuentaId/cobrar" @{ uid = [guid]::NewGuid().ToString(); modo = "personas"; personas = @(1); metodo = "efectivo"; recibido = ($p1 + 5000) }
Check "persona 1 pagada: cambio correcto" (($r.detalle.pagos | Select-Object -Last 1).cambio -eq 5000)
Check "falta = total - persona 1" ($r.detalle.totales.falta -eq $total - $p1)
Check "mesa sigue abierta" ($r.detalle.cuenta.estado -eq "abierta")
$uidPago = [guid]::NewGuid().ToString()
$p2 = ($r.detalle.comensales | Where-Object numero -eq 2).pendiente
$r = Api $caja Post "/api/cuentas/$cuentaId/cobrar" @{ uid = $uidPago; modo = "personas"; personas = @(2); metodo = "transferencia" }
$r = Api $caja Post "/api/cuentas/$cuentaId/cobrar" @{ uid = $uidPago; modo = "personas"; personas = @(2); metodo = "transferencia" }
Check "reintento del mismo cobro no cobra dos veces" ($r.detalle.totales.pagado -eq $p1 + $p2)
Check "cobrar de más falla (409)" ((Falla $caja Post "/api/cuentas/$cuentaId/cobrar" @{ modo = "monto"; monto = $total * 10; metodo = "efectivo" }) -eq 409)
Check "efectivo menor al cobro falla (400)" ((Falla $caja Post "/api/cuentas/$cuentaId/cobrar" @{ modo = "monto"; monto = 1000; metodo = "efectivo"; recibido = 500 }) -eq 400)
$r = Api $caja Post "/api/cuentas/$cuentaId/cobrar" @{ modo = "monto"; monto = 1000; metodo = "efectivo"; etiqueta = "Abono" }
$falta = $r.detalle.totales.falta
$r = Api $caja Post "/api/cuentas/$cuentaId/cobrar" @{ modo = "todo"; metodo = "tarjeta" }
Check "cobro final cierra la cuenta" ($r.detalle.cuenta.estado -eq "pagada" -and $r.detalle.totales.falta -eq 0)
Check "pagado = total" ($r.detalle.totales.pagado -eq $total)
$mesas = Api $mesera Get "/api/mesas"
Check "mesa 1 queda libre" (($mesas.mesas | Where-Object id -eq 1).estado -eq "libre")
Check "no se puede pedir en cuenta pagada (409)" ((Falla $mesera Post "/api/cuentas/$cuentaId/pedido" @{ lineas = @(@{ productoId = $simple[0].id; cantidad = 1; persona = 1 }) }) -eq 409)

Write-Host "`n== Encuesta"
$e = Api $mesera Post "/api/cuentas/$cuentaId/encuesta" @{ atencion = 5; comida = 4; comentario = "Todo muy rico" }
Check "encuesta guardada" ($null -ne $e.mensaje)
Check "segunda encuesta rechazada (409)" ((Falla $mesera Post "/api/cuentas/$cuentaId/encuesta" @{ atencion = 3 }) -eq 409)

Write-Host "`n== Encuesta por mesera"
$cp = Api $mesera2 Post "/api/mesas/2/abrir" @{ personas = 1 }
Api $mesera2 Post "/api/cuentas/$($cp.cuenta.id)/pedido" @{ lineas = @(@{ productoId = $simple[0].id; cantidad = 1; persona = 0 }) } | Out-Null
Api $caja Post "/api/cuentas/$($cp.cuenta.id)/cobrar" @{ modo = "todo"; metodo = "efectivo" } | Out-Null
Api $caja Post "/api/cuentas/$($cp.cuenta.id)/encuesta" @{ atencion = 3; comida = 4 } | Out-Null
$est = Api $admin Get "/api/estadisticas"
$m5 = $est.encuestas.porMesera | Where-Object id -eq 5
$m4 = $est.encuestas.porMesera | Where-Object id -eq 4
Check "encuesta se carga a quien atendió (Mariana)" ($m5.total -eq 1 -and $m5.atencion -eq 3)
Check "la otra mesera (Liliana) conserva la suya" ($m4.total -eq 1 -and $m4.atencion -eq 5)

Write-Host "`n== Anulaciones"
Check "mesera no anula cobro (403)" ((Falla $mesera Post "/api/pagos/1/anular" @{ motivo = "x" }) -eq 403)
$u = Api $admin Post "/api/pagos/$($r.pagoId)/anular" @{ motivo = "Prueba" }
Check "admin anula el último cobro y la cuenta se reabre" ($u.cuenta.estado -eq "abierta" -and $u.totales.falta -gt 0)
$r = Api $caja Post "/api/cuentas/$cuentaId/cobrar" @{ modo = "todo"; metodo = "efectivo" }
Check "se vuelve a cobrar" ($r.detalle.cuenta.estado -eq "pagada")

Write-Host "`n== Para llevar y anular cuenta"
$l = Api $mesera Post "/api/llevar" @{ etiqueta = "Sra. Marta" }
$ln = Api $mesera Post "/api/cuentas/$($l.cuenta.id)/pedido" @{ lineas = @(@{ productoId = $simple[0].id; cantidad = 1; persona = 1 }) }
Check "pedido para llevar" ($ln.cuenta.mesaId -eq $null -and $ln.totales.total -eq $simple[0].precio)
$lineaId = $ln.lineas[0].id
$an = Api $mesera Post "/api/lineas/$lineaId/anular" @{ motivo = "Se equivocó" }
Check "línea anulada no suma" ($an.totales.total -eq 0)
Check "mesera no anula cuenta (403)" ((Falla $mesera Post "/api/cuentas/$($l.cuenta.id)/anular" @{ motivo = "x" }) -eq 403)
$an = Api $admin Post "/api/cuentas/$($l.cuenta.id)/anular" @{ motivo = "Cliente se fue" }
Check "admin anula cuenta" ($an.cuenta.estado -eq "anulada")

Write-Host "`n== Caja"
Check "mesera no registra gastos (403)" ((Falla $mesera Post "/api/caja/gastos" @{ concepto = "x"; monto = 1 }) -eq 403)
Api $caja Post "/api/caja/gastos" @{ concepto = "Hielo"; monto = 12000 } | Out-Null
Api $caja Post "/api/caja/gastos" @{ concepto = "Servilletas"; monto = 8000 } | Out-Null
$hoy = Api $caja Get "/api/caja/hoy"
Check "2 gastos pendientes" ($hoy.gastos.Count -eq 2)
$cierre = Api $caja Post "/api/caja/cierre" @{ baseInicial = 100000; efectivoContado = 500000; otrosReportados = 50000; desglose = @{ b50000 = 10 }; observaciones = "Prueba" }
Check "ingreso = efectivo + otros + gastos - base" ($cierre.cierre.ingreso -eq 500000 + 50000 + 20000 - 100000)
Check "gastos quedan en el cierre" ($cierre.gastos.Count -eq 2)
$hoy = Api $caja Get "/api/caja/hoy"
Check "sin gastos pendientes" ($hoy.gastos.Count -eq 0)

Write-Host "`n== Administración"
$nuevo = Api $admin Post "/api/admin/productos" @{ categoriaId = $menu.categorias[0].id; nombre = "Producto de prueba"; precio = 9900 }
$m2 = Api $mesera Get "/api/menu"
Check "producto nuevo aparece en el menú" (@($m2.productos | Where-Object nombre -eq "Producto de prueba").Count -eq 1)
Api $admin Put "/api/admin/productos/$($nuevo.id)/precio" @{ precio = 12000 } | Out-Null
$m2 = Api $mesera Get "/api/menu"
Check "precio actualizado" ((($m2.productos | Where-Object nombre -eq "Producto de prueba").precio) -eq 12000)
Check "mesera no edita precios (403)" ((Falla $mesera Put "/api/admin/productos/$($nuevo.id)/precio" @{ precio = 1 }) -eq 403)
$vista = Api $admin Post "/api/admin/precios/vista" @{ porcentaje = 10; redondeo = 100 }
Check "vista previa de ajuste" ($vista.total -gt 100)
Api $admin Delete "/api/admin/productos/$($nuevo.id)" | Out-Null
$u = Api $admin Post "/api/admin/usuarios" @{ nombre = "Prueba Mesera"; rol = "mesero"; pin = "4321" }
Check "usuario creado" ($u.id -gt 9)
Check "PIN inválido rechazado (400)" ((Falla $admin Post "/api/admin/usuarios" @{ nombre = "Otra"; rol = "mesero"; pin = "12" }) -eq 400)

Write-Host "`n== Cambio de PIN con aprobación"
$ale = Sesion 9 "Alejandra"
$r = Api $ale Post "/api/auth/cambiar-pin" @{ actual = "0000"; nuevo = "7777" }
Check "la mesera queda con solicitud pendiente" ($r.pendiente -eq $true)
$ok0000 = try { Invoke-RestMethod "$Url/api/auth/entrar" -Method Post -ContentType "application/json" -Body '{"usuarioId":9,"pin":"0000"}' | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
Check "mientras no se aprueba sigue valiendo el PIN anterior" ($ok0000 -eq 200)
Check "la mesera no ve ni aprueba solicitudes (403)" ((Falla $ale Get "/api/admin/solicitudes-pin") -eq 403)
$pend = @(Api $admin Get "/api/admin/solicitudes-pin")
Check "el administrador ve la solicitud de Alejandra" ($pend.Count -eq 1 -and $pend[0].nombre -eq "Alejandra")
Api $admin Post "/api/admin/solicitudes-pin/$($pend[0].id)/aprobar" | Out-Null
$ok7777 = try { Invoke-RestMethod "$Url/api/auth/entrar" -Method Post -ContentType "application/json" -Body '{"usuarioId":9,"pin":"7777"}' | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
$viejo = try { Invoke-RestMethod "$Url/api/auth/entrar" -Method Post -ContentType "application/json" -Body '{"usuarioId":9,"pin":"0000"}' | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
Check "aprobado: entra con el PIN nuevo" ($ok7777 -eq 200)
Check "aprobado: el PIN viejo ya no sirve" ($viejo -eq 401)
$r2 = Api $ale Post "/api/auth/cambiar-pin" @{ actual = "7777"; nuevo = "0000" }
$pend = @(Api $admin Get "/api/admin/solicitudes-pin")
Api $admin Post "/api/admin/solicitudes-pin/$($pend[0].id)/rechazar" | Out-Null
$sigue = try { Invoke-RestMethod "$Url/api/auth/entrar" -Method Post -ContentType "application/json" -Body '{"usuarioId":9,"pin":"7777"}' | Out-Null; 200 } catch { [int]$_.Exception.Response.StatusCode }
Check "rechazado: el PIN no cambia" ($sigue -eq 200)

Write-Host "`n== Estadísticas"
$est = Api $admin Get "/api/estadisticas"
Check "hay productos más pedidos" ($est.productos.Count -gt 0)
Check "caja usa solo el cierre" ($est.caja.ingreso -eq 500000 + 50000 + 20000 - 100000)
Check "encuesta promedio" ($est.encuestas.total -ge 2)

Write-Host "`nResultado: $ok bien, $mal con falla"
if ($mal -gt 0) { exit 1 }
