using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ScrcpyDex.Services
{
    internal static class ScrcpyDisplayProbe
    {
        public static async Task<string> ListAsync(string executable, string adbPath, string serial, CancellationToken ct)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(serial);
            start.ArgumentList.Add("--list-displays");
            start.Environment["ADB"] = adbPath;
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("Could not start scrcpy display listing.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                string output = await stdout.ConfigureAwait(false);
                string errors = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("scrcpy display listing returned an error.");
                return output + "\n" + errors;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
                throw;
            }
        }
    }
}
