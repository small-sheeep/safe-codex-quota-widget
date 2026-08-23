using System;
using System.Threading;
using System.Threading.Tasks;
using SafeCodexQuotaWidget;

internal static class RetryPolicyTests
{
    private static int Main()
    {
        try
        {
            SuccessDoesNotRetry();
            TransientFailuresRetryUntilSuccess();
            ExhaustionRethrowsLastFailure();
            PermanentFailureDoesNotRetry();
            CancellationDuringDelayStopsRetrying();
            Console.WriteLine("Retry policy tests passed: 5");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void SuccessDoesNotRetry()
    {
        int attempts = 0;
        int retryNotifications = 0;
        string result = AsyncRetryPolicy.ExecuteAsync(
            delegate
            {
                attempts++;
                return Task.FromResult("ok");
            },
            new[] { 1, 1, 1 },
            delegate { return true; },
            delegate { retryNotifications++; },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert(result == "ok", "The first successful result should be returned.");
        Assert(attempts == 1, "A successful first attempt must run once.");
        Assert(retryNotifications == 0, "A successful first attempt must not report a retry.");
    }

    private static void TransientFailuresRetryUntilSuccess()
    {
        int attempts = 0;
        int retryNotifications = 0;
        string result = AsyncRetryPolicy.ExecuteAsync(
            delegate
            {
                attempts++;
                if (attempts < 4) throw new InvalidOperationException("temporary " + attempts);
                return Task.FromResult("recovered");
            },
            new[] { 1, 1, 1 },
            delegate { return true; },
            delegate(int retryNumber, int totalRetries, int delayMilliseconds, Exception error)
            {
                retryNotifications++;
                Assert(retryNumber == retryNotifications, "Retry numbers should be sequential.");
                Assert(totalRetries == 3, "The configured retry count should be reported.");
                Assert(delayMilliseconds == 1, "The configured delay should be reported.");
            },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert(result == "recovered", "The later successful result should be returned.");
        Assert(attempts == 4, "Three retries should permit four total attempts.");
        Assert(retryNotifications == 3, "Every scheduled retry should be reported once.");
    }

    private static void ExhaustionRethrowsLastFailure()
    {
        int attempts = 0;
        int retryNotifications = 0;
        try
        {
            AsyncRetryPolicy.ExecuteAsync<string>(
                delegate
                {
                    attempts++;
                    throw new InvalidOperationException("failure " + attempts);
                },
                new[] { 1, 1, 1 },
                delegate { return true; },
                delegate { retryNotifications++; },
                CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("The final failure should have been rethrown.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message == "failure 4", "The last read error should remain visible after exhaustion.");
        }

        Assert(attempts == 4, "Exhaustion should stop after the initial attempt and three retries.");
        Assert(retryNotifications == 3, "Only actual retries should be reported.");
    }

    private static void PermanentFailureDoesNotRetry()
    {
        int attempts = 0;
        int retryNotifications = 0;
        try
        {
            AsyncRetryPolicy.ExecuteAsync<string>(
                delegate
                {
                    attempts++;
                    throw new InvalidOperationException("permanent");
                },
                new[] { 1, 1, 1 },
                delegate { return false; },
                delegate { retryNotifications++; },
                CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("A permanent failure should have been rethrown.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message == "permanent", "The permanent error should be preserved.");
        }

        Assert(attempts == 1, "A permanent failure must not be retried.");
        Assert(retryNotifications == 0, "A permanent failure must not report a retry.");
    }

    private static void CancellationDuringDelayStopsRetrying()
    {
        int attempts = 0;
        CancellationTokenSource cancellation = new CancellationTokenSource();
        try
        {
            AsyncRetryPolicy.ExecuteAsync<string>(
                delegate
                {
                    attempts++;
                    throw new InvalidOperationException("temporary");
                },
                new[] { 1000, 1000, 1000 },
                delegate { return true; },
                delegate { cancellation.Cancel(); },
                cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation should interrupt the retry delay.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }

        Assert(attempts == 1, "Cancellation during the delay must prevent another attempt.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
