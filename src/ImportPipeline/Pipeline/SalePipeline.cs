using ImportPipeline.Parsing;

namespace ImportPipeline.Pipeline;

public sealed class ImportContext(SaleRecord sale)
{
    public SaleRecord Sale { get; } = sale;
    public decimal? TotalBrl { get; set; }
    public string? RejectionReason { get; private set; }
    public bool IsRejected => RejectionReason is not null;

    public void Reject(string reason) => RejectionReason ??= reason;
}

/// <summary>Uma etapa do pipeline. Mesmo formato do <c>RequestDelegate</c> do ASP.NET Core.</summary>
public delegate Task SaleHandler(ImportContext context, CancellationToken cancellationToken);

/// <summary>
/// Monta o pipeline como uma cadeia de delegates: cada middleware recebe o
/// próximo e decide se continua, faz algo antes e depois, ou interrompe.
/// </summary>
public sealed class SalePipelineBuilder
{
    private readonly List<Func<SaleHandler, SaleHandler>> _middlewares = [];

    public SalePipelineBuilder Use(Func<SaleHandler, SaleHandler> middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>Atalho para middlewares escritos em linha, com <paramref name="inline"/> chamando o próximo quando quiser.</summary>
    public SalePipelineBuilder Use(Func<ImportContext, CancellationToken, Func<Task>, Task> inline) =>
        Use(next => (context, cancellationToken) => inline(context, cancellationToken, () => next(context, cancellationToken)));

    public SaleHandler Build(SaleHandler terminal)
    {
        var handler = terminal;

        // Monta de trás para frente: o primeiro registrado é o primeiro a executar.
        for (var i = _middlewares.Count - 1; i >= 0; i--)
            handler = _middlewares[i](handler);

        return handler;
    }
}
