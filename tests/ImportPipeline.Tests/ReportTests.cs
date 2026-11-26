using ImportPipeline.Parsing;
using ImportPipeline.Processing;
using ImportPipeline.Reports;

namespace ImportPipeline.Tests;

public class SalesReportsTests
{
    private static ImportedSale Sale(string id, string date, string region, string product, int quantity, decimal totalBrl) =>
        new(new SaleRecord(id, DateOnly.Parse(date), region, product, quantity, totalBrl / quantity, "BRL"), totalBrl);

    private static readonly ImportedSale[] Sales =
    [
        Sale("1", "2026-09-10", "Sul", "Notebook", 1, 4000m),
        Sale("2", "2026-09-20", "Norte", "Mouse", 2, 200m),
        Sale("3", "2026-10-05", "Sul", "Mouse", 3, 300m),
        Sale("4", "2026-10-15", "Sudeste", "Notebook", 1, 4000m),
        Sale("5", "2026-11-01", "Sudeste", "Monitor", 1, 1500m)
    ];

    [Fact]
    public void Groups_by_region_ordered_by_total_with_share()
    {
        var regions = SalesReports.ByRegion(Sales);

        Assert.Equal(new[] { "Sudeste", "Sul", "Norte" }, regions.Select(r => r.Region));
        Assert.Equal(5500m, regions[0].Total);
        Assert.Equal(2, regions[0].Orders);
        Assert.Equal(100m, regions.Sum(r => r.Share), precision: 0);
    }

    [Fact]
    public void Ranks_top_products_by_revenue()
    {
        var top = SalesReports.TopProducts(Sales, 2);

        Assert.Equal(2, top.Count);
        Assert.Equal(new ProductRanking(1, "Notebook", 2, 8000m), top[0]);
        Assert.Equal(new ProductRanking(2, "Monitor", 1, 1500m), top[1]);
    }

    [Fact]
    public void Monthly_trend_computes_growth_over_previous_month()
    {
        var months = SalesReports.MonthlyTrend(Sales);

        Assert.Equal(3, months.Count);
        Assert.Null(months[0].GrowthPercent);
        Assert.Equal(4200m, months[0].Total);
        Assert.Equal(4300m, months[1].Total);
        Assert.Equal(2.4m, months[1].GrowthPercent);
        Assert.Equal(-65.1m, months[2].GrowthPercent);
    }

    [Fact]
    public void Optional_filters_are_combined_only_when_present()
    {
        Assert.Equal(5, SalesReports.Filter(Sales, new SalesFilter()).Count());
        Assert.Equal(2, SalesReports.Filter(Sales, new SalesFilter(Region: "sul")).Count());
        Assert.Equal(new[] { "3", "4" }, SalesReports
            .Filter(Sales, new SalesFilter(From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 31)))
            .Select(s => s.Sale.OrderId));
    }
}

public class EnumerableExtensionsTests
{
    [Fact]
    public void Pairwise_returns_consecutive_pairs()
    {
        Assert.Equal(new[] { (1, 2), (2, 3), (3, 4) }, new[] { 1, 2, 3, 4 }.Pairwise());
        Assert.Empty(new[] { 1 }.Pairwise());
    }

    [Fact]
    public void Operators_use_deferred_execution()
    {
        var enumerated = 0;
        IEnumerable<int> Source()
        {
            enumerated++;
            yield return 1;
            yield return 2;
        }

        var query = Source().Pairwise().WhereIf(true, pair => pair.Current > 0);
        Assert.Equal(0, enumerated);

        _ = query.ToList();
        _ = query.ToList();
        Assert.Equal(2, enumerated);
    }
}
