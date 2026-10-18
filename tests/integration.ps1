param([int]$Port = 5094, [switch]$KeepServer, [string]$FixtureDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runDir = Join-Path $root ('TestResults\simple-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$oldDb = $env:STORE_DB_PATH
$oldPass = $env:DEFAULT_ADMIN_PASSWORD
$env:STORE_DB_PATH = Join-Path $runDir 'test.db'
$env:DEFAULT_ADMIN_PASSWORD = 'Test-Admin-123!'
$dll = Join-Path $root '.verify-build\AlmacenPrimavera.dll'
if (-not (Test-Path $dll)) { throw 'Primero compila con dotnet build --no-restore -o .verify-build' }
$process = Start-Process dotnet -ArgumentList @($dll, '--urls', "http://127.0.0.1:$Port") -WorkingDirectory $root -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir 'server.log') -RedirectStandardError (Join-Path $runDir 'error.log')
$base = "http://127.0.0.1:$Port"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$script:assertions = 0
$success = $false
function Assert($condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    $script:assertions++
    Write-Host "PASS: $message"
}
function JsonBody($value) { Write-Output -NoEnumerate ([Text.Encoding]::UTF8.GetBytes(($value | ConvertTo-Json -Depth 40 -Compress))) }
function Call([string]$path, [string]$method = 'GET', $body = $null) {
    $parameters = @{Uri=($base+$path); Method=$method; WebSession=$session; ContentType='application/json; charset=utf-8'}
    if ($null -ne $body) { $parameters.Body=JsonBody $body }
    Invoke-RestMethod @parameters
}
function Reject([string]$path, [string]$method, $body, [int]$status, [string]$message) {
    try { Call $path $method $body | Out-Null; throw "No se rechazó: $message" }
    catch { Assert ($_.Exception.Response.StatusCode.value__ -eq $status) $message }
}
function Source($rows, $label='test', $separator='.') {
    if ($rows.Count -gt 0 -and $rows[0] -is [string]) { $rows = ,$rows }
    return @{rows=$rows; mapping=@{barcode=0;name=1;quantity=2;cost=3;sellingPrice=4;taxRate=5};label=$label;decimalSeparator=$separator}
}
function Upload([string]$path) {
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.CookieContainer=$session.Cookies
    $client=New-Object System.Net.Http.HttpClient($handler)
    $form=New-Object System.Net.Http.MultipartFormDataContent
    $fileContent=New-Object System.Net.Http.ByteArrayContent -ArgumentList (,[IO.File]::ReadAllBytes($path))
    $form.Add($fileContent,'file',[IO.Path]::GetFileName($path))
    try {
        $response=$client.PostAsync($base+'/api/excel/read',$form).GetAwaiter().GetResult()
        $text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw $text }
        return $text | ConvertFrom-Json
    } finally { $form.Dispose(); $client.Dispose() }
}
try {
    $ready=$false
    for($i=0;$i -lt 60;$i++) {
        if($process.HasExited) { throw (Get-Content (Join-Path $runDir 'error.log') -Raw) }
        try { Invoke-WebRequest $base -UseBasicParsing -TimeoutSec 1 | Out-Null; $ready=$true; break } catch { Start-Sleep -Milliseconds 250 }
    }
    Assert $ready 'Servidor inicia con base aislada'
    Reject '/api/products' 'GET' $null 401 'El catálogo requiere login'
    Reject '/api/auth/login' 'POST' @{username='admin';password='wrong'} 401 'Clave incorrecta rechazada'
    $user=Call '/api/auth/login' 'POST' @{username='admin';password='Test-Admin-123!'}
    Assert $user.canEdit 'Login conserva permisos de edición'
    $ocrRows=@(@{productCode='001234';name='Café molido';quantity=2.5;price=1250.50},@{productCode='AB-123';name='=SUM(A1:A2)';quantity=0;price=0})
    Reject '/api/ocr/export' 'POST' @{reviewed=$false;rows=$ocrRows} 400 'OCR exige revisión explícita antes de exportar'
    Reject '/api/ocr/export' 'POST' @{reviewed=$true;rows=@(@{productCode='A';name='Producto';price=1})} 400 'OCR no transforma cantidad sin leer en cero'
    Reject '/api/ocr/export' 'POST' @{reviewed=$true;rows=@(@{productCode='A';name='Producto';quantity=1;price=1.234})} 400 'OCR rechaza pérdida de precisión en precio'
    Reject '/api/ocr/export' 'POST' @{reviewed=$true;rows=@()} 400 'OCR rechaza archivo vacío'
    $ocrExport=Join-Path $runDir 'ocr-export.xlsx'
    Invoke-WebRequest ($base+'/api/ocr/export') -Method POST -WebSession $session -ContentType 'application/json' -Body (JsonBody @{reviewed=$true;rows=$ocrRows}) -OutFile $ocrExport -UseBasicParsing
    $ocrBook=Upload $ocrExport
    Assert (($ocrBook.sheets[0].rows[0] -join ',') -eq 'Product Code,Name,Quantity,Price') 'OCR exporta exactamente las cuatro columnas solicitadas y en orden'
    Assert ($ocrBook.sheets[0].rows[1][0] -eq '001234' -and $ocrBook.sheets[0].rows[1][2] -eq '2.5' -and $ocrBook.sheets[0].rows[1][3] -eq '1250.5') 'OCR exporta código como texto y valores numéricos exactos'
    Assert ($ocrBook.sheets[0].numericCells[1][2] -and $ocrBook.sheets[0].numericCells[1][3] -and -not $ocrBook.sheets[0].numericCells[1][0]) 'OCR genera Quantity y Price como celdas numéricas reales'
    Assert ($ocrBook.sheets[0].rows[2][1] -eq '=SUM(A1:A2)') 'OCR conserva texto parecido a fórmula sin ejecutarlo'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $ocrZip=[IO.Compression.ZipFile]::OpenRead($ocrExport)
    try {
        $ocrReader=New-Object IO.StreamReader($ocrZip.GetEntry('xl/worksheets/sheet1.xml').Open())
        $ocrXml=[xml]$ocrReader.ReadToEnd();$ocrReader.Dispose()
        Assert ($ocrXml.SelectNodes("//*[local-name()='f']").Count -eq 0) 'El Excel OCR no contiene fórmulas introducidas por el texto'
    } finally { $ocrZip.Dispose() }
    Assert ((Call '/api/products').total -eq 0) 'Exportar OCR no modifica el inventario'
    $ocrAsset=Invoke-WebRequest ($base+'/vendor/tesseract/lang/eng.traineddata.gz') -UseBasicParsing
    Assert ($ocrAsset.Headers['Content-Type'] -match 'application/gzip') 'Datos de idioma OCR disponibles localmente con tipo correcto'
    $ocrScript=Invoke-WebRequest ($base+'/vendor/tesseract/worker.min.js') -UseBasicParsing
    Assert ($ocrScript.StatusCode -eq 200 -and $ocrScript.Headers['Content-Security-Policy'] -match 'wasm-unsafe-eval') 'Worker OCR y política de WebAssembly disponibles sin CDN'
    $lookups=Call '/api/lookups'
    Assert ($lookups.categories.Count -eq 0 -and $lookups.importers.Count -eq 0) 'Instalación nueva sin datos de ejemplo'
    $parent=Call '/api/categories' 'POST' @{name='Bebidas';abbreviation='BEB';targetMarkup=40;minimumMargin=20}
    $child=Call '/api/subcategories' 'POST' @{name='Gaseosas';categoryId=$parent.id;targetMarkup=50;minimumMargin=25}
    Assert ($child.sequence_number -eq 1 -and $child.abbreviation -eq '') 'Subcategorías numeradas desde 1 sin abreviación'
    Reject ("/api/categories/"+$parent.id) 'PUT' @{name='Bebidas';abbreviation='BEB';parentId=$child.id;targetMarkup=40;minimumMargin=20} 400 'Jerarquía rechaza ciclos indirectos'
    Reject '/api/categories' 'POST' @{name='Duplicada';abbreviation='BEB';targetMarkup=40;minimumMargin=20} 409 'Abreviaciones únicas'
    Reject '/api/categories' 'POST' @{name='Sin abreviación'} 400 'Categorías requieren abreviación'
    Reject '/api/subcategories' 'POST' @{name='Tercer nivel';categoryId=$child.id} 400 'No admite subcategorías dentro de subcategorías'
    Reject '/api/products' 'POST' @{name='Raíz inválida';barcode='ROOT';categoryId=$parent.id;cost=1;sellingPrice=2;quantity=1} 400 'Productos requieren subcategoría y no categoría principal'
    $lookups=Call '/api/lookups'
    Assert ($lookups.categories.Count -eq 1 -and $lookups.subcategories.Count -eq 1) 'Categorías y subcategorías se entregan por separado'
    $importer=Call '/api/importers' 'POST' @{name='Importador uno'}
    $secondImporter=Call '/api/importers' 'POST' @{name='Importador dos'}
    Assert ($importer.id -eq 1 -and $secondImporter.id -eq 2) 'Importadores numerados desde 1'
    Call ("/api/importers/"+$importer.id) 'PUT' @{name='Importador editado'} | Out-Null
    Assert ((Call '/api/lookups').importers[0].name -eq 'Importador editado') 'Nombre del importador editable sin cambiar código'
    $first=Source @(@('00123','Agua','3','100.50','150','13'),@('00123','Agua','2','100.50','150','13'),@('00009','Jugo','4','200','300','0')) 'Excel 1'
    $second=Source @(@('00123','Agua nueva','7','110.25','165.50','13'),@('999','Té','1','400','600','0')) 'Excel 2'
    $merged=Call '/api/excel/merge' 'POST' @{first=$first;second=$second;prefer='second'}
    Assert ($merged.rows.Count -eq 3 -and $merged.repeatedRows -eq 2) 'Unifica repetidos dentro de un Excel y entre ambos'
    $water=$merged.rows | Where-Object barcode -eq '00123'
    Assert ($water.quantity -eq 12 -and $water.barcode -eq '00123') 'Suma cantidades y conserva ceros iniciales'
    Assert ($water.cost -eq 110.25 -and $water.name -eq 'Agua nueva' -and $merged.conflicts.Count -eq 1) 'Muestra diferencias y aplica prioridad elegida'
    $preferFirst=Call '/api/excel/merge' 'POST' @{first=$first;second=$second;prefer='first'}
    Assert (($preferFirst.rows | Where-Object barcode -eq '00123').cost -eq 100.50) 'Prioridad al primer archivo funciona'
    $bad=Source @(@('E','Error','no-numero','10','20','0'))
    $invalid=Call '/api/excel/merge' 'POST' @{first=$first;second=$bad}
    Assert ($invalid.issues.Count -eq 1 -and $invalid.issues[0].row -eq 2) 'Fila inválida se informa con número, no se silencia'
    $locale=Source @(@('77','Local','1,5','1.250,50','1.800,25','13')) 'Coma decimal' ','
    $localized=Call '/api/excel/merge' 'POST' @{first=$locale;second=(Source @(@('88','Otro','1','1','2','0')))}
    Assert ($localized.rows[0].cost -eq 1250.50 -and $localized.rows[0].quantity -eq 1.5) 'Separadores decimales y miles validados'
    $export=Join-Path $runDir 'roundtrip.xlsx'
    Invoke-WebRequest ($base+'/api/excel/export') -Method POST -WebSession $session -ContentType 'application/json' -Body (JsonBody @{rows=$merged.rows}) -OutFile $export -UseBasicParsing
    $book=Upload $export
    Assert ($book.sheets[0].rows.Count -eq 4 -and $book.sheets[0].rows[1][0] -eq '00123') 'Descarga Excel real y vuelve a leer sin perder códigos'
    if ($FixtureDir) {
        $sparse=Upload (Join-Path $FixtureDir 'sparse.xlsx')
        Assert ($sparse.sheets[0].rows[2][0] -eq '000123' -and $sparse.sheets[0].rows[2][1] -eq 'Producto especial') 'Lee shared strings y formato de ceros iniciales'
        Assert ($sparse.sheets[0].rows[2][2] -eq '3' -and $sparse.sheets[0].rows[2][5] -eq '13') 'Lee fórmula guardada e impuesto con formato porcentaje'
        $quoted=Upload (Join-Path $FixtureDir 'quoted.csv')
        Assert ($quoted.sheets[0].rows.Count -eq 2 -and $quoted.sheets[0].rows[1][0] -eq '00042' -and $quoted.sheets[0].rows[1][1].Contains("`n")) 'CSV UTF-8 acepta comillas, separadores dentro del nombre y saltos de línea'
        $missingCache=$false
        try { Upload (Join-Path $FixtureDir 'no-cache.xlsx') | Out-Null } catch { $missingCache=$_.Exception.Message.Contains('fórmula sin resultado') }
        Assert $missingCache 'Rechaza fórmulas que no tienen resultado guardado'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($export)
    try {
        $reader=New-Object IO.StreamReader($zip.GetEntry('_rels/.rels').Open())
        $xml=[xml]$reader.ReadToEnd();$reader.Dispose()
        Assert ($xml.Relationships.Relationship.Type -eq 'http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument') 'Paquete Excel tiene relaciones OOXML válidas'
    } finally { $zip.Dispose() }
    $imported=Call '/api/excel/apply' 'POST' @{rows=$merged.rows}
    Assert ($imported.created -eq 3) 'Productos nuevos entran a la cola de categorización'
    $pending=Call '/api/products?pending=true'
    Assert ($pending.total -eq 3 -and $pending.items[0].review_pending -eq 1) 'Nuevos productos requieren revisión'
    $product=($pending.items | Where-Object barcode -eq '00123')
    Call '/api/excel/apply' 'POST' @{rows=$merged.rows} | Out-Null
    Assert ((Call ("/api/products/"+$product.id)).quantity -eq 12 -and (Call '/api/products').total -eq 3) 'Reimportación actualiza cantidades sin duplicar productos ni existencias'
    $product=Call ("/api/products/"+$product.id)
    Reject '/api/pricing/apply' 'POST' @{productId=$product.id;version=$product.version;sellingPrice=200;completeReview=$true} 400 'No permite completar sin categoría e importador'
    Call '/api/products/classify' 'POST' @{ids=@($pending.items.id);categoryId=$child.id;importerId=$importer.id} | Out-Null
    $product=Call ("/api/products/"+$product.id)
    Assert ($product.internal_code -eq 'BEB-1-1' -and $product.product_number -eq 1 -and $product.importer_id -eq 1) 'Código automático ABREVIACIÓN-NÚMERO SUBCATEGORÍA-NÚMERO PRODUCTO'
    Assert ((Call ("/api/products?categoryId="+$parent.id)).total -eq 3) 'Filtro de categoría incluye sus subcategorías'
    $rec=Call '/api/pricing/recommend' 'POST' @{productId=$product.id;extraCost=10;roundTo=5}
    Assert ($rec.landedCost -eq 120.25 -and $rec.suggestedPrice -eq 185 -and $rec.comparableCount -eq 0) 'Recomendación decimal y redondeo sin comparables usa regla de categoría'
    Assert ($rec.grossMargin -ne $rec.markup -and $rec.profit -eq 64.75) 'Distingue margen, recargo y ganancia'
    Call '/api/pricing/apply' 'POST' @{productId=$product.id;version=$rec.version;sellingPrice=$rec.suggestedPrice;completeReview=$true} | Out-Null
    Assert ((Call '/api/products?pending=true').total -eq 2) 'Revisar precio termina el flujo del producto'
    Reject '/api/pricing/apply' 'POST' @{productId=$product.id;version=$rec.version;sellingPrice=190;completeReview=$true} 409 'Rechaza recomendaciones desactualizadas'
    $product=Call ("/api/products/"+$product.id)
    $edit=@{name='Agua editada';barcode=$product.barcode;internalCode=$product.internal_code;categoryId=$child.id;importerId=$importer.id;cost=$product.cost;sellingPrice=$product.selling_price;taxRate=13;quantity=12;version=$product.version}
    $edited=Call ("/api/products/"+$product.id) 'PUT' $edit
    Assert ($edited.name -eq 'Agua editada' -and $edited.internal_code -eq $product.internal_code) 'Edición conserva código interno'
    Reject ("/api/products/"+$product.id) 'PUT' $edit 409 'Rechaza edición desde una versión antigua'
    $duplicate=@{name='Duplicado';barcode=$product.barcode;cost=10;sellingPrice=20;quantity=1;taxRate=0}
    Reject '/api/products' 'POST' $duplicate 409 'Código de barras único'
    Assert ((Call '/api/products').total -eq 3) 'Alta fallida revierte transacción completa'
    Call ("/api/subcategories/"+$child.id) 'PUT' @{name='Gaseosas editadas';categoryId=$parent.id;targetMarkup=50;minimumMargin=25} | Out-Null
    Assert ((Call ("/api/products/"+$product.id)).internal_code -eq $product.internal_code) 'Renombrar subcategoría conserva número y códigos'
    $newCostRows=@(@{barcode='00123';name='Agua';quantity=12;cost=130;sellingPrice=185;taxRate=13})
    Call '/api/excel/apply' 'POST' @{rows=$newCostRows} | Out-Null
    $updated=Call ("/api/products/"+$product.id)
    Assert ($updated.review_pending -eq 1 -and $updated.category_id -eq $child.id -and $updated.importer_id -eq 1) 'Costo nuevo pide revisar precio y conserva clasificación'
    foreach ($price in @(120,150,180)) {
        $peer=Call '/api/products' 'POST' @{name="Comparable $price";barcode="PEER-$price";categoryId=$child.id;importerId=$importer.id;cost=100;sellingPrice=$price;quantity=1;taxRate=0}
        Call '/api/pricing/apply' 'POST' @{productId=$peer.id;version=$peer.version;sellingPrice=$price;completeReview=$true} | Out-Null
    }
    $median=Call '/api/pricing/recommend' 'POST' @{productId=$product.id;roundTo=1}
    Assert ($median.comparableCount -eq 3 -and $median.medianMarkup -eq 50 -and $median.suggestedPrice -eq 195) 'Usa mediana correcta de 3 comparables revisados'
    $manual=Call '/api/pricing/recommend' 'POST' @{productId=$product.id;markup=0;roundTo=1}
    Assert ($manual.suggestedPrice -eq 174 -and $manual.grossMargin -ge 25) 'Recargo manual cero respeta margen mínimo de categoría'
    $duplicateRows=@($merged.rows[0],$merged.rows[0])
    Reject '/api/excel/apply' 'POST' @{rows=$duplicateRows} 400 'Importación duplicada rechazada antes de guardar'
    $child2=Call '/api/subcategories' 'POST' @{name='Jugos';categoryId=$parent.id}
    $homeCategory=Call '/api/categories' 'POST' @{name='Hogar';abbreviation='HOG'}
    $homeChild=Call '/api/subcategories' 'POST' @{name='Cocina';categoryId=$homeCategory.id}
    Assert ($child2.sequence_number -eq 2 -and $homeChild.sequence_number -eq 1) 'La numeración de subcategorías empieza en 1 en cada categoría'
    $one=Call '/api/products' 'POST' @{name='Jugo uno';barcode='NEW-1';internalCode='MANUAL';categoryId=$child2.id;cost=10;sellingPrice=20;quantity=1}
    Assert ($one.internal_code -eq 'BEB-2-1' -and $one.product_number -eq 1) 'Nueva subcategoría inicia productos desde 1 e ignora códigos manuales'
    Reject '/api/products' 'POST' @{name='Duplicado';barcode='NEW-1';categoryId=$child2.id;cost=10;sellingPrice=20;quantity=1} 409 'Alta fallida revierte asignación de número'
    $two=Call '/api/products' 'POST' @{name='Jugo dos';barcode='NEW-2';categoryId=$child2.id;cost=10;sellingPrice=20;quantity=1}
    Assert ($two.internal_code -eq 'BEB-2-2') 'El contador no consume números por transacciones fallidas'
    Call '/api/products/classify' 'POST' @{ids=@($one.id);categoryId=$child2.id} | Out-Null
    Assert ((Call ('/api/products/'+$one.id)).internal_code -eq 'BEB-2-1') 'Reclasificar a la misma subcategoría no renumera'
    Call ('/api/categories/'+$parent.id) 'PUT' @{name='Bebidas editadas';abbreviation='BE'} | Out-Null
    Assert ((Call ('/api/products/'+$one.id)).internal_code -eq 'BE-2-1' -and (Call ('/api/products/'+$product.id)).internal_code -eq 'BE-1-1') 'Cambiar abreviación actualiza los códigos de todas las subcategorías'
    Reject ('/api/products/'+$two.id) 'PUT' @{name='Vieja edición';barcode='NEW-2';categoryId=$child2.id;cost=10;sellingPrice=20;quantity=1;version=$two.version} 409 'Cambio de abreviación invalida ediciones antiguas de producto'
    Call '/api/products/classify' 'POST' @{ids=@($one.id);categoryId=$homeChild.id} | Out-Null
    Assert ((Call ('/api/products/'+$one.id)).internal_code -eq 'HOG-1-1') 'Mover producto asigna número desde la subcategoría destino'
    $three=Call '/api/products' 'POST' @{name='Jugo tres';barcode='NEW-3';categoryId=$child2.id;cost=10;sellingPrice=20;quantity=1}
    Assert ($three.internal_code -eq 'BE-2-3') 'Los números liberados no se reutilizan'
    Call '/api/products/classify' 'POST' @{ids=@($one.id);categoryId=$child2.id} | Out-Null
    Assert ((Call ('/api/products/'+$one.id)).internal_code -eq 'BE-2-4') 'Volver a la subcategoría asigna el siguiente número disponible'
    $movedSub=Call ('/api/subcategories/'+$child2.id) 'PUT' @{name='Jugos';categoryId=$homeCategory.id}
    Assert ($movedSub.sequence_number -eq 2 -and (Call ('/api/products/'+$one.id)).internal_code -eq 'HOG-2-4') 'Mover subcategoría actualiza prefijo y conserva números de sus productos'
    Call ('/api/subcategories/'+$child2.id) 'PUT' @{name='Jugos';categoryId=$parent.id} | Out-Null
    $fourthSub=Call '/api/subcategories' 'POST' @{name='Agua';categoryId=$parent.id}
    Assert ($fourthSub.sequence_number -eq 4 -and (Call ('/api/products/'+$two.id)).internal_code -eq 'BE-3-2') 'Números de subcategoría tampoco se reutilizan tras moverlas'
    $draft=Call '/api/products' 'POST' @{name='Borrador';barcode='DRAFT';cost=1;sellingPrice=2;quantity=1}
    Assert ($null -eq $draft.internal_code -and $null -eq $draft.product_number) 'Borrador sin clasificación no inventa código ni número'
    Reject '/api/products/classify' 'POST' @{ids=@($draft.id);categoryId=$parent.id} 400 'Clasificación por lotes también exige subcategoría'
    Reject '/api/reports/inventory' 'GET' $null 404 'Reportes retirados'
    Reject '/api/inventory/adjust' 'POST' @{} 404 'Operaciones de inventario antiguas retiradas'
    Reject '/api/audit' 'GET' $null 404 'Auditoría retirada de la API pública'
    Call '/api/auth/logout' 'POST' | Out-Null
    Reject '/api/products' 'GET' $null 401 'Cerrar sesión invalida el acceso'
    Write-Host ("TOTAL: "+$script:assertions+" comprobaciones correctas.")
    $success = $true
    if($KeepServer) { Write-Host ("UI_TEST_SERVER_PID="+$process.Id+" URL="+$base+" DB="+$env:STORE_DB_PATH) }
} finally {
    if((-not $KeepServer -or -not $success) -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    $env:STORE_DB_PATH=$oldDb
    $env:DEFAULT_ADMIN_PASSWORD=$oldPass
}
