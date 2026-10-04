namespace TinyMouseMacro;

// A screen destination is replaceable. Arbitrary macros and loops must never be queued
// behind each other: they could type or click in a window the user did not expect.
internal sealed class SwitchRequestQueue
{
    private string? _next;
    internal bool Offer(MacroProfile running, MacroProfile requested)
    {
        if (!ExternalTrigger.IsEligible(running) || !ExternalTrigger.IsEligible(requested)) return false;
        _next = requested.Id == running.Id ? null : requested.Id;
        return true;
    }
    internal string? Take() { var result = _next; _next = null; return result; }
    internal void Clear() => _next = null;
}
