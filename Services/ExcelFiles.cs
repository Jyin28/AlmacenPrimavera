using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AlmacenPrimavera;

// OOXML data import/export: no macros, external links or formulas are executed.
public static class ExcelFiles
{
    public const string Mime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    const int MaxRows = 20000;
    static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public record Sheet(string Name, List<List<string>> Rows, List<List<bool>>? NumericCells = null);
    public record Workbook(List<Sheet> Sheets);
    public static Workbook Read(Stream input, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".csv") {
            using var reader = new StreamReader(input, new UTF8Encoding(false,true), true);
            try { return new([new("CSV", ReadCsv(reader.ReadToEnd()))]); }
            catch (DecoderFallbackException) { throw new DomainException("Guarda el CSV como UTF-8 o usa Excel .xlsx."); }
        }
        if (extension != ".xlsx") throw new DomainException("Usa un archivo .xlsx o .csv. Para un Excel antiguo .xls, ábrelo en Excel y guárdalo como .xlsx.");
        try {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > 2000 || zip.Entries.Sum(e=>e.Length)>64*1024*1024) throw new DomainException("El Excel es demasiado grande. Divide el archivo en partes.");
            XDocument? Xml(string name) {
                var entry=zip.GetEntry(name); if(entry is null) return null;
                using var stream=entry.Open();
                using var reader=XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=64*1024*1024 });
                return XDocument.Load(reader);
            }
            var book=Xml("xl/workbook.xml")??throw new DomainException("El archivo no es un Excel .xlsx válido.");
            var rels=Xml("xl/_rels/workbook.xml.rels")??throw new DomainException("Faltan las hojas del Excel.");
            var shared=Xml("xl/sharedStrings.xml")?.Root?.Elements(X+"si").Select(si=>string.Concat(si.Descendants(X+"t").Select(t=>t.Value))).ToList()??[];
            var styles=Xml("xl/styles.xml");
            var formats=styles?.Root?.Element(X+"numFmts")?.Elements(X+"numFmt").ToDictionary(n=>(string)n.Attribute("numFmtId")!,n=>(string)n.Attribute("formatCode")!)??new();
            var styleIds=styles?.Root?.Element(X+"cellXfs")?.Elements(X+"xf").Select(n=>(string?)n.Attribute("numFmtId")??"0").ToList()??[];
            var sheets=new List<Sheet>();
            var allSheets=book.Descendants(X+"sheet").ToList();
            if(allSheets.Count>20) throw new DomainException("El archivo puede contener hasta 20 hojas.");
            foreach(var sheet in allSheets) {
                var relationship=rels.Root?.Elements().FirstOrDefault(e=>(string?)e.Attribute("Id")== (string?)sheet.Attribute(R+"id"));
                if(relationship is null || (string?)relationship.Attribute("TargetMode")=="External") continue;
                var target=(string?)relationship.Attribute("Target")??"";
                var uri=new Uri(new Uri("https://local/xl/workbook.xml"),target);
                var doc=Xml(Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
                if(doc is null) continue;
                var rows=new List<List<string>>(); var numericRows=new List<List<bool>>();
                foreach(var row in doc.Descendants(X+"sheetData").Elements(X+"row")) {
                    var cells=row.Elements(X+"c").Where(c=>c.Element(X+"v") is not null || c.Element(X+"is") is not null || c.Element(X+"f") is not null).ToList();
                    if(cells.Count==0) continue;
                    var rowIndex=int.TryParse((string?)row.Attribute("r"),out var rowNo)?rowNo:rows.Count+1;
                    if(rowIndex>MaxRows+1) throw new DomainException($"Cada hoja admite hasta {MaxRows:N0} filas de productos.");
                    while(rows.Count<rowIndex) {rows.Add([]);numericRows.Add([]);}
                    var values=rows[rowIndex-1]; var numeric=numericRows[rowIndex-1];
                    foreach(var cell in cells) {
                        var address=(string?)cell.Attribute("r")??"";
                        var column=ColumnIndex(address);
                        if(column>=100) throw new DomainException("Cada hoja admite hasta 100 columnas con contenido.");
                        while(values.Count<=column){values.Add("");numeric.Add(false);}
                        var type=(string?)cell.Attribute("t");
                        var value=cell.Element(X+"v")?.Value??"";
                        if(cell.Element(X+"f") is not null && value.Length==0) throw new DomainException($"La celda {address} tiene una fórmula sin resultado. Abre y guarda el archivo en Excel antes de subirlo.");
                        if(type=="e") throw new DomainException($"La celda {address} contiene un error de Excel ({value}). Corrígelo antes de continuar.");
                        if(type=="s") value=int.TryParse(value,out var index) && index>=0 && index<shared.Count?shared[index]:throw new DomainException("Texto compartido del Excel no válido.");
                        else if(type=="inlineStr") value=string.Concat(cell.Element(X+"is")?.Descendants(X+"t").Select(t=>t.Value)??[]);
                        else if(type is null or "n" && value.Length>0) {
                            numeric[column]=true;
                            if(decimal.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)) {
                                value=number.ToString("G29",CultureInfo.InvariantCulture);
                                if(int.TryParse((string?)cell.Attribute("s"),out var percentStyle) && percentStyle>=0 && percentStyle<styleIds.Count &&
                                    (styleIds[percentStyle] is "9" or "10" || formats.TryGetValue(styleIds[percentStyle],out var percentFormat) && Regex.IsMatch(percentFormat,@"^[0#.,]+%$")))
                                    value=(number*100).ToString("G29",CultureInfo.InvariantCulture);
                                if(int.TryParse((string?)cell.Attribute("s"),out var s) && s>=0 && s<styleIds.Count && formats.TryGetValue(styleIds[s],out var format) && Regex.IsMatch(format,"^0{2,30}$") && number>=0 && decimal.Truncate(number)==number)
                                    value=number.ToString(format,CultureInfo.InvariantCulture);
                            }
                        }
                        values[column]=value;
                    }
                }
                if(rows.Count>0) sheets.Add(new((string?)sheet.Attribute("name")??"Hoja",rows,numericRows));
            }
            if(sheets.Count==0) throw new DomainException("El Excel no contiene hojas con datos.");
            return new(sheets);
        }
        catch(DomainException){throw;}
        catch(Exception e) when(e is InvalidDataException or XmlException or FormatException or UriFormatException or OverflowException) {throw new DomainException("No se pudo leer el Excel. Comprueba que no esté dañado ni protegido con contraseña.");}
    }
    static int ColumnIndex(string reference) {
        var n=0;
        foreach(var c in reference.TakeWhile(char.IsLetter)) {n=checked(n*26+char.ToUpperInvariant(c)-'A'+1); if(n>100)return 100;}
        return Math.Max(0,n-1);
    }
    static List<List<string>> ReadCsv(string text)
    {
        text=text.TrimStart('\uFEFF');
        char Separator() {
            var quoted=false; var commas=0; var semicolons=0; var tabs=0;
            foreach(var ch in text) {
                if(ch=='"')quoted=!quoted;
                if(!quoted){if(ch is '\n' or '\r')break;if(ch==',')commas++;if(ch==';')semicolons++;if(ch=='\t')tabs++;}
            }
            return tabs>commas && tabs>semicolons?'\t':semicolons>commas?';':',';
        }
        var separator=Separator(); var rows=new List<List<string>>(); var row=new List<string>(); var value=new StringBuilder(); var inQuotes=false; var endedQuote=false;
        void Cell() {row.Add(value.ToString());value.Clear();endedQuote=false;if(row.Count>100)throw new DomainException("El CSV admite hasta 100 columnas.");}
        void Row() {Cell();rows.Add(row);row=[];if(rows.Count>MaxRows+1)throw new DomainException("El CSV admite hasta 20000 productos.");}
        for(var i=0;i<text.Length;i++) {
            var ch=text[i];
            if(inQuotes) {if(ch=='"'){if(i+1<text.Length && text[i+1]=='"'){value.Append('"');i++;}else{inQuotes=false;endedQuote=true;}}else value.Append(ch);}
            else if(ch=='"' && value.Length==0 && !endedQuote)inQuotes=true;
            else if(ch==separator)Cell();
            else if(ch is '\n' or '\r'){if(ch=='\r' && i+1<text.Length && text[i+1]=='\n')i++;Row();}
            else {if(endedQuote && !char.IsWhiteSpace(ch))throw new DomainException("El CSV contiene comillas mal cerradas.");if(!endedQuote)value.Append(ch);}
        }
        if(inQuotes)throw new DomainException("El CSV contiene comillas sin cerrar.");
        if(value.Length>0 || row.Count>0 || endedQuote)Row();
        if(rows.Count<2)throw new DomainException("El archivo debe incluir encabezados y al menos un producto.");
        return rows;
    }
    static decimal Number(string text,string separator,bool numeric,string field) {
        text=text.Trim().Replace("₡","").Replace("$","").Replace("%","").Replace(" ","").Replace("\u00a0","");
        if(text.Length==0)throw new DomainException($"Falta {field}.");
        var dec=numeric?".":separator;
        var pattern=dec==","?@"^[+-]?(?:\d+|\d{1,3}(?:\.\d{3})+)(?:,\d+)?$":@"^[+-]?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?$";
        if(!Regex.IsMatch(text,pattern))throw new DomainException($"{field}: '{text}' no es un número válido con decimal '{dec}'.");
        text=dec==","?text.Replace(".","").Replace(",","."):text.Replace(",","");
        if(!decimal.TryParse(text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var result))throw new DomainException($"{field} no es válido.");
        return result;
    }
    public static object Merge(MergeRequest r)
    {
        if(r.Prefer is not ("first" or "second"))throw new DomainException("Elige qué archivo tiene prioridad para nombres y precios.");
        var merged=new Dictionary<string,MergedProduct>(StringComparer.OrdinalIgnoreCase);
        var issues=new List<object>();var conflicts=new List<object>();var inputRows=0;
        foreach(var source in new[]{r.First,r.Second}) {
            if(source is null || source.Rows is null || source.Mapping is null || source.Rows.Count==0 || source.Rows.Count>MaxRows || source.DecimalSeparator is not ("." or ","))throw new DomainException("Cada archivo debe contener productos. Revisa también el límite de filas y el separador decimal.");
            foreach(var required in new[]{"barcode","name","quantity","cost","sellingPrice"})
                if(!source.Mapping.TryGetValue(required,out var idx)||idx<0||idx>=100)throw new DomainException($"{source.Label}: elige la columna de {required}.");
            if(source.Mapping.Where(p=>p.Value>=0).Select(p=>p.Value).Distinct().Count()!=source.Mapping.Count(p=>p.Value>=0))throw new DomainException($"{source.Label}: cada campo debe usar una columna diferente.");
            for(var i=0;i<source.Rows.Count;i++) {
                var cells=source.Rows[i];if(cells.All(string.IsNullOrWhiteSpace))continue;inputRows++;
                string Value(string key)=>source.Mapping.TryGetValue(key,out var n)&&n>=0&&n<cells.Count?cells[n].Trim():"";
                decimal Parse(string key) {
                    var n=source.Mapping.GetValueOrDefault(key,-1);
                    var numeric=n>=0 && source.NumericCells is not null && i<source.NumericCells.Count && n<source.NumericCells[i].Count && source.NumericCells[i][n];
                    return Number(Value(key),source.DecimalSeparator,numeric,key);
                }
                try {
                    var tax=Value("taxRate").Length==0?0:Parse("taxRate");
                    var row=new MergedProduct(Value("barcode"),Value("name"),Parse("quantity"),Parse("cost"),Parse("sellingPrice"),tax);
                    ValidateRow(row);
                    if(merged.TryGetValue(row.Barcode,out var old)) {
                        var differences=new List<string>();
                        if(old.Name!=row.Name)differences.Add($"Nombre: «{old.Name}» / «{row.Name}»");
                        if(old.Cost!=row.Cost)differences.Add($"Compra: {old.Cost} / {row.Cost}");
                        if(old.SellingPrice!=row.SellingPrice)differences.Add($"Venta: {old.SellingPrice} / {row.SellingPrice}");
                        if(old.TaxRate!=row.TaxRate)differences.Add($"Impuesto: {old.TaxRate} / {row.TaxRate}");
                        if(differences.Count>0)conflicts.Add(new{barcode=row.Barcode,source=source.Label,row=i+source.StartRow,details=differences});
                        var winner=r.Prefer=="second"?row:old;
                        var total=old.Quantity+row.Quantity;
                        ValidateNumbers(winner.Cost,winner.SellingPrice,total,winner.TaxRate);
                        merged[row.Barcode]=winner with{Quantity=total};
                    }else merged.Add(row.Barcode,row);
                }catch(DomainException e){issues.Add(new{source=source.Label,row=i+source.StartRow,error=e.Message});}
            }
        }
        if(inputRows==0)throw new DomainException("No hay productos para fusionar.");
        return new{rows=merged.Values.ToList(),issues,conflicts,inputRows,uniqueProducts=merged.Count,repeatedRows=inputRows-issues.Count-merged.Count,totalQuantity=merged.Values.Sum(x=>x.Quantity)};
    }
    public static void ValidateNumbers(decimal cost,decimal price,decimal quantity,decimal tax) {
        if(cost<0 || price<0 || quantity<0 || cost>1_000_000_000 || price>1_000_000_000 || quantity>1_000_000_000 || tax<0 || tax>100)
            throw new DomainException("Compra, venta y cantidad deben estar entre 0 y 1000000000; impuesto entre 0 y 100.");
        if(decimal.Round(quantity,3)!=quantity)throw new DomainException("La cantidad admite hasta 3 decimales.");
        if(decimal.Round(cost,2)!=cost || decimal.Round(price,2)!=price)throw new DomainException("Los precios admiten hasta 2 decimales.");
    }
    static void ValidateRow(MergedProduct row) {
        if(string.IsNullOrWhiteSpace(row.Barcode)||row.Barcode.Length>100||row.Barcode.Any(char.IsControl))throw new DomainException("Falta el código de barras o no es válido.");
        if(string.IsNullOrWhiteSpace(row.Name)||row.Name.Length>160)throw new DomainException("El nombre debe tener de 1 a 160 caracteres.");
        ValidateNumbers(row.Cost,row.SellingPrice,row.Quantity,row.TaxRate);
    }
    public static void ValidateRows(List<MergedProduct>? rows) {
        if(rows is null || rows.Count==0 || rows.Count>2*MaxRows)throw new DomainException("El resultado debe contener entre 1 y 40000 productos.");
        var codes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows){ValidateRow(row);if(!codes.Add(row.Barcode.Trim()))throw new DomainException($"El código {row.Barcode} se repite. Fusiona los archivos de nuevo.");}
    }
    // All identifiers/text use inline strings, never formulas. Numbers remain numeric cells.
    public static byte[] Export(List<MergedProduct> rows)
    {
        if(rows.Count>40000)throw new DomainException("Se pueden exportar hasta 40000 productos.");
        return ExportTable(
            ["Código de barras","Nombre","Cantidad","Precio de compra","Precio de venta","Impuesto %","Código interno","Abreviación categoría","Importador","Código importador"],
            rows.Select(p => new object?[] {p.Barcode,p.Name,p.Quantity,p.Cost,p.SellingPrice,p.TaxRate,p.InternalCode,p.CategoryCode,p.ImporterName,p.ImporterCode}).ToList());
    }
    // Shared XLSX writer. OCR calls this with exactly four columns.
    public static byte[] ExportTable(string[] headers, IReadOnlyList<object?[]> rows)
    {
        if(headers.Length is <1 or >26 || rows.Any(row=>row.Length!=headers.Length)) throw new DomainException("La tabla de exportación no es válida.");
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true)) {
            void Part(string path,XDocument doc) {using var stream=zip.CreateEntry(path).Open();doc.Save(stream);}
            XNamespace types="http://schemas.openxmlformats.org/package/2006/content-types";
            XNamespace rel="http://schemas.openxmlformats.org/package/2006/relationships";
            Part("[Content_Types].xml",new(new XElement(types+"Types",new XElement(types+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(types+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),new XElement(types+"Override",new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),new XElement(types+"Override",new XAttribute("PartName","/xl/worksheets/sheet1.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
            Part("_rels/.rels",new(new XElement(rel+"Relationships",new XElement(rel+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/officeDocument"),new XAttribute("Target","xl/workbook.xml")))));
            Part("xl/workbook.xml",new(new XElement(X+"workbook",new XElement(X+"sheets",new XElement(X+"sheet",new XAttribute("name","Productos"),new XAttribute("sheetId",1),new XAttribute(R+"id","rId1"))))));
            Part("xl/_rels/workbook.xml.rels",new(new XElement(rel+"Relationships",new XElement(rel+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/worksheet"),new XAttribute("Target","worksheets/sheet1.xml")))));
            var data=new XElement(X+"sheetData");
            void Row(int index,object?[] values) {
                var row=new XElement(X+"row",new XAttribute("r",index));
                for(var i=0;i<values.Length;i++) {
                    var c=new XElement(X+"c",new XAttribute("r",$"{(char)('A'+i)}{index}"));
                    if(values[i] is decimal or long or int)c.Add(new XElement(X+"v",Convert.ToString(values[i],CultureInfo.InvariantCulture)));
                    else {c.Add(new XAttribute("t","inlineStr"));c.Add(new XElement(X+"is",new XElement(X+"t",new XAttribute(XNamespace.Xml+"space","preserve"),Convert.ToString(values[i])??"")));}
                    row.Add(c);
                }
                data.Add(row);
            }
            Row(1,headers.Cast<object?>().ToArray());
            for(var i=0;i<rows.Count;i++) Row(i+2,rows[i]);
            Part("xl/worksheets/sheet1.xml",new(new XElement(X+"worksheet",
                new XElement(X+"sheetViews",new XElement(X+"sheetView",new XAttribute("workbookViewId",0),new XElement(X+"pane",new XAttribute("ySplit",1),new XAttribute("topLeftCell","A2"),new XAttribute("state","frozen")))),
                new XElement(X+"cols",Enumerable.Range(1,headers.Length).Select(i=>new XElement(X+"col",new XAttribute("min",i),new XAttribute("max",i),new XAttribute("width",i==2?42:24),new XAttribute("customWidth",1)))),data,
                new XElement(X+"autoFilter",new XAttribute("ref",$"A1:{(char)('A'+headers.Length-1)}{rows.Count+1}")))));
        }
        return output.ToArray();
    }
}
