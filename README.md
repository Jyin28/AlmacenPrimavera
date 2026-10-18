# Almacén Primavera

Aplicación local simplificada: login, fusión de Excel, categorización rápida, ayudante de precios e imagen a Excel con OCR. ASP.NET Core 8 + SQLite. No necesita servicios externos para procesar archivos o sugerir precios.

## Abrir la app

Requiere .NET SDK 8 o posterior. Desde esta carpeta:

~~~powershell
dotnet restore --configfile .\NuGet.Config
dotnet run --urls http://localhost:5074
~~~

Abre http://localhost:5074. En instalaciones nuevas, usuario **admin**. La contraseña inicial es la variable DEFAULT_ADMIN_PASSWORD o, solo para desarrollo, Primavera-Admin!. Las cuentas y contraseñas existentes se conservan. Configura una contraseña inicial segura antes del primer arranque y usa HTTPS si expones la app a una red.

## Flujo de trabajo

1. **Fusionar Excel:** elige dos archivos .xlsx (o CSV UTF-8), su hoja y la fila de encabezados. Confirma las columnas de código de barras, nombre, cantidad, compra, venta e impuesto opcional.
2. Los códigos repetidos se unifican **sumando sus cantidades**, incluso si se repiten dentro del mismo archivo. Los ceros iniciales se conservan cuando el código está guardado como texto o con formato de ceros en Excel.
3. Si difieren nombres, precios o impuestos, elige prioridad del primer dato o del último (Excel 2 por defecto). Revisa las diferencias y, si hace falta, edita las filas antes de guardar o descargar el Excel fusionado.
4. **Guardar y categorizar:** actualiza el catálogo por código de barras. La cantidad resultante **reemplaza** la anterior; no vuelve a sumarse sobre ella. Los productos ausentes no se eliminan. Los existentes conservan categoría, importador y código interno.
5. **Por categorizar:** los productos nuevos quedan pendientes. Selecciona varios (hasta 50 por página), elige categoría y luego subcategoría en sus selectores separados, asigna importador y revisa el precio de cada producto. Desmarca «Solo pendientes» para reclasificar productos anteriores.
6. **Revisar precio:** acepta la sugerencia, escribe otro precio o conserva el precio de venta actual. Confirmar «Marcar producto como revisado» requiere código de barras, categoría y subcategoría, importador y compra mayor que cero. Después se abre el siguiente pendiente clasificado de la página.
7. **Productos y categorías:** busca, edita productos y descarga el catálogo. Los botones **Categorías**, **Subcategorías** e **Importadores** abren sus administradores por separado. **Ayudante de precios:** revisa precios de productos en cualquier momento.

## Imagen a Excel (OCR)

Entra a **Imagen a Excel** después de iniciar sesión:

1. Selecciona una foto o captura JPG, PNG o WebP (hasta 10 MB y 20 megapíxeles). Gírala si hace falta; usa texto impreso, bien enfocado y visto de frente.
2. Elige español/inglés, el separador decimal y el orden de las cuatro columnas de la imagen. Pulsa **Leer imagen**.
3. Compara las filas editables con la foto. Corrige errores y revisa las líneas no convertidas; puedes recuperarlas, añadir filas o quitar filas. Un dato numérico sin leer queda vacío, nunca se convierte automáticamente en cero.
4. Si la imagen contiene más columnas o la separación falla, abre **Texto leído y corrección manual**. Usa una línea por producto con cuatro campos separados por `|`, por ejemplo: `00123 | Café molido | 2 | 1250.50`. Pulsa **Convertir este texto a filas**. Cambiar texto, orden o separador requiere volver a convertir antes de descargar.
5. Confirma la revisión y pulsa **Descargar Excel (.xlsx)**. El archivo contiene exactamente **Product Code, Name, Quantity, Price**, en ese orden. Los códigos se guardan como texto para conservar ceros iniciales; cantidades y precios son celdas numéricas. Máximo 5000 productos, cantidades con 3 decimales y precios con 2.

El OCR utiliza [Tesseract.js](https://github.com/naptha/tesseract.js) dentro del navegador, con motor e idiomas incluidos en la app (aproximadamente 48 MB). No requiere clave de API ni internet una vez servida la app local. La foto no sale del navegador: solo las filas revisadas se envían al servidor de esta app para crear el Excel. La lectura puede tardar; se puede cancelar. El OCR no garantiza exactitud, especialmente con fotos borrosas o manuscritas.

Esta pestaña no actualiza inventario ni calcula precios, y conserva filas repetidas. `Price` es el precio que aparece en el documento; no calcula impuestos ni totales. Recargar, limpiar o cerrar sesión descarta la foto y el borrador. PDF y HEIC no se admiten.

Los recursos locales ya están incluidos en `wwwroot/vendor/tesseract`; Node.js no es necesario para ejecutar la app. Para regenerarlos durante desarrollo con Node.js y pnpm:

~~~powershell
pnpm install --frozen-lockfile --ignore-scripts
pnpm run vendor:ocr
~~~

Mantén esta carpeta al publicar. Incluye el manifiesto SHA-256 y los avisos de terceros.

## Categorías, importadores y códigos

- **Categoría:** nombre y abreviación obligatoria y única (1–12 letras o números), por ejemplo Bebidas → BEB.
- **Subcategoría:** pertenece a una categoría, no lleva abreviación. Número automático desde 1 dentro de su categoría: 1, 2, 3… Solo se admiten estos dos niveles.
- **Producto:** número automático desde 1 dentro de su subcategoría. El código interno es `ABREVIACIÓN-NÚMERO SUBCATEGORÍA-NÚMERO PRODUCTO`, sin ceros de relleno. Ejemplos: `BEB-1-1`, `BEB-1-2`, `BEB-2-1` y `HOG-1-1`.
- Los números se asignan al guardar; no son los IDs de la base de datos y no se editan manualmente. Editar nombres o precios conserva la numeración. En una clasificación por lote se asignan en orden de ID de producto.
- Cambiar un producto de subcategoría le asigna el siguiente número del destino y actualiza su código. Mover una subcategoría a otra categoría le asigna el siguiente número del destino, conservando los números de sus productos. Cambiar la abreviación actualiza los códigos de todos los productos de esa categoría.
- Los números liberados no se reutilizan; volver a una clasificación anterior toma el siguiente número. Un guardado fallido no consume números. No se renumeran los otros registros para cerrar huecos.
- Sin clasificación, el producto queda pendiente y sin código interno hasta elegir categoría y subcategoría. El código de barras no cambia.
- Importadores con número automático y estable desde 1. Cambiar el nombre no cambia el número.
- Cada producto conserva nombre, código de barras, código interno, categoría y subcategoría, importador, compra, venta, cantidad e impuesto.

## Recomendación de precios

No usa IA externa ni consulta precios de mercado. Calcula una sugerencia explicable:

- Costo base = compra + costo adicional por unidad.
- Con al menos 3 productos revisados comparables de la misma subcategoría, usa la mediana de su recargo. Prioriza el mismo importador si hay al menos 3 comparables de ese importador.
- Sin suficientes comparables, utiliza el recargo configurado en la subcategoría. El formulario de nuevas subcategorías precarga las reglas de la categoría principal; después se editan independientemente.
- Respeta el margen mínimo de la subcategoría y redondea hacia arriba al múltiplo elegido.
- Recargo = (venta / costo − 1) × 100. Margen = (venta − costo) / venta × 100.
- Compra y venta deben usar la misma base de impuestos. El impuesto se conserva como dato; no se añade automáticamente otra vez.
- La sugerencia no guarda nada. El usuario confirma el precio final. Si el producto cambió mientras se revisaba, se exige recalcular.
- Un cambio de costo al importar vuelve a dejar el producto pendiente.

## Archivos admitidos y límites

- .xlsx: hasta 10 MB, 20 hojas, 100 columnas y 20 000 filas de productos por hoja seleccionada.
- CSV UTF-8: comas, punto y coma o tabulaciones; campos con comillas y saltos de línea.
- .xls, archivos cifrados y formatos con macros no se admiten. Abre el archivo en Excel y guarda como .xlsx.
- Se procesa la hoja seleccionada de cada archivo, no todas las hojas juntas.
- Solo se fusionan los seis campos seleccionados. Las demás columnas originales no se copian. La categorización e importadores se completan en la app.
- Las fórmulas no se ejecutan: se leen sus resultados guardados. Si falta el resultado o hay errores de Excel, se pide corregir y guardar el archivo.
- Mantén los códigos de barras como texto: los dígitos que Excel ya haya redondeado no se pueden recuperar.
- Precios con hasta 2 decimales; cantidades con hasta 3; valores no negativos.
- Las filas inválidas se muestran con su archivo y número de fila. La interfaz impide guardar o descargar un resultado incompleto.
- La vista previa de fusión vive en la pestaña del navegador; recargar o cerrar sesión la descarta. Los productos ya guardados sí persisten.

## Datos existentes y seguridad

La base sigue en data/store.db (o STORE_DB_PATH). En el primer arranque de esta versión se copia el catálogo anterior a las tablas simplificadas en una única transacción. Los datos históricos y las cuentas permanecen intactos. Los productos activos anteriores aparecen pendientes para facilitar su revisión; se conserva su clasificación actual y la cantidad total de sus ubicaciones. Los proveedores asociados a productos se convierten en importadores numerados.

La migración de numeración se ejecuta una sola vez al arrancar la nueva versión. Conserva categorías y productos, asigna los nuevos números en orden de ID y regenera sus códigos internos. Los productos que estaban directamente en una categoría se colocan en una subcategoría «General» de esa misma categoría. Los niveles más profundos se convierten en subcategorías de la raíz, conservando su ruta en el nombre. Las categorías antiguas sin abreviación reciben una provisional `CAT1`, `CAT2`… que puedes editar. Los productos inactivos conservan su estado y también reservan su número.

La estructura y los códigos anteriores quedan respaldados dentro de la misma base en `catalog_taxonomy_before_numbering` y `catalog_product_codes_before_numbering`; los cambios de código quedan registrados en `catalog_code_history`. No se borran productos ni se modifican códigos de barras, cantidades o precios. Reinicia la app y recarga el navegador después de actualizar; no mezcles el frontend nuevo con un servidor viejo.

No hay pestañas ni operaciones públicas de compras, ventas, inventario por ubicación, reportes o auditoría. Sus tablas históricas se conservan, pero no se siguen actualizando. No ejecutes la versión vieja y la nueva simultáneamente contra la misma base; después de migrar, la versión vieja no refleja los cambios nuevos.

Los administradores y gerentes pueden guardar cambios. Los demás roles existentes conservan acceso de consulta. Sesiones de 12 horas con cookie HttpOnly/SameSite Strict y contraseñas PBKDF2. Hay limitación de intentos de login, validación en servidor, protección de origen y detección de ediciones desactualizadas. Importes calculados con decimal y almacenados como texto decimal exacto.

Respalda la base antes de actualizar; haz la copia con la app detenida o mediante una herramienta compatible con SQLite/WAL.

## Verificar

~~~powershell
dotnet build --no-restore -o .verify-build
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\integration.ps1
node --check wwwroot/app.js
node --check wwwroot/catalog.js
node --check wwwroot/ocr.js
node --test tests/ocr-parser.test.cjs tests/ocr-state.test.cjs
node --test tests/catalog-ui.test.cjs
python tests/numbering-migration.py
~~~

Las pruebas de integración levantan un servidor local con una base nueva y aislada bajo TestResults. No modifican data/store.db. Cubren login, separación de categorías/subcategorías, numeración por grupo, movimientos, rollback de contadores, importadores, fusión, cantidades, Excel real, clasificación, precios, conflictos de edición y exportación OCR con cuatro columnas. La prueba de migración verifica preservación de datos, reinicios, concurrencia y rollback ante una jerarquía inválida. Las pruebas JavaScript comprueban los selectores y formularios separados, y el parser y revisión obligatoria de OCR; no sustituyen una revisión visual en navegador.

Prueba de OCR real con una imagen sintética (requiere dependencias Node instaladas y Python con Pillow; la fuente del ejemplo usa Arial de Windows):

~~~powershell
python tests/ocr-fixture.py
node tests/ocr-engine.cjs
~~~
