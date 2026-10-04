namespace TinyMouseMacro;

internal static class PlaybackSelfTest
{
    internal static void Run(Action<string, bool> check)
    {
        static MacroStep Click(int x = 1500) => new() { Type = MacroStepType.LeftClick, X = x, Y = 300, RecordedDelayMs = 0 };
        static MacroStep Key() => new() { Type = MacroStepType.KeyPress, KeyCode = (int)Keys.A, RecordedDelayMs = 0 };
        static bool Fails(Action action)
        { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var desktop = new FakeDesktop { Remote = true, Clipped = true, DelayedMoves = 1 };
        var input = new FakeInput(desktop);
        var executor = new MacroExecutor(desktop, input);
        executor.RunAsync(new() { ReleaseRemoteInput = false, Steps = [Click(), Key()] }).GetAwaiter().GetResult();
        check("single invocation escapes clip then clicks once and types", desktop.Moves == 2 && desktop.Activations == 1 && desktop.Releases == 1 &&
            input.Events.SequenceEqual(["mouse:2@22", "mouse:4@22", "key:65:True@22", "key:65:False@22"]));
        check("all input follows confirmed focus and unclipped target", input.InputWasReady);

        desktop = new() { ActivationWorks = false, Remote = true, Clipped = true };
        input = new(desktop); executor = new(desktop, input);
        check("denied activation and unresponsive click stops before typing and never clicks twice",
            Fails(() => executor.RunAsync(new() { Steps = [Click(), Key()] }).GetAwaiter().GetResult()) &&
            input.Events.SequenceEqual(["mouse:2@11", "mouse:4@11"]) && desktop.Activations == 1);

        desktop = new() { ActivationWorks = false, Remote = true, Clipped = true };
        input = new(desktop); executor = new(desktop, input);
        input.AfterInput = () => { if (input.Events.Last() == "mouse:4@11") desktop.Foreground = 22; };
        executor.RunAsync(new() { Steps = [Click(), Key()] }).GetAwaiter().GetResult();
        check("denied foreground request uses original click then types at confirmed destination", desktop.Activations == 1 &&
            input.Events.SequenceEqual(["mouse:2@11", "mouse:4@11", "key:65:True@22", "key:65:False@22"]));

        desktop = new() { ActivationWorks = false, Remote = true, Clipped = true, DelayedMoves = 99 };
        input = new(desktop); executor = new(desktop, input);
        check("click activation fallback never clicks an unreachable point",
            Fails(() => executor.RunAsync(new() { Steps = [Click(), Key()] }).GetAwaiter().GetResult()) && input.Events.Count == 0);

        desktop = new() { Remote = true }; input = new(desktop); executor = new(desktop, input);
        input.AfterInput = () => { if (input.Events.Last() == "mouse:4@22") { desktop.Cursor = new(999, 300); desktop.Clipped = true; } };
        executor.RunAsync(new() { Steps = [Click(), Key()] }).GetAwaiter().GetResult();
        check("post-click recapture corrects movement without replaying button and before typing", desktop.Cursor.X == 1500 &&
            desktop.Releases == 1 && input.Events.Count == 4 && desktop.Moves == 2);

        desktop = new() { Remote = true }; input = new(desktop); executor = new(desktop, input);
        var clickDelays = new List<int>();
        input.AfterInput = () => { if (input.Events.Last() == "mouse:2@22") clickDelays.Add(desktop.Delays); };
        var doubleClick = Click(); doubleClick.Type = MacroStepType.DoubleClick;
        executor.RunAsync(new() { Steps = [doubleClick] }).GetAwaiter().GetResult();
        check("double click has no handoff correction wait between button pairs", clickDelays.Count == 2 && clickDelays[0] == clickDelays[1] && input.Events.Count == 4);

        desktop = new() { ActivationWorks = false }; input = new(desktop); executor = new(desktop, input);
        check("named window denial never falls back to speculative click",
            Fails(() => executor.RunAsync(new() { TargetWindowTitle = "test", Steps = [Key()] }).GetAwaiter().GetResult()) && input.Events.Count == 0);

        var bounds = new Rectangle(-1920, -1080, 8320, 2520);
        desktop = new(); input = new(desktop); executor = new(desktop, input);
        executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.KeyPress, KeyCode = (int)Keys.Menu }] }).GetAwaiter().GetResult();
        check("standalone Alt plays one key-down and key-up without a hotkey parser", input.Events.SequenceEqual(["key:18:True@11", "key:18:False@11"]));

        var motion = WindowsPlaybackInput.AbsoluteMovePacket(new(2272, -457), bounds).U.Mi;
        var decoded = new Point(bounds.X + (int)((long)motion.Dx * bounds.Width / 65536), bounds.Y + (int)((long)motion.Dy * bounds.Height / 65536));
        check("absolute motion maps negative multi-monitor coordinates to the requested pixel", decoded == new Point(2272, -457));
        check("absolute motion carries virtual-desktop no-coalesce and injection tag", motion.Flags == 0xE001 && motion.ExtraInfo == (nuint)NativeMethods.MacroInputTag);
        foreach (var edge in new[] { bounds.Location, new Point(bounds.Right - 1, bounds.Bottom - 1) })
        {
            motion = WindowsPlaybackInput.AbsoluteMovePacket(edge, bounds).U.Mi;
            decoded = new(bounds.X + (int)((long)motion.Dx * bounds.Width / 65536), bounds.Y + (int)((long)motion.Dy * bounds.Height / 65536));
            check("absolute desktop edge " + edge, decoded == edge);
        }

        var queue = new SwitchRequestQueue();
        var a = new MacroProfile { Steps = [Click()] }; var b = new MacroProfile { Steps = [Click()] }; var c = new MacroProfile { Steps = [Click()] };
        check("switch queue keeps last destination only", queue.Offer(a, b) && queue.Offer(a, c) && queue.Take() == c.Id && queue.Take() is null);
        queue.Offer(a, b); queue.Offer(a, a);
        check("repeated current shortcut never queues a duplicate click", queue.Take() is null);
        queue.Offer(a, b); queue.Clear();
        check("stop clears pending destination", queue.Take() is null);
        check("arbitrary macros never enter switch queue", !queue.Offer(a, new() { Steps = [Click(), Key()] }) && !queue.Offer(new() { RepeatCount = 0, Steps = [Click()] }, a));

        desktop = new() { Remote = true, Clipped = true, DelayedMoves = 99 };
        input = new(desktop); executor = new(desktop, input);
        check("persistent border trapping stops without click or keyboard",
            Fails(() => executor.RunAsync(new() { Steps = [Click(), Key()] }).GetAwaiter().GetResult()) && input.Events.Count == 0 && desktop.Moves == 6);

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.Move, X = 1500, Y = 300, RecordedDelayMs = 0 }, Key()] }).GetAwaiter().GetResult();
        check("normal recorded hover does not steal focus or add verification delay", desktop.Activations == 0 && desktop.Delays == 0 && input.Events.All(e => e.EndsWith("@11")));

        desktop = new() { Remote = true }; input = new(desktop); executor = new(desktop, input);
        executor.RunAsync(new() { Steps = [Click(500), Key()] }).GetAwaiter().GetResult();
        check("guest internal click keeps remote console focused", desktop.Activations == 0 && desktop.Releases == 0 && input.Events.All(e => e.EndsWith("@11")));

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        executor.RunAsync(new() { RepeatCount = 2, Steps = [Click(), Key(), Click(500), Key()] }).GetAwaiter().GetResult();
        check("recorded multi-window loop follows every target", desktop.Activations == 4 && input.Events.Count == 16 && input.Events[2].EndsWith("@22") && input.Events[6].EndsWith("@11"));

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.Drag, X = 1500, Y = 300, DragEndX = 500, DragEndY = 300 }, Key()] }).GetAwaiter().GetResult();
        check("cross-window drag retains drag owner and releases once", desktop.Activations == 1 && input.Events.SequenceEqual(["mouse:2@22", "mouse:4@22", "key:65:True@22", "key:65:False@22"]));

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        input.AfterInput = () => { if (input.Events.Last() == "key:164:False@11") desktop.Foreground = 22; };
        executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.KeyCombo, KeyCode = (int)Keys.Tab, KeyModifiers = 1 }, Key()] }).GetAwaiter().GetResult();
        check("explicit Alt Tab follows changed foreground on next key", input.Events.TakeLast(2).All(e => e.EndsWith("@22")));

        desktop = new();
        var session = new PlaybackSession(desktop, null);
        desktop.Foreground = 33; desktop.DialogAllowed = true;
        session.VerifyFocus();
        check("asynchronous same-application dialog can receive next key", desktop.Foreground == 33);

        foreach (var type in new[] { MacroStepType.LeftClick, MacroStepType.RightClick, MacroStepType.MiddleClick, MacroStepType.DoubleClick })
        {
            desktop = new() { Clipped = true }; input = new(desktop); executor = new(desktop, input);
            var step = Click(); step.Type = type;
            executor.RunAsync(new() { Steps = [step, Key()] }).GetAwaiter().GetResult();
            check($"automatic handoff covers {type}", desktop.Activations == 1 && input.Events.Count(e => e.StartsWith("mouse:")) == (type == MacroStepType.DoubleClick ? 4 : 2));
        }

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        input.AfterInput = () => { if (input.Events.Last() == "mouse:4@22") desktop.Foreground = 33; };
        check("unrelated focus change after click blocks following key", Fails(() => executor.RunAsync(new() { Steps = [Click(), Key()] }).GetAwaiter().GetResult()) && input.Events.Count == 2);

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        input.AfterInput = () => desktop.Foreground = 33;
        check("text stops immediately when focus changes", Fails(() => executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.TypeText, TextToType = "ab" }] }).GetAwaiter().GetResult()) && input.Events.Count == 1);

        desktop = new(); input = new(desktop) { Modifiers = 4 }; executor = new(desktop, input);
        check("held physical Shift prevents movement focus and modified click", Fails(() => executor.RunAsync(new() { Steps = [Click(), Key()] }).GetAwaiter().GetResult()) && input.Events.Count == 0 && desktop.Moves == 0 && desktop.Activations == 0);

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        check("missing named window never types into previous foreground", Fails(() => executor.RunAsync(new() { TargetWindowTitle = "missing", Steps = [Key()] }).GetAwaiter().GetResult()) && input.Events.Count == 0);

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        using (var cancellation = new CancellationTokenSource())
        {
            input.AfterInput = () => { if (input.Events.Last().StartsWith("key:65:True")) cancellation.Cancel(); };
            var cancelled = false;
            try { executor.RunAsync(new() { Steps = [new() { Type = MacroStepType.KeyCombo, KeyCode = (int)Keys.A, KeyModifiers = 6 }] }, cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            check("cancellation releases combo main key and both modifiers", cancelled && input.Events.Count == 6 &&
                input.Events.TakeLast(3).SequenceEqual(["key:65:False@11", "key:160:False@11", "key:162:False@11"]));
        }

        desktop = new(); input = new(desktop); executor = new(desktop, input);
        using (var cancellation = new CancellationTokenSource())
        {
            input.AfterInput = () => { if (input.Events.Last() == "mouse:2@22") cancellation.Cancel(); };
            var cancelled = false;
            try { executor.RunAsync(new() { Steps = [Click()] }, cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            check("cancelled click releases button exactly once", cancelled && input.Events.SequenceEqual(["mouse:2@22", "mouse:4@22"]));
        }

        check("right and left physical modifiers recognized", PhysicalModifiers.Read(k => k is Keys.RShiftKey or Keys.RControlKey or Keys.RMenu or Keys.RWin) == 15 && PhysicalModifiers.Read(k => k == Keys.LShiftKey) == 4);
        var packet = WindowsPlaybackInput.KeyPacket((int)Keys.A, 0x1E, true).U.Ki;
        check("ordinary key uses physical scan code with macro tag", packet.WVk == 0 && packet.WScan == 0x1E && packet.DwFlags == 8 && packet.DwExtraInfo == (nint)NativeMethods.MacroInputTag);
        packet = WindowsPlaybackInput.KeyPacket((int)Keys.Right, 0xE04D, false).U.Ki;
        check("extended scan key carries extended and release flags", packet.WScan == 0x4D && packet.DwFlags == 11);
        packet = WindowsPlaybackInput.KeyPacket((int)Keys.F24, 0, true).U.Ki;
        check("unmapped key retains virtual-key fallback", packet.WVk == (int)Keys.F24 && packet.DwFlags == 0);
        packet = WindowsPlaybackInput.KeyPacket((int)Keys.Pause, 0xE11D, true).U.Ki;
        check("Pause preserves virtual-key special sequence", packet.WVk == (int)Keys.Pause && packet.DwFlags == 0);
    }

    private sealed class FakeDesktop : IPlaybackDesktop
    {
        public nint Foreground { get; set; } = 11;
        public Point Cursor { get; set; } = new(500, 300);
        public bool Remote, Clipped, DialogAllowed, ActivationWorks = true;
        public int DelayedMoves, Moves, Activations, Releases, Delays;
        public nint WindowAt(Point point) => point.X >= 1000 ? 22 : 11;
        public nint FindWindow(string title) => title == "missing" ? 0 : 22;
        public bool IsWindow(nint window) => window is 11 or 22;
        public bool SameApplication(nint first, nint second) => first == second || DialogAllowed;
        public bool IsRemote(nint window) => Remote && window == 11;
        public bool IsOnScreen(Point point) => point.X >= 0 && point.X < 2000;
        public bool OutsideClip(Point point) => Clipped && point.X >= 1000;
        public bool ReleaseClip() { Releases++; Clipped = false; return true; }
        public bool Activate(nint window) { Activations++; if (ActivationWorks) Foreground = window; return true; }
        public bool Move(Point point) { Moves++; Cursor = Moves <= DelayedMoves || OutsideClip(point) ? new(999, point.Y) : point; return true; }
        public Task Delay(int ms, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Delays++; return Task.CompletedTask; }
    }

    private sealed class FakeInput(FakeDesktop desktop) : IPlaybackInput
    {
        public uint Modifiers { get; set; }
        public readonly List<string> Events = [];
        public Action? AfterInput;
        public bool InputWasReady = true;
        private void Add(string text)
        {
            Events.Add(text + "@" + desktop.Foreground);
            InputWasReady &= !desktop.Clipped;
            AfterInput?.Invoke();
        }
        public void Key(int key, bool down) => Add($"key:{key}:{down}");
        public void Character(char character) => Add($"char:{character}");
        public void Mouse(uint flags, uint data = 0) => Add($"mouse:{flags}");
        public void MouseAt(Point point, uint flags)
        {
            InputWasReady &= PointerPlacement.IsAtTarget(desktop.Cursor, point);
            Mouse(flags);
        }
    }
}
