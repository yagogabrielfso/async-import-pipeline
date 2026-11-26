using System.Collections.Concurrent;
using ImportPipeline.Rates;

namespace ImportPipeline.Pipeline;

/// <summary>Etapas prontas do pipeline. Cada uma é uma função que recebe o próximo handler.</summary>
public static class SaleSteps
{
    public static Func<SaleHandler, SaleHandler> AllowCurrencies(params string[] currencies)
    {
        var allowed = new HashSet<string>(currencies, StringComparer.OrdinalIgnoreCase);

        return next => (context, cancellationToken) =>
        {
            if (!allowed.Contains(context.Sale.Currency))
            {
                context.Reject($"moeda não suportada: {context.Sale.Currency}");
                return Task.CompletedTask;
            }

            return next(context, cancellationToken);
        };
    }

    /// <summary>Descarta pedidos repetidos. Seguro com vários workers ao mesmo tempo.</summary>
    public static Func<SaleHandler, SaleHandler> Deduplicate()
    {
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        return next => (context, cancellationToken) =>
        {
            if (!seen.TryAdd(context.Sale.OrderId, 0))
            {
                context.Reject($"pedido duplicado: {context.Sale.OrderId}");
                return Task.CompletedTask;
            }

            return next(context, cancellationToken);
        };
    }

    public static Func<SaleHandler, SaleHandler> ConvertToBrl(ExchangeRateCache rates) =>
        next => async (context, cancellationToken) =>
        {
            var rate = await rates.GetRateAsync(context.Sale.Currency, cancellationToken);
            context.TotalBrl = Math.Round(context.Sale.Total * rate, 2);
            await next(context, cancellationToken);
        };
}
