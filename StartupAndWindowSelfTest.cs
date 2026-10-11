using System.Xml.Linq;

namespace TinyMouseMacro;

internal static class StartupAndWindowSelfTest
{
    internal static void Run(string output, Action<string, bool> check)
    {
        const string exe = @"C:\Apps & Tools\TinyMouseMacro\TinyMouseMacro.exe";
        const string sid = "S-1-5-21-100-200-300-1001";
        var xml = AutoStart.BuildTaskXml(exe, sid);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = XElement.Parse(xml);
        check("startup task escapes paths and keeps current-user elevated resident settings", AutoStart.MatchesTask(xml, exe, sid));
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var normalized = XElement.Parse(AutoStart.BuildTaskXml(exe, identity.User!.Value));
        normalized.Element(ns + "Triggers")!.Descendants(ns + "UserId").Single().Value = identity.Name;
        normalized.Descendants(ns + "Enabled").Remove();
        check("startup accepts Windows-normalized account names and omitted true defaults",
            AutoStart.MatchesTask(normalized.ToString(), exe, identity.User.Value));
        check("startup refuses another executable or user", !AutoStart.IsOwnedTask(xml, @"C:\Other\TinyMouseMacro.exe", sid)
            && !AutoStart.IsOwnedTask(xml, exe, sid + "1"));
        var disabled = new XElement(root);
        disabled.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value = "false";
        check("disabled scheduled startup is not reported enabled", !AutoStart.IsTaskEnabled(disabled.ToString())
            && !AutoStart.MatchesTask(disabled.ToString(), exe, sid));
        foreach (var setting in new[] { "ExecutionTimeLimit", "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "RunLevel", "LogonType", "WorkingDirectory" })
        {
            var changed = new XElement(root);
            changed.Descendants(ns + setting).Single().Value = "unexpected";
            check("startup detects incorrect " + setting, !AutoStart.MatchesTask(changed.ToString(), exe, sid));
        }
        var multiple = new XElement(root);
        multiple.Element(ns + "Actions")!.Add(new XElement(ns + "Exec", new XElement(ns + "Command", exe)));
        check("startup refuses tasks with extra actions", !AutoStart.IsOwnedTask(multiple.ToString(), exe, sid));
        check("startup respects Windows disabled legacy state", AutoStart.IsLegacyDisabled([3, 0])
            && AutoStart.IsLegacyDisabled([7, 0]) && !AutoStart.IsLegacyDisabled([2, 0]) && !AutoStart.IsLegacyDisabled(null));
        check("startup legacy ownership checks the complete command", AutoStart.IsOwnedLegacyCommand(AutoStart.BuildCommand(exe), exe)
            && !AutoStart.IsOwnedLegacyCommand(AutoStart.BuildCommand(exe) + " --unexpected", exe)
            && !AutoStart.IsOwnedLegacyCommand(@"C:\Other\TinyMouseMacro.exe", exe));

        var path = Path.Combine(output, "window-state-test.json");
        var initial = new WindowPlacement(-1800, -900, 1400, 800, 144, true, "test-monitor");
        new WindowPlacementStore(path).Save(initial);
        check("window placement survives a new store instance", new WindowPlacementStore(path).Load() == initial);
        var changedPlacement = initial with { X = -1600, Width = 1500, Maximized = false };
        new WindowPlacementStore(path).Save(changedPlacement);
        check("window placement replaces atomically and keeps a previous backup", new WindowPlacementStore(path).Load() == changedPlacement
            && new WindowPlacementStore(path + ".backup").Load() == initial && !File.Exists(path + ".tmp"));
        File.WriteAllText(path, "{interrupted");
        check("window placement recovers a damaged main file", new WindowPlacementStore(path).Load() == initial);
        new WindowPlacementStore(path).Save(changedPlacement);
        check("repairing a damaged window file preserves the good recovery copy", new WindowPlacementStore(path + ".backup").Load() == initial);
        var negative = new WindowPlacement(-1800, -900, 1200, 700);
        check("window placement preserves valid negative monitor coordinates", negative.FitToWorkingArea(new(-1920, -1080, 1920, 1080), 96, new(1080, 700))
            == new Rectangle(-1800, -900, 1200, 700));
        check("window placement adapts dimensions to changed DPI", negative.FitToWorkingArea(new(-1920, -1080, 3840, 2160), 144, new(1080, 700)).Size == new Size(1800, 1050));
        var fitted = negative.FitToWorkingArea(new(0, 0, 1920, 1040), 96, new(1080, 700));
        check("disconnected monitor placement returns to a visible work area", fitted == new Rectangle(0, 0, 1200, 700));
        check("invalid saved geometry is rejected", !new WindowPlacement(0, 0, 0, 800).IsValid && !new WindowPlacement(0, 0, 1200, 800, 0).IsValid);

        using var form = new MainForm(preview: true, startInTray: true, previewDataDirectory: output);
        form.VerifyWindowPlacementLifecycle(output, check);
    }
}

public sealed partial class MainForm
{
    internal void VerifyWindowPlacementLifecycle(string output, Action<string, bool> check)
    {
        // SetVisibleCore's autostart branch performs initialization without showing a window.
        SetVisibleCore(true);
        check("autostart initializes once without showing its window", _mainFormLoaded && !Visible && !_positionTimer.Enabled);
        var area = Screen.FromControl(this).WorkingArea;
        var expected = new WindowPlacement(area.Left + 20, area.Top + 20, Width, Height, DeviceDpi);
        _windowPlacementStore = new WindowPlacementStore(Path.Combine(output, "window-lifecycle.json"));
        _windowPlacementStore.Save(expected);
        RestoreWindowPlacement();
        check("real form restores saved bounds after DPI initialization", Bounds == expected.FitToWorkingArea(area, DeviceDpi, MinimumSize));
        _windowPlacementReady = true;
        SaveWindowPlacement();
        check("restoring without a user change preserves the saved placement", _windowPlacementStore.Load() == expected);
        var disconnected = expected with { X = -99900, Y = -99900, Monitor = "missing-test-monitor" };
        _windowPlacementStore.Save(disconnected);
        RestoreWindowPlacement();
        SaveWindowPlacement();
        check("temporary missing monitor fallback does not overwrite the saved location", _windowPlacementStore.Load() == disconnected);
        var normal = _normalWindowPlacement!;
        WindowState = FormWindowState.Maximized;
        SaveWindowPlacement();
        check("maximizing preserves normal restore bounds", _windowPlacementStore.Load() == normal with { Maximized = true });
        WindowState = FormWindowState.Minimized;
        SaveWindowPlacement();
        check("minimizing retains last non-minimized state", _windowPlacementStore.Load() == normal with { Maximized = true });
        _normalWindowPlacement = normal with { X = normal.X + 5 };
        _windowPlacementTimer.Start();
        var message = Message.Create(Handle, 0x0011, 0, 0);
        WndProc(ref message);
        check("session shutdown flushes pending placement while hidden", !_windowPlacementTimer.Enabled
            && _windowPlacementStore.Load() == _normalWindowPlacement with { Maximized = true });
        var prior = _profiles;
        MainForm_Load(this, EventArgs.Empty);
        check("tray initialization does not reload profiles a second time", ReferenceEquals(prior, _profiles));
    }
}
