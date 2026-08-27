using System.Diagnostics;

namespace EvilIslands.DatabaseEditor.Verification;

internal sealed class VerificationLog
{
    private readonly List<string> failures = [];
    private int passed;
    private int skipped;

    public IReadOnlyList<string> Failures => failures;

    public void Pass(string message)
    {
        passed++;
        Write("PASS", ConsoleColor.Green, message);
    }

    public void Skip(string message)
    {
        skipped++;
        Write("SKIP", ConsoleColor.Yellow, message);
    }

    public void Fail(string message)
    {
        failures.Add(message);
        Write("FAIL", ConsoleColor.Red, message);
    }

    public void Info(string message) => Write("INFO", ConsoleColor.Cyan, message);

    public void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new VerificationException(message);
        }

        Pass(message);
    }

    public async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        long allocationBefore = GC.GetTotalAllocatedBytes(precise: false);
        long managedBefore = GC.GetTotalMemory(forceFullCollection: false);
        long workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
        var stopwatch = Stopwatch.StartNew();
        T result = await action().ConfigureAwait(false);
        stopwatch.Stop();
        long allocationDelta = GC.GetTotalAllocatedBytes(precise: false) - allocationBefore;
        long managedDelta = GC.GetTotalMemory(forceFullCollection: false) - managedBefore;
        long workingSetAfter = Process.GetCurrentProcess().WorkingSet64;

        Info(
            $"{operation}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
            $"allocated {FormatBytes(allocationDelta)}; " +
            $"managed delta {FormatSignedBytes(managedDelta)}; " +
            $"working set {FormatBytes(workingSetBefore)} -> {FormatBytes(workingSetAfter)}.");

        return result;
    }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"Итог: PASS={passed}, FAIL={failures.Count}, SKIP={skipped}");
    }

    private static string FormatBytes(long bytes)
    {
        const double scale = 1024;
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = Math.Abs((double)bytes);
        var unit = 0;
        while (value >= scale && unit < units.Length - 1)
        {
            value /= scale;
            unit++;
        }

        return $"{value:F1} {units[unit]}";
    }

    private static string FormatSignedBytes(long bytes) =>
        $"{(bytes < 0 ? "-" : "+")}{FormatBytes(bytes)}";

    private static void Write(string label, ConsoleColor color, string message)
    {
        ConsoleColor originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"[{label}] ");
        Console.ForegroundColor = originalColor;
        Console.WriteLine(message);
    }
}

internal sealed class VerificationException(string message) : Exception(message);
