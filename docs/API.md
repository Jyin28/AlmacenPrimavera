# API simplificada

Todas las rutas /api requieren sesión salvo POST /api/auth/login. Administradores y gerentes pueden guardar cambios. Otros roles son de consulta. No hay API pública de compras, ventas, auditoría o movimientos de inventario.

| Método y ruta | Uso |
| --- | --- |
| POST /api/auth/login | {username,password}; crea cookie de sesión |
| GET /api/auth/me | Usuario, nombre, rol y canEdit |
| POST /api/auth/logout | Invalida la sesión |
| GET /api/lookups | categories (solo raíces), subcategories (parent_id y sequence_number), importers y pending |
| GET /api/products?q=&categoryId=&pending=true&page=1 | Búsqueda, categoría con descendientes, 50 resultados por página |
| GET /api/products/{id} | Producto con versión |
| POST /api/products | Crear producto pendiente |
| PUT /api/products/{id} | Editar; version obligatorio para evitar sobrescritura |
| POST /api/products/classify | {ids,categoryId?,importerId?}; categoryId es la subcategoría; código automático en transacción por lote |
| POST /api/categories | {name,abbreviation,targetMarkup,minimumMargin}; solo categorías principales |
| PUT /api/categories/{id} | Mismos campos; cambiar abreviación actualiza códigos de sus productos |
| POST /api/subcategories | {name,categoryId,targetMarkup,minimumMargin}; categoryId es la categoría principal; número automático |
| PUT /api/subcategories/{id} | Mismos campos; moverla asigna número en el destino y actualiza códigos de sus productos |
| POST /api/importers | {name}; código automático |
| PUT /api/importers/{id} | {name}; mantiene el código |
| POST /api/excel/read | multipart/form-data con archivo en campo file; devuelve hojas, filas y marcas de celdas numéricas |
| POST /api/excel/merge | {first,second,prefer: first o second}; devuelve filas, diferencias y errores |
| POST /api/excel/export | {rows}; descarga .xlsx |
| POST /api/excel/apply | {rows}; actualización por código de barras, nuevos pendientes |
| POST /api/ocr/export | {reviewed:true,rows:[{productCode,name,quantity,price}]}; descarga .xlsx de cuatro columnas sin modificar el catálogo |
| GET /api/products/export | Descarga catálogo .xlsx |
| POST /api/pricing/recommend | {productId,extraCost,markup?,roundTo}; calcula sin guardar |
| POST /api/pricing/apply | {productId,version,sellingPrice,completeReview}; guarda tras confirmación |

ProductEdit: name, barcode, categoryId e importerId opcionales, cost, sellingPrice, taxRate, quantity, version. Por compatibilidad, el campo `categoryId` de un producto/clasificación representa la **subcategoría**, no la raíz. Una raíz se rechaza. `internalCode` ya no es editable; se genera como ABREVIACIÓN-NÚMERO SUBCATEGORÍA-NÚMERO PRODUCTO. Las respuestas incluyen `root_category_id`, `category`, `subcategory`, `subcategory_number`, `product_number` e `internal_code`. Un borrador sin subcategoría tiene número y código interno nulos.

La numeración comienza en 1 para cada grupo y es independiente de los IDs. Repetir la misma clasificación conserva el número; mover a otro grupo asigna el siguiente. Los números no se reutilizan. Categorías y subcategorías no pueden cambiar de tipo ni anidarse en un tercer nivel. Cambiar códigos invalida versiones de producto antiguas. Los campos internos de numeración no son parámetros de escritura.

SourceTable: rows (filas de datos sin encabezado), mapping (barcode, name, quantity, cost, sellingPrice, taxRate: índices desde 0; -1 sin columna), decimalSeparator (. o ,), label, numericCells opcional (matriz de booleanos devuelta por read), startRow (número de primera fila de datos en el origen).

MergedProduct: barcode, name, quantity, cost, sellingPrice, taxRate. La exportación del catálogo añade internalCode, categoryCode, importerName, importerCode; la importación solo modifica los seis campos base y no sobrescribe la clasificación.

Los importes JSON usan números. Los códigos de barras e internos son cadenas, nunca números. 400: validación; 401: sesión; 403: permiso; 404: no existe o módulo retirado; 409: unicidad o versión desactualizada; 429: exceso de intentos de login.

OCR: la imagen se procesa únicamente en el navegador; no hay endpoint de subida de fotos. La exportación requiere sesión (cualquier rol) y `reviewed:true`, entre 1 y 5000 filas, productCode de 1–100 caracteres, name de 1–300 y números no negativos hasta 1000000000. Quantity admite 3 decimales y Price 2; campos ausentes se rechazan. Se conservan códigos como texto y filas duplicadas. Los encabezados son exactamente `Product Code`, `Name`, `Quantity`, `Price`.
