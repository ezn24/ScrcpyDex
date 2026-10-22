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
            TimeSpan? detectionTimeout = null)
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

                var before = ParseDisplays(await Shell(serial, "dumpsys display", ct).ConfigureAwait(false)).Keys.ToHashSet();
                // Record ownership before mutation: even a canceled ADB request may have reached Android.
                _serial = serial;
                _setting = setting;
                try
                {
                    await Shell(serial, $"settings put global overlay_display_devices {setting}", ct).ConfigureAwait(false);
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed < (detectionTimeout ?? TimeSpan.FromSeconds(10)))
                    {
                        ct.ThrowIfCancellationRequested();
                        string dump = await Shell(serial, "dumpsys display", ct).ConfigureAwait(false);
                        int? id = FindCreatedDisplay(before, dump, int.Parse(size.Groups[1].Value), int.Parse(size.Groups[2].Value));
                        if (id.HasValue) return id.Value;
                        await Task.Delay(200, ct).ConfigureAwait(false);
                    }
                    throw new TimeoutException("The simulated HDMI display was not detected. This firmware may not support HDMI simulation; device.activationMode can be set to wireless for the previous method.");
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
            }
            _serial = null;
            _setting = null;
        }

        private static Dictionary<int, string> ParseDisplays(string dump)
        {
            var displays = new Dictionary<int, string>();
            var blocks = Regex.Matches(dump, @"(?ms)^\s*mDisplayId\s*=\s*(\d+)[^\r\n]*.*?(?=^\s*mDisplayId\s*=|\z)");
            foreach (Match block in blocks) displays[int.Parse(block.Groups[1].Value)] = block.Value;
            if (displays.Count == 0)
            {
                foreach (string line in dump.Split('\n'))
                {
                    var id = Regex.Match(line, @"DisplayInfo\{.*?\bdisplayId\s+(\d+)");
                    if (id.Success) displays[int.Parse(id.Groups[1].Value)] = line;
                }
            }
            return displays;
        }

        public static int? FindCreatedDisplay(ISet<int> before, string dump, int width, int height)
        {
            var matches = ParseDisplays(dump).Where(display => display.Key > 0 && !before.Contains(display.Key) &&
                Regex.IsMatch(display.Value, $@"\b{width}\s*x\s*{height}\b", RegexOptions.IgnoreCase)).Select(display => display.Key).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("More than one matching display appeared; refusing to select an unrelated display.");
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
