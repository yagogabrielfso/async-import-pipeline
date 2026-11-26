using ImportPipeline.Parsing;
using ImportPipeline.Pipeline;
using ImportPipeline.Processing;
using ImportPipeline.Rates;

namespace ImportPipeline.Tests;

public class RetryPolicyTests
{
    private static RetryOptions Options(int attempts, List<int>? retries = null) => new(
        attempts,
        Backoff: _ => TimeSpan.Zero,
        ShouldRetry: exception => exception is HttpRequestException,
        OnRetry: (attempt, _) => retries?.Add(attempt));

    [Fact]
    public async Task Retries_transient_failures_until_success()
    {
        var calls = 0;
        var retries = new List<int>();

        var result = await RetryPolicy.ExecuteAsync(
            _ => ++calls < 3 ? throw new HttpRequestException("falha") : Task.FromResult(42),
            Options(attempts: 3, retries),
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { 1, 2 }, retries);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => RetryPolicy.ExecuteAsync<int>(
            _ => { calls++; throw new HttpRequestException("falha"); },
            Options(attempts: 3),
            CancellationToken.None));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Does_not_retry_errors_that_are_not_transient()
    {
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RetryPolicy.ExecuteAsync<int>(
            _ => { calls++; throw new InvalidOperationException("bug"); },
            Options(attempts: 5),
            CancellationToken.None));

        Assert.Equal(1, calls);
    }
}

public class ExchangeRateCacheTests
{
    private static readonly RetryOptions NoDelay = RetryOptions.Default with { Backoff = _ => TimeSpan.Zero };

    [Fact]
    public async Task Concurrent_requests_for_the_same_currency_hit_the_provider_once()
    {
        var provider = new CountingProvider(TimeSpan.FromMilliseconds(50));
        var cache = new ExchangeRateCache(provider, NoDelay);

        var rates = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetRateAsync("USD", CancellationToken.None)));

        Assert.All(rates, rate => Assert.Equal(5m, rate));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Brl_does_not_call_the_provider()
    {
        var provider = new CountingProvider(TimeSpan.Zero);

        var rate = await new ExchangeRateCache(provider, NoDelay).GetRateAsync("brl", CancellationToken.None);

        Assert.Equal(1m, rate);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Simulated_provider_recovers_with_retry()
    {
        var provider = new SimulatedExchangeRateProvider(TimeSpan.Zero);

        var rate = await new ExchangeRateCache(provider, NoDelay).GetRateAsync("EUR", CancellationToken.None);

        Assert.Equal(5.87m, rate);
        Assert.Equal(2, provider.CallsFor("EUR"));
    }

    private sealed class CountingProvider(TimeSpan latency) : IExchangeRateProvider
    {
        private int _calls;

        public int Calls => _calls;

        public async Task<decimal> GetRateToBrlAsync(string currency, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(latency, cancellationToken);
            return 5m;
        }
    }
}

public class ConcurrentImporterTests
{
    private static async IAsyncEnumerable<ParseResult> Source(int count, params ParseResult[] extra)
    {
        for (var i = 1; i <= count; i++)
        {
            await Task.Yield();
            yield return ParseResult.Ok(i, new SaleRecord($"PED-{i:000}", new DateOnly(2026, 10, 1), "Sul", "Mouse", 1, 10m, "BRL"));
        }

        foreach (var result in extra)
            yield return result;
    }

    [Fact]
    public async Task Imports_all_records_and_separates_rejected_and_invalid()
    {
        var pipeline = new SalePipelineBuilder().Use(SaleSteps.Deduplicate());
        var duplicate = ParseResult.Ok(99, new SaleRecord("PED-001", new DateOnly(2026, 10, 1), "Sul", "Mouse", 1, 10m, "BRL"));
        var invalid = ParseResult.Fail(100, "data inválida");

        var summary = await new ConcurrentImporter(pipeline, workers: 4).ImportAsync(Source(30, duplicate, invalid));

        Assert.Equal(30, summary.Imported.Count);
        Assert.Single(summary.Rejected);
        Assert.Single(summary.Invalid);
        Assert.Equal("PED-001", summary.Imported[0].Sale.OrderId);
    }

    [Fact]
    public async Task Never_runs_more_records_at_once_than_the_number_of_workers()
    {
        var active = 0;
        var maxActive = 0;
        var pipeline = new SalePipelineBuilder().Use(async (_, cancellationToken, next) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(20, cancellationToken);
            Interlocked.Decrement(ref active);
            await next();
        });

        await new ConcurrentImporter(pipeline, workers: 3).ImportAsync(Source(30));

        Assert.InRange(maxActive, 2, 3);
    }

    [Fact]
    public async Task One_failing_record_does_not_stop_the_import()
    {
        var pipeline = new SalePipelineBuilder().Use((context, _, next) =>
            context.Sale.OrderId == "PED-005" ? throw new InvalidOperationException("quebrou") : next());

        var summary = await new ConcurrentImporter(pipeline, workers: 2).ImportAsync(Source(10));

        Assert.Equal(9, summary.Imported.Count);
        Assert.Equal("erro ao processar: quebrou", Assert.Single(summary.Rejected).Reason);
    }

    [Fact]
    public async Task Progress_is_reported_for_every_processed_record()
    {
        var events = 0;
        var importer = new ConcurrentImporter(new SalePipelineBuilder(), workers: 2);
        importer.ProgressChanged += (_, _) => Interlocked.Increment(ref events);

        await importer.ImportAsync(Source(15));

        Assert.Equal(15, events);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
            Interlocked.CompareExchange(ref target, value, current);
    }
}
