namespace ImportPipeline.Reports;

/// <summary>Operadores LINQ próprios. Todos têm execução adiada, como os do framework.</summary>
public static class EnumerableExtensions
{
    /// <summary>Aplica o filtro só quando a condição é verdadeira. Útil para filtros opcionais.</summary>
    public static IEnumerable<T> WhereIf<T>(this IEnumerable<T> source, bool condition, Func<T, bool> predicate) =>
        condition ? source.Where(predicate) : source;

    /// <summary>Devolve cada elemento junto com o anterior: (a, b), (b, c), (c, d)...</summary>
    public static IEnumerable<(T Previous, T Current)> Pairwise<T>(this IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Iterate(source);

        static IEnumerable<(T Previous, T Current)> Iterate(IEnumerable<T> source)
        {
            using var enumerator = source.GetEnumerator();
            if (!enumerator.MoveNext())
                yield break;

            var previous = enumerator.Current;
            while (enumerator.MoveNext())
            {
                yield return (previous, enumerator.Current);
                previous = enumerator.Current;
            }
        }
    }
}
