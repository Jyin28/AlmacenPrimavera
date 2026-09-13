# API

Todas las rutas bajo `/api`, excepto `POST /api/auth/login`, exigen la cookie de sesión. Los errores de negocio usan JSON `{ "error": "mensaje" }` y códigos 400, 401, 403, 404 o 409.

| Método | Ruta | Uso / permiso principal |
|---|---|---|
| POST | `/api/auth/login` | Iniciar sesión |
| POST | `/api/auth/logout` | Cerrar sesión |
| GET | `/api/auth/me` | Usuario y permisos actuales |
| GET | `/api/lookups` | Categorías, proveedores, ubicaciones y configuración |
| GET | `/api/dashboard` | Indicadores y alertas |
| GET | `/api/products?q=&filter=&category=&page=` | Buscar y filtrar productos |
| GET | `/api/products/{id}` | Producto, saldos, proveedores e historia |
| POST | `/api/products` | Crear producto (`product.write`) |
| PUT | `/api/products/{id}` | Editar todos los datos comerciales (`product.write`) |
| PATCH | `/api/products/{id}/status` | Activar/desactivar (`product.deactivate`) |
| POST | `/api/products/{id}/barcodes` | Asignar o resolver código (`product.write` / `barcode.resolve`) |
| POST | `/api/inventory/adjust` | Ajuste por cantidad contada (`inventory.adjust`) |
| POST | `/api/inventory/transfer` | Traslado atómico (`inventory.adjust`) |
| POST | `/api/sales` | Registrar venta desde POS (`sale.record`) |
| POST | `/api/pricing/recommend` | Crear recomendación pendiente (`price.view`) |
| POST | `/api/pricing/{id}/decision` | Aprobar/rechazar (`price.approve`) |
| GET | `/api/import/template` | Plantilla CSV |
| POST | `/api/import/preview` | Previsualizar actualización de inventario CSV (`product.import`) |
| POST | `/api/import/apply` | Actualizar inventario en una transacción (`product.import`) |
| GET | `/api/reports/reorder` | Reposición (`reports.basic`) |
| GET | `/api/reports/inventory` | Valor y margen (`reports.profit`) |
| GET | `/api/audit` | Auditoría (`audit.view`) |

## Ejemplo: registrar una venta

```json
{
  "locationId": 1,
  "reference": "POS-0001042",
  "lines": [
    { "productId": 1, "quantity": 2, "unitPrice": null }
  ]
}
```

`unitPrice: null` usa el precio vigente. La operación valida existencias, crea cabecera y líneas, descuenta el saldo, agrega movimientos al libro mayor y audita dentro de una sola transacción.

## Ejemplo: costo puesto y recomendación

```json
{
  "productId": 1,
  "freight": 25,
  "duties": 0,
  "handling": 10,
  "discount": 0,
  "other": 0,
  "comparableCount": 5
}
```

La respuesta incluye `landedCost`, `suggestedPrice`, `markup`, `grossMargin`, `expectedProfit`, rango, confianza, explicación, advertencias y los comparables. El resultado queda `pending`; se aplica únicamente al enviar `approved` a la ruta de decisión.
