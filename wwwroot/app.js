"use strict";
const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];
const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const money = value => new Intl.NumberFormat("es-CR", { style: "currency", currency: "CRC", maximumFractionDigits: 2 }).format(Number(value || 0));
const number = value => new Intl.NumberFormat("es-CR", { maximumFractionDigits: 3 }).format(Number(value || 0));
const state = { user: null, view: "merge", lookups: { categories: [], subcategories: [], importers: [] }, files: [null, null], merge: null, mergePage: 1,
    prefer: "second", acknowledged: false, query: "", category: "", subcategory: "", page: 1, onlyPending: true, selected: new Set(), items: [], total: 0 };
let toastTimer;
let dialogGeneration = 0;
let renderGeneration = 0;
function toast(message) { $("#toast").textContent = message; $("#toast").hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $("#toast").hidden = true, 6500); }
function errorText(e) { return e.message || "No se pudo completar la operación."; }
async function api(path, options = {}) {
    if (options.body && !(options.body instanceof FormData)) {
        options.headers = { "Content-Type": "application/json", ...options.headers };
        options.body = JSON.stringify(options.body);
    }
    const response = await fetch(path, options);
    if (!response.ok) {
        const message = await response.json().catch(() => ({}));
        if (response.status === 401 && !path.endsWith("/login")) {
            state.user = null; state.files = [null, null]; state.merge = null; state.items = []; state.selected.clear();
            if (typeof clearOcr === 'function') clearOcr();
            closeDialog(); $("#app").hidden = true; $("#login").hidden = false;
        }
        throw new Error(message.error || (response.status === 429 ? "Demasiados intentos. Espera un minuto." : "No se pudo completar la operación."));
    }
    if (options.download) return response.blob();
    return response.status === 204 ? null : response.json();
}
async function run(button, fn, errorTarget) {
    if (button?.disabled) return;
    if (button) { button.disabled = true; button.setAttribute("aria-busy", "true"); }
    if (errorTarget) errorTarget.textContent = "";
    try { await fn(); }
    catch (e) { if (errorTarget) errorTarget.textContent = errorText(e); else toast(errorText(e)); }
    finally { if (button) { button.disabled = false; button.removeAttribute("aria-busy"); } }
}
function writable() { return state.user?.canEdit ? "" : "disabled"; }
function formValues(form) { return Object.fromEntries(new FormData(form)); }
function nullableId(value) { return value ? Number(value) : null; }
function categoryPath(id, seen = new Set()) {
    if (!id || seen.has(Number(id))) return "";
    seen.add(Number(id));
    const c = [...state.lookups.categories,...state.lookups.subcategories].find(c => c.id === Number(id));
    if (!c) return "";
    const parent = categoryPath(c.parent_id, seen);
    return (parent ? parent + " / " + c.sequence_number + ' · ' : "") + c.name;
}
function subcategoryOptions(parentId, selected, blank = "Selecciona una subcategoría") {
    return '<option value="">' + esc(blank) + '</option>' + state.lookups.subcategories
        .filter(c => c.parent_id === Number(parentId)).sort((a,b) => a.sequence_number-b.sequence_number)
        .map(c => '<option value="' + c.id + '" ' + (c.id === Number(selected) ? 'selected' : '') + '>' + c.sequence_number + ' · ' + esc(c.name) + '</option>').join('');
}
function bindSubcategories(categorySelector, subcategorySelector, blank) {
    const category=$(categorySelector), subcategory=$(subcategorySelector);
    const update=() => { subcategory.innerHTML=subcategoryOptions(category.value,null,blank); subcategory.disabled=!category.value; };
    category.addEventListener('change',update);
    subcategory.disabled=!category.value;
}
function categoryOptions(selected, blank = "Sin categoría") {
    return '<option value="">' + esc(blank) + "</option>" + [...state.lookups.categories]
        .sort((a,b) => categoryPath(a.id).localeCompare(categoryPath(b.id), "es"))
        .map(c => '<option value="' + c.id + '" ' + (Number(selected) === c.id ? "selected" : "") + ">" + esc(categoryPath(c.id)) + (c.abbreviation ? " · " + esc(c.abbreviation) : "") + "</option>").join("");
}
function importerOptions(selected, blank = "Sin importador") {
    return '<option value="">' + esc(blank) + "</option>" + state.lookups.importers.map(i => '<option value="' + i.id + '" ' + (Number(selected) === i.id ? "selected" : "") + ">" + i.id + " · " + esc(i.name) + "</option>").join("");
}
async function lookups() { state.lookups = await api("/api/lookups"); $("#pending-count").textContent = state.lookups.pending; }
function openDialog(title, subtitle, html) {
    dialogGeneration++;
    $("#dialog-content").innerHTML = '<div class="dialog-head"><p class="eyebrow">PRIMAVERA</p><h2 id="dialog-title">' + esc(title) + "</h2>" + (subtitle ? "<p>" + esc(subtitle) + "</p>" : "") + "</div>" + html;
    if (!$("#dialog").open) $("#dialog").showModal();
}
function closeDialog() { dialogGeneration++; $("#dialog").close(); }
$("#dialog").addEventListener("cancel", () => dialogGeneration++);
$("#close-dialog").onclick = closeDialog;
$("#dialog").addEventListener("click", e => { if (e.target === $("#dialog")) { const r = $("#dialog").getBoundingClientRect(); if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) closeDialog(); } });
async function enter(user) { state.user = user; $("#login").hidden = true; $("#app").hidden = false; $("#user-name").textContent = user.displayName; await lookups(); await navigate(state.view); }
$("#login-form").onsubmit = e => { e.preventDefault(); run($('button[type="submit"]', e.target), async () => { await enter(await api("/api/auth/login", { method: "POST", body: formValues(e.target) })); e.target.reset(); }, $("#login-error")); };
$("#logout").onclick = e => run(e.currentTarget, async () => {
    await api("/api/auth/logout", { method: "POST" }); state.user = null; state.files = [null, null]; state.merge = null; state.items = []; state.selected.clear();
    if (typeof clearOcr === 'function') clearOcr();
    closeDialog(); $("#app").hidden = true; $("#login").hidden = false; $("#content").innerHTML = ""; $('#login-form input').focus();
});
$("#nav").onclick = e => { const button = e.target.closest("[data-view]"); if (button) run(button, () => navigate(button.dataset.view)); };
async function navigate(view) {
    state.view = view; state.selected.clear(); state.query = ""; state.category = ""; state.subcategory = ""; state.page = 1;
    $$("#nav button").forEach(b => { const active = b.dataset.view === view; b.classList.toggle("active", active); if (active) b.setAttribute("aria-current", "page"); else b.removeAttribute("aria-current"); });
    if (view === "merge") renderMerge();
    else if (view === "ocr") renderOcr();
    else await renderCatalog();
}
async function refresh() { await lookups(); if (state.view === "merge") renderMerge(); else if (state.view === "ocr") renderOcr(); else await renderCatalog(); }
function download(blob, filename) {
    const url = URL.createObjectURL(blob); const a = document.createElement("a"); a.href = url; a.download = filename; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
const mergeFields = [["barcode","Código de barras"],["name","Nombre"],["quantity","Cantidad"],["cost","Precio de compra"],["sellingPrice","Precio de venta"],["taxRate","Impuesto % (opcional)"]];
const normalize = s => String(s).normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLowerCase().replace(/[^a-z0-9]/g, "");
function guess(headers) {
    const aliases = { barcode: ["codigodebarras","codigobarras","barcode","barras","codigo","codigoproducto"], name: ["nombre","producto","descripcion","name","nombreproducto"], quantity: ["cantidad","cantidadexistente","existencia","existencias","stock","quantity"],
        cost: ["preciodecompra","preciocompra","cost","costo","costounitario","compra"], sellingPrice: ["preciodeventa","precioventa","sellingprice","venta","precio"], taxRate: ["impuesto","impuestos","impuesto13","impuestoporcentaje","iva","taxrate"] };
    const used = new Set(); const mapping = {};
    for (const [key] of mergeFields) { const index = headers.findIndex((h,i) => !used.has(i) && aliases[key].includes(normalize(h))); mapping[key] = index; if (index >= 0) used.add(index); }
    return mapping;
}
function filePanel(index) {
    const file = state.files[index];
    let extra = "";
    if (file) {
        const sheet = file.book.sheets[file.sheet]; const headers = sheet.rows[file.header] || [];
        extra = '<p class="file-name">' + esc(file.name) + " · " + number(Math.max(0, sheet.rows.length - file.header - 1)) + ' filas</p><div class="form-grid">' +
            '<label>Hoja<select data-sheet="' + index + '">' + file.book.sheets.map((s,i) => '<option value="' + i + '" ' + (i === file.sheet ? "selected" : "") + ">" + esc(s.name) + "</option>").join("") + "</select></label>" +
            '<label>Fila de encabezados<input type="number" min="1" max="' + sheet.rows.length + '" value="' + (file.header + 1) + '" data-header="' + index + '"></label>' +
            '<label class="wide">Decimales en celdas de texto<select data-decimal="' + index + '"><option value="." ' + (file.decimal === "." ? "selected" : "") + '>Punto: 1,250.50</option><option value="," ' + (file.decimal === "," ? "selected" : "") + '>Coma: 1.250,50</option></select></label></div><div class="mapping">' +
            mergeFields.map(([key,label]) => '<label>' + esc(label) + '<select data-map="' + key + '" data-index="' + index + '"><option value="-1">— ' + (key === "taxRate" ? "Sin impuesto" : "Selecciona columna") + " —</option>" +
                headers.map((h,i) => '<option value="' + i + '" ' + (file.mapping[key] === i ? "selected" : "") + ">" + (i + 1) + ". " + esc(h || "(Sin título)") + "</option>").join("") + "</select></label>").join("") + "</div>";
    }
    return '<section class="panel"><div class="upload-label"><span class="step">' + (index + 1) + '</span><h3>Excel ' + (index + 1) + '</h3></div><label>Seleccionar archivo<input type="file" accept=".xlsx,.csv" data-upload="' + index + '"></label>' + extra + "</section>";
}
function invalidateMerge() { state.merge = null; state.mergePage = 1; state.acknowledged = false; }
function renderMerge() {
    renderGeneration++;
    $("#content").innerHTML = '<div class="page-head"><div><p class="eyebrow">PASO 1 · COMBINA TUS ARCHIVOS</p><h1>Fusionar Excel</h1><p>Dos archivos, una sola lista. Los códigos repetidos quedan una sola vez y sus cantidades se suman, incluso dentro del mismo archivo.</p></div></div>' +
        '<div class="grid">' + filePanel(0) + filePanel(1) + '</div><section class="panel"><div class="form-grid"><label>Si difieren nombres, precios o impuestos<select id="prefer"><option value="second" ' + (state.prefer === "second" ? "selected" : "") + '>Usar el último dato (prioridad al Excel 2)</option><option value="first" ' + (state.prefer === "first" ? "selected" : "") + '>Usar el primer dato (prioridad al Excel 1)</option></select></label><div><p class="muted">Te mostraremos las diferencias antes de guardar. Puedes editar el resultado.</p></div></div>' +
        '<p class="inline-message">Archivos .xlsx o CSV UTF-8, hasta 10 MB y 20 000 productos por archivo. Conserva los códigos de barras como texto en Excel para no perder ceros. Un impuesto de 13 % se indica como 13.</p><button id="merge-button" class="primary" ' + (state.files.every(Boolean) ? "" : "disabled") + '>Fusionar y revisar →</button><p id="merge-error" class="error" role="alert"></p></section><div id="merge-result"></div>';
    $$("[data-upload]").forEach(input => input.onchange = () => run(input, async () => {
        const file = input.files[0]; if (!file) return;
        if (file.size > 10 * 1024 * 1024) throw new Error("El tamaño máximo por archivo es 10 MB.");
        const body = new FormData(); body.append("file", file);
        const book = await api("/api/excel/read", { method: "POST", body });
        const index = Number(input.dataset.upload);
        state.files[index] = { name: file.name, book, sheet: 0, header: 0, decimal: ".", mapping: guess(book.sheets[0].rows[0]) };
        invalidateMerge(); renderMerge();
    }, $("#merge-error")));
    $$("[data-sheet]").forEach(input => input.onchange = () => { const f = state.files[Number(input.dataset.sheet)]; f.sheet = Number(input.value); f.header = 0; f.mapping = guess(f.book.sheets[f.sheet].rows[0]); invalidateMerge(); renderMerge(); });
    $$("[data-header]").forEach(input => input.onchange = () => { const f = state.files[Number(input.dataset.header)]; f.header = Math.min(Math.max(0, Number(input.value) - 1), f.book.sheets[f.sheet].rows.length - 1); f.mapping = guess(f.book.sheets[f.sheet].rows[f.header]); invalidateMerge(); renderMerge(); });
    $$("[data-decimal]").forEach(input => input.onchange = () => { state.files[Number(input.dataset.decimal)].decimal = input.value; invalidateMerge(); renderMergeResult(); });
    $$("[data-map]").forEach(input => input.onchange = () => { state.files[Number(input.dataset.index)].mapping[input.dataset.map] = Number(input.value); invalidateMerge(); renderMergeResult(); });
    $("#prefer").onchange = e => { state.prefer = e.target.value; invalidateMerge(); renderMergeResult(); };
    $("#merge-button").onclick = e => run(e.currentTarget, async () => {
        function source(f) { const s = f.book.sheets[f.sheet]; return { rows: s.rows.slice(f.header + 1), numericCells: s.numericCells?.slice(f.header + 1), mapping: f.mapping, label: f.name + " / " + s.name, decimalSeparator: f.decimal, startRow: f.header + 2 }; }
        invalidateMerge(); state.merge = await api("/api/excel/merge", { method: "POST", body: { first: source(state.files[0]), second: source(state.files[1]), prefer: state.prefer } }); renderMergeResult(); $("#merge-result").scrollIntoView({ behavior: "smooth", block: "start" });
    }, $("#merge-error"));
    renderMergeResult();
}
function renderMergeResult() {
    const target = $("#merge-result"); if (!target) return;
    const result = state.merge; if (!result) { target.innerHTML = ""; return; }
    const start = (state.mergePage - 1) * 25; const pages = Math.max(1, Math.ceil(result.rows.length / 25));
    target.innerHTML = '<section class="panel"><div class="panel-title"><h2>Revisa el resultado</h2><span class="badge">Vista previa · aún no guardado</span></div><div class="summary">' +
        [[result.inputRows,"Filas originales"],[result.rows.length,"Productos únicos"],[result.repeatedRows,"Filas repetidas unificadas"],[result.rows.reduce((n,r)=>n+r.quantity,0),"Cantidad total"]].map(([n,l]) => '<div class="stat"><strong>' + number(n) + "</strong><span>" + l + "</span></div>").join("") + "</div>" +
        (result.issues.length ? '<div class="notice warning">Hay ' + result.issues.length + ' filas con errores. No se puede descargar ni guardar un resultado incompleto. Corrige los archivos o las columnas y vuelve a fusionar.<ul class="conflict-list">' + result.issues.map(i => "<li>" + esc(i.source) + ", fila " + i.row + ": " + esc(i.error) + "</li>").join("") + "</ul></div>" : "") +
        (result.conflicts.length ? '<details><summary>' + result.conflicts.length + ' diferencias de nombre, precio o impuesto</summary><ul class="conflict-list">' + result.conflicts.map(c => "<li><b>" + esc(c.barcode) + "</b> · " + esc(c.source) + ", fila " + c.row + "<br>" + c.details.map(esc).join("<br>") + "</li>").join("") + '</ul><label class="check"><input type="checkbox" id="acknowledge" ' + (state.acknowledged ? "checked" : "") + '>He revisado las diferencias y los datos finales.</label></details>' : "") +
        '<div class="table-wrap"><table><thead><tr><th>Código de barras</th><th>Nombre</th><th>Cantidad</th><th>Compra</th><th>Venta</th><th>Impuesto</th><th></th></tr></thead><tbody>' +
        result.rows.slice(start, start + 25).map((r,i) => '<tr><td class="code">' + esc(r.barcode) + '</td><td class="product-name">' + esc(r.name) + '</td><td class="number">' + number(r.quantity) + '</td><td class="number">' + money(r.cost) + '</td><td class="number">' + money(r.sellingPrice) + '</td><td class="number">' + number(r.taxRate) + '%</td><td><button class="small" data-merge-edit="' + (i + start) + '">Editar</button></td></tr>').join("") +
        '</tbody></table></div><div class="pager"><span>Página ' + state.mergePage + " de " + pages + '</span><div class="actions"><button id="merge-prev" ' + (state.mergePage <= 1 ? "disabled" : "") + '>Anterior</button><button id="merge-next" ' + (state.mergePage >= pages ? "disabled" : "") + '>Siguiente</button></div></div>' +
        '<div class="notice">Al guardar, los nuevos pasan a «Por categorizar». En los existentes se conservan la categoría, el importador y el código interno. Su cantidad se reemplaza por este total; los productos ausentes no se eliminan.</div>' +
        '<div class="result-actions"><button id="apply-merge" class="primary" ' + writable() + '>Guardar y categorizar →</button><button id="download-merge">Descargar Excel fusionado</button></div><p id="result-error" class="error" role="alert"></p></section>';
    const blocked = result.issues.length > 0 || !result.rows.length || (result.conflicts.length > 0 && !state.acknowledged);
    $("#apply-merge").disabled = blocked || !state.user.canEdit; $("#download-merge").disabled = blocked;
    if ($("#acknowledge")) $("#acknowledge").onchange = e => { state.acknowledged = e.target.checked; renderMergeResult(); };
    $("#merge-prev").onclick = () => { state.mergePage--; renderMergeResult(); };
    $("#merge-next").onclick = () => { state.mergePage++; renderMergeResult(); };
    $$("[data-merge-edit]").forEach(b => b.onclick = () => editMerged(Number(b.dataset.mergeEdit)));
    $("#download-merge").onclick = e => run(e.currentTarget, async () => download(await api("/api/excel/export", { method: "POST", body: { rows: result.rows }, download: true }), "productos-fusionados.xlsx"), $("#result-error"));
    $("#apply-merge").onclick = e => run(e.currentTarget, async () => {
        if (!confirm("¿Guardar " + result.rows.length + " productos? Las cantidades y los precios existentes se actualizarán con los de esta vista previa.")) return;
        const saved = await api("/api/excel/apply", { method: "POST", body: { rows: result.rows } });
        toast(saved.created + " nuevos pendientes · " + saved.updated + " actualizados.");
        state.merge = null; await lookups(); await navigate("queue");
    }, $("#result-error"));
}
function editMerged(index) {
    const p = state.merge.rows[index];
    openDialog("Editar fila fusionada", "Los archivos originales no se modifican.", '<form id="merged-form"><div class="form-grid">' +
        inputField("Código de barras","barcode",p.barcode) + inputField("Nombre","name",p.name) +
        inputField("Cantidad","quantity",p.quantity,"number","0.001") + inputField("Compra","cost",p.cost,"number") +
        inputField("Venta","sellingPrice",p.sellingPrice,"number") + inputField("Impuesto %","taxRate",p.taxRate,"number") +
        '</div><p class="error" id="merged-error"></p><div class="dialog-actions"><button class="primary">Guardar fila</button></div></form>');
    $("#merged-form").onsubmit = e => { e.preventDefault(); const v = formValues(e.target);
        if (state.merge.rows.some((r,i) => i !== index && r.barcode.toLowerCase() === v.barcode.trim().toLowerCase())) { $("#merged-error").textContent = "Este código ya aparece en otra fila."; return; }
        state.merge.rows[index] = { ...p, barcode: v.barcode.trim(), name: v.name.trim(), quantity: Number(v.quantity), cost: Number(v.cost), sellingPrice: Number(v.sellingPrice), taxRate: Number(v.taxRate) };
        closeDialog(); renderMergeResult();
    };
}
function inputField(label, name, value, type = "text", step = "0.01") {
    return '<label>' + esc(label) + '<input name="' + name + '" type="' + type + '" value="' + esc(value) + '" ' + (type === "number" ? 'min="0" max="1000000000" step="' + step + '"' : 'maxlength="' + (name === "name" ? 160 : 100) + '"') + ' required></label>';
}
