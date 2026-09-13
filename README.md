# Almacén Primavera

Sistema web de administración para una tienda minorista. Reúne catálogo, códigos de barras, precios, inventario por ubicación, actualización CSV y reportes en una aplicación ASP.NET Core + SQLite sin servicios externos obligatorios.

## Inicio rápido

Requisitos: .NET SDK 8 o posterior.

```powershell
$env:DEFAULT_ADMIN_PASSWORD = 'Una-clave-larga-y-unica'
dotnet restore --configfile .\NuGet.Config
dotnet run
```

Abra la dirección que muestra la consola (por defecto `http://localhost:5000`). En una instalación nueva el usuario inicial es `admin`. Si no se define `DEFAULT_ADMIN_PASSWORD`, la clave de desarrollo es `Primavera-Admin!`; cámbiela antes de exponer el servicio en una red.

Para usar el puerto documentado en el ejemplo:

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:5074'
dotnet run
```

La base se crea en `data/store.db`. Puede definir `STORE_DB_PATH` para ubicarla en otro directorio. El arranque crea esquema, índices, restricciones, configuración, catálogos y datos de demostración de forma idempotente.

## Qué incluye

- ID interno estable e independiente del código de barras, automático o manual, con formato inicial `DEPARTAMENTO-CATEGORIA-000001`.
- Departamentos, categorías anidadas, familias/variantes, etiquetas, atributos configurables, unidades, proveedores y ubicaciones normalizados.
- Códigos UPC, EAN, ISBN, GTIN y de proveedor; índice único para códigos activos y resolución administrativa de conflictos.
- Inventario por ubicación con saldo protegido por versión y libro mayor permanente para aperturas, ventas, recepciones, devoluciones, daños, pérdidas, traslados, conteos y correcciones.
- Transferencias atómicas que no cambian la existencia total de la empresa y bloqueo de inventario negativo.
- Recomendación de precios por comparables, costo puesto en tienda, mediana, promedio ponderado, regla de categoría, rango, confianza y terminación `.95`.
- Recargo y margen bruto calculados y presentados como conceptos distintos. Ninguna recomendación cambia el precio hasta una aprobación explícita.
- Interfaz de registro de ventas para integración con un POS, con salida de inventario transaccional y costo histórico por línea.
- Actualización de inventario por CSV con plantilla, previsualización, errores por fila, modo seco y transacción única al aplicar.
- Asistente CSV que detecta archivos con código de barras, nombre, cantidad, precios e impuesto; permite completar clasificación y ubicación y genera el archivo final descargable.
- Búsqueda por ID, código o nombre; filtros de bajo inventario, agotados, inactivos e incompletos.
- Reportes de reposición, valor y rentabilidad; exportación CSV desde el navegador.
- Roles de administrador, gerente, inventario, consulta de costos y cajero, con permisos comprobados en el servidor.
- Sesiones opacas en cookie `HttpOnly`/`SameSite=Strict`, contraseñas PBKDF2, restricciones de base de datos, cabeceras de seguridad y auditoría append-only protegida con triggers.

## Pruebas

```powershell
dotnet build --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\integration.ps1
```

La suite usa una base aislada en `TestResults`, levanta el servidor y valida autenticación, ID interno, edición, escaneo, duplicados, recomendación/aprobación, traslado, ventas, actualización CSV y desactivación auditada.

## Operación y respaldo

- Use HTTPS mediante IIS, nginx, Caddy o el proxy de su plataforma en producción.
- Respalde `data/store.db` con una herramienta consciente de SQLite o deteniendo brevemente la aplicación. También conserve los archivos `-wal` y `-shm` si copia mientras está en ejecución.
- Defina una contraseña inicial segura antes del primer arranque y limite el acceso al archivo de la base de datos.
- La moneda inicial es CRC; los importes usan `decimal` en .NET y `NUMERIC` en SQLite, redondeados a dos decimales con `MidpointRounding.AwayFromZero`.
- Las marcas de tiempo se guardan en UTC ISO-8601 y se muestran en la zona local del navegador.

## Supuestos y límites actuales

Esta primera versión está enfocada en una tienda o pequeño grupo de ubicaciones y en los flujos de catálogo, precio, inventario y compra. El modelo admite familias, variantes, etiquetas y atributos, aunque su administración avanzada todavía requiere endpoints o mantenimiento administrativo adicional. Lotes, series, vencimientos, etiquetas imprimibles, promociones, conteos guiados y tableros de velocidad/rotación están previstos en el modelo operativo, pero no tienen aún una pantalla especializada. Ventas se ofrece como API de integración, no como caja/POS completo. Para alta disponibilidad o muchas cajas concurrentes conviene migrar la capa SQL a PostgreSQL manteniendo los servicios de dominio.

Consulte [la referencia de API](docs/API.md) y [las decisiones técnicas](docs/ARCHITECTURE.md).
