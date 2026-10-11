using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace TinyMouseMacro;

internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "TinyMouseMacro";
    internal const string StartupArgument = "--autostart";
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static string UserSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
    internal static string TaskName => "TinyMouseMacro-" + UserSid;
    internal static string BuildCommand(string processPath) => "\"" + processPath + "\"";

    internal static string BuildTaskXml(string processPath, string sid)
    {
        XElement E(string name, params object[] content) => new(TaskNs + name, content);
        return new XDocument(E("Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "TinyMouseMacro current-user logon startup")),
            E("Triggers", E("LogonTrigger", E("Enabled", true), E("UserId", sid), E("Delay", "PT10S"))),
            E("Principals", E("Principal", new XAttribute("id", "CurrentUser"), E("UserId", sid),
                E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("StartWhenAvailable", true), E("Enabled", true),
                E("ExecutionTimeLimit", "PT0S")),
            E("Actions", new XAttribute("Context", "CurrentUser"), E("Exec", E("Command", processPath),
                E("Arguments", StartupArgument), E("WorkingDirectory", Path.GetDirectoryName(processPath)!)))))
            .ToString();
    }

    internal static bool IsOwnedPath(string? path, string processPath) => !string.IsNullOrWhiteSpace(path)
        && (string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "TinyMouseMacro.exe"), StringComparison.OrdinalIgnoreCase));

    internal static bool IsOwnedLegacyCommand(string command, string processPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        return IsOwnedPath(expanded.Trim('"'), processPath);
    }

    private static XElement ReadXml(string xml) => XElement.Parse(xml);
    private static string? Value(XElement root, string name) => root.Descendants(TaskNs + name).SingleOrDefault()?.Value;
    private static bool MatchesUser(string? user, string sid)
    {
        if (string.Equals(user, sid, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(user) || user.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return false;
        // Task Scheduler rewrites LogonTrigger.UserId to DOMAIN\user on registration.
        try { return ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
        catch (SystemException) { return false; }
    }
    internal static bool IsOwnedTask(string xml, string processPath, string sid)
    {
        var root = ReadXml(xml);
        var actions = root.Element(TaskNs + "Actions")?.Elements().ToArray();
        var principals = root.Element(TaskNs + "Principals")?.Elements().ToArray();
        return actions is { Length: 1 } && actions[0].Name == TaskNs + "Exec"
            && IsOwnedPath(Value(actions[0], "Command"), processPath)
            && Value(actions[0], "Arguments") == StartupArgument
            && principals is { Length: 1 } && MatchesUser(Value(principals[0], "UserId"), sid);
    }

    internal static bool IsTaskEnabled(string xml) =>
        ReadXml(xml).Element(TaskNs + "Settings")?.Element(TaskNs + "Enabled")?.Value != "false";

    internal static bool MatchesTask(string xml, string processPath, string sid)
    {
        if (!IsOwnedTask(xml, processPath, sid) || !IsTaskEnabled(xml)) return false;
        var root = ReadXml(xml);
        var triggers = root.Element(TaskNs + "Triggers")?.Elements().ToArray();
        return triggers is { Length: 1 } && triggers[0].Name == TaskNs + "LogonTrigger"
            && MatchesUser(Value(triggers[0], "UserId"), sid) && Value(triggers[0], "Enabled") != "false"
            && Value(root, "Command") == processPath
            && Value(root, "WorkingDirectory") == Path.GetDirectoryName(processPath)
            && Value(root, "LogonType") == "InteractiveToken" && Value(root, "RunLevel") == "HighestAvailable"
            && Value(root, "ExecutionTimeLimit") == "PT0S" && Value(root, "DisallowStartIfOnBatteries") == "false"
            && Value(root, "StopIfGoingOnBatteries") == "false" && Value(root, "MultipleInstancesPolicy") == "IgnoreNew";
    }

    private static string? LegacyCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    internal static bool IsLegacyDisabled(byte[]? approval) => approval is { Length: > 0 } && (approval[0] & 1) != 0;
    private static bool LegacyDisabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath);
        return IsLegacyDisabled(key?.GetValue(ValueName) as byte[]);
    }

    public static bool IsEnabled()
    {
        try
        {
            using var scheduler = new Scheduler();
            var xml = scheduler.Read();
            // Disabled tasks stay disabled; do not resurrect them from an old Run entry.
            if (xml is not null) return MatchesTask(xml, Environment.ProcessPath!, UserSid);
            return LegacyCommand() is { } legacy && IsOwnedLegacyCommand(legacy, Environment.ProcessPath!) && !LegacyDisabled();
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enable, out string? error) => Configure(Environment.ProcessPath!, enable, out error);
    internal static bool RepairEnabled(string processPath, out string? error) => Configure(processPath, null, out error);
    internal static bool CheckEnabled(string processPath, out string? error) => Configure(processPath, null, out error, checkOnly: true);

    private static bool Configure(string processPath, bool? requested, out string? error, bool checkOnly = false)
    {
        error = null;
        try
        {
            if (!Path.IsPathFullyQualified(processPath) || !File.Exists(processPath))
                throw new IOException("找不到自启程序：" + processPath);
            using var scheduler = new Scheduler();
            var previousXml = scheduler.Read();
            var legacy = LegacyCommand();
            if (previousXml is not null && !IsOwnedTask(previousXml, processPath, UserSid))
                throw new InvalidOperationException("同名自启任务不属于当前安装，已保留原设置。");
            if (legacy is not null && !IsOwnedLegacyCommand(legacy, processPath))
                throw new InvalidOperationException("旧自启项指向其他安装，已保留原设置。");
            var enable = requested ?? (previousXml is not null ? IsTaskEnabled(previousXml) : legacy is not null && !LegacyDisabled());
            if (requested is null && !enable) return true;
            if (enable && previousXml is not null && MatchesTask(previousXml, processPath, UserSid) && legacy is null) return true;
            if (checkOnly)
            {
                scheduler.Validate(BuildTaskXml(processPath, UserSid));
                return true;
            }

            var taskChanged = false;
            try
            {
                if (enable)
                {
                    scheduler.Register(BuildTaskXml(processPath, UserSid));
                    taskChanged = true;
                    var registered = scheduler.Read();
                    if (registered is null || !MatchesTask(registered, processPath, UserSid))
                    {
                        Console.Error.WriteLine(registered ?? "Task missing after registration.");
                        throw new IOException("自启计划任务写入后核验失败。");
                    }
                }
                else if (previousXml is not null)
                {
                    scheduler.Delete();
                    taskChanged = true;
                    if (scheduler.Read() is not null) throw new IOException("自启计划任务删除后核验失败。");
                }
                // Retire the old entry only after the replacement has been verified.
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                if (LegacyCommand() is not null) throw new IOException("旧自启项清理失败。");
                return true;
            }
            catch (Exception original)
            {
                try
                {
                    if (taskChanged)
                    {
                        if (previousXml is not null) scheduler.Register(previousXml);
                        else if (scheduler.Read() is not null) scheduler.Delete();
                    }
                    if (legacy is not null && LegacyCommand() != legacy)
                    {
                        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                        key.SetValue(ValueName, legacy);
                    }
                }
                catch (Exception rollback) { throw new IOException(original.Message + " 恢复原自启设置失败：" + rollback.Message, original); }
                throw;
            }
        }
        catch (Exception ex)
        {
            error = ex.HResult == unchecked((int)0x80070005)
                ? "需要管理员权限才能更新开机自启，请以管理员身份运行程序或发布脚本。" : ex.Message;
            return false;
        }
    }

    private sealed class Scheduler : IDisposable
    {
        private readonly dynamic _service;
        private readonly dynamic _folder;
        internal Scheduler()
        {
            _service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            try { _service.Connect(); _folder = _service.GetFolder(@"\"); }
            catch { Marshal.FinalReleaseComObject(_service); throw; }
        }
        internal string? Read()
        {
            object? task = null;
            try { task = _folder.GetTask(TaskName); return (string)((dynamic)task).Xml; }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
            finally { if (task is not null) Marshal.FinalReleaseComObject(task); }
        }
        internal void Register(string xml)
        {
            object task = _folder.RegisterTask(TaskName, xml, 6, UserSid, null, 3, null);
            Marshal.FinalReleaseComObject(task);
        }
        internal void Validate(string xml)
        {
            // TASK_VALIDATE_ONLY checks Windows' XML schema without registering/running a task.
            object? task = _folder.RegisterTask(TaskName, xml, 1, UserSid, null, 3, null);
            if (task is not null) Marshal.FinalReleaseComObject(task);
        }
        internal void Delete() => _folder.DeleteTask(TaskName, 0);
        public void Dispose() { Marshal.FinalReleaseComObject(_folder); Marshal.FinalReleaseComObject(_service); }
    }
}
