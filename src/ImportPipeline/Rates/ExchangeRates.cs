using System.Collections.Concurrent;

namespace ImportPipeline.Rates;

public interface IExchangeRateProvider
{
    Task<decimal> GetRateToBrlAsync(string currency, CancellationToken cancellationToken);
}

/// <summary>
/// Cache de cotações para uso concorrente. <see cref="Lazy{T}"/> com <see cref="Task"/>
/// garante uma única chamada ao provedor por moeda, mesmo com vários workers pedindo a
/// mesma cotação ao mesmo tempo. Falhas não ficam no cache: a próxima chamada tenta de novo.
/// </summary>
public sealed class ExchangeRateCache(IExchangeRateProvider provider, RetryOptions? retry = null)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<decimal>>> _rates = new(StringComparer.OrdinalIgnoreCase);
    private readonly RetryOptions _retry = retry ?? RetryOptions.Default;

    public async Task<decimal> GetRateAsync(string currency, CancellationToken cancellationToken)
    {
        if (string.Equals(currency, "BRL", StringComparison.OrdinalIgnoreCase))
            return 1m;

        var entry = _rates.GetOrAdd(currency, key => new Lazy<Task<decimal>>(() => FetchAsync(key)));

        try
        {
            // A busca é compartilhada e não usa o token de quem chamou primeiro;
            // cada chamador só para de esperar quando o próprio token é cancelado.
            return await entry.Value.WaitAsync(cancellationToken);
        }
        catch when (entry.Value.IsFaulted)
        {
            _rates.TryRemove(new KeyValuePair<string, Lazy<Task<decimal>>>(currency, entry));
            throw;
        }
    }

    private Task<decimal> FetchAsync(string currency) =>
        RetryPolicy.ExecuteAsync(token => provider.GetRateToBrlAsync(currency, token), _retry, CancellationToken.None);
}

/// <summary>
/// Provedor de exemplo com latência e uma falha transitória na primeira chamada
/// de cada moeda, para mostrar o retry funcionando.
/// </summary>
public sealed class SimulatedExchangeRateProvider(TimeSpan latency) : IExchangeRateProvider
{
    private static readonly Dictionary<string, decimal> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 5.42m,
        ["EUR"] = 5.87m
    };

    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

    public int CallsFor(string currency) => _calls.GetValueOrDefault(currency);

    public async Task<decimal> GetRateToBrlAsync(string currency, CancellationToken cancellationToken)
    {
        var call = _calls.AddOrUpdate(currency, 1, (_, count) => count + 1);
        await Task.Delay(latency, cancellationToken);

        if (call == 1)
            throw new HttpRequestException($"Serviço de cotação indisponível ({currency}).");

        return Rates.TryGetValue(currency, out var rate)
            ? rate
            : throw new KeyNotFoundException($"Sem cotação para {currency}.");
    }
}
