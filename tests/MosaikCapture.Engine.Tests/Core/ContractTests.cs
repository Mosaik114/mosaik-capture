using System.Web.Script.Serialization;

namespace MosaikCapture.Engine.Tests.Core;

/// <summary>
/// Checks the engine against contract/contract.json, the file the host's tests (src/contract.rs) check the host
/// against. Every expected value comes from the file, so a change on one side fails until both sides agree.
/// </summary>
public sealed class ContractTests
{
    const int WmApp = 0x8000;

    static readonly Dictionary<string, object> Contract = Load();

    static Dictionary<string, object> Load()
    {
        string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "contract.json");
        return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file));
    }

    static object Get(params string[] path) => path.Aggregate((object)Contract, (node, key) => ((Dictionary<string, object>)node)[key]);

    static string Text(params string[] path) => (string)Get(path);

    static int Number(params string[] path) => (int)Get(path);

    static int Message(params string[] path) => WmApp + Number(path);

    static IEnumerable<KeyValuePair<string, object>> Entries(params string[] path) =>
        ((Dictionary<string, object>)Get(path)).Where(entry => entry.Key[0] != '$');

    static Dictionary<string, object> Key(string key) => (Dictionary<string, object>)Get("settings", "keys", key);

    // What the engine does with each key it reads, as seen through the snapshot.
    static readonly Dictionary<string, Func<CaptureSettingsSnapshot, object>> EngineKeys = new()
    {
        ["screenshot_folder"] = s => s.ScreenshotFolder,
        ["recording_folder"] = s => s.RecordingFolder,
        ["microphone"] = s => s.Microphone ? 1 : 0,
        ["system_audio"] = s => s.SystemAudio ? 1 : 0,
        ["show_clicks"] = s => s.Clicks ? 1 : 0,
        ["fps"] = s => s.Fps,
        ["countdown"] = s => s.Countdown,
    };

    static IEnumerable<string> NumberKeys => EngineKeys.Keys.Where(key => (string)Key(key)["kind"] != "folder");

    static int Expected(string key, int? stored)
    {
        var spec = Key(key);
        int value = stored ?? (int)spec["default"];
        if ((string)spec["kind"] == "flag") return value != 0 ? 1 : 0;
        return ((object[])spec["allowed"]).Cast<int>().Contains(value) ? value : (int)spec["otherwise"];
    }

    static CaptureSettingsSnapshot LoadIni(string lines)
    {
        using var settings = new TestSettings("[" + Text("settings", "section") + "]\r\n" + lines);
        return settings.Load();
    }

    [Fact]
    public void TheEngineReadsExactlyTheKeysTheContractGivesIt()
    {
        var engineReads = Entries("settings", "keys").Where(entry => (bool)((Dictionary<string, object>)entry.Value)["engine_reads"]).Select(entry => entry.Key);

        Assert.Equal(engineReads.OrderBy(key => key, StringComparer.Ordinal), EngineKeys.Keys.OrderBy(key => key, StringComparer.Ordinal));
        var defaults = LoadIni("");
        foreach (var (key, read) in EngineKeys.Select(entry => (entry.Key, entry.Value)))
        {
            string changed = (string)Key(key)["kind"] == "folder" ? "C:\\Contract\\" + key : Expected(key, null) == 0 ? "1" : "0";
            if (key is "fps" or "countdown") changed = ((object[])Key(key)["allowed"]).Cast<int>().First(value => value != Expected(key, null)).ToString(CultureInfo.InvariantCulture);
            var loaded = LoadIni(key + "=" + changed + "\r\n");
            Assert.NotEqual(read(defaults), read(loaded));
            Assert.Equal(changed, Convert.ToString(read(loaded), CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void MissingKeysYieldTheContractDefaults()
    {
        var loaded = LoadIni("");

        foreach (string key in NumberKeys) Assert.Equal(Expected(key, null), EngineKeys[key](loaded));
        var folders = (Dictionary<string, object>)Get("settings", "default_folders");
        Assert.Equal(UiText.Files.ScreenshotFolder, Text("settings", "default_folders", "screenshot_folder", "name"));
        Assert.Equal(UiText.Files.RecordingFolder, Text("settings", "default_folders", "recording_folder", "name"));
        Assert.Equal("Pictures", (string)((Dictionary<string, object>)folders["screenshot_folder"])["known_folder"]);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), UiText.Files.ScreenshotFolder), loaded.ScreenshotFolder);
        Assert.Equal("Videos", (string)((Dictionary<string, object>)folders["recording_folder"])["known_folder"]);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), UiText.Files.RecordingFolder), loaded.RecordingFolder);
    }

    [Fact]
    public void StoredNumbersAreReadAsTheContractSays()
    {
        foreach (object[] testCase in (object[])Get("settings", "numbers", "cases"))
        {
            string raw = (string)testCase[0];
            int? stored = (int?)testCase[1];
            var loaded = LoadIni(string.Concat(NumberKeys.Select(key => key + "=" + raw + "\r\n")));
            var wrong = NumberKeys.Where(key => !Expected(key, stored).Equals(EngineKeys[key](loaded))).Select(key => key + "=" + raw);

            Assert.Empty(wrong);
        }
    }

    [Fact]
    public void TheLongestFolderTheHostWritesIsReadWhole()
    {
        int longest = Number("settings", "max_folder_length");

        // The read buffer holds the value plus its terminator.
        Assert.True(CaptureSettings.MaxValueLength - 1 >= longest, $"read cap {CaptureSettings.MaxValueLength} < {longest} + 1");
        string folder = "C:\\" + new string('a', longest - 3);
        Assert.Equal(folder, LoadIni("screenshot_folder=" + folder + "\r\n").ScreenshotFolder);
    }

    [Fact]
    public void ChoicesAreNormalizedOverTheWholeRange()
    {
        for (int value = -2; value <= 130; value++)
        {
            Assert.Equal(Expected("fps", value), CaptureSettings.NormalizeFps(value));
            Assert.Equal(Expected("countdown", value), CaptureSettings.NormalizeCountdown(value));
        }
    }

    [Fact]
    public void SettingsFileLocation()
    {
        Assert.Equal(CaptureSettings.AppFolderName, Text("settings", "app_folder"));
        Assert.Equal(Path.Combine(Text("settings", "app_folder"), Text("settings", "file_name")), Path.Combine(Path.GetFileName(Path.GetDirectoryName(CaptureSettings.ResolveFileName(null))), Path.GetFileName(CaptureSettings.ResolveFileName(null))));
        Assert.Equal(CaptureSettings.TestConfigVariable, Text("test_isolation", "config_variable"));
    }

    [Fact]
    public void EngineNamesArgumentsAndMessages()
    {
        Assert.Equal(Text("engine", "window_title"), CaptureBridge.Title);
        Assert.Equal(Text("engine", "mutex"), CaptureBridge.MutexName);
        Assert.Equal(Text("engine", "executable"), typeof(CaptureBridge).Assembly.GetName().Name + ".exe");
        Assert.Equal(CaptureBridge.ScreenshotMessage, Message("engine", "messages", "screenshot"));
        Assert.Equal(CaptureBridge.RecordingMessage, Message("engine", "messages", "recording"));
        Assert.Equal(CaptureBridge.QuitMessage, Message("engine", "messages", "quit"));
        Assert.Equal(CaptureBridge.StateMessage, Message("engine", "messages", "query_state"));
        Assert.Equal(CaptureBridge.ScreenshotMessage, CaptureBridge.CommandBase + CaptureBridge.ParseCommand(new[] { Text("engine", "arguments", "screenshot") }));
        Assert.Equal(CaptureBridge.RecordingMessage, CaptureBridge.CommandBase + CaptureBridge.ParseCommand(new[] { Text("engine", "arguments", "recording") }));
        Assert.Equal(0, CaptureBridge.ParseCommand(new[] { Text("engine", "arguments", "idle") }));
        Assert.Equal(Number("engine", "state_bits", "busy"), CaptureBridge.StateFlags(busy: true, live: false));
        Assert.Equal(Number("engine", "state_bits", "live"), CaptureBridge.StateFlags(busy: false, live: true));
    }

    [Fact]
    public void WhatTheEngineSendsToTheHost()
    {
        Assert.Equal(Text("host", "window_class"), CaptureSettings.HostWindowClass);
        Assert.Equal(CaptureSettings.SaveAudioMessage, Message("host", "messages", "audio_settings"));
        Assert.Equal(Number("host", "audio_bits", "microphone"), CaptureSettings.AudioFlags(true, false, false));
        Assert.Equal(Number("host", "audio_bits", "system_audio"), CaptureSettings.AudioFlags(false, true, false));
        Assert.Equal(Number("host", "audio_bits", "show_clicks"), CaptureSettings.AudioFlags(false, false, true));
    }

    [Fact]
    public void TestIsolationNames()
    {
        Assert.Equal(TestIsolation.InstanceVariable, Text("test_isolation", "instance_variable"));
        Assert.Equal(TestIsolation.Infix, Text("test_isolation", "infix"));
        Assert.Equal(CaptureSettings.AppFolderName + TestIsolation.Infix, Text("test_isolation", "local_app_data_prefix"));
        Assert.Equal(TestIsolation.SimulateBusyMessage, Message("test_isolation", "simulate_busy_message"));
        Assert.Null(TestIsolation.Instance);   // the test process itself is never isolated
        Assert.Null(TestIsolation.Sanitize(""));
        foreach (Dictionary<string, object> example in (object[])Get("test_isolation", "examples"))
        {
            string? instance = TestIsolation.Sanitize((string)example["instance"]);
            Assert.Equal((string)example["engine_mutex"], TestIsolation.Isolate(Text("engine", "mutex"), instance));
            Assert.Equal((string)example["engine_window_title"], TestIsolation.Isolate(Text("engine", "window_title"), instance));
            Assert.Equal((string)example["host_window_class"], TestIsolation.Isolate(Text("host", "window_class"), instance));
        }
    }

    [Fact]
    public void ASimulatedBusyEngineRefusesToQuit()
    {
        static (IntPtr Result, bool Handled) Send(int message, int wParam = 0)
        {
            bool handled = false;
            IntPtr result = TestIsolation.OnMessage(IntPtr.Zero, message, new IntPtr(wParam), IntPtr.Zero, ref handled);
            return (result, handled);
        }

        int busyBit = Number("engine", "state_bits", "busy");
        int shutdowns = 0;
        try
        {
            Assert.False(Send(CaptureBridge.QuitMessage).Handled, "quit always reaches the bridge");
            Assert.False(Send(CaptureBridge.StateMessage).Handled, "so does the state query");
            Assert.False(EngineActivity.Busy);

            Assert.Equal((new IntPtr(1), true), Send(TestIsolation.SimulateBusyMessage, 1));
            Assert.True(EngineActivity.Busy, "simulated work counts as the engine's own");
            Assert.Equal(busyBit, CaptureBridge.State() & busyBit);
            Assert.False(CaptureBridge.Quit(() => shutdowns++), "the bridge itself refuses");
            Assert.False(Send(CaptureBridge.QuitMessage).Handled);

            Assert.Equal((new IntPtr(1), true), Send(TestIsolation.SimulateBusyMessage, 0));
            Assert.False(EngineActivity.Busy);
            Assert.Equal(0, CaptureBridge.State() & busyBit);
            Assert.Equal(0, shutdowns);
            Assert.True(CaptureBridge.Quit(() => shutdowns++));
            Assert.Equal(1, shutdowns);
        }
        finally
        {
            Send(TestIsolation.SimulateBusyMessage, 0);
        }
    }
}
