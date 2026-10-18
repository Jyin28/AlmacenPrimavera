/* Pure parsing logic, shared by the browser and regression tests. */
(function (root, factory) {
    const parser = factory();
    if (typeof module === 'object' && module.exports) module.exports = parser;
    else root.ProductOcrParser = parser;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';
    const fields = ['productCode', 'name', 'quantity', 'price'];
    const normalized = value => String(value || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
    function amount(value, separator = '.', precision = 2) {
        let text = String(value ?? '').trim().replace(/(?:CRC|USD|US\$|₡|\$)/gi, '').replace(/[\s\u00a0]/g, '');
        if (!text) return { value: '', error: 'Dato sin leer; no se convierte en cero.' };
        const pattern = separator === ',' ? /^\+?(?:\d+|\d{1,3}(?:\.\d{3})+)(?:,\d+)?$/ : /^\+?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?$/;
        if (!pattern.test(text)) return { value: '', error: 'Número dudoso: «' + value + '». Revisa separadores y letras confundidas con dígitos.' };
        text = separator === ',' ? text.replaceAll('.', '').replace(',', '.') : text.replaceAll(',', '');
        const fraction = (text.split('.')[1] || '').replace(/0+$/, '');
        if (fraction.length > precision || !Number.isFinite(Number(text)) || Number(text) > 1000000000)
            return { value: '', error: 'Número fuera de rango o con demasiados decimales: «' + value + '».' };
        return { value: String(Number(text)), error: null };
    }
    function header(text) {
        const s = normalized(text);
        return [/(?:\bcode\b|\bcodigo\b|\bsku\b|\bbarcode\b)/, /(?:\bname\b|\bnombre\b|\bdescripcion\b)/,
            /(?:\bquantity\b|\bcantidad\b|\bqty\b|\bcant\b)/, /(?:\bprice\b|\bprecio\b)/].filter(re => re.test(s)).length >= 3;
    }
    function parseLine(line, options) {
        const text = line.text.trim();
        if (!text || header(text)) return { header: true };
        if (/^(?:total|subtotal|iva|impuesto|tax|fecha|date|pagina|page|factura|invoice|telefono|tel)(?:\s|:|$)/i.test(normalized(text)))
            return { skipped: 'Posible encabezado, total o dato ajeno al producto.' };
        const delimited = /[|\t]/.test(line.text);
        // Preserve empty explicit cells: a missing quantity must not shift the price.
        let cells = line.cells || (delimited ? line.text.split(/\s*\|\s*|\t/).map(s => s.trim()) : text.split(/\s{2,}/).map(s => s.trim()));
        const notes = [];
        if (cells.length !== 4) {
            if (cells.length > 4) return { skipped: 'Hay más de cuatro columnas. Indica los cuatro campos usando | en el texto editable.' };
            if (delimited) return { skipped: 'Faltan columnas. Usa cuatro campos separados con |; deja vacío cualquier dato sin leer.' };
            const tokens = text.split(/\s+/);
            const nameIndex = options.order.indexOf('name');
            if (tokens.length < 4) return { skipped: 'No se pudieron separar los cuatro campos.' };
            const trailing = 3 - nameIndex;
            cells = [...tokens.slice(0, nameIndex), tokens.slice(nameIndex, trailing ? -trailing : undefined).join(' '), ...tokens.slice(tokens.length - trailing)];
            if (!trailing) cells = [...tokens.slice(0, nameIndex), tokens.slice(nameIndex).join(' ')];
            notes.push('Separación estimada por espacios; compara los cuatro campos con la imagen.');
        }
        const values = Object.fromEntries(options.order.map((field, i) => [field, cells[i]]));
        if (!values.productCode || !/^[\p{L}\p{N}][\p{L}\p{N}._/\-]*$/u.test(values.productCode))
            return { skipped: 'No se identificó un código de producto fiable.' };
        const q = amount(values.quantity, options.decimalSeparator, 3);
        const p = amount(values.price, options.decimalSeparator, 2);
        if (q.error) notes.push('Quantity: ' + q.error);
        if (p.error) notes.push('Price: ' + p.error);
        if (line.confidence !== undefined && line.confidence < 80) notes.push('Lectura OCR poco segura; revisa esta fila con cuidado.');
        // Never repair O/0, I/1, decimal punctuation, codes, or unreadable values by guessing.
        return { row: { productCode: values.productCode, name: values.name, quantity: q.value, price: p.value, source: text, notes } };
    }
    function parseLines(lines, options = {}) {
        const settings = { order: options.order || fields, decimalSeparator: options.decimalSeparator || '.' };
        if (settings.order.length !== 4 || new Set(settings.order).size !== 4 || fields.some(f => !settings.order.includes(f)))
            throw new Error('Elige cada columna una sola vez.');
        if (!['.', ','].includes(settings.decimalSeparator)) throw new Error('Separador decimal no válido.');
        if (lines.length > 5000) throw new Error('La imagen contiene demasiadas líneas. Divide el documento en imágenes más pequeñas.');
        const rows = [], skipped = [];
        lines.forEach((line, index) => {
            const parsed = parseLine(line, settings);
            if (parsed.row) rows.push(parsed.row);
            else if (parsed.skipped) skipped.push({ line: index + 1, text: line.text, reason: parsed.skipped });
        });
        return { rows, skipped };
    }
    function parseText(text, options) {
        return parseLines(String(text).split(/\r?\n/).map(text => ({ text })), options);
    }
    function parseOcr(data, options) {
        const words = [];
        for (const block of data.blocks || []) for (const paragraph of block.paragraphs || [])
            for (const line of paragraph.lines || []) for (const word of line.words || [])
                if (word.text?.trim() && word.bbox) words.push(word);
        if (!words.length) return parseText(data.text || '', options);
        const groups = [];
        const height = w => Math.max(1, w.bbox.y1 - w.bbox.y0);
        words.sort((a,b) => (a.bbox.y0 + a.bbox.y1) / 2 - (b.bbox.y0 + b.bbox.y1) / 2 || a.bbox.x0 - b.bbox.x0);
        for (const word of words) {
            const cy = (word.bbox.y0 + word.bbox.y1) / 2;
            const group = groups.slice(-4).find(g => Math.abs(g.cy - cy) < Math.max(g.height, height(word)) * .55);
            if (group) { group.words.push(word); group.cy = group.words.reduce((n,w) => n + (w.bbox.y0+w.bbox.y1)/2, 0)/group.words.length; }
            else groups.push({ cy, height: height(word), words: [word] });
        }
        const lines = groups.sort((a,b) => a.cy - b.cy).map(group => {
            const sorted = group.words.sort((a,b) => a.bbox.x0 - b.bbox.x0);
            const sizes = sorted.map(w => (w.bbox.x1 - w.bbox.x0) / Math.max(1,w.text.length)).sort((a,b) => a-b);
            const gap = Math.max(group.height, sizes[Math.floor(sizes.length/2)] * 2.8);
            const cells = []; let cell = '';
            sorted.forEach((word,i) => {
                if (i && word.bbox.x0 - sorted[i-1].bbox.x1 > gap) { cells.push(cell); cell = ''; }
                cell += (cell ? ' ' : '') + word.text;
            });
            if (cell) cells.push(cell);
            return { text: sorted.map(w => w.text).join(' '), cells: cells.length >= 4 ? cells : null,
                confidence: sorted.reduce((n,w) => n + (w.confidence ?? 0),0)/sorted.length };
        });
        return parseLines(lines, options);
    }
    function validate(row) {
        const errors = [];
        if (!row.productCode?.trim() || row.productCode.trim().length > 100 || /[\u0000-\u001f]/.test(row.productCode)) errors.push('Product Code requerido (máximo 100 caracteres).');
        if (!row.name?.trim() || row.name.trim().length > 300 || /[\u0000-\u001f]/.test(row.name)) errors.push('Name requerido (máximo 300 caracteres).');
        for (const [field, precision] of [['quantity',3],['price',2]]) {
            const text = String(row[field] ?? '').trim();
            if (!/^\d+(?:\.\d+)?$/.test(text) || text.split('.')[1]?.replace(/0+$/,'').length > precision || !Number.isFinite(Number(text)) || Number(text)>1000000000)
                errors.push(field === 'quantity' ? 'Quantity: número no negativo, hasta 3 decimales.' : 'Price: número no negativo, hasta 2 decimales.');
        }
        return errors;
    }
    return { fields, amount, parseText, parseOcr, validate };
});
