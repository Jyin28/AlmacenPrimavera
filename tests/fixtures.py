"""Build synthetic inputs for regression tests; never opens the user's database."""
from pathlib import Path
import re
import sqlite3
import sys
import zipfile

root = Path(__file__).resolve().parents[1]
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=True)

# A deliberately sparse workbook using shared strings, a custom leading-zero
# format, a percentage style and a cached formula. These aren't exported by our app.
parts = {
    "[Content_Types].xml": '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/></Types>',
    "xl/workbook.xml": '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Datos" sheetId="1" r:id="rId1"/></sheets></workbook>',
    "xl/_rels/workbook.xml.rels": '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
    "xl/sharedStrings.xml": '<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Código de barras</t></si><si><t>Nombre</t></si><si><t>Cantidad</t></si><si><t>Compra</t></si><si><t>Venta</t></si><si><t>Impuesto</t></si><si><r><t>Producto </t></r><r><t>especial</t></r></si></sst>',
    "xl/styles.xml": '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="000000"/></numFmts><cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="164"/><xf numFmtId="9"/></cellXfs></styleSheet>',
    "xl/worksheets/sheet1.xml": '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1">' + ''.join(f'<c r="{chr(65+i)}1" t="s"><v>{i}</v></c>' for i in range(6)) + '</row><row r="3"><c r="A3" s="1"><v>123</v></c><c r="B3" t="s"><v>6</v></c><c r="C3"><f>1+2</f><v>3</v></c><c r="D3"><v>100.25</v></c><c r="E3"><v>180</v></c><c r="F3" s="2"><v>0.13</v></c></row></sheetData></worksheet>',
}
with zipfile.ZipFile(out / "sparse.xlsx", "w", zipfile.ZIP_DEFLATED) as z:
    for name, data in parts.items():
        z.writestr(name, data)
parts["xl/worksheets/sheet1.xml"] = parts["xl/worksheets/sheet1.xml"].replace('<f>1+2</f><v>3</v>', '<f>1+2</f>')
with zipfile.ZipFile(out / "no-cache.xlsx", "w", zipfile.ZIP_DEFLATED) as z:
    for name, data in parts.items():
        z.writestr(name, data)
(out / "quoted.csv").write_text('\ufeffCódigo;Nombre;Cantidad;Compra;Venta;Impuesto\r\n00042;"Nombre; con ""comillas""\ny salto";1,5;1.250,50;1.800,25;13\r\n', encoding="utf-8", newline="")

# Historical database containing related records. New catalog migration is tested
# on a separate server; original tables must remain unchanged and copy only once.
schema = re.search(r'private const string Schema = """\s*(.*?)\s*""";', (root / "Data/StoreDb.cs").read_text(encoding="utf-8"), re.S).group(1)
with sqlite3.connect(out / "legacy.db") as db:
    db.executescript(schema)
    db.executescript("""
INSERT INTO users VALUES(1,'legacy','Legacy','administrator','unused','unused',1,'2020-01-01');
INSERT INTO departments VALUES(1,'Anterior','ANT',1);
INSERT INTO categories(id,department_id,name,code) VALUES(1,1,'Original','ORI');
INSERT INTO suppliers(id,name) VALUES(10,'Importador anterior');
INSERT INTO locations VALUES(1,'Tienda','MAIN',1),(2,'Bodega','WH',1);
INSERT INTO products(id,internal_id,name,category_id,unit_of_measure,currency,cost,selling_price,created_at,updated_at,created_by)
 VALUES(1,'ANT-ORI-000001','Producto anterior',1,'unidad','CRC',100.25,180.5,'2020','2020',1);
INSERT INTO barcodes(product_id,code,type,is_primary,created_at) VALUES(1,'0000123','OTHER',1,'2020');
INSERT INTO supplier_products(product_id,supplier_id,cost,updated_at) VALUES(1,10,100.25,'2020');
INSERT INTO inventory_balances(product_id,location_id,on_hand,updated_at) VALUES(1,1,3.5,'2020'),(1,2,2,'2020');
""")
print(out)
