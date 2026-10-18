using System.Xml;

namespace AlmacenPrimavera;

public record OcrProductRow(string? ProductCode, string? Name, decimal? Quantity, decimal? Price);
public record OcrExportRequest(List<OcrProductRow?>? Rows, bool Reviewed = false);

public static class OcrExport
{
    public static byte[] Create(OcrExportRequest request)
    {
        if (!request.Reviewed) throw new DomainException("Revisa los resultados del OCR y confirma que los comparaste con la imagen.");
        if (request.Rows is null || request.Rows.Count is <1 or >5000)
            throw new DomainException("La exportación debe contener entre 1 y 5000 productos.");
        var rows = new List<object?[]>();
        for (var i = 0; i < request.Rows.Count; i++)
        {
            var p = request.Rows[i] ?? throw new DomainException($"Fila {i+1}: faltan los datos del producto.");
            var code = p.ProductCode?.Trim() ?? "";
            var name = p.Name?.Trim() ?? "";
            if (code.Length is <1 or >100 || code.Any(char.IsControl))
                throw new DomainException($"Fila {i+1}: indica un Product Code válido, de hasta 100 caracteres.");
            if (name.Length is <1 or >300 || name.Any(char.IsControl))
                throw new DomainException($"Fila {i+1}: indica un Name válido, de hasta 300 caracteres.");
            try { XmlConvert.VerifyXmlChars(code); XmlConvert.VerifyXmlChars(name); }
            catch (XmlException) { throw new DomainException($"Fila {i+1}: el texto contiene caracteres no válidos para Excel."); }
            if (p.Quantity is null || p.Price is null)
                throw new DomainException($"Fila {i+1}: completa Quantity y Price. Un dato sin leer no se convierte en cero.");
            if (p.Quantity < 0 || p.Quantity > 1_000_000_000 || p.Price < 0 || p.Price > 1_000_000_000)
                throw new DomainException($"Fila {i+1}: Quantity y Price deben estar entre 0 y 1000000000.");
            if (decimal.Round(p.Quantity.Value,3) != p.Quantity || decimal.Round(p.Price.Value,2) != p.Price)
                throw new DomainException($"Fila {i+1}: Quantity admite 3 decimales y Price admite 2.");
            rows.Add([code,name,p.Quantity.Value,p.Price.Value]);
        }
        // Preserve repeated rows and text identifiers. OCR is not inventory import.
        return ExcelFiles.ExportTable(["Product Code","Name","Quantity","Price"],rows);
    }
}
