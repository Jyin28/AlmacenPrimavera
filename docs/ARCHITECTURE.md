# Arquitectura simplificada

- Program.cs: arranque, autenticación, protección de solicitudes y rutas mínimas.
- Data/StoreDb.cs: conexión SQLite, esquema histórico y cuentas existentes.
- Data/CatalogSchema.cs: migración idempotente y no destructiva a las tablas simplificadas.
- Data/CatalogNumbering.cs: migración v2, contadores transaccionales por grupo, códigos automáticos y registro de códigos anteriores.
- Services/StoreService.cs: catálogo, clasificación por lotes, numeración, importación transaccional y precios.
- Services/ExcelFiles.cs: lectura de datos OOXML/CSV, fusión y exportación .xlsx con compresión/XML de .NET, sin dependencias externas ni ejecución de macros.
- Services/CatalogModels.cs: contratos de datos.
- wwwroot/app.js: sesión, navegación, carga, asignación de columnas y vista previa de fusión.
- wwwroot/catalog.js: catálogo, categorización rápida, importadores y revisión de precios.
- wwwroot/ocr.js y ocr.css: foto, rotación, OCR local en Web Worker, corrección y descarga sin persistencia.
- wwwroot/ocr-parser.js: separación de filas por posiciones de palabras o texto delimitado, orden configurable, validación sin adivinar números.
- Services/OcrExport.cs: valida filas revisadas y exporta exactamente cuatro columnas usando ExcelFiles.ExportTable.
- wwwroot/vendor/tesseract: motor, WASM e idiomas español/inglés autocontenidos; scripts/vendor-ocr.mjs los copia desde dependencias fijadas y genera su manifiesto SHA-256.

## Estado y conservación de datos

Las tablas catalog_categories, importers y catalog_products son el catálogo activo. catalog_migrations registra la migración inicial. Las tablas anteriores se conservan como histórico; ningún endpoint antiguo las modifica. Las cuentas y sesiones se reutilizan.

La migración v1 toma el código de barras principal activo y suma las cantidades de todas las ubicaciones. Conserva también los productos inactivos como inactivos. Los proveedores vinculados se convierten en importadores con IDs nuevos desde 1 y referencia interna a su ID anterior.

La migración v2 archiva la taxonomía y los códigos originales antes de asignar números y regenerar códigos. Se mantiene la tabla catalog_categories para no romper IDs/relaciones: raíces con abreviación y parent_id nulo, subcategorías con parent_id de una raíz, sequence_number y abreviación vacía. La API y la interfaz exponen ambos tipos por separado y rechazan tercer nivel y cambios de tipo. Productos clasificados apuntan solo a subcategorías mediante category_id y guardan product_number. Los no clasificados no tienen número ni código.

Los productos de una raíz se trasladan a una subcategoría General bajo esa raíz. Las rutas antiguas más profundas se conservan como nombres de subcategoría de su raíz. Las raíces sin abreviación válida reciben CAT seguido de un número disponible. Se migran activos e inactivos por ID, sin modificar precios, cantidades, códigos de barras o importadores. Las tablas catalog_taxonomy_before_numbering y catalog_product_codes_before_numbering conservan los valores previos. Toda la migración y su marcador comparten transacción; un error revierte también el cambio de esquema.

Los productos nuevos entran con review_pending=1. La clasificación y el cambio de costo requieren nueva revisión; solo la confirmación explícita de precio con los campos requeridos completos elimina esa marca. Las cantidades fusionadas son saldos absolutos, lo que hace idempotente su reimportación respecto a existencias.

## Consistencia

Código de barras e interno únicos sin distinguir mayúsculas; IDs de importador autoincrementales. catalog_number_counters guarda el último número por (tipo, propietario) y se incrementa con UPSERT RETURNING dentro de la transacción de escritura, también bajo concurrencia. Índices únicos protegen (parent_id,sequence_number) y (category_id,product_number). Mover registros no libera números para reutilización; un fallo revierte la asignación.

El código es ABBR-subcategory_number-product_number, sin relleno. Renombrar no renumera; mover un producto asigna número del destino; mover una subcategoría conserva sus números de producto y asigna nuevo número de subcategoría. Cambiar abreviación regenera los códigos de todas sus subcategorías. catalog_code_history guarda código anterior/nuevo y fecha. Cambiar código incrementa version para rechazar formularios o recomendaciones desactualizados.

Escrituras por lote y migración atómicas. Edición individual y aplicación de precios verifican version. Cantidades y dinero almacenados como texto decimal exacto, calculados con decimal; no hay aritmética monetaria SQL que trunque divisiones.

## Excel

No se ejecutan fórmulas. Se leen resultados guardados, shared strings, inline strings, filas dispersas, referencias de hojas y formatos simples de ceros iniciales. Límites de archivo y contenido descomprimido protegen memoria. DTD y recursos XML externos deshabilitados. La exportación usa celdas de texto para identificadores (incluido texto que comience por =); no genera fórmulas con entradas del usuario.

Los archivos se procesan en memoria. La vista previa no persiste. La escritura al catálogo vuelve a validar números, campos requeridos y códigos únicos. El usuario ve errores y diferencias antes de descargar o confirmar la actualización.

## OCR

La foto permanece en memoria del navegador y se procesa con Tesseract.js 7. No se sube a la API ni a proveedores externos. Los recursos del worker, WASM y modelos se sirven desde la app; CSP permite workers del mismo origen y compilación WASM sin habilitar `unsafe-eval` general. El servidor reconoce MIME de .wasm y .gz.

El borrador requiere revisión explícita antes de exportar. Campos numéricos ilegibles quedan vacíos, y las líneas que no se pueden interpretar se muestran para recuperación manual. Cambiar texto o asignación de columnas invalida la conversión y bloquea la descarga hasta volver a procesar. Cancelar/limpiar/cerrar sesión invalida resultados asíncronos anteriores y termina el worker disponible. Solo las cuatro columnas revisadas llegan a POST /api/ocr/export; esta ruta no escribe en SQLite ni mezcla duplicados.
