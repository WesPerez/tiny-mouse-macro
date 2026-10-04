namespace TinyMouseMacro;

// Opt-in, same-desktop local messages. Only explicitly enabled stored profiles are callable.
// No command text, program paths, network service, or guest agent is accepted.
internal sealed class ExternalTrigger : IDisposable
{
    private readonly nint _window;
    private readonly Dictionary<uint, string> _profiles = [];
    public ExternalTrigger(nint window) => _window = window;
    public void Configure(IEnumerable<MacroProfile> profiles, bool enabled)
    {
        var desired = enabled ? profiles.Where(p => p.Enabled && IsEligible(p) && Guid.TryParseExact(p.Id, "N", out _))
            .ToDictionary(p => NativeMethods.RegisterWindowMessage("TinyMouseMacro.Run." + p.Id), p => p.Id) : [];
        var added = new List<uint>();
        try
        {
            foreach (var message in desired.Keys.Except(_profiles.Keys))
            {
                if (message == 0 || !NativeMethods.ChangeWindowMessageFilterEx(_window, message, 1, 0))
                    throw new InvalidOperationException("无法启用 iCUE 本地启动入口。");
                added.Add(message);
            }
        }
        catch
        {
            foreach (var message in added) NativeMethods.ChangeWindowMessageFilterEx(_window, message, 0, 0);
            throw;
        }
        foreach (var message in _profiles.Keys.Except(desired.Keys)) NativeMethods.ChangeWindowMessageFilterEx(_window, message, 0, 0);
        _profiles.Clear(); foreach (var item in desired) _profiles[item.Key] = item.Value;
    }
    public bool TryGetProfile(uint message, out string id) => _profiles.TryGetValue(message, out id!);
    public static bool IsEligible(MacroProfile p) => p.RepeatCount == 1 && string.IsNullOrWhiteSpace(p.TargetWindowTitle) &&
        p.Steps.Count == 1 && p.Steps[0].Type is MacroStepType.Move or MacroStepType.LeftClick;
    public void Dispose()
    { foreach (var message in _profiles.Keys) NativeMethods.ChangeWindowMessageFilterEx(_window, message, 0, 0); _profiles.Clear(); }
}
