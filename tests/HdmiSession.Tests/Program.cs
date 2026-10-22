using ScrcpyDex.Services;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

Check(HdmiDisplaySession.BuildSetting("2560x1440 (QHD)", 1920, 1080, 160) == "2560x1440/160,hdmi", "Selected resolution must take precedence over old width/height defaults.");
await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(HdmiDisplaySession.BuildSetting("9999x1080", null, null, 160)));
var before = new HashSet<int> { 0, 4 };
string logical = "  mDisplayId=0\n    mName=Built-in, 1920 x 1080\n  mDisplayId=4\n    mName=Existing, 1920 x 1080\n  mDisplayId=7\n    mName=Overlay #1, 1920 x 1080\n";
Check(HdmiDisplaySession.FindCreatedDisplay(before, logical, 1920, 1080) == 7, "Select only the newly created matching logical display.");
Check(HdmiDisplaySession.FindCreatedDisplay(before, "DisplayInfo{\"Overlay #1\", displayId 8, real 1920 x 1080}", 1920, 1080) == 8, "Support DisplayInfo-only dumps.");
Check(HdmiDisplaySession.FindCreatedDisplay(before, "mDisplayId=0\n1920 x 1080", 1920, 1080) == null, "Never select the phone display.");
await Throws<InvalidOperationException>(() => Task.FromResult(HdmiDisplaySession.FindCreatedDisplay(before, logical + "mDisplayId=8\n1920 x 1080", 1920, 1080)));

var fake = new FakeDevice();
var session = new HdmiDisplaySession(fake.Execute);
Check(await session.StartAsync("TEST_DEVICE", "1920x1080/320,hdmi") == 7, "Activation should return its new display ID.");
Check(session.HasSession && fake.Setting == "1920x1080/320,hdmi", "Retain ownership while running.");
await session.StopAsync();
Check(!session.HasSession && fake.Setting == "null", "Stop must remove the owned display.");
Check(fake.Commands.All(x => x.StartsWith("-s TEST_DEVICE shell ")), "Every operation must target the captured device.");

fake = new FakeDevice { Setting = "1280x720/160,hdmi" };
session = new HdmiDisplaySession(fake.Execute);
await Throws<InvalidOperationException>(() => session.StartAsync("TEST_DEVICE", "1920x1080/320,hdmi"));
Check(fake.Commands.Count == 1 && fake.Setting == "1280x720/160,hdmi", "Do not overwrite another owner's display.");

fake = new FakeDevice();
session = new HdmiDisplaySession(fake.Execute);
await session.StartAsync("TEST_DEVICE", "1920x1080/320,hdmi");
fake.Setting = "2560x1440/160,hdmi";
await session.StopAsync();
Check(fake.Setting == "2560x1440/160,hdmi" && !fake.Commands.Any(x => x.Contains("settings delete")), "Do not delete a display replaced by another owner.");

fake = new FakeDevice { ShowDisplay = false };
session = new HdmiDisplaySession(fake.Execute);
await Throws<TimeoutException>(() => session.StartAsync("TEST_DEVICE", "1920x1080/320,hdmi", detectionTimeout: TimeSpan.Zero));
Check(fake.Setting == "null" && !session.HasSession, "Startup timeout must roll back the setting.");

using var cancellation = new CancellationTokenSource();
fake = new FakeDevice { AfterPut = cancellation.Cancel };
session = new HdmiDisplaySession(fake.Execute);
await Throws<OperationCanceledException>(() => session.StartAsync("TEST_DEVICE", "1920x1080/320,hdmi", cancellation.Token));
Check(fake.Setting == "null" && !session.HasSession, "Cancellation must clean up with a fresh token.");

fake = new FakeDevice();
session = new HdmiDisplaySession(fake.Execute);
await session.StartAsync("TEST_DEVICE", "1920x1080/320,hdmi");
fake.FailDelete = true;
await Throws<IOException>(() => session.StopAsync());
Check(session.HasSession, "Keep ownership when cleanup fails so Stop can be retried.");
fake.FailDelete = false;
await session.StopAsync();
Check(!session.HasSession && fake.Setting == "null", "Cleanup retry must succeed.");

var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
fake = new FakeDevice();
bool firstRead = true;
session = new HdmiDisplaySession(async (command, ct) =>
{
    if (firstRead)
    {
        firstRead = false;
        entered.SetResult();
        await release.Task;
    }
    return await fake.Execute(command, ct);
});
var starting = session.StartAsync("TEST_DEVICE", "1920x1080/320,hdmi");
await entered.Task;
Check(session.IsStarting, "Expose in-flight activation before settings are changed.");
var stopping = session.StopAsync();
Check(!stopping.IsCompleted, "Closing during activation must wait for ownership to settle.");
release.SetResult();
await starting;
await stopping;
Check(!session.HasSession && !session.IsStarting && fake.Setting == "null", "Closing during activation must remove the new display.");
Console.WriteLine("HDMI display selection, ownership, rollback, cancellation, and cleanup retry checks passed.");

sealed class FakeDevice
{
    public string Setting = "null";
    public bool ShowDisplay = true;
    public bool FailDelete;
    public Action? AfterPut;
    public List<string> Commands = new();

    public Task<string> Execute(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Commands.Add(command);
        string shell = command.Substring(command.IndexOf(" shell ", StringComparison.Ordinal) + 7);
        if (shell == "settings get global overlay_display_devices") return Task.FromResult(Setting);
        if (shell.StartsWith("settings put global overlay_display_devices "))
        {
            Setting = shell.Substring("settings put global overlay_display_devices ".Length);
            AfterPut?.Invoke();
            return Task.FromResult("");
        }
        if (shell == "settings delete global overlay_display_devices")
        {
            if (FailDelete) throw new IOException("Synthetic disconnected device.");
            Setting = "null";
            return Task.FromResult("");
        }
        if (shell == "dumpsys display") return Task.FromResult("mDisplayId=0\n1080 x 2340\n" +
            (Setting != "null" && ShowDisplay ? "mDisplayId=7\nmName=Overlay #1\n1920 x 1080\n" : ""));
        throw new Exception("Unexpected command: " + shell);
    }
}
