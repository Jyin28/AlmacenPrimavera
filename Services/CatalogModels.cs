namespace AlmacenPrimavera;

public record CategoryEdit(string Name, string? Abbreviation, long? ParentId, decimal TargetMarkup = 35, decimal MinimumMargin = 20);
public record SubcategoryEdit(string Name, long CategoryId, decimal TargetMarkup = 35, decimal MinimumMargin = 20);
public record ImporterEdit(string Name);
public record ProductEdit(string Name, string Barcode, long? CategoryId, long? ImporterId,
    decimal Cost, decimal SellingPrice, decimal TaxRate, decimal Quantity, int Version = 0);
public record Classification(long[] Ids, long? CategoryId, long? ImporterId);
public record PriceRequest(long ProductId, decimal ExtraCost = 0, decimal? Markup = null, int RoundTo = 1);
public record PriceApply(long ProductId, int Version, decimal SellingPrice, bool CompleteReview = false);
public record SourceTable(List<List<string>> Rows, Dictionary<string,int> Mapping, string DecimalSeparator = ".", string Label = "Archivo", List<List<bool>>? NumericCells = null, int StartRow = 2);
public record MergeRequest(SourceTable First, SourceTable Second, string Prefer = "second");
public record MergedProduct(string Barcode, string Name, decimal Quantity, decimal Cost, decimal SellingPrice, decimal TaxRate = 0,
    string? InternalCode = null, string? CategoryCode = null, string? ImporterName = null, long? ImporterCode = null);
public record ExportRequest(List<MergedProduct> Rows);
public record ImportRequest(List<MergedProduct> Rows);
