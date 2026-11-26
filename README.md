# async-import-pipeline

[![CI](https://github.com/yagogabrielfso/async-import-pipeline/actions/workflows/ci.yml/badge.svg)](https://github.com/yagogabrielfso/async-import-pipeline/actions/workflows/ci.yml)

Importador de vendas em CSV feito em .NET 10. Lê o arquivo em streaming, processa os registros em paralelo, converte moedas consultando um serviço externo (simulado) e no fim gera relatórios.

Montei esse projeto para juntar num lugar só coisas que uso no dia a dia e que costumam aparecer em entrevista: async/await de verdade (não só `await` em chamada de banco), delegates e LINQ.

## Como funciona

```
CSV ──► CsvSaleReader ──► Channel (limitado) ──► N workers ──► pipeline de etapas ──► resultado
        IAsyncEnumerable      back-pressure         paralelo     (delegates)
```

1. `CsvSaleReader.ReadAsync` devolve um `IAsyncEnumerable<ParseResult>`. Lê linha a linha, então o arquivo nunca fica inteiro em memória. Linha inválida não para a importação: vira um resultado com o número da linha e o motivo.
2. `ConcurrentImporter` coloca os registros num `Channel` limitado. Se os workers ficam para trás, a leitura espera (back-pressure). O número de workers é configurável.
3. Cada registro passa por um pipeline montado com delegates, no mesmo estilo dos middlewares do ASP.NET Core:

```csharp
var pipeline = new SalePipelineBuilder()
    .Use(SaleSteps.AllowCurrencies("BRL", "USD", "EUR"))
    .Use(SaleSteps.Deduplicate())
    .Use(SaleSteps.ConvertToBrl(rates));
```

Cada etapa é um `Func<SaleHandler, SaleHandler>`: recebe a próxima e decide se continua ou rejeita o registro.

4. No final, os relatórios saem com LINQ: total por região com participação, top produtos e evolução mês a mês.

## Pontos que valem olhar

**Async/await**
- `IAsyncEnumerable` com `[EnumeratorCancellation]` no leitor.
- Produtor/consumidor com `Channel<T>` e `BoundedChannelFullMode.Wait`.
- `CancellationToken` passado em tudo; Ctrl+C no console cancela a importação.
- `ExchangeRateCache` usa `ConcurrentDictionary<string, Lazy<Task<decimal>>>`: 20 workers pedindo a cotação do dólar ao mesmo tempo geram **uma** chamada ao serviço. A busca é compartilhada e não usa o token do primeiro chamador (`WaitAsync` em cada um), e uma falha não fica presa no cache.
- Erro em um registro vira rejeição; não derruba a importação.

**Delegates**
- `SaleHandler` é um delegate próprio, e o pipeline é uma composição de `Func<SaleHandler, SaleHandler>`.
- `RetryPolicy` recebe o comportamento por delegates: quantas tentativas, o backoff (`Func<int, TimeSpan>`), quais erros repetir (`Func<Exception, bool>`) e um callback a cada tentativa.
- `ConcurrentImporter.ProgressChanged` é um evento (`EventHandler<ImportProgress>`).

**LINQ**
- `GroupBy` com chave composta, `Sum`, `OrderByDescending`/`ThenBy` e `Select` com índice para o ranking.
- Operadores próprios em `EnumerableExtensions`: `WhereIf` para filtros opcionais e `Pairwise` para comparar cada mês com o anterior. Os dois têm execução adiada, e há teste que garante isso.

## Rodando

```bash
dotnet run --project src/ImportPipeline.Cli -- --workers 4
```

Usa o arquivo de exemplo em `src/ImportPipeline.Cli/data/sales.csv`, que tem de propósito um pedido duplicado, uma moeda não suportada e algumas linhas quebradas. Para outro arquivo:

```bash
dotnet run --project src/ImportPipeline.Cli -- caminho/do/arquivo.csv --workers 8
```

O serviço de cotação simulado falha na primeira chamada de cada moeda, então dá para ver o retry no console.

## Testes

```bash
dotnet test
```

Entre os testes estão: ordem de execução dos middlewares, curto-circuito na rejeição, retry (sucesso depois de falhas, limite de tentativas, erro que não deve ser repetido), cache de cotação com chamadas concorrentes, limite de paralelismo dos workers, cancelamento da leitura e execução adiada dos operadores LINQ.
