const { createWorker } = require('tesseract.js');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const parser = require('../wwwroot/ocr-parser.js');

(async () => {
    const worker = await createWorker('spa+eng', 1, { langPath: path.resolve(__dirname,'../wwwroot/vendor/tesseract/lang'), cacheMethod:'none' });
    try {
        await worker.setParameters({ tessedit_pageseg_mode:'6', preserve_interword_spaces:'1', user_defined_dpi:'300' });
        const {data} = await worker.recognize(path.resolve(__dirname,'../TestResults/ocr/products.png'), {}, {text:true,blocks:true});
        const result = parser.parseOcr(data);
        fs.writeFileSync(path.resolve(__dirname,'../TestResults/ocr/recognized.json'), JSON.stringify({text:data.text,...result},null,2));
        assert.equal(result.rows.length,3);
        assert.deepEqual(result.rows.map(r=>r.productCode),['001234','009876','AB-123']);
        assert.deepEqual(result.rows.map(r=>r.name),['Coffee beans','White rice','Green tea']);
        assert.deepEqual(result.rows.map(r=>r.quantity),['2','3','1']);
        assert.deepEqual(result.rows.map(r=>r.price),['1250.5','800.25','450']);
        assert.equal(result.skipped.length,0);
        console.log('PASS: real local OCR -> four-column parsing, names, numeric values and leading-zero identifiers.');
    } finally { await worker.terminate(); }
})().catch(e => { console.error(e); process.exitCode=1; });
