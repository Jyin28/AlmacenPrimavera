const test = require('node:test');
const assert = require('node:assert/strict');
const parser = require('../wwwroot/ocr-parser.js');

test('four fields, zero-prefixed code, multi-word name, decimal numbers', () => {
    const { rows, skipped } = parser.parseText('Product Code | Name | Quantity | Price\n00123 | Café molido 500 g | 2 | 1,250.50');
    assert.equal(skipped.length,0); assert.equal(rows.length,1);
    assert.equal(rows[0].productCode,'00123'); assert.equal(rows[0].name,'Café molido 500 g');
    assert.equal(rows[0].quantity,'2'); assert.equal(rows[0].price,'1250.5'); assert.deepEqual(parser.validate(rows[0]),[]);
});
test('Spanish decimal comma and fractional quantity', () => {
    const { rows } = parser.parseText('0007 | Arroz | 1,5 | ₡1.250,25',{decimalSeparator:','});
    assert.equal(rows[0].quantity,'1.5'); assert.equal(rows[0].price,'1250.25');
});
test('OCR does not guess confused digits, missing values or negative amounts', () => {
    for (const value of ['', 'O.50', 'l0', '-1', '1.2345', '1e3']) assert.ok(parser.amount(value,'.',2).error);
    const { rows } = parser.parseText('A01 | Producto | O | 99.99');
    assert.equal(rows[0].quantity,''); assert.ok(parser.validate(rows[0]).length);
});
test('zero is a valid number and is not treated as missing', () => {
    const { rows } = parser.parseText('A01 | Producto | 0 | 0'); assert.deepEqual(parser.validate(rows[0]),[]);
});

test('explicit empty cells stay in place and never become zero', () => {
    for (const text of ['001 | Coffee | | 5', '001\tCoffee\t\t5']) {
        const { rows } = parser.parseText(text);
        assert.equal(rows.length,1); assert.equal(rows[0].name,'Coffee');
        assert.equal(rows[0].quantity,''); assert.equal(rows[0].price,'5');
        assert.ok(parser.validate(rows[0]).length);
    }
    assert.equal(parser.parseText('001\tCoffee\t5\t').rows[0].price,'');
    assert.equal(parser.parseText('001 | Coffee | 5').skipped.length,1);
    assert.equal(parser.parseText('001 | | 2 | 5').rows[0].name,'');
});
test('codes remain text and duplicate rows remain separate', () => {
    const { rows } = parser.parseText('0001 | Producto | 1 | 25\n0001 | Producto | 2 | 25');
    assert.equal(rows.length,2); assert.equal(rows[0].productCode,'0001');
});
test('custom column order and name as last field', () => {
    const { rows } = parser.parseText('2 A001 50.25 Producto largo',{order:['quantity','productCode','price','name']});
    assert.equal(rows[0].name,'Producto largo'); assert.equal(rows[0].quantity,'2'); assert.equal(rows[0].price,'50.25');
});
test('invalid mapping is rejected', () => {
    assert.throws(()=>parser.parseText('',{order:['name','name','quantity','price']}));
});
test('totals, unknown lines and extra columns are exposed instead of hidden', () => {
    const { rows, skipped } = parser.parseText('TOTAL 1500\nIlegible\n001 | Producto | 2 | 100 | 200');
    assert.equal(rows.length,0); assert.equal(skipped.length,3);
});
test('bounding boxes assemble table rows even across separate OCR blocks', () => {
    const word=(text,x,y,width,confidence=95)=>({text,confidence,bbox:{x0:x,y0:y,x1:x+width,y1:y+25}});
    const data={text:'',blocks:[{paragraphs:[{lines:[{words:[word('0012',10,10,60),word('0013',10,60,60)]}]}]},
        {paragraphs:[{lines:[{words:[word('Coffee',200,10,80),word('Rice',200,60,60)]}]}]},
        {paragraphs:[{lines:[{words:[word('2',700,10,15),word('1',700,60,15),word('50.00',900,10,70),word('25.50',900,60,70,60)]}]}]}]};
    const {rows}=parser.parseOcr(data);
    assert.equal(rows.length,2); assert.equal(rows[0].productCode,'0012'); assert.equal(rows[1].price,'25.5');
});
test('validation rejects dangerous controls, blank fields and precision loss', () => {
    assert.ok(parser.validate({productCode:'a\u0001',name:'A',quantity:'2',price:'1'}).length);
    assert.ok(parser.validate({productCode:'A',name:'',quantity:'2',price:'1'}).length);
    assert.ok(parser.validate({productCode:'A',name:'A',quantity:'',price:'1'}).length);
    assert.ok(parser.validate({productCode:'A',name:'A',quantity:'1',price:'12.345'}).length);
});
