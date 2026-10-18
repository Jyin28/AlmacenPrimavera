'use strict';
const ocrDraft = { image: null, name: '', rotation: 0, rows: [], skipped: [], text: '', busy: false, worker: null, run: 0,
    progress: 0, status: '', error: '', reviewed: false, edited: false, needsReparse: false, page: 1,
    order: ['productCode','name','quantity','price'], decimalSeparator: '.', language: 'spa+eng' };
const ocrLabels = { productCode: 'Product Code', name: 'Name', quantity: 'Quantity', price: 'Price' };
let ocrEnginePromise;
function clearOcr() {
    ocrDraft.run++;
    ocrDraft.worker?.terminate().catch(() => {});
    if (ocrDraft.image) URL.revokeObjectURL(ocrDraft.image);
    Object.assign(ocrDraft, { image: null, name: '', rotation: 0, rows: [], skipped: [], text: '', busy: false,
        worker: null, progress: 0, status: '', error: '', reviewed: false, edited: false, needsReparse: false, page: 1 });
}
function invalidateOcrParsing() {
    ocrDraft.reviewed = false;
    ocrDraft.needsReparse = true;
    refreshOcrExport();
}
function renderOcr() {
    renderGeneration++;
    $('#content').innerHTML = '<div class="page-head"><div><p class="eyebrow">DE LA FOTO A TU HOJA DE CÁLCULO</p><h1>Imagen a Excel</h1><p>Lee una foto, revisa los productos y descarga un Excel con solo cuatro columnas: Product Code, Name, Quantity y Price.</p></div></div>' +
        '<div class="ocr-workspace"><section class="panel"><h3>1. Selecciona la imagen</h3><label>Foto o captura (JPG, PNG o WebP)<input id="ocr-file" type="file" accept="image/jpeg,image/png,image/webp" ' + (ocrDraft.busy ? 'disabled' : '') + '></label><p class="file-name">' + esc(ocrDraft.name) + '</p>' +
        '<canvas id="ocr-preview" class="ocr-image" aria-label="Imagen original para comparar los productos" ' + (ocrDraft.image ? '' : 'hidden') + '></canvas>' +
        (!ocrDraft.image ? '<div class="empty"><h3>Una foto clara hace la diferencia</h3><p>Usa una imagen de frente, bien iluminada, con las cuatro columnas visibles. Máximo 10 MB y 20 megapíxeles.</p></div>' : '') +
        '<div class="actions"><button id="ocr-rotate" ' + (!ocrDraft.image || ocrDraft.busy ? 'disabled' : '') + '>Girar 90°</button><button id="ocr-clear" ' + (ocrDraft.busy ? 'disabled' : '') + '>Limpiar</button></div><p class="inline-message">La foto se procesa en este navegador. No se envía a un servicio externo ni se guarda en el catálogo. Solo los datos revisados se envían a esta app para crear el Excel.</p></section>' +
        '<section class="panel"><h3>2. Lee y organiza los productos</h3><div class="form-grid"><label>Idioma de la imagen<select id="ocr-language" ' + (ocrDraft.busy ? 'disabled' : '') + '><option value="spa+eng">Español e inglés</option><option value="spa">Español</option><option value="eng">Inglés</option></select></label><label>Decimales de la imagen<select id="ocr-decimal" ' + (ocrDraft.busy ? 'disabled' : '') + '><option value=".">Punto: 1,250.50</option><option value=",">Coma: 1.250,50</option></select></label></div>' +
        '<p class="muted">Orden de las columnas en la imagen, de izquierda a derecha:</p><div class="ocr-column-order">' + ocrDraft.order.map((field,i) => '<label>Columna ' + (i+1) + '<select data-ocr-order="' + i + '" ' + (ocrDraft.busy ? 'disabled' : '') + '>' + Object.entries(ocrLabels).map(([key,label]) => '<option value="' + key + '" ' + (key===field ? 'selected' : '') + '>' + label + '</option>').join('') + '</select></label>').join('') + '</div>' +
        '<p class="inline-message">Price será el precio que aparece en la imagen. No se calcula ni se reemplaza por el total de la factura. Si tu documento tiene más columnas, usa el texto editable para elegir estos cuatro campos.</p>' +
        '<div class="actions"><button id="ocr-start" class="primary" ' + (!ocrDraft.image || ocrDraft.busy ? 'disabled' : '') + '>Leer imagen</button><button id="ocr-cancel" ' + (ocrDraft.busy ? '' : 'hidden') + '>Cancelar</button></div>' +
        '<div id="ocr-progress-wrap" ' + (ocrDraft.busy ? '' : 'hidden') + '><progress id="ocr-progress" max="1" value="' + ocrDraft.progress + '"></progress><p id="ocr-status" role="status" aria-live="polite">' + esc(ocrDraft.status) + '</p></div><p id="ocr-error" class="error" role="alert">' + esc(ocrDraft.error) + '</p>' +
        '<details id="ocr-raw"><summary>Texto leído y corrección manual</summary><p class="inline-message">También puedes pegar texto. Una línea por producto, separada con |. Ejemplo: 00123 | Café molido | 2 | 1250.50. Usa el orden de columnas elegido arriba.</p><label>Texto de la imagen<textarea id="ocr-text" rows="10" spellcheck="false" ' + (ocrDraft.busy ? 'disabled' : '') + '>' + esc(ocrDraft.text) + '</textarea></label><button id="ocr-reparse" ' + (ocrDraft.busy ? 'disabled' : '') + '>Convertir este texto a filas</button></details></section></div><section id="ocr-results"></section>';
    $('#ocr-language').value = ocrDraft.language; $('#ocr-decimal').value = ocrDraft.decimalSeparator;
    $('#ocr-language').onchange = e => ocrDraft.language = e.target.value;
    $('#ocr-decimal').onchange = e => { ocrDraft.decimalSeparator = e.target.value; invalidateOcrParsing(); };
    $$('[data-ocr-order]').forEach(select => select.onchange = () => { ocrDraft.order[Number(select.dataset.ocrOrder)] = select.value; invalidateOcrParsing(); });
    $('#ocr-text').oninput = e => { ocrDraft.text = e.target.value; invalidateOcrParsing(); };
    $('#ocr-file').onchange = e => run(e.target, async () => {
        const file = e.target.files[0]; if (!file) return;
        if (!['image/png','image/jpeg','image/webp'].includes(file.type)) throw new Error('Usa una imagen JPG, PNG o WebP. PDF y HEIC no se admiten.');
        if (file.size === 0 || file.size > 10 * 1024 * 1024) throw new Error('La imagen debe tener contenido y pesar como máximo 10 MB.');
        const loadRun = ocrDraft.run, url = URL.createObjectURL(file);
        try { const image = await loadOcrImage(url); if (image.naturalWidth * image.naturalHeight > 20000000) throw new Error('La imagen supera 20 megapíxeles. Reduce su tamaño antes de continuar.'); }
        catch (e) { URL.revokeObjectURL(url); throw e; }
        if (loadRun !== ocrDraft.run || !state.user) { URL.revokeObjectURL(url); return; }
        if (ocrDraft.rows.length && !confirm('¿Reemplazar la imagen y descartar las filas de esta lectura?')) { URL.revokeObjectURL(url); return; }
        clearOcr(); ocrDraft.image = url; ocrDraft.name = file.name; if (state.view === 'ocr') renderOcr();
    }, $('#ocr-error'));
    $('#ocr-rotate').onclick = () => { ocrDraft.rotation = (ocrDraft.rotation + 90) % 360; ocrDraft.reviewed = false; refreshOcrExport(); paintOcrPreview().catch(e => toast(errorText(e))); };
    $('#ocr-clear').onclick = () => { if (ocrDraft.rows.length && !confirm('¿Descartar la imagen y las filas de esta lectura?')) return; clearOcr(); renderOcr(); };
    $('#ocr-start').onclick = () => startOcr();
    $('#ocr-cancel').onclick = () => { ocrDraft.run++; ocrDraft.worker?.terminate().catch(() => {}); ocrDraft.worker = null; ocrDraft.busy = false; ocrDraft.status = 'Lectura cancelada.'; renderOcr(); };
    $('#ocr-reparse').onclick = e => run(e.currentTarget, async () => {
        if (ocrDraft.edited && !confirm('¿Reemplazar las filas editadas usando este texto?')) return;
        applyOcrResult(ProductOcrParser.parseText(ocrDraft.text, ocrDraft)); renderOcrResults();
    }, $('#ocr-error'));
    renderOcrResults();
    if (ocrDraft.image) paintOcrPreview().catch(e => { ocrDraft.error = errorText(e); if ($('#ocr-error')) $('#ocr-error').textContent = ocrDraft.error; });
}
function loadOcrImage(url) {
    return new Promise((resolve,reject) => { const image = new Image(); image.onload = () => resolve(image); image.onerror = () => reject(new Error('No se pudo abrir la imagen. Comprueba que el archivo no esté dañado.')); image.src = url; });
}
async function ocrCanvas() {
    const image = await loadOcrImage(ocrDraft.image);
    const scale = Math.min(1, 3200 / Math.max(image.naturalWidth, image.naturalHeight));
    const width = Math.round(image.naturalWidth * scale), height = Math.round(image.naturalHeight * scale);
    const canvas = document.createElement('canvas'), swap = ocrDraft.rotation % 180 !== 0;
    canvas.width = swap ? height : width; canvas.height = swap ? width : height;
    const context = canvas.getContext('2d');
    context.fillStyle = '#fff'; context.fillRect(0,0,canvas.width,canvas.height);
    context.translate(canvas.width/2,canvas.height/2); context.rotate(ocrDraft.rotation*Math.PI/180);
    context.drawImage(image,-width/2,-height/2,width,height);
    return canvas;
}
async function paintOcrPreview() {
    const preview = $('#ocr-preview'); const canvas = await ocrCanvas();
    if (!preview || preview !== $('#ocr-preview')) return;
    preview.width = canvas.width; preview.height = canvas.height; preview.getContext('2d').drawImage(canvas,0,0);
}
function loadOcrEngine() {
    if (window.Tesseract) return Promise.resolve(window.Tesseract);
    if (!ocrEnginePromise) ocrEnginePromise = new Promise((resolve,reject) => {
        const script = document.createElement('script'); script.src = '/vendor/tesseract/tesseract.min.js';
        script.onload = () => resolve(window.Tesseract);
        script.onerror = () => { script.remove(); ocrEnginePromise = null; reject(new Error('No se pudo cargar el motor OCR local. Revisa que estén instalados los archivos de OCR.')); };
        document.head.append(script);
    });
    return ocrEnginePromise;
}
function applyOcrResult(parsed) {
    ocrDraft.rows = parsed.rows; ocrDraft.skipped = parsed.skipped; ocrDraft.reviewed = false; ocrDraft.edited = false; ocrDraft.needsReparse = false; ocrDraft.page = 1;
}
async function startOcr() {
    if (ocrDraft.busy || !ocrDraft.image) return;
    if (ocrDraft.edited && !confirm('¿Leer otra vez y reemplazar las filas editadas?')) return;
    try { ProductOcrParser.parseText('', ocrDraft); } catch (e) { ocrDraft.error = errorText(e); renderOcr(); return; }
    const runId = ++ocrDraft.run;
    ocrDraft.busy = true; ocrDraft.reviewed = false; ocrDraft.error = ''; ocrDraft.progress = 0; ocrDraft.status = 'Preparando el motor OCR local…'; renderOcr();
    let worker, timer, rejectWorkerError;
    const workerError = new Promise((_,reject) => { rejectWorkerError = reject; });
    const active = () => runId === ocrDraft.run;
    const job = async () => {
        const engine = await loadOcrEngine(); if (!active()) return;
        worker = await engine.createWorker(ocrDraft.language, 1, {
            workerPath: '/vendor/tesseract/worker.min.js', corePath: '/vendor/tesseract/core', langPath: '/vendor/tesseract/lang',
            workerBlobURL: false, cacheMethod: 'none', gzip: true, errorHandler: rejectWorkerError,
            logger: message => {
                if (!active()) return;
                ocrDraft.progress = message.progress || 0;
                ocrDraft.status = message.status === 'recognizing text' ? 'Leyendo texto: ' + Math.round(ocrDraft.progress*100) + '%' : 'Preparando idiomas y motor OCR…';
                if (state.view === 'ocr' && $('#ocr-progress')) { $('#ocr-progress').value = ocrDraft.progress; $('#ocr-status').textContent = ocrDraft.status; }
            }
        });
        if (!active()) { await worker.terminate(); return; }
        ocrDraft.worker = worker;
        await worker.setParameters({ tessedit_pageseg_mode: '6', preserve_interword_spaces: '1', user_defined_dpi: '300' });
        const canvas = await ocrCanvas();
        const { data } = await worker.recognize(canvas, {}, { text: true, blocks: true });
        if (!active()) return;
        ocrDraft.text = data.text || '';
        applyOcrResult(ProductOcrParser.parseOcr(data, ocrDraft));
        if (!ocrDraft.text.trim()) throw new Error('No se encontró texto. Prueba una foto más clara, de frente y con letras más grandes.');
        ocrDraft.status = 'Lectura terminada. Compara los resultados con la imagen.';
    };
    try {
        await Promise.race([job(), workerError, new Promise((_,reject) => { timer = setTimeout(() => reject(new Error('La lectura tardó demasiado. Prueba con una imagen más pequeña.')), 120000); })]);
    } catch (e) { if (active()) ocrDraft.error = 'No se completó la lectura: ' + errorText(e); }
    finally {
        clearTimeout(timer);
        if (active()) { ocrDraft.run++; ocrDraft.busy = false; ocrDraft.worker = null; if (state.view === 'ocr' && state.user) renderOcr(); }
        if (worker) await worker.terminate().catch(() => {});
    }
}
function renderOcrResults() {
    const target = $('#ocr-results'); if (!target) return;
    const rows = ocrDraft.rows, pages = Math.max(1,Math.ceil(rows.length/30));
    ocrDraft.page = Math.min(ocrDraft.page,pages); const start = (ocrDraft.page-1)*30;
    target.innerHTML = '<div class="panel"><div class="panel-title"><h3>3. Revisa y descarga</h3><span class="badge">' + rows.length + ' filas</span></div>' +
        '<p class="muted">El OCR puede confundir 0/O, 1/I y separadores decimales. Revisa cada código y número. Las filas repetidas se conservan, no se fusionan.</p>' +
        (ocrDraft.skipped.length ? '<details class="ocr-skipped"><summary>' + ocrDraft.skipped.length + ' líneas no convertidas: revísalas para no omitir productos</summary><div class="manage-list">' + ocrDraft.skipped.map((line,i) => '<div class="manage-row"><div><b>' + esc(line.text) + '</b><small>' + esc(line.reason) + '</small></div><button data-ocr-recover="' + i + '" ' + (ocrDraft.busy?'disabled':'') + '>Agregar como fila</button></div>').join('') + '</div></details>' : '') +
        (rows.length ? '<div class="table-wrap"><table class="ocr-table"><thead><tr><th>Product Code</th><th>Name</th><th>Quantity</th><th>Price</th><th>Revisión</th></tr></thead><tbody>' + rows.slice(start,start+30).map((r,n) => {
            const i=n+start;
            return '<tr>' + ['productCode','name','quantity','price'].map(field => '<td><input data-ocr-row="' + i + '" data-ocr-field="' + field + '" aria-label="' + ocrLabels[field] + ', fila ' + (i+1) + '" value="' + esc(r[field]) + '" ' + (['quantity','price'].includes(field) ? 'type="number" min="0" max="1000000000" step="' + (field==='quantity'?'0.001':'0.01') + '"' : 'type="text" maxlength="' + (field==='name'?300:100) + '"') + (ocrDraft.busy?' disabled':'') + '></td>').join('') +
                '<td><details><summary>Ver origen</summary><p>' + esc(r.source || 'Fila manual') + '</p>' + (r.notes||[]).map(note=>'<p class="inline-message">'+esc(note)+'</p>').join('') + '</details><button class="small" data-ocr-delete="' + i + '" ' + (ocrDraft.busy?'disabled':'') + '>Quitar fila</button></td></tr>';
        }).join('') + '</tbody></table></div>' : '<div class="empty"><h3>Todavía no hay filas</h3><p>Lee una imagen, convierte el texto editable o agrega una fila manual.</p></div>') +
        '<div class="pager"><button id="ocr-add" ' + (ocrDraft.busy?'disabled':'') + '>＋ Agregar fila</button><span>Página ' + ocrDraft.page + ' de ' + pages + '</span><div class="actions"><button id="ocr-prev" ' + (ocrDraft.page===1?'disabled':'') + '>Anterior</button><button id="ocr-next" ' + (ocrDraft.page===pages?'disabled':'') + '>Siguiente</button></div></div>' +
        '<p id="ocr-validation" class="error" role="status"></p><label class="check"><input id="ocr-reviewed" type="checkbox" ' + (ocrDraft.reviewed?'checked':'') + (ocrDraft.busy?' disabled':'') + '>Comparé las cuatro columnas con la imagen y revisé las líneas no convertidas.</label>' +
        '<div class="result-actions"><button id="ocr-download" class="primary">Descargar Excel (.xlsx)</button><span class="inline-message">Solo Product Code · Name · Quantity · Price. No modifica inventario ni precios.</span></div><p id="ocr-export-error" class="error" role="alert"></p></div>';
    $$('[data-ocr-row]').forEach(input => input.oninput = () => { ocrDraft.rows[Number(input.dataset.ocrRow)][input.dataset.ocrField] = input.value; ocrDraft.reviewed=false; ocrDraft.edited=true; refreshOcrExport(); });
    $$('[data-ocr-delete]').forEach(button => button.onclick = () => {
        if (!confirm('¿Quitar esta fila del Excel?')) return;
        const removed=ocrDraft.rows.splice(Number(button.dataset.ocrDelete),1)[0];
        ocrDraft.skipped.push({text:removed.source || [removed.productCode,removed.name,removed.quantity,removed.price].join(' | '),reason:'Fila quitada manualmente. Puedes recuperarla.'});
        ocrDraft.reviewed=false; ocrDraft.edited=true; renderOcrResults();
    });
    $$('[data-ocr-recover]').forEach(button => button.onclick = () => { const line=ocrDraft.skipped[Number(button.dataset.ocrRecover)]; if (addOcrRow(line.text)) { ocrDraft.skipped.splice(Number(button.dataset.ocrRecover),1); renderOcrResults(); } });
    $('#ocr-add').onclick = () => addOcrRow('');
    $('#ocr-prev').onclick = () => { ocrDraft.page--; renderOcrResults(); };
    $('#ocr-next').onclick = () => { ocrDraft.page++; renderOcrResults(); };
    $('#ocr-reviewed').onchange = e => { ocrDraft.reviewed=e.target.checked; refreshOcrExport(); };
    $('#ocr-download').onclick = e => run(e.currentTarget, async () => {
        if (ocrDraft.busy || ocrDraft.needsReparse || !ocrDraft.rows.length || !ocrDraft.reviewed || ocrDraft.rows.some(r=>ProductOcrParser.validate(r).length)) throw new Error('Convierte el texto actualizado, completa y revisa todas las filas.');
        const body={reviewed:true,rows:ocrDraft.rows.map(r=>({productCode:r.productCode.trim(),name:r.name.trim(),quantity:Number(r.quantity),price:Number(r.price)}))};
        download(await api('/api/ocr/export',{method:'POST',body,download:true}),'productos-desde-imagen.xlsx');
    },$('#ocr-export-error'));
    refreshOcrExport();
}
function addOcrRow(source) {
    if (ocrDraft.rows.length>=5000) { toast('El máximo es 5000 productos por archivo.'); return false; }
    ocrDraft.rows.push({productCode:'',name:'',quantity:'',price:'',source,notes:[]});
    ocrDraft.reviewed=false; ocrDraft.edited=true; ocrDraft.page=Math.ceil(ocrDraft.rows.length/30); renderOcrResults(); return true;
}
function refreshOcrExport() {
    if (!$('#ocr-download')) return;
    const invalid=ocrDraft.rows.map((row,i)=>({row:i+1,errors:ProductOcrParser.validate(row)})).filter(r=>r.errors.length);
    $('#ocr-validation').textContent=ocrDraft.needsReparse ? 'Cambiaste el texto o sus columnas. Pulsa «Convertir este texto a filas» (en Texto leído y corrección manual) o vuelve a leer la imagen antes de descargar.' : invalid.length ? invalid.length+' filas por corregir. '+invalid.slice(0,4).map(r=>'Fila '+r.row+': '+r.errors.join(' ')).join(' ') : '';
    $('#ocr-reviewed').checked=ocrDraft.reviewed;
    $('#ocr-reviewed').disabled=ocrDraft.busy || ocrDraft.needsReparse;
    $('#ocr-download').disabled=ocrDraft.busy || ocrDraft.needsReparse || !ocrDraft.rows.length || invalid.length>0 || !ocrDraft.reviewed;
}
