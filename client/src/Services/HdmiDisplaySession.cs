using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScrcpyDex.Services
{
    // Own only the simulated display created by this session, not another app's display.
    public sealed class HdmiDisplaySession
    {
        private readonly Func<string, CancellationToken, Task<string>> _execute;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string? _serial;
        private string? _setting;
        private int _starting;

        public bool HasSession => _serial != null;
        public bool IsStarting => Volatile.Read(ref _starting) != 0;
        public event Action<string>? Diagnostic;

        public HdmiDisplaySession(Func<string, CancellationToken, Task<string>> execute)
        {
            _execute = execute;
        }

        public static string BuildSetting(string? resolution, int? width, int? height, int? dpi)
        {
            var size = Regex.Match(resolution ?? "", @"^(\d+)\s*[xX]\s*(\d+)");
            int w = size.Success ? int.Parse(size.Groups[1].Value) : width ?? 1920;
            int h = size.Success ? int.Parse(size.Groups[2].Value) : height ?? 1080;
            int density = dpi ?? 320;
            if (w < 640 || w > 4096 || h < 480 || h > 4096 || density < 120 || density > 640)
                throw new ArgumentOutOfRangeException(nameof(resolution), "Unsupported HDMI display size or density.");
            return $"{w}x{h}/{density},hdmi";
        }

        private Task<string> Shell(string serial, string command, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(serial) || !Regex.IsMatch(serial, @"\A[A-Za-z0-9_.:\-]+\z"))
                throw new ArgumentException("Invalid ADB device selector.", nameof(serial));
            return _execute($"-s {serial} shell {command}", ct);
        }

        private static bool IsEmpty(string value) => string.IsNullOrWhiteSpace(value) || value == "null" || value == "none";

        public async Task<int> StartAsync(string serial, string setting, CancellationToken ct = default,
            TimeSpan? detectionTimeout = null, Func<CancellationToken, Task<string>>? listDisplays = null)
        {
            var size = Regex.Match(setting, @"\A(\d+)x(\d+)/(\d+),hdmi\z");
            if (!size.Success || BuildSetting(null, int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value),
                int.Parse(size.Groups[3].Value)) != setting)
                throw new ArgumentException("Invalid HDMI display setting.", nameof(setting));

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _starting, 1);
            try
            {
                if (HasSession) throw new InvalidOperationException("Stop the existing HDMI session first.");
                string current = (await Shell(serial, "settings get global overlay_display_devices", ct).ConfigureAwait(false)).Trim();
                if (!IsEmpty(current))
                    throw new InvalidOperationException("A simulated display already exists. Stop its owner or select None under Developer options > Simulate secondary displays before starting DeX.");

                var beforeSnapshot = await ReadDisplaysAsync(serial, listDisplays, ct).ConfigureAwait(false);
                if (beforeSnapshot.Count == 0)
                    throw new InvalidOperationException("Display enumeration returned no display IDs. No simulated display was created.");
                var before = beforeSnapshot.Keys.ToHashSet();
                Diagnostic?.Invoke($"HDMI requested {setting}; before: {Describe(beforeSnapshot)}");
                // Record ownership before mutation: even a canceled ADB request may have reached Android.
                _serial = serial;
                _setting = setting;
                try
                {
                    await Shell(serial, $"settings put global overlay_display_devices {setting}", ct).ConfigureAwait(false);
                    var timer = Stopwatch.StartNew();
                    var lastSnapshot = beforeSnapshot;
                    while (timer.Elapsed < (detectionTimeout ?? TimeSpan.FromSeconds(10)))
                    {
                        ct.ThrowIfCancellationRequested();
                        lastSnapshot = await ReadDisplaysAsync(serial, listDisplays, ct).ConfigureAwait(false);
                        int? id = FindCreatedDisplay(before, lastSnapshot, int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value));
                        if (id.HasValue)
                        {
                            Diagnostic?.Invoke($"HDMI display selected: {id.Value}; after: {Describe(lastSnapshot)}");
                            return id.Value;
                        }
                        await Task.Delay(200, ct).ConfigureAwait(false);
                    }
                    throw new TimeoutException($"HDMI display ID detection timed out. Requested {setting}; before: {Describe(beforeSnapshot)}; after: {Describe(lastSnapshot)}. The display may exist even though its ID or dimensions did not match.");
                }
                catch (Exception startError)
                {
                    try { await StopCoreAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("HDMI activation failed and cleanup must be retried.", startError, cleanupError);
                    }
                    throw;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _starting, 0);
                _gate.Release();
            }
        }

        public async Task StopAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { await StopCoreAsync(ct).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        private async Task StopCoreAsync(CancellationToken ct)
        {
            if (_serial == null) return;
            string current = (await Shell(_serial, "settings get global overlay_display_devices", ct).ConfigureAwait(false)).Trim();
            if (current == _setting)
            {
                await Shell(_serial, "settings delete global overlay_display_devices", ct).ConfigureAwait(false);
                string remaining = (await Shell(_serial, "settings get global overlay_display_devices", ct).ConfigureAwait(false)).Trim();
                if (!IsEmpty(remaining)) throw new InvalidOperationException("The simulated HDMI display could not be removed. Retry Stop or select None under Developer options > Simulate secondary displays.");
                Diagnostic?.Invoke("HDMI simulated display setting removed.");
            }
            _serial = null;
            _setting = null;
        }

        private static Dictionary<int, string> ParseDisplays(string dump)
        {
            var displays = new Dictionary<int, string>();
            var blocks = Regex.Matches(dump, @"(?ms)^\s*mDisplayId\s*=\s*(\d+)[^\r\n]*.*?(?=^\s*mDisplayId\s*=|\z)");
            foreach (Match block in blocks) displays[int.Parse(block.Groups[1].Value)] = block.Value;
            // Samsung dumps can mix logical blocks with independent DisplayInfo lines.
            // Always merge explicit IDs, even if a primary-display block was already found.
            foreach (string line in dump.Split('\n'))
            {
                var id = Regex.Match(line, @"DisplayInfo\{.*?\bdisplayId\s+(\d+)");
                if (id.Success) displays[int.Parse(id.Groups[1].Value)] = line;
            }
            return displays;
        }

        private async Task<Dictionary<int, string>> ReadDisplaysAsync(string serial,
            Func<CancellationToken, Task<string>>? listDisplays, CancellationToken ct)
        {
            var snapshot = ParseDisplays(await Shell(serial, "dumpsys display", ct).ConfigureAwait(false));
            if (listDisplays != null)
            {
                try
                {
                    using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    foreach (var display in ParseScrcpyDisplays(await listDisplays(probeTimeout.Token).ConfigureAwait(false)))
                        snapshot[display.Key] = display.Value;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    Diagnostic?.Invoke($"scrcpy display listing unavailable ({error.GetType().Name}); using Android display data.");
                }
            }
            return snapshot;
        }

        public static Dictionary<int, string> ParseScrcpyDisplays(string output)
        {
            var snapshot = new Dictionary<int, string>();
            foreach (Match display in Regex.Matches(output, @"--display-id[=\s]+(\d+)\s+\((\d+)\s*[xX]\s*(\d+)\)"))
                snapshot[int.Parse(display.Groups[1].Value)] = $"{display.Groups[2].Value}x{display.Groups[3].Value}";
            return snapshot;
        }

        private static string Describe(Dictionary<int, string> snapshot)
        {
            return string.Join("; ", snapshot.OrderBy(x => x.Key).Select(display =>
            {
                var sizes = Regex.Matches(display.Value, @"\b\d{3,5}\s*[xX]\s*\d{3,5}\b")
                    .Select(match => Regex.Replace(match.Value, @"\s+", "")).Distinct();
                return $"id={display.Key} sizes=[{string.Join(",", sizes)}]";
            }));
        }

        public static int? FindCreatedDisplay(ISet<int> before, string dump, int width, int height)
        {
            return FindCreatedDisplay(before, ParseDisplays(dump), width, height);
        }

        private static int? FindCreatedDisplay(ISet<int> before, Dictionary<int, string> snapshot, int width, int height)
        {
            var matches = snapshot.Where(display => display.Key > 0 && !before.Contains(display.Key) &&
                Regex.IsMatch(display.Value, $@"\b{width}\s*x\s*{height}\b", RegexOptions.IgnoreCase)).Select(display => display.Key).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("More than one matching display appeared; refusing to select an unrelated display.");
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
