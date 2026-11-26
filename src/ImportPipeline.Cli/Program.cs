using System.Globalization;
using ImportPipeline.Parsing;
using ImportPipeline.Pipeline;
using ImportPipeline.Processing;
using ImportPipeline.Rates;
using ImportPipeline.Reports;

var culture = CultureInfo.GetCultureInfo("pt-BR");
var path = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
    ?? Path.Combine(AppContext.BaseDirectory, "data", "sales.csv");
var workers = ReadOption(args, "--workers", defaultValue: 4);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var retry = RetryOptions.Default with
{
    OnRetry = (attempt, exception) => Console.WriteLine($"  retry {attempt}: {exception.Message}")
};
var rates = new ExchangeRateCache(new SimulatedExchangeRateProvider(TimeSpan.FromMilliseconds(150)), retry);

var pipeline = new SalePipelineBuilder()
    .Use(SaleSteps.AllowCurrencies("BRL", "USD", "EUR"))
    .Use(SaleSteps.Deduplicate())
    .Use(SaleSteps.ConvertToBrl(rates));

var importer = new ConcurrentImporter(pipeline, workers);

Console.WriteLine($"Importando {Path.GetFileName(path)} com {workers} workers...");

using var reader = File.OpenText(path);
var summary = await importer.ImportAsync(CsvSaleReader.ReadAsync(reader, cancellation.Token), cancellation.Token);

Console.WriteLine();
Console.WriteLine($"Importadas: {summary.Imported.Count} · Rejeitadas: {summary.Rejected.Count} · Inválidas: {summary.Invalid.Count} · Tempo: {summary.Elapsed.TotalMilliseconds:N0} ms");

foreach (var invalid in summary.Invalid)
    Console.WriteLine($"  linha {invalid.Line}: {invalid.Error}");
foreach (var rejected in summary.Rejected)
    Console.WriteLine($"  {rejected.Sale.OrderId}: {rejected.Reason}");

Console.WriteLine();
Console.WriteLine("Vendas por região");
foreach (var region in SalesReports.ByRegion(summary.Imported))
    Console.WriteLine($"  {region.Region,-10} {region.Orders,3} pedidos  {region.Total.ToString("C", culture),15}  {region.Share,5}%");

Console.WriteLine();
Console.WriteLine("Top 3 produtos");
foreach (var product in SalesReports.TopProducts(summary.Imported, 3))
    Console.WriteLine($"  {product.Position}. {product.Product,-18} {product.Quantity,4} un.  {product.Revenue.ToString("C", culture),15}");

Console.WriteLine();
Console.WriteLine("Evolução mensal");
foreach (var month in SalesReports.MonthlyTrend(summary.Imported))
{
    var growth = month.GrowthPercent is { } value ? $"{value:+0.0;-0.0}%" : "-";
    Console.WriteLine($"  {month.Month:00}/{month.Year}  {month.Total.ToString("C", culture),15}  {growth,8}");
}

static int ReadOption(string[] args, string name, int defaultValue)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : defaultValue;
}
