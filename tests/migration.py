"""Verify one-time migration against a synthetic database created by fixtures.py."""
from pathlib import Path
import os
import sqlite3
import subprocess
import sys
import time
import urllib.request

root = Path(__file__).resolve().parents[1]
fixture = Path(sys.argv[1]).resolve()
port = int(sys.argv[2]) if len(sys.argv) > 2 else 5199
database = fixture / "legacy.db"
assert database.exists(), "Run fixtures.py first."
env = dict(os.environ, STORE_DB_PATH=str(database))

def start_and_verify(second=False):
    with (fixture / "migration-server.log").open("a", encoding="utf-8") as log:
        process = subprocess.Popen(["dotnet", str(root / ".verify-build/AlmacenPrimavera.dll"), "--urls", f"http://127.0.0.1:{port}"], cwd=root, env=env, stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        try:
            for attempt in range(60):
                assert process.poll() is None, "Migration server exited."
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{port}", timeout=1):
                        break
                except OSError:
                    time.sleep(.25)
            else:
                raise AssertionError("Migration server did not start.")
            with sqlite3.connect(database) as db:
                p = db.execute("SELECT barcode,internal_code,name,cost,selling_price,quantity,review_pending,importer_id FROM catalog_products").fetchone()
                assert p == ('0000123','ORI-1-1','Editado' if second else 'Producto anterior','100.25','180.5','5.5',1,1), p
                assert db.execute("SELECT internal_code FROM catalog_product_codes_before_numbering WHERE id=1").fetchone()[0] == 'ANT-ORI-000001'
                assert db.execute("SELECT name,sequence_number FROM catalog_categories WHERE parent_id=1").fetchone() == ('General',1)
                assert db.execute("SELECT product_number FROM catalog_products WHERE id=1").fetchone()[0] == 1
                assert db.execute("SELECT COUNT(*) FROM catalog_products").fetchone()[0] == 1
                assert db.execute("SELECT name FROM products").fetchone()[0] == 'Producto anterior'
                assert db.execute("SELECT id,legacy_supplier_id FROM importers").fetchone() == (1,10)
                assert db.execute("SELECT COUNT(*) FROM catalog_migrations").fetchone()[0] == 2
                if not second:
                    db.execute("UPDATE catalog_products SET name='Editado'")
            print('PASS: ' + ('Restart preserves edits and numbering without repeating migration' if second else 'Migration preserves data and old code snapshot, generating the new numbered code'))
        finally:
            process.terminate()
            process.wait(timeout=10)

start_and_verify()
start_and_verify(second=True)
