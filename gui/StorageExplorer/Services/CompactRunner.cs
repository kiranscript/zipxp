using System.Diagnostics;

namespace StorageExplorer.Services;

public sealed record CompactResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Diagnostics => $"compact exit {ExitCode}\n{StandardOutput}\n{StandardError}";
}

public interface ICompactRunner
{
    Task<CompactResult> RunAsync(string path, bool compress);
}

public sealed class CompactRunner : ICompactRunner
{
    public static ProcessStartInfo CreateStartInfo(string path, bool compress)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "compact.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(compress ? "/C" : "/U");
        start.ArgumentList.Add("/A");
        start.ArgumentList.Add(path);
        return start;
    }

    public async Task<CompactResult> RunAsync(string path, bool compress)
    {
        using var process = new Process { StartInfo = CreateStartInfo(path, compress) };
        if (!process.Start()) throw new IOException("compact.exe did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // Normal cancellation is checked between files. Never kill an in-flight process.
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new(process.ExitCode, await stdout, await stderr);
    }
}
