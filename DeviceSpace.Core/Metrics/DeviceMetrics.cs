using System;
using Castle.DynamicProxy;
using System.Diagnostics;
using System.Diagnostics.Metrics;

public class MetricsInterceptor : IInterceptor
{
    public void Intercept(IInvocation invocation)
    {
        // Get a clean name for the "tag"
        var methodNameTag = $"{invocation.Method.DeclaringType?.Name}.{invocation.Method.Name}";

        // --- Data to record ---
        int exceptionCount = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Run the actual method
            invocation.Proceed();
        }
        catch (Exception)
        {
            exceptionCount = 1; // Mark that an exception happened
            throw; // Re-throw the exception
        }
        finally
        {
            stopwatch.Stop();
            var duration = stopwatch.Elapsed.TotalMilliseconds;

            // --- Record the metrics with "tags" ---
            // Tags (or "dimensions") let you filter in your dashboard
            var tags = new TagList
            {
                { "method.name", methodNameTag },
                { "method.exception", exceptionCount }
            };

            // 1. Record the duration
            DeviceMetrics.MethodDuration.Record(duration, tags);

            // 2. Record the count
            DeviceMetrics.MethodCalls.Add(1, tags);
        }
    }
}
public static class DeviceMetrics
{
    // 1. Create a "Meter" - this is the source of all your metrics
    public static readonly Meter WcsMeter = new Meter("FireServer", "1.0.0");

    // 2. Create the "Counter" for "how many times"
    public static readonly Counter<int> MethodCalls = WcsMeter.CreateCounter<int>(
        name: "wcs.method.calls",
        unit: "{call}",
        description: "Number of times a method is called."
    );

    // 3. Create the "Histogram" for "how long"
    // We use 'double' and 'ms' for high-precision timing
    public static readonly Histogram<double> MethodDuration = WcsMeter.CreateHistogram<double>(
        name: "wcs.method.duration",
        unit: "ms",
        description: "The duration of a method call in milliseconds."
    );
}


