"""Migration, restart and concurrent numbering checks; only synthetic databases."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import json
import os
import re
import sqlite3
import subprocess
import sys
import time
import urllib.request
from uuid import uuid4

root = Path(__file__).resolve().parents[1]
out = root / 'TestResults' / ('numbering-' + uuid4().hex)
out.mkdir(parents=True)
port = int(sys.argv[1]) if len(sys.argv) > 1 else 5199
base = f'http://127.0.0.1:{port}'
schema = re.search(r'Db.Exec\(db, """\s*(.*?)\s*"""\);', (root / 'Data/CatalogSchema.cs').read_text(encoding='utf-8'), re.S).group(1)
database = out / 'v1.db'
with sqlite3.connect(database) as db:
    db.executescript(schema)
    db.executescript("""
INSERT INTO catalog_migrations VALUES(1);
INSERT INTO catalog_categories(id,parent_id,name,abbreviation) VALUES
 (1,NULL,'Bebidas','B'),(2,NULL,'Hogar','H'),(3,NULL,'Sin abreviación',''),
 (10,1,'General','GEN'),(11,10,'Detalle','DET'),(12,2,'Cocina','COC');
INSERT INTO catalog_products(id,barcode,internal_code,name,category_id,cost,selling_price,quantity,active) VALUES
 (100,'00100','B-1-1','En raíz',1,'10.25','20.5','7',1),
 (101,'00101','B-3-1','En subcategoría',10,'10','20','2',1),
 (102,'00102','CUSTOM','En tercer nivel',11,'10','20','3',1),
 (103,'00103','PRD-103','Sin clasificación',NULL,'10','20','4',1),
 (104,'00104','OLD-INACTIVE','Inactivo',10,'10','20','5',0);
""")

def launch(db_path):
    log = (out / (db_path.stem + '.log')).open('a', encoding='utf-8')
    process = subprocess.Popen(['dotnet', str(root / '.verify-build/AlmacenPrimavera.dll'), '--urls', base], cwd=root,
        env=dict(os.environ, STORE_DB_PATH=str(db_path), DEFAULT_ADMIN_PASSWORD='Test-Numbering-123!'), stdout=log, stderr=log,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    return process, log

def wait_ready(process):
    for _ in range(60):
        assert process.poll() is None, 'Test server failed to start.'
        try:
            with urllib.request.urlopen(base, timeout=1):
                return
        except OSError:
            time.sleep(.25)
    raise AssertionError('Test server did not respond.')

def stop(process, log):
    if process.poll() is None:
        process.terminate()
    process.wait(timeout=10)
    log.close()

def snapshot(db):
    return {table: sorted(db.execute('SELECT * FROM ' + table).fetchall(),key=repr) for table in
        ['catalog_categories','catalog_products','catalog_number_counters','catalog_code_history','catalog_migrations']}

process, log = launch(database)
try:
    wait_ready(process)
    with sqlite3.connect(database) as db:
        rows=db.execute('SELECT id,internal_code,product_number FROM catalog_products ORDER BY id').fetchall()
        assert rows == [(100,'B-3-1',1),(101,'B-1-1',1),(102,'B-2-1',1),(103,None,None),(104,'B-1-2',2)], rows
        assert db.execute('SELECT cost,selling_price,quantity FROM catalog_products WHERE id=100').fetchone() == ('10.25','20.5','7')
        assert db.execute('SELECT parent_id,name,sequence_number,abbreviation FROM catalog_categories WHERE id=11').fetchone() == (1,'General / Detalle',2,'')
        assert db.execute('SELECT abbreviation FROM catalog_categories WHERE id=3').fetchone()[0] == 'CAT1'
        assert db.execute('SELECT parent_id,abbreviation FROM catalog_taxonomy_before_numbering WHERE id=11').fetchone() == (10,'DET')
        assert db.execute('SELECT internal_code FROM catalog_product_codes_before_numbering WHERE id=100').fetchone()[0] == 'B-1-1'
        assert db.execute('SELECT old_code,new_code FROM catalog_code_history WHERE product_id=100').fetchone() == ('B-1-1','B-3-1')
        assert db.execute('SELECT active FROM catalog_products WHERE id=104').fetchone()[0] == 0
    print('PASS: v1 migration preserves data and snapshots, flattens deep paths, reserves inactive numbers and handles code swaps.')

    request=urllib.request.Request(base+'/api/auth/login',data=json.dumps({'username':'admin','password':'Test-Numbering-123!'}).encode(),headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(request) as response:
        cookie=response.headers['Set-Cookie'].split(';',1)[0]
    def post(path, body):
        request=urllib.request.Request(base+path,data=json.dumps(body).encode(),headers={'Content-Type':'application/json','Cookie':cookie})
        with urllib.request.urlopen(request,timeout=20) as response:
            return json.load(response)
    with ThreadPoolExecutor(max_workers=4) as pool:
        subs=list(pool.map(lambda n:post('/api/subcategories',{'name':f'Paralela {n}','categoryId':2}),range(8)))
        products=list(pool.map(lambda n:post('/api/products',{'name':f'Paralelo {n}','barcode':f'TEST-{n}','categoryId':10,'cost':10,'sellingPrice':20,'quantity':1}),range(12)))
    assert sorted(s['sequence_number'] for s in subs) == list(range(2,10))
    assert sorted(p['product_number'] for p in products) == list(range(3,15))
    assert len({p['internal_code'] for p in products}) == 12
    print('PASS: concurrent requests allocate unique sequential numbers atomically, starting after migrated records.')
    with sqlite3.connect(database) as db:
        before_restart=snapshot(db)
finally:
    stop(process,log)

process, log = launch(database)
try:
    wait_ready(process)
    with sqlite3.connect(database) as db:
        assert snapshot(db) == before_restart
    print('PASS: restart preserves numbering, counters and code history without repeating migration.')
finally:
    stop(process,log)

invalid=out/'cyclic.db'
with sqlite3.connect(invalid) as db:
    db.executescript(schema)
    db.executescript("INSERT INTO catalog_migrations VALUES(1); INSERT INTO catalog_categories(id,parent_id,name,abbreviation) VALUES(1,2,'Cycle A','A'),(2,1,'Cycle B','B');")
process, log = launch(invalid)
try:
    assert process.wait(timeout=20) != 0, 'Invalid hierarchy must not be migrated partially.'
    with sqlite3.connect(invalid) as db:
        assert 'sequence_number' not in [row[1] for row in db.execute('PRAGMA table_info(catalog_categories)')]
        assert db.execute('SELECT version FROM catalog_migrations').fetchall() == [(1,)]
        assert db.execute('SELECT abbreviation FROM catalog_categories ORDER BY id').fetchall() == [('A',),('B',)]
        assert not db.execute("SELECT name FROM sqlite_master WHERE name='catalog_taxonomy_before_numbering'").fetchall()
    print('PASS: invalid legacy hierarchy rolls back the entire numbering migration.')
finally:
    stop(process,log)
print(out)
