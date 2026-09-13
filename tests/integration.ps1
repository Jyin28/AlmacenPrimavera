param([int]$Port = 5094)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$db = Join-Path $results 'integration.db'
Remove-Item -LiteralPath $db -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath ($db + '-shm') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath ($db + '-wal') -Force -ErrorAction SilentlyContinue
$oldUrl = $env:ASPNETCORE_URLS
$oldDb = $env:STORE_DB_PATH
$oldPass = $env:DEFAULT_ADMIN_PASSWORD
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
$env:STORE_DB_PATH = $db
$env:DEFAULT_ADMIN_PASSWORD = 'Test-Admin-123!'
$process = Start-Process dotnet -ArgumentList 'run --no-build' -WorkingDirectory $root -PassThru -WindowStyle Hidden
function Assert($condition, [string]$message) { if (-not $condition) { throw "FAIL: $message" }; Write-Host "PASS: $message" }
function JsonBody($value) { $bytes=[Text.Encoding]::UTF8.GetBytes(($value | ConvertTo-Json -Depth 12 -Compress)); Write-Output -NoEnumerate $bytes }
try {
  $ready = $false
  for ($i=0; $i -lt 40; $i++) { try { Invoke-WebRequest "http://127.0.0.1:$Port" -UseBasicParsing | Out-Null; $ready=$true; break } catch { Start-Sleep -Milliseconds 250 } }
  Assert $ready 'El servidor inicia y entrega la aplicación'
  $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  $login = Invoke-RestMethod "http://127.0.0.1:$Port/api/auth/login" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{username='admin';password='Test-Admin-123!'})
  Assert ($login.role -eq 'administrator') 'Autenticación y rol de administrador'
  $product = Invoke-RestMethod "http://127.0.0.1:$Port/api/products" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{name='Té frío limón 500 ml';categoryId=1;brand='Sol';description='Prueba';unitOfMeasure='botella';packageSize='500 ml';cost=600;sellingPrice=995;taxRate=13;reorderLevel=5;desiredStock=20;barcode='7441999999999';barcodeType='EAN';supplierId=1;supplierSku='TEST-TE';openingStock=10;locationId=1;familyId=$null})
  Assert ($product.internalId -match '^BEV-DRINK-\d{6}$') 'ID interno generado y separado del código de barras'
  $scan = Invoke-RestMethod "http://127.0.0.1:$Port/api/products?q=7441999999999" -WebSession $session
  Assert ($scan.total -eq 1 -and $scan.items[0].internal_id -eq $product.internalId) 'Búsqueda inmediata por código de barras'
  $edited = Invoke-RestMethod "http://127.0.0.1:$Port/api/products/$($product.id)" -Method Put -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{internalId=$product.internalId;name='Té frío limón editado';description='Producto actualizado';categoryId=1;unitOfMeasure='botella';packageSize='500 ml';cost=610;sellingPrice=1095;taxRate=13;reorderLevel=6;desiredStock=22;barcode='7441999999999';supplierId=1;familyId=$null;reason='Prueba de edición'})
  Assert ($edited.internalId -eq $product.internalId) 'Edición completa conserva el ID interno'
  try {
    Invoke-WebRequest "http://127.0.0.1:$Port/api/products" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{name='Duplicado';categoryId=1;brand='Sol';unitOfMeasure='unidad';cost=1;sellingPrice=2;taxRate=0;reorderLevel=0;desiredStock=0;barcode='7441999999999';barcodeType='EAN';openingStock=0;locationId=1}) -UseBasicParsing | Out-Null
    throw 'FAIL: Se aceptó un código duplicado'
  } catch { Assert ($_.Exception.Response.StatusCode.value__ -in 400,409) 'Código de barras activo duplicado rechazado' }
  $recommendation = Invoke-RestMethod "http://127.0.0.1:$Port/api/pricing/recommend" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{productId=$product.id;freight=20;duties=10;handling=5;discount=0;other=0;comparableCount=5})
  Assert ($recommendation.suggestedPrice -gt $recommendation.landedCost -and $recommendation.grossMargin -ne $recommendation.markup) 'Precio: costo puesto, recargo y margen correctos y distintos'
  $decision = Invoke-RestMethod "http://127.0.0.1:$Port/api/pricing/$($recommendation.id)/decision" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{decision='approved';reason='Aprobación automatizada'})
  Assert $decision.applied 'Aprobación explícita aplica la recomendación'
  $transfer = Invoke-RestMethod "http://127.0.0.1:$Port/api/inventory/transfer" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{productId=$product.id;fromLocationId=1;toLocationId=2;quantity=4;reason='Prueba de traslado'})
  $detail = Invoke-RestMethod "http://127.0.0.1:$Port/api/products/$($product.id)" -WebSession $session
  Assert ((($detail.stock | Measure-Object on_hand -Sum).Sum) -eq 10 -and $detail.stock.Count -eq 2) 'Transferencia conserva el total de la compañía'
  $sale = Invoke-RestMethod "http://127.0.0.1:$Port/api/sales" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{locationId=1;reference='POS-TEST-001';lines=@(@{productId=$product.id;quantity=2;unitPrice=$null})})
  $afterSale = Invoke-RestMethod "http://127.0.0.1:$Port/api/products/$($product.id)" -WebSession $session
  Assert ($sale.total -gt 0 -and (($afterSale.stock | Measure-Object on_hand -Sum).Sum) -eq 8) 'Venta registra ingreso y movimiento de inventario'
  $csv = "internal_id,barcode,location_code,quantity,reason`n$($product.internalId),,MAIN,21,Conteo CSV"
  $preview = Invoke-RestMethod "http://127.0.0.1:$Port/api/import/preview" -Method Post -WebSession $session -ContentType 'text/csv; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($csv))
  Assert ($preview.valid -eq 1 -and $preview.invalid -eq 0 -and $preview.rows[0].newQuantity -eq 21) 'CSV previsualiza la actualización sin guardar'
  $imported = Invoke-RestMethod "http://127.0.0.1:$Port/api/import/apply" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{csv=$csv})
  $afterCsv = Invoke-RestMethod "http://127.0.0.1:$Port/api/products/$($product.id)" -WebSession $session
  Assert ($imported.updated -eq 1 -and ($afterCsv.stock | Where-Object location_id -eq 1).on_hand -eq 21) 'CSV actualiza el inventario existente con movimiento trazable'
  $newCsv = "internal_id,barcode,name,category_code,description,package_size,unit_of_measure,supplier_id,cost,selling_price,tax_rate,quantity,location_code,reorder_level,desired_stock,reason`n,7441777777777,Producto desde asistente,SODA,,500 ml,unidad,,500,795,13,7,MAIN,2,10,Carga inicial"
  $newPreview = Invoke-RestMethod "http://127.0.0.1:$Port/api/import/preview" -Method Post -WebSession $session -ContentType 'text/csv; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($newCsv))
  Assert ($newPreview.valid -eq 1) 'CSV enriquecido valida un producto nuevo'
  Invoke-RestMethod "http://127.0.0.1:$Port/api/import/apply" -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{csv=$newCsv}) | Out-Null
  $createdByCsv = Invoke-RestMethod "http://127.0.0.1:$Port/api/products?q=7441777777777" -WebSession $session
  Assert ($createdByCsv.total -eq 1 -and $createdByCsv.items[0].stock -eq 7) 'CSV enriquecido registra el producto y su inventario'
  Invoke-RestMethod "http://127.0.0.1:$Port/api/products/$($product.id)/status" -Method Patch -WebSession $session -ContentType 'application/json; charset=utf-8' -Body (JsonBody @{active=$false;reason='Fin de prueba'}) | Out-Null
  $audit = Invoke-RestMethod "http://127.0.0.1:$Port/api/audit" -WebSession $session
  Assert (@($audit | Where-Object action -eq 'product.deactivated').Count -ge 1) 'Desactivación conserva historial y deja auditoría'
  Write-Host "`nAll integration tests passed."
} finally {
  if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
  $env:ASPNETCORE_URLS=$oldUrl; $env:STORE_DB_PATH=$oldDb; $env:DEFAULT_ADMIN_PASSWORD=$oldPass
}
