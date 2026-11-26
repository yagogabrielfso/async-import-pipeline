using System.Globalization;
using System.Runtime.CompilerServices;

namespace ImportPipeline.Parsing;

public sealed record SaleRecord(
    string OrderId,
    DateOnly Date,
    string Region,
    string Product,
    int Quantity,
    decimal UnitPrice,
    string Currency)
{
    public decimal Total => Quantity * UnitPrice;
}

/// <summary>Resultado da leitura de uma linha: o registro ou o motivo do erro.</summary>
public readonly record struct ParseResult(int Line, SaleRecord? Record, string? Error)
{
    public bool IsSuccess => Record is not null;

    public static ParseResult Ok(int line, SaleRecord record) => new(line, record, null);

    public static ParseResult Fail(int line, string error) => new(line, null, error);
}

public static class CsvSaleReader
{
    public const string Header = "order_id,date,region,product,quantity,unit_price,currency";
    private const int ColumnCount = 7;

    /// <summary>
    /// Lê o arquivo linha a linha, sem carregar tudo em memória. Linhas inválidas
    /// não interrompem a leitura: viram um <see cref="ParseResult"/> com o erro.
    /// </summary>
    public static async IAsyncEnumerable<ParseResult> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (lineNumber == 1 && line.StartsWith("order_id", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return Parse(lineNumber, line);
        }
    }

    public static ParseResult Parse(int lineNumber, string line)
    {
        var columns = line.Split(',', StringSplitOptions.TrimEntries);

        if (columns.Length != ColumnCount)
            return ParseResult.Fail(lineNumber, $"esperadas {ColumnCount} colunas, encontradas {columns.Length}");
        if (string.IsNullOrEmpty(columns[0]))
            return ParseResult.Fail(lineNumber, "order_id vazio");
        if (!DateOnly.TryParseExact(columns[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return ParseResult.Fail(lineNumber, $"data inválida: '{columns[1]}'");
        if (!int.TryParse(columns[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            return ParseResult.Fail(lineNumber, $"quantidade inválida: '{columns[4]}'");
        if (!decimal.TryParse(columns[5], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var unitPrice))
            return ParseResult.Fail(lineNumber, $"preço inválido: '{columns[5]}'");

        return ParseResult.Ok(
            lineNumber,
            new SaleRecord(columns[0], date, columns[2], columns[3], quantity, unitPrice, columns[6].ToUpperInvariant()));
    }
}
