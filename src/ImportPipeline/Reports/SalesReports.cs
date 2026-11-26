using ImportPipeline.Processing;

namespace ImportPipeline.Reports;

public sealed record RegionTotal(string Region, int Orders, decimal Total, decimal Share);

public sealed record ProductRanking(int Position, string Product, int Quantity, decimal Revenue);

public sealed record MonthlyTotal(int Year, int Month, decimal Total, decimal? GrowthPercent);

public sealed record SalesFilter(string? Region = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>Relatórios com LINQ sobre as vendas importadas (valores sempre em BRL).</summary>
public static class SalesReports
{
    public static IEnumerable<ImportedSale> Filter(IEnumerable<ImportedSale> sales, SalesFilter filter) =>
        sales
            .WhereIf(filter.Region is not null, sale => string.Equals(sale.Sale.Region, filter.Region, StringComparison.OrdinalIgnoreCase))
            .WhereIf(filter.From is not null, sale => sale.Sale.Date >= filter.From)
            .WhereIf(filter.To is not null, sale => sale.Sale.Date <= filter.To);

    public static IReadOnlyList<RegionTotal> ByRegion(IEnumerable<ImportedSale> sales)
    {
        var groups = sales
            .GroupBy(sale => sale.Sale.Region)
            .Select(group => (Region: group.Key, Orders: group.Count(), Total: group.Sum(sale => sale.TotalBrl)))
            .ToList();

        var grandTotal = groups.Sum(group => group.Total);

        return groups
            .OrderByDescending(group => group.Total)
            .ThenBy(group => group.Region, StringComparer.Ordinal)
            .Select(group => new RegionTotal(
                group.Region,
                group.Orders,
                group.Total,
                grandTotal == 0 ? 0 : Math.Round(group.Total / grandTotal * 100, 1)))
            .ToList();
    }

    public static IReadOnlyList<ProductRanking> TopProducts(IEnumerable<ImportedSale> sales, int top) =>
        sales
            .GroupBy(sale => sale.Sale.Product)
            .Select(group => new
            {
                Product = group.Key,
                Quantity = group.Sum(sale => sale.Sale.Quantity),
                Revenue = group.Sum(sale => sale.TotalBrl)
            })
            .OrderByDescending(product => product.Revenue)
            .ThenBy(product => product.Product, StringComparer.Ordinal)
            .Take(top)
            .Select((product, index) => new ProductRanking(index + 1, product.Product, product.Quantity, product.Revenue))
            .ToList();

    public static IReadOnlyList<MonthlyTotal> MonthlyTrend(IEnumerable<ImportedSale> sales)
    {
        var months = sales
            .GroupBy(sale => (sale.Sale.Date.Year, sale.Sale.Date.Month))
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group => new MonthlyTotal(group.Key.Year, group.Key.Month, group.Sum(sale => sale.TotalBrl), null))
            .ToList();

        if (months.Count == 0)
            return months;

        // O primeiro mês não tem anterior; os demais ganham o crescimento sobre o mês anterior.
        return months
            .Take(1)
            .Concat(months.Pairwise().Select(pair => pair.Current with
            {
                GrowthPercent = pair.Previous.Total == 0
                    ? null
                    : Math.Round((pair.Current.Total - pair.Previous.Total) / pair.Previous.Total * 100, 1)
            }))
            .ToList();
    }
}
