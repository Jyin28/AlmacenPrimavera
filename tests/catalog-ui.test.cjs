const test=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');

// Exercise generated markup, event handlers and payloads without a browser.
// This intentionally does not assert layout or replace visual browser testing.
function setup() {
    const nodes=new Map(), calls=[];
    function node(selector) {
        if (!nodes.has(selector)) nodes.set(selector,{value:'',innerHTML:'',disabled:false,checked:false,
            listeners:{},addEventListener(type,fn){this.listeners[type]=fn;},setAttribute(){},removeAttribute(){},
            querySelector(s){return node(s);},querySelectorAll(){return [];}});
        return nodes.get(selector);
    }
    const context=vm.createContext({document:{querySelector:node,querySelectorAll:()=>[]},Intl,URLSearchParams,Set,setTimeout,clearTimeout,
        confirm:()=>true,calls,testApi:async(url,options)=>{
            calls.push({url,body:options?.body});
            if(url==='/api/products/1')return {id:1,name:'Agua',barcode:'001',internal_code:'BEB-1-1',root_category_id:10,category_id:11,product_number:1,cost:10,selling_price:20,quantity:1,tax_rate:0,version:2};
            if(url.startsWith('/api/products?'))return {items:[{id:1,name:'Agua',category_id:11,cost:10,selling_price:20}],total:1,page:1,pages:1};
            if(url==='/api/products/classify')return {updated:1};
            return {};
        }});
    vm.runInContext(fs.readFileSync(path.join(__dirname,'../wwwroot/app.js'),'utf8'),context);
    vm.runInContext(fs.readFileSync(path.join(__dirname,'../wwwroot/catalog.js'),'utf8').replace(/^api\('\/api\/auth\/me'\).*$/m,''),context);
    vm.runInContext(`
        api=testApi; refresh=async()=>{}; toast=()=>{}; closeDialog=()=>{};
        openDialog=(title,subtitle,html)=>{globalThis.dialog={title,subtitle,html};};
        formValues=form=>form.values;
        run=(_,fn)=>{globalThis.job=fn();return globalThis.job;};
        state.user={canEdit:true};
        state.lookups={categories:[{id:10,name:'Bebidas',abbreviation:'BEB',target_markup:40,minimum_margin:20},{id:20,name:'Hogar',abbreviation:'HOG'}],
            subcategories:[{id:12,parent_id:10,name:'Jugos',sequence_number:2},{id:11,parent_id:10,name:'Agua',sequence_number:1,target_markup:35,minimum_margin:20},{id:21,parent_id:20,name:'<Cocina>',sequence_number:1}],importers:[]};
        globalThis.catalogTest={state,categoryOptions,subcategoryOptions,categoryPath,bindSubcategories,manageCategories,manageSubcategories,categoryForm,subcategoryForm,editProduct,renderCatalog};
    `,context);
    return {...context.catalogTest,context,node,calls};
}

test('category choices contain roots only, with abbreviations',()=>{
    const s=setup(),html=s.categoryOptions(10);
    assert.match(html,/Bebidas/);assert.match(html,/BEB/);assert.doesNotMatch(html,/Agua|Jugos/);
    assert.match(html,/value="10" selected/);
});
test('subcategory choices are numbered, sorted, scoped and safely escaped',()=>{
    const s=setup(),html=s.subcategoryOptions(10,12);
    assert.ok(html.indexOf('1 · Agua')<html.indexOf('2 · Jugos'));
    assert.doesNotMatch(html,/Cocina|BEB/);assert.match(html,/value="12" selected/);
    assert.match(s.subcategoryOptions(20,null),/&lt;Cocina&gt;/);
    assert.equal(s.categoryPath(11),'Bebidas / 1 · Agua');
});
test('changing category resets subcategory choices and empty category disables them',()=>{
    const s=setup(),root=s.node('#root'),child=s.node('#child');root.value='10';
    s.bindSubcategories('#root','#child','Seleccionar');assert.equal(child.disabled,false);
    root.value='20';root.listeners.change();assert.match(child.innerHTML,/Cocina/);assert.doesNotMatch(child.innerHTML,/Agua/);
    root.value='';root.listeners.change();assert.equal(child.disabled,true);assert.doesNotMatch(child.innerHTML,/value="21"/);
});
test('separate category and subcategory managers show the correct fields',()=>{
    const s=setup();s.manageCategories();assert.equal(s.context.dialog.title,'Categorías');
    assert.match(s.context.dialog.html,/Abreviación: BEB/);assert.doesNotMatch(s.context.dialog.html,/Jugos/);
    s.manageSubcategories(10);assert.equal(s.context.dialog.title,'Subcategorías');
    assert.match(s.context.dialog.html,/N.º 1/);assert.doesNotMatch(s.context.dialog.html,/Abreviación:/);
    s.categoryForm(10);assert.match(s.context.dialog.html,/name="abbreviation"[^>]*required/);assert.doesNotMatch(s.context.dialog.html,/name="parentId"/);
    s.subcategoryForm(11);assert.doesNotMatch(s.context.dialog.html,/name="abbreviation"/);
    assert.match(s.context.dialog.html,/id="subcategory-number" readonly/);
});
test('product editor separates selectors and makes internal code read-only',async()=>{
    const s=setup();await s.editProduct(1);
    assert.match(s.context.dialog.html,/id="product-category"/);assert.match(s.context.dialog.html,/id="product-subcategory" name="categoryId"/);
    assert.match(s.context.dialog.html,/id="product-internal-code" readonly value="BEB-1-1"/);
    assert.doesNotMatch(s.context.dialog.html,/name="internalCode"/);
});
test('bulk classification sends the subcategory, never the root, and generates codes automatically',async()=>{
    const s=setup();s.state.view='queue';s.state.selected.add(1);await s.renderCatalog();
    assert.match(s.node('#content').innerHTML,/id="subcategories"/);assert.doesNotMatch(s.node('#content').innerHTML,/bulk-codes/);
    assert.match(s.node('#content').innerHTML,/<th>Categoría<\/th><th>Subcategoría<\/th>/);
    s.node('#bulk-category').value='10';s.node('#bulk-subcategory').value='11';
    await s.node('#classify').onclick({currentTarget:s.node('#classify')});
    const request=s.calls.find(c=>c.url==='/api/products/classify');assert.equal(request.body.categoryId,11);
    assert.equal('regenerateCodes' in request.body,false);
    s.state.selected.add(1);s.node('#bulk-subcategory').value='';
    await assert.rejects(s.node('#classify').onclick({currentTarget:s.node('#classify')}),/también una subcategoría/);
});
test('filtering uses subcategory when selected, otherwise the whole category',async()=>{
    const s=setup();s.state.category='10';s.state.subcategory='11';await s.renderCatalog();
    assert.match(s.calls.at(-1).url,/categoryId=11/);
    s.state.subcategory='';await s.renderCatalog();assert.match(s.calls.at(-1).url,/categoryId=10/);
});
test('product save uses selected subcategory and blocks a root-only selection',async()=>{
    const s=setup();await s.editProduct(1);
    const form=s.node('#product-form');form.values={name:'Agua',barcode:'001',categoryId:'12',cost:'10',sellingPrice:'20',quantity:'1',taxRate:'0'};
    s.node('#product-category').value='10';s.node('#product-subcategory').value='12';
    form.onsubmit({preventDefault(){},target:form});await s.context.job;
    assert.equal(s.calls.at(-1).body.categoryId,12);assert.equal('internalCode' in s.calls.at(-1).body,false);
    s.node('#product-subcategory').value='';form.onsubmit({preventDefault(){},target:form});
    await assert.rejects(s.context.job,/también una subcategoría/);
});
