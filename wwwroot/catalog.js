async function renderCatalog() {
    const rendering = ++renderGeneration;
    const queue = state.view === "queue", pricing = state.view === "prices";
    const filterCategory=state.subcategory || state.category;
    const result = await api("/api/products?" + new URLSearchParams({ q: state.query, ...(filterCategory ? { categoryId: filterCategory } : {}), pending: queue && state.onlyPending, page: state.page }));
    if (rendering !== renderGeneration) return;
    if (state.page > result.pages) { state.page = result.pages; return renderCatalog(); }
    state.items = result.items; state.total = result.total;
    state.selected = new Set([...state.selected].filter(id => state.items.some(p => p.id === id)));
    const title = queue ? "Por categorizar" : pricing ? "Ayudante de precios" : "Productos y categorías";
    const subtitle = queue ? "Clasifica varios productos de una vez. Después revisa el precio de cada uno para completar su registro. También puedes incluir los productos anteriores." :
        pricing ? "Encuentra un producto y revisa un precio sugerido según su compra, categoría y productos comparables." : "Tu lista de productos, categorías, subcategorías e importadores. Solo lo que necesitas.";
    $("#content").innerHTML = '<div class="page-head"><div><p class="eyebrow">' + (queue ? "PASO 2 · ORGANIZA Y REVISA" : pricing ? "PRECIOS CON CRITERIO" : "TU CATÁLOGO") + '</p><h1>' + title + '</h1><p>' + subtitle + '</p></div><div class="actions">' +
        (!pricing ? '<button id="categories">Categorías</button><button id="subcategories">Subcategorías</button><button id="importers">Importadores</button>' : '') +
        (!queue && !pricing ? '<button id="new-product" class="primary" ' + writable() + '>＋ Producto</button>' : '') + '</div></div>' +
        (queue ? '<div class="notice"><b>Código automático: ABREVIACIÓN-N.º SUBCATEGORÍA-N.º PRODUCTO.</b> Ejemplo: BEB-1-1. Cada categoría numera sus subcategorías desde 1 y cada subcategoría numera sus productos desde 1. Reclasificar actualiza el código.</div>' : '') +
        '<form id="search-form" class="filters"><label>Buscar producto<input name="q" value="' + esc(state.query) + '" placeholder="Nombre, código de barras, código interno o importador"></label><label>Categoría<select name="category" id="search-category">' + categoryOptions(state.category, "Todas las categorías") + '</select></label><label>Subcategoría<select name="subcategory" id="search-subcategory">' + subcategoryOptions(state.category,state.subcategory,"Todas las subcategorías") + '</select></label>' +
        (queue ? '<label class="check"><input name="pending" type="checkbox" ' + (state.onlyPending ? 'checked' : '') + '>Solo pendientes</label>' : '') + '<button>Buscar</button></form>' +
        (queue ? '<section class="bulk"><label>Categoría<select id="bulk-category">' + categoryOptions(null, "No cambiar clasificación") + '</select></label><label>Subcategoría<select id="bulk-subcategory">' + subcategoryOptions(null,null) + '</select></label><label>Importador<select id="bulk-importer">' + importerOptions(null, "No cambiar importador") + '</select></label><button id="classify" class="primary" ' + writable() + '>Clasificar seleccionados (<span id="selection-count">' + state.selected.size + '</span>)</button></section>' : '') +
        (!result.items.length ? '<div class="empty"><h3>' + (queue ? 'No hay productos pendientes con estos filtros' : 'No hay productos con estos filtros') + '</h3><p>' + (queue ? 'Fusiona tus archivos o desmarca «Solo pendientes» para trabajar con productos anteriores.' : 'Agrega un producto o fusiona tus archivos Excel para comenzar.') + '</p></div>' :
        '<div class="table-wrap"><table><thead><tr>' + (queue ? '<th><input type="checkbox" id="select-page" aria-label="Seleccionar los 50 productos de esta página"></th>' : '') + '<th>Producto</th><th>Códigos</th><th>Categoría</th><th>Subcategoría</th><th>Importador</th><th>Compra</th><th>Venta</th><th>Estado</th><th></th></tr></thead><tbody>' +
        result.items.map(p => '<tr>' + (queue ? '<td><input type="checkbox" data-select="' + p.id + '" aria-label="Seleccionar ' + esc(p.name) + '" ' + (state.selected.has(p.id) ? 'checked' : '') + '></td>' : '') + '<td class="product-name"><b>' + esc(p.name) + '</b><small>Cantidad: ' + number(p.quantity) + '</small></td><td class="code">' + esc(p.barcode || 'Sin código de barras') + '<small>' + esc(p.internal_code || 'Pendiente de clasificar') + '</small></td><td>' + esc(p.category || 'Sin categoría') + '</td><td>' + (p.subcategory_number ? esc(p.subcategory_number+' · '+p.subcategory) : 'Sin subcategoría') + '</td><td>' + (p.importer_id ? '<small>N.º ' + p.importer_id + '</small>' + esc(p.importer) : '<span class="muted">Sin importador</span>') + '</td><td class="number">' + money(p.cost) + '</td><td class="number">' + money(p.selling_price) + '</td><td><span class="badge ' + (p.review_pending ? 'pending' : 'done') + '">' + (p.review_pending ? 'Por revisar' : 'Revisado') + '</span></td><td><div class="row-actions"><button class="small" data-edit="' + p.id + '" ' + writable() + '>Editar</button><button class="small" data-price="' + p.id + '">' + (queue ? 'Revisar precio' : 'Sugerir precio') + '</button></div></td></tr>').join('') + '</tbody></table></div>') +
        '<div class="pager"><span>' + number(result.total) + ' productos · Página ' + result.page + ' de ' + result.pages + '</span><div class="actions">' + (!queue && !pricing ? '<button id="export-catalog">Descargar catálogo</button>' : '') + '<button id="prev-page" ' + (result.page <= 1 ? 'disabled' : '') + '>Anterior</button><button id="next-page" ' + (result.page >= result.pages ? 'disabled' : '') + '>Siguiente</button></div></div>';
    bindSubcategories('#search-category','#search-subcategory','Todas las subcategorías');
    if (queue) bindSubcategories('#bulk-category','#bulk-subcategory','Selecciona una subcategoría');
    $("#search-form").onsubmit = e => { e.preventDefault(); run($('button', e.target), async () => { const v = formValues(e.target); state.query = v.q; state.category = v.category; state.subcategory = v.subcategory || ''; if (queue) state.onlyPending = !!v.pending; state.page = 1; state.selected.clear(); await renderCatalog(); }); };
    $("#prev-page").onclick = e => run(e.currentTarget, async () => { state.page--; state.selected.clear(); await renderCatalog(); });
    $("#next-page").onclick = e => run(e.currentTarget, async () => { state.page++; state.selected.clear(); await renderCatalog(); });
    if ($("#categories")) $("#categories").onclick = () => manageCategories();
    if ($("#subcategories")) $("#subcategories").onclick = () => manageSubcategories();
    if ($("#importers")) $("#importers").onclick = () => manageImporters();
    if ($("#new-product")) $("#new-product").onclick = () => editProduct();
    if ($("#export-catalog")) $("#export-catalog").onclick = e => run(e.currentTarget, async () => download(await api("/api/products/export", { download: true }), "catalogo-primavera.xlsx"));
    $$("[data-edit]").forEach(b => b.onclick = () => run(b, () => editProduct(Number(b.dataset.edit))));
    $$("[data-price]").forEach(b => b.onclick = () => run(b, () => priceProduct(Number(b.dataset.price))));
    function selectionChanged() {
        $("#selection-count").textContent = state.selected.size;
        const page = $("#select-page"); if (page) { page.checked = state.items.length > 0 && state.selected.size === state.items.length; page.indeterminate = state.selected.size > 0 && !page.checked; }
    }
    $$("[data-select]").forEach(c => c.onchange = () => { const id = Number(c.dataset.select); if (c.checked) state.selected.add(id); else state.selected.delete(id); selectionChanged(); });
    if ($("#select-page")) $("#select-page").onchange = e => { state.selected = new Set(e.target.checked ? state.items.map(p => p.id) : []); $$("[data-select]").forEach(c => c.checked = e.target.checked); selectionChanged(); };
    if ($("#classify")) $("#classify").onclick = e => run(e.currentTarget, async () => {
        if (!state.selected.size) throw new Error("Selecciona los productos que quieres clasificar.");
        if ($('#bulk-category').value && !$('#bulk-subcategory').value) throw new Error('Selecciona también una subcategoría.');
        const body = { ids: [...state.selected], categoryId: nullableId($("#bulk-subcategory").value), importerId: nullableId($("#bulk-importer").value) };
        const r = await api("/api/products/classify", { method: "POST", body }); state.selected.clear(); await refresh(); toast(r.updated + " productos clasificados. Ya puedes revisar sus precios.");
    });
}
async function editProduct(id) {
    const p = id ? await api("/api/products/" + id) : { name: '', barcode: '', internal_code: '', cost: 0, selling_price: 0, quantity: 0, tax_rate: 0, version: 0 };
    openDialog(id ? "Editar producto" : "Nuevo producto", "El código interno se genera al guardar: ABREVIACIÓN-N.º SUBCATEGORÍA-N.º PRODUCTO.", '<form id="product-form"><div class="form-grid">' +
        inputField('Nombre','name',p.name) + inputField('Código de barras','barcode',p.barcode) +
        '<label>Categoría<select id="product-category">' + categoryOptions(p.root_category_id) + '</select></label><label>Subcategoría<select id="product-subcategory" name="categoryId">' + subcategoryOptions(p.root_category_id,p.category_id) + '</select></label><label class="wide">Código interno (automático)<input id="product-internal-code" readonly value="' + esc(p.internal_code) + '" placeholder="Pendiente de clasificar"></label><label class="wide">Importador<select name="importerId">' + importerOptions(p.importer_id) + '</select></label>' +
        inputField('Precio de compra','cost',p.cost,'number') + inputField('Precio de venta','sellingPrice',p.selling_price,'number') + inputField('Cantidad','quantity',p.quantity,'number','0.001') + inputField('Impuesto %','taxRate',p.tax_rate,'number') +
        '</div><p class="inline-message">El número de producto empieza en 1 en cada subcategoría. Se conserva al editar; al cambiar de subcategoría se asigna el siguiente número del destino. Sin clasificación, queda pendiente y sin código interno.</p><p id="product-error" class="error" role="alert"></p><div class="dialog-actions"><button class="primary" ' + writable() + '>Guardar producto</button></div></form>');
    bindSubcategories('#product-category','#product-subcategory','Selecciona una subcategoría');
    const previewCode=() => {
        const sub=state.lookups.subcategories.find(c=>c.id===Number($('#product-subcategory').value));
        const parent=state.lookups.categories.find(c=>c.id===sub?.parent_id);
        $('#product-internal-code').value=sub ? parent.abbreviation+'-'+sub.sequence_number+'-'+(sub.id===p.category_id ? p.product_number : '(automático al guardar)') : '';
    };
    $('#product-category').addEventListener('change',previewCode); $('#product-subcategory').addEventListener('change',previewCode);
    $("#product-form").onsubmit = e => { e.preventDefault(); run($('button', e.target), async () => {
        if ($('#product-category').value && !$('#product-subcategory').value) throw new Error('Selecciona también una subcategoría o deja la categoría sin asignar.');
        const v = formValues(e.target); const body = { ...v, categoryId: nullableId(v.categoryId), importerId: nullableId(v.importerId), cost: Number(v.cost), sellingPrice: Number(v.sellingPrice), quantity: Number(v.quantity), taxRate: Number(v.taxRate), version: p.version };
        await api('/api/products' + (id ? '/' + id : ''), { method: id ? 'PUT' : 'POST', body }); closeDialog(); await refresh(); toast('Producto guardado.');
    }, $("#product-error")); };
}
function manageCategories() {
    openDialog('Categorías', 'Solo las categorías tienen abreviación. Las subcategorías se administran por separado y tienen número automático.', '<button id="new-category" class="primary" ' + writable() + '>＋ Categoría</button><div class="manage-list">' +
        state.lookups.categories.map(c => '<div class="manage-row"><div><b>' + esc(c.name) + '</b><small>Abreviación: ' + esc(c.abbreviation) + ' · ' + state.lookups.subcategories.filter(s=>s.parent_id===c.id).length + ' subcategorías</small></div><div class="actions"><button data-category-subs="' + c.id + '">Subcategorías</button><button data-category-edit="' + c.id + '" ' + writable() + '>Editar</button></div></div>').join('') + (!state.lookups.categories.length ? '<p class="muted">Crea la primera categoría para comenzar.</p>' : '') + '</div>');
    $("#new-category").onclick = () => categoryForm();
    $$("[data-category-edit]").forEach(b => b.onclick = () => categoryForm(Number(b.dataset.categoryEdit)));
    $$('[data-category-subs]').forEach(b=>b.onclick=()=>manageSubcategories(Number(b.dataset.categorySubs)));
}
function categoryForm(id) {
    const c = state.lookups.categories.find(c => c.id === id) || { name: '', abbreviation: '', target_markup: 35, minimum_margin: 20 };
    openDialog(id ? 'Editar categoría' : 'Crear categoría', 'Ejemplo: Bebidas → BEB. Cambiar la abreviación actualiza automáticamente los códigos de todos sus productos.', '<form id="category-form"><div class="form-grid">' +
        inputField('Nombre','name',c.name) + '<label>Abreviación<input name="abbreviation" value="' + esc(c.abbreviation) + '" placeholder="Ej. BEB" pattern="[A-Za-z0-9]{1,12}" maxlength="12" required></label></div>' +
        '<details><summary>Reglas iniciales del ayudante de precios</summary><p class="muted">El formulario de nuevas subcategorías precarga estos valores. No reemplazan las reglas de subcategorías existentes.</p><div class="form-grid">' + inputField('Recargo sobre compra %','targetMarkup',c.target_markup,'number') + inputField('Margen mínimo sobre venta %','minimumMargin',c.minimum_margin,'number') + '</div></details>' +
        '<p id="category-error" class="error" role="alert"></p><div class="dialog-actions"><button type="button" id="back-categories">Volver</button><button class="primary" ' + writable() + '>Guardar categoría</button></div></form>');
    $("#back-categories").onclick = manageCategories;
    $("#category-form").onsubmit = e => { e.preventDefault(); run($('button.primary', e.target), async () => {
        const v = formValues(e.target);
        if (id && v.abbreviation.trim().toUpperCase()!==c.abbreviation && !confirm('Cambiar la abreviación actualizará los códigos internos de todos los productos de esta categoría. ¿Continuar?')) return;
        await api('/api/categories' + (id ? '/' + id : ''), { method: id ? 'PUT' : 'POST', body: { ...v, targetMarkup: Number(v.targetMarkup), minimumMargin: Number(v.minimumMargin) } });
        await refresh(); manageCategories(); toast('Categoría guardada.');
    }, $("#category-error")); };
}
function manageSubcategories(categoryId=null) {
    const subs=state.lookups.subcategories.filter(s=>!categoryId || s.parent_id===categoryId);
    openDialog('Subcategorías','Se numeran automáticamente desde 1 dentro de cada categoría. No necesitan abreviación.',
        '<div class="filters"><label>Categoría<select id="manage-subcategory-parent">'+categoryOptions(categoryId,'Todas las categorías')+'</select></label><button id="new-subcategory" class="primary" '+writable()+' '+(!state.lookups.categories.length?'disabled':'')+'>＋ Subcategoría</button></div><div class="manage-list">'+
        subs.map(s=>'<div class="manage-row"><div><b>'+esc(categoryPath(s.id))+'</b><small>N.º '+s.sequence_number+' · Recargo: '+number(s.target_markup)+'% · Margen mínimo: '+number(s.minimum_margin)+'%</small></div><button data-subcategory-edit="'+s.id+'" '+writable()+'>Editar</button></div>').join('')+
        (!subs.length?'<p class="muted">No hay subcategorías. Crea primero una categoría si todavía no tienes ninguna.</p>':'')+'</div>');
    $('#manage-subcategory-parent').onchange=e=>manageSubcategories(nullableId(e.target.value));
    $('#new-subcategory').onclick=()=>subcategoryForm(null,categoryId);
    $$('[data-subcategory-edit]').forEach(b=>b.onclick=()=>subcategoryForm(Number(b.dataset.subcategoryEdit)));
}
function subcategoryForm(id,categoryId=null) {
    const s=state.lookups.subcategories.find(s=>s.id===id) || {name:'',parent_id:categoryId,target_markup:35,minimum_margin:20};
    openDialog(id?'Editar subcategoría':'Crear subcategoría','El número se asigna al guardar. Cambiar de categoría asigna el siguiente número del destino y actualiza los códigos de sus productos.',
        '<form id="subcategory-form"><div class="form-grid">'+inputField('Nombre','name',s.name)+
        '<label>Categoría<select name="categoryId" required>'+categoryOptions(s.parent_id,'Selecciona una categoría')+'</select></label><label>N.º subcategoría (automático)<input id="subcategory-number" readonly value="'+esc(s.sequence_number || 'Se asignará al guardar')+'"></label></div>'+
        '<details><summary>Reglas del ayudante de precios</summary><div class="form-grid">'+inputField('Recargo sobre compra %','targetMarkup',s.target_markup,'number')+inputField('Margen mínimo sobre venta %','minimumMargin',s.minimum_margin,'number')+'</div></details>'+
        '<p id="subcategory-error" class="error" role="alert"></p><div class="dialog-actions"><button type="button" id="back-subcategories">Volver</button><button class="primary" '+writable()+'>Guardar subcategoría</button></div></form>');
    $('#back-subcategories').onclick=()=>manageSubcategories(s.parent_id);
    $('#subcategory-form select').onchange=e=>{
        $('#subcategory-number').value=id && Number(e.target.value)===s.parent_id ? s.sequence_number : 'Se asignará al guardar';
        if (!id) { const c=state.lookups.categories.find(c=>c.id===Number(e.target.value)); if(c) { $('#subcategory-form [name="targetMarkup"]').value=c.target_markup; $('#subcategory-form [name="minimumMargin"]').value=c.minimum_margin; } }
    };
    if (!id && s.parent_id) $('#subcategory-form select').onchange({target:$('#subcategory-form select')});
    $('#subcategory-form').onsubmit=e=>{e.preventDefault();run($('button.primary',e.target),async()=>{
        const v=formValues(e.target), parent=nullableId(v.categoryId);
        if (id && parent!==s.parent_id && !confirm('Mover esta subcategoría cambiará su número y los códigos internos de todos sus productos. ¿Continuar?')) return;
        await api('/api/subcategories'+(id?'/'+id:''),{method:id?'PUT':'POST',body:{name:v.name,categoryId:parent,targetMarkup:Number(v.targetMarkup),minimumMargin:Number(v.minimumMargin)}});
        await refresh();manageSubcategories(parent);toast('Subcategoría guardada.');
    },$('#subcategory-error'));};
}
function manageImporters() {
    openDialog('Importadores', 'El código se asigna automáticamente: 1, 2, 3… y se conserva al cambiar el nombre.', '<button id="new-importer" class="primary" ' + writable() + '>＋ Importador</button><div class="manage-list">' +
        state.lookups.importers.map(i => '<div class="manage-row"><div><b>' + esc(i.name) + '</b><small>Código de importador: ' + i.id + '</small></div><button data-importer-edit="' + i.id + '" ' + writable() + '>Editar</button></div>').join('') + (!state.lookups.importers.length ? '<p class="muted">Agrega tu primer importador.</p>' : '') + '</div>');
    $("#new-importer").onclick = () => importerForm();
    $$("[data-importer-edit]").forEach(b => b.onclick = () => importerForm(Number(b.dataset.importerEdit)));
}
function importerForm(id) {
    const i = state.lookups.importers.find(i => i.id === id) || { name: '' };
    openDialog(id ? 'Editar importador' : 'Nuevo importador', id ? 'Código de importador: ' + id : 'Se asignará el siguiente número disponible.', '<form id="importer-form">' + inputField('Nombre del importador','name',i.name) + '<p id="importer-error" class="error" role="alert"></p><div class="dialog-actions"><button type="button" id="back-importers">Volver</button><button class="primary" ' + writable() + '>Guardar importador</button></div></form>');
    $("#back-importers").onclick = manageImporters;
    $("#importer-form").onsubmit = e => { e.preventDefault(); run($('button.primary', e.target), async () => {
        await api('/api/importers' + (id ? '/' + id : ''), { method: id ? 'PUT' : 'POST', body: formValues(e.target) }); await refresh(); manageImporters(); toast('Importador guardado.');
    }, $("#importer-error")); };
}
async function priceProduct(id) {
    const p = await api('/api/products/' + id);
    const ready = p.category_id && p.importer_id && p.barcode && p.cost > 0;
    openDialog('Revisar precio', p.name + ' · ' + (p.barcode || p.internal_code), '<div class="notice">' + esc(categoryPath(p.category_id) || 'Sin categoría') + ' · Importador: ' + esc(p.importer || 'Sin asignar') + '</div>' +
        (!ready ? '<div class="notice warning">Antes de completar la revisión, indica categoría y subcategoría, importador, código de barras y precio de compra mayor que cero. <button id="fix-product" class="small" ' + writable() + '>Completar producto</button></div>' : '') +
        '<form id="recommend-form"><div class="form-grid">' + inputField('Costo adicional por unidad','extraCost',0,'number') + '<label>Recargo manual % (opcional)<input name="markup" type="number" min="0" max="1000" step="0.01" placeholder="Usar recomendación automática"></label><label>Redondear hacia arriba a múltiplos de<select name="roundTo"><option value="1">₡1</option><option value="5">₡5</option><option value="10">₡10</option><option value="50">₡50</option><option value="100">₡100</option></select></label><div class="actions"><button>Calcular sugerencia</button></div></div><p id="recommend-error" class="error" role="alert"></p></form><div id="price-result"></div>');
    if ($("#fix-product")) $("#fix-product").onclick = () => run($("#fix-product"), () => editProduct(id));
    const generation = dialogGeneration;
    let calculation = 0;
    async function calculate() {
        const request = ++calculation;
        const v = formValues($("#recommend-form"));
        $("#price-result").innerHTML = '';
        const r = await api('/api/pricing/recommend', { method: 'POST', body: { productId: id, extraCost: Number(v.extraCost), markup: v.markup === '' ? null : Number(v.markup), roundTo: Number(v.roundTo) } });
        if (generation !== dialogGeneration || request !== calculation) return;
        $("#price-result").innerHTML = '<div class="price-hero"><div><small>PRECIO SUGERIDO</small><strong>' + money(r.suggestedPrice) + '</strong></div><div><small>Venta actual</small><p>' + money(r.currentPrice) + '</p></div></div>' +
            '<div class="price-metrics"><div><small>Compra + costo adicional</small><strong>' + money(r.landedCost) + '</strong></div><div><small>Ganancia por unidad</small><strong>' + money(r.profit) + '</strong></div><div><small>Margen sobre venta</small><strong>' + number(r.grossMargin) + '%</strong></div></div><p>' + esc(r.explanation) + '</p><p class="inline-message">Recargo sobre compra: ' + number(r.markup) + ' %. Margen mínimo de categoría: ' + number(r.minimumMargin) + ' %. ' + esc(r.note) + '</p>' +
            (r.comparableCount ? '<details><summary>' + r.comparableCount + ' productos comparables revisados</summary><ul class="conflict-list">' + r.comparables.map(c => '<li>' + esc(c.name) + ' · Compra ' + money(c.cost) + ' · Venta ' + money(c.price) + ' · Recargo ' + number(c.markup) + '%</li>').join('') + '</ul></details>' : '') +
            '<form id="apply-price-form"><label>Precio de venta que vas a guardar<input name="sellingPrice" type="number" min="0.01" max="1000000000" step="0.01" value="' + r.suggestedPrice + '" required></label><div class="actions"><button type="button" id="keep-price" class="quiet" ' + (r.currentPrice > 0 ? '' : 'disabled') + '>Conservar precio actual (' + money(r.currentPrice) + ')</button></div>' +
            '<label class="check"><input type="checkbox" name="completeReview" ' + (ready ? 'checked' : 'disabled') + '>Marcar producto como revisado</label><p id="apply-price-error" class="error" role="alert"></p><div class="dialog-actions"><button class="primary" ' + writable() + '>Guardar precio' + (state.view === 'queue' ? ' y continuar →' : '') + '</button></div></form>';
        $("#keep-price").onclick = () => { $('#apply-price-form input[name="sellingPrice"]').value = r.currentPrice; };
        $("#apply-price-form").onsubmit = e => { e.preventDefault(); run($('button.primary', e.target), async () => {
            const values = formValues(e.target); const price = Number(values.sellingPrice);
            if (price < r.landedCost && !confirm('Este precio es menor que el costo. ¿Quieres guardarlo de todos modos?')) return;
            await api('/api/pricing/apply', { method: 'POST', body: { productId: id, version: r.version, sellingPrice: price, completeReview: !!values.completeReview } });
            closeDialog(); await refresh(); toast(values.completeReview ? 'Precio guardado y producto revisado.' : 'Precio guardado; revisión pendiente.');
            if (state.view === 'queue' && values.completeReview) { const next = state.items.find(x => x.id !== id && x.review_pending && x.category_id && x.importer_id && x.cost > 0); if (next) await priceProduct(next.id); }
        }, $("#apply-price-error")); };
    }
    $("#recommend-form").onsubmit = e => { e.preventDefault(); run($('button', e.target), calculate, $("#recommend-error")); };
    // Changing drivers invalidates the old suggestion and prevents applying a stale calculation.
    $("#recommend-form").oninput = () => { calculation++; $("#price-result").innerHTML = ''; };
    if (p.category_id && p.cost > 0) await run($('#recommend-form button'), calculate, $("#recommend-error"));
}
api('/api/auth/me').then(enter).catch(e => { if (state.user) toast(errorText(e)); });
