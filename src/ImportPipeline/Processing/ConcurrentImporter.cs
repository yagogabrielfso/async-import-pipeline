using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using ImportPipeline.Parsing;
using ImportPipeline.Pipeline;

namespace ImportPipeline.Processing;

public sealed record ImportedSale(SaleRecord Sale, decimal TotalBrl);

public sealed record RejectedSale(SaleRecord Sale, string Reason);

public sealed record ImportProgress(int Read, int Imported, int Rejected, int Invalid);

public sealed record ImportSummary(
    IReadOnlyList<ImportedSale> Imported,
    IReadOnlyList<RejectedSale> Rejected,
    IReadOnlyList<ParseResult> Invalid,
    TimeSpan Elapsed);

/// <summary>
/// Produtor/consumidor com <see cref="Channel{T}"/>: um produtor lê o arquivo de forma
/// assíncrona e N workers processam em paralelo. O canal é limitado, então a leitura
/// espera quando os workers ficam para trás (back-pressure) e a memória não cresce
/// com o tamanho do arquivo.
/// </summary>
public sealed class ConcurrentImporter
{
    private readonly SalePipelineBuilder _pipeline;
    private readonly int _workers;
    private readonly int _capacity;

    public ConcurrentImporter(SalePipelineBuilder pipeline, int workers = 4, int capacity = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _pipeline = pipeline;
        _workers = workers;
        _capacity = capacity;
    }

    public event EventHandler<ImportProgress>? ProgressChanged;

    public async Task<ImportSummary> ImportAsync(IAsyncEnumerable<ParseResult> source, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var counters = new Counters();
        var imported = new ConcurrentQueue<ImportedSale>();
        var rejected = new ConcurrentQueue<RejectedSale>();
        var invalid = new ConcurrentQueue<ParseResult>();

        var handler = _pipeline.Build((context, _) =>
        {
            imported.Enqueue(new ImportedSale(context.Sale, context.TotalBrl ?? context.Sale.Total));
            return Task.CompletedTask;
        });

        var channel = Channel.CreateBounded<SaleRecord>(new BoundedChannelOptions(_capacity)
        {
            SingleWriter = true,
            SingleReader = _workers == 1,
            FullMode = BoundedChannelFullMode.Wait
        });

        var producer = ProduceAsync(source, channel.Writer, invalid, counters, cancellationToken);
        var consumers = Enumerable.Range(0, _workers)
            .Select(_ => ConsumeAsync(channel.Reader, handler, rejected, counters, cancellationToken))
            .ToArray();

        await Task.WhenAll(consumers.Append(producer));

        return new ImportSummary(
            imported.OrderBy(sale => sale.Sale.OrderId, StringComparer.Ordinal).ToList(),
            rejected.OrderBy(sale => sale.Sale.OrderId, StringComparer.Ordinal).ToList(),
            invalid.OrderBy(result => result.Line).ToList(),
            stopwatch.Elapsed);
    }

    private async Task ProduceAsync(
        IAsyncEnumerable<ParseResult> source,
        ChannelWriter<SaleRecord> writer,
        ConcurrentQueue<ParseResult> invalid,
        Counters counters,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in source.WithCancellation(cancellationToken))
            {
                Interlocked.Increment(ref counters.Read);

                if (result.Record is null)
                {
                    invalid.Enqueue(result);
                    Interlocked.Increment(ref counters.Invalid);
                    continue;
                }

                // Aguarda aqui se o canal estiver cheio.
                await writer.WriteAsync(result.Record, cancellationToken);
            }

            writer.Complete();
        }
        catch (Exception exception)
        {
            writer.Complete(exception);
            throw;
        }
    }

    private async Task ConsumeAsync(
        ChannelReader<SaleRecord> reader,
        SaleHandler handler,
        ConcurrentQueue<RejectedSale> rejected,
        Counters counters,
        CancellationToken cancellationToken)
    {
        // Yield faz o restante rodar no thread pool, para os workers não começarem em sequência.
        await Task.Yield();

        await foreach (var sale in reader.ReadAllAsync(cancellationToken))
        {
            var context = new ImportContext(sale);

            try
            {
                await handler(context, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Um registro com erro não derruba a importação inteira.
                context.Reject($"erro ao processar: {exception.Message}");
            }

            if (context.IsRejected)
            {
                rejected.Enqueue(new RejectedSale(sale, context.RejectionReason!));
                Interlocked.Increment(ref counters.Rejected);
            }
            else
            {
                Interlocked.Increment(ref counters.Imported);
            }

            ProgressChanged?.Invoke(this, counters.Snapshot());
        }
    }

    private sealed class Counters
    {
        public int Read;
        public int Imported;
        public int Rejected;
        public int Invalid;

        public ImportProgress Snapshot() => new(
            Volatile.Read(ref Read),
            Volatile.Read(ref Imported),
            Volatile.Read(ref Rejected),
            Volatile.Read(ref Invalid));
    }
}
