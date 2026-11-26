using ImportPipeline.Parsing;
using ImportPipeline.Pipeline;

namespace ImportPipeline.Tests;

public class CsvSaleReaderTests
{
    [Fact]
    public async Task Reads_valid_lines_and_reports_invalid_ones_without_stopping()
    {
        const string csv = """
            order_id,date,region,product,quantity,unit_price,currency
            PED-1,2026-10-01,Sul,Mouse,2,100.50,brl
            PED-2,2026-13-01,Sul,Mouse,1,100.00,BRL

            PED-3,2026-10-02,Norte,Teclado,1,300.00,USD
            PED-4,2026-10-03,Norte
            """;

        var results = new List<ParseResult>();
        await foreach (var result in CsvSaleReader.ReadAsync(new StringReader(csv)))
            results.Add(result);

        Assert.Equal(4, results.Count);
        Assert.Equal(new[] { "PED-1", "PED-3" }, results.Where(r => r.IsSuccess).Select(r => r.Record!.OrderId));
        Assert.Equal("BRL", results[0].Record!.Currency);
        Assert.Equal(201.00m, results[0].Record!.Total);

        var invalid = results.Where(r => !r.IsSuccess).ToList();
        Assert.Equal(3, invalid[0].Line);
        Assert.Contains("data inválida", invalid[0].Error);
        Assert.Equal(6, invalid[1].Line);
        Assert.Contains("7 colunas", invalid[1].Error);
    }

    [Theory]
    [InlineData("PED-1,2026-10-01,Sul,Mouse,0,10.00,BRL", "quantidade")]
    [InlineData("PED-1,2026-10-01,Sul,Mouse,1,dez,BRL", "preço")]
    [InlineData(",2026-10-01,Sul,Mouse,1,10.00,BRL", "order_id")]
    public void Invalid_fields_are_reported(string line, string expectedError)
    {
        var result = CsvSaleReader.Parse(1, line);

        Assert.False(result.IsSuccess);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public async Task Reading_stops_when_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var lines = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"PED-{i},2026-10-01,Sul,Mouse,1,10.00,BRL"));
        var read = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in CsvSaleReader.ReadAsync(new StringReader(lines), cancellation.Token))
            {
                if (++read == 10)
                    cancellation.Cancel();
            }
        });

        Assert.Equal(10, read);
    }
}

public class SalePipelineTests
{
    private static ImportContext Context(string orderId = "PED-1", string currency = "BRL") =>
        new(new SaleRecord(orderId, new DateOnly(2026, 10, 1), "Sul", "Mouse", 1, 10m, currency));

    [Fact]
    public async Task Middlewares_run_in_registration_order_around_the_terminal()
    {
        var log = new List<string>();
        var handler = new SalePipelineBuilder()
            .Use(async (_, _, next) => { log.Add("A antes"); await next(); log.Add("A depois"); })
            .Use(async (_, _, next) => { log.Add("B antes"); await next(); log.Add("B depois"); })
            .Build((_, _) => { log.Add("terminal"); return Task.CompletedTask; });

        await handler(Context(), CancellationToken.None);

        Assert.Equal(new[] { "A antes", "B antes", "terminal", "B depois", "A depois" }, log);
    }

    [Fact]
    public async Task Rejecting_step_short_circuits_the_rest()
    {
        var reachedTerminal = false;
        var handler = new SalePipelineBuilder()
            .Use(SaleSteps.AllowCurrencies("BRL"))
            .Build((_, _) => { reachedTerminal = true; return Task.CompletedTask; });

        var context = Context(currency: "JPY");
        await handler(context, CancellationToken.None);

        Assert.False(reachedTerminal);
        Assert.Equal("moeda não suportada: JPY", context.RejectionReason);
    }

    [Fact]
    public async Task Deduplicate_rejects_the_second_occurrence()
    {
        var handler = new SalePipelineBuilder()
            .Use(SaleSteps.Deduplicate())
            .Build((_, _) => Task.CompletedTask);

        var first = Context("PED-7");
        var second = Context("ped-7");
        await handler(first, CancellationToken.None);
        await handler(second, CancellationToken.None);

        Assert.False(first.IsRejected);
        Assert.True(second.IsRejected);
    }
}
