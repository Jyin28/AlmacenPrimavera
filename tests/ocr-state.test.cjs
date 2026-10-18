const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const parser = require('../wwwroot/ocr-parser.js');

// State-machine tests only; these do not replace a real browser/UI test.
function setup() {
    const nodes = new Map(['#ocr-download','#ocr-reviewed','#ocr-validation'].map(id => [id,{}]));
    const revoked = [];
    const context = vm.createContext({ ProductOcrParser: parser, $: id => nodes.get(id), URL: {revokeObjectURL: url => revoked.push(url)} });
    vm.runInContext(fs.readFileSync(path.join(__dirname,'../wwwroot/ocr.js'),'utf8') +
        '\nthis.ocrTest = {draft: ocrDraft, applyOcrResult, refreshOcrExport, invalidateOcrParsing, clearOcr};',context);
    return { ...context.ocrTest, nodes, revoked };
}
const sample = () => parser.parseText('001 | Coffee | 2 | 1250.50');

test('download requires valid rows and explicit review', () => {
    const s=setup(); s.refreshOcrExport(); assert.equal(s.nodes.get('#ocr-download').disabled,true);
    s.applyOcrResult(sample()); s.refreshOcrExport(); assert.equal(s.nodes.get('#ocr-download').disabled,true);
    s.draft.reviewed=true; s.refreshOcrExport(); assert.equal(s.nodes.get('#ocr-download').disabled,false);
    s.draft.rows[0].quantity=''; s.refreshOcrExport(); assert.equal(s.nodes.get('#ocr-download').disabled,true);
});
test('text or mapping changes block export until reparsed and reviewed again', () => {
    const s=setup(); s.applyOcrResult(sample()); s.draft.reviewed=true;
    s.invalidateOcrParsing(); assert.equal(s.draft.reviewed,false);
    assert.equal(s.nodes.get('#ocr-reviewed').disabled,true);
    s.draft.reviewed=true; s.refreshOcrExport(); assert.equal(s.nodes.get('#ocr-download').disabled,true);
    s.applyOcrResult(sample()); assert.equal(s.draft.needsReparse,false);
    s.draft.reviewed=true; s.refreshOcrExport(); assert.equal(s.nodes.get('#ocr-download').disabled,false);
});
test('OCR busy state blocks downloads even with reviewed rows', () => {
    const s=setup(); s.applyOcrResult(sample()); s.draft.reviewed=true; s.draft.busy=true;
    s.refreshOcrExport(); assert.equal(s.nodes.get('#ocr-download').disabled,true);
});
test('clear/logout discard photo and results and invalidate in-flight work', () => {
    const s=setup(); s.applyOcrResult(sample()); const previousRun=s.draft.run;
    let terminated=false;
    s.draft.image='blob:test-photo'; s.draft.text='private image text';
    s.draft.worker={terminate:()=>{terminated=true;return Promise.resolve();}};
    s.clearOcr();
    assert.equal(s.draft.run,previousRun+1); assert.equal(s.draft.rows.length,0);
    assert.equal(s.draft.text,''); assert.equal(s.draft.image,null); assert.equal(terminated,true);
    assert.deepEqual(s.revoked,['blob:test-photo']);
});
