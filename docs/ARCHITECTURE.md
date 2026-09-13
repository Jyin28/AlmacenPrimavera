# Arquitectura y reglas de dominio

## Componentes

- `Program.cs`: tubería HTTP, sesiones, permisos y endpoints.
- `Services/StoreService.cs`: transacciones y reglas de producto, inventario, venta, precio, compra e importación.
- `Data/StoreDb.cs`: conexión SQLite, migración idempotente, restricciones, índices y datos iniciales.
- `wwwroot/`: SPA accesible y adaptable, escrita sin dependencias de frontend ni CDN.
- `tests/integration.ps1`: pruebas HTTP sobre base aislada.

## Integridad

La clave técnica `products.id` y el identificador legible `products.internal_id` son columnas separadas. `internal_id` es único e inmutable en los flujos normales. La secuencia es transaccional por categoría. Los códigos activos tienen un índice único parcial, por lo que una carrera concurrente tampoco puede crear duplicados.

Cada cambio de stock inserta `stock_movements` y sincroniza `inventory_balances` dentro de la misma transacción. `version` permite añadir control optimista cuando se conecten clientes offline. El stock negativo se bloquea antes de vender/trasladar y también mediante actualizaciones condicionales.

Precios y costos se conservan en tablas históricas. Una recomendación guarda entradas, comparables, explicación, precio anterior, decisión, responsable y fecha. La fórmula de recargo divide entre costo; la de margen divide entre precio.

Los eventos de auditoría solo se insertan. Triggers de SQLite rechazan actualizaciones y eliminaciones incluso si un error futuro intentara modificar el historial.

## Roles

- **Administrador:** todo, incluidos conflictos de código, configuración, margen y auditoría.
- **Gerente:** operación completa, aprobaciones y reportes financieros.
- **Inventario:** catálogo, importación, ajustes, recepción y reportes básicos.
- **Consulta de costos:** catálogo, recomendaciones y reportes de costo/margen.
- **Cajero/consulta:** consulta, reportes básicos y registro de ventas.

Los botones se ocultan por comodidad, pero la seguridad real se aplica de nuevo en cada endpoint.

## Evolución recomendada

Para una segunda etapa: administración visual de usuarios/roles, reglas y plantillas; lotes/series/vencimientos; conteos guiados; promociones; etiquetas PDF; sugerencias de compra por velocidad; importación XLSX; y métricas de venta/rotación. El acceso SQL está concentrado para facilitar PostgreSQL o SQL Server cuando la concurrencia supere el perfil de SQLite.
