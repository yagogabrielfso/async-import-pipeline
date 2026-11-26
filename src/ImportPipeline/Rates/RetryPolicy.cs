namespace ImportPipeline.Rates;

/// <summary>
/// O comportamento do retry é todo injetado por delegates: quantas tentativas,
/// quanto esperar, quais erros valem uma nova tentativa e o que fazer a cada uma.
/// </summary>
public sealed record RetryOptions(
    int MaxAttempts,
    Func<int, TimeSpan> Backoff,
    Func<Exception, bool> ShouldRetry,
    Action<int, Exception>? OnRetry = null)
{
    public static RetryOptions Default { get; } = new(
        MaxAttempts: 3,
        Backoff: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)),
        ShouldRetry: exception => exception is HttpRequestException or TimeoutException);
}

public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        RetryOptions options,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (Exception exception) when (
                attempt < options.MaxAttempts
                && options.ShouldRetry(exception)
                && !cancellationToken.IsCancellationRequested)
            {
                options.OnRetry?.Invoke(attempt, exception);
                await Task.Delay(options.Backoff(attempt), timeProvider ?? TimeProvider.System, cancellationToken);
            }
        }
    }
}
