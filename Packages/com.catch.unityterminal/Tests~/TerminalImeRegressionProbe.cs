using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor and create Temp/RunTerminalImeProbe (contents: native for OS IME).
    // Uses isolated input controls and an in-memory app screen; never sends a shell command.
    public sealed class TerminalImeRegressionProbe : UnityTerminalWindow
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type WindowType = typeof(UnityTerminalWindow);
        private static bool native;
        private EditorWindow previous;
        private readonly StringBuilder trace = new StringBuilder();
        private readonly StringBuilder sent = new StringBuilder();
        private object view;
        private bool interactive;
        private bool painted;
        private bool finished;
        private bool sawComposition;
        private int stage;
        private double next;
        private double deadline;
        private IntPtr originalLayout;
        private IMECompositionMode originalMode;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            const string marker = "Temp/RunTerminalImeProbe";
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
            native = File.ReadAllText(marker).Trim() == "native";
            File.Delete(marker);
            EditorApplication.update += Open;
        }

        private static void Open()
        {
            EditorApplication.update -= Open;
            try
            {
                EditorWindow old = focusedWindow;
                var probe = CreateInstance<TerminalImeRegressionProbe>();
                probe.previous = old;
                probe.titleContent = new GUIContent("Terminal Korean input check");
                probe.position = new Rect(100, 100, 900, 400);
                probe.ShowUtility(); probe.Focus();
            }
            catch (Exception ex) { File.WriteAllText("Temp/TerminalImeProbe.result.txt", "FAIL: startup " + ex); }
        }

        private void OnEnable()
        {
            originalMode = UnityEngine.Input.imeCompositionMode;
            Set("currentWorkingDirectory", Path.GetFullPath("."));
            next = EditorApplication.timeSinceStartup + 0.5;
            deadline = next + 30;
            EditorApplication.update += Tick;
        }
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            WindowType.GetMethod("OnDisable", Private).Invoke(this, null);
            if (originalLayout != IntPtr.Zero) ActivateKeyboardLayout(originalLayout, 0);
            UnityEngine.Input.imeCompositionMode = originalMode;
        }
        private void OnGUI()
        {
            try
            {
                if (!interactive) WindowType.GetMethod("OnGUI", Private).Invoke(this, null);
                else
                {
                    WindowType.GetMethod("InitStyles", Private).Invoke(this, null);
                    var font = ((GUIStyle)Get("terminalStyle")).font;
                    view.GetType().GetMethod("Draw").Invoke(view, new object[] { new Rect(0, 0, position.width, position.height), font, 12, focusedWindow == this });
                    view.GetType().GetMethod("HandleInput").Invoke(view, null);
                    WindowType.GetMethod("UpdateInputMethod", Private).Invoke(this, null);
                }
                if (UnityEngine.Input.compositionString.Length > 0) sawComposition = true;
                if (Event.current.type == EventType.Repaint) painted = true;
            }
            catch (Exception ex) { Finish("FAIL: GUI " + ex); }
        }
        private void Tick()
        {
            if (finished || EditorApplication.timeSinceStartup < next) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new Exception("IME check timed out");
                if (!painted || focusedWindow != this) { Focus(); Repaint(); return; }
                if (native && !EnsureNativeFocus()) { next = EditorApplication.timeSinceStartup + 0.2; return; }
                if (primeStep >= 0)
                {
                    PrimeKoreanMode();
                    next = EditorApplication.timeSinceStartup + 0.6;
                    Repaint();
                    return;
                }
                switch (stage++)
                {
                    case 0:
                        Require(UnityEngine.Input.imeCompositionMode == IMECompositionMode.On, "Primary input did not enable IME");
                        foreach (char c in "\u314E\uD55C\uAE00") Press(c);
                        Require((string)Get("currentInput") == "\u314E\uD55C\uAE00", "Primary field dropped Korean character events");
                        trace.AppendLine("PASS: primary field enables IME and accepts Korean character events");
                        Set("currentInput", ""); Set("pendingCaret", 0);
                        if (native) EnableKoreanKeyboard();
                        break;
                    case 1:
                        // Priming confirms Hangul composition, then types the real keys.
                        if (native) { EnableHangulConversion(); BeginPriming(); }
                        break;
                    case 2:
                        if (native)
                        {
                            Require(sawComposition, "No native Korean composition was observed");
                            SendKey(0x0D); // Korean IME: Enter commits the syllable and submits in one press.
                        }
                        break;
                    case 3:
                        if (native)
                        {
                            // No shell runs in this window, so a submission shows as [System Error] and keeps the text.
                            Require((string)Get("currentInput") == "\uD55C\uAE00", "Native Korean result: " + Escape((string)Get("currentInput")));
                            object buffer = Get("buffer");
                            string log = (string)buffer.GetType().GetMethod("GetPlainText").Invoke(buffer, null);
                            Require(log.Contains("[System Error]"), "One Enter after a Korean commit did not submit");
                            Require(log.IndexOf("[System Error]") == log.LastIndexOf("[System Error]"), "Korean commit Enter submitted twice");
                            trace.AppendLine("PASS: actual Windows Korean IME composes in primary field; one Enter commits and submits once");
                        }
                        WindowType.GetMethod("RestoreInputMethod", Private).Invoke(this, null);
                        Require(UnityEngine.Input.imeCompositionMode == originalMode, "IME mode was not restored");
                        trace.AppendLine("PASS: previous IME mode is restored when leaving input");
                        CreateInteractiveView();
                        break;
                    case 4:
                        Require(UnityEngine.Input.imeCompositionMode == IMECompositionMode.On, "Interactive input did not enable IME");
                        foreach (char c in "\u314E\uD55C\uAE00") Press(c);
                        Require(sent.ToString() == "\u314E\uD55C\uAE00", "Interactive field dropped or duplicated characters: " + sent);
                        trace.AppendLine("PASS: interactive field enables IME and sends Korean commits exactly once");
                        sent.Length = 0; sawComposition = false;
                        if (native) BeginPriming();
                        break;
                    case 5:
                        if (native)
                        {
                            Require(sawComposition, "Interactive input did not receive native composition");
                            SendKey(0x0D); // Korean IME: Enter commits the syllable and is forwarded once.
                        }
                        break;
                    case 6:
                        if (native)
                        {
                            Require(sent.ToString() == "\uD55C\uAE00\r", "Native interactive Korean commit + Enter: " + Escape(sent.ToString()));
                            trace.AppendLine("PASS: actual Windows Korean IME commits once and one Enter is forwarded once");
                            SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return, character = '\r' });
                            SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.Return, character = '\r' });
                            Require(sent.ToString() == "\uD55C\uAE00\r\r", "A later Enter was swallowed or duplicated");
                            trace.AppendLine("PASS: the next Enter is forwarded normally after composition");
                            // \uAC00\uB098 -> Backspace x2 erases the composing \uB098 (no commit) -> the third Backspace must
                            // reach the app and delete \uAC00, not be taken as accepting a commit.
                            sent.Length = 0;
                            TypeAtTypingSpeed('R', 'K', 'S', 'K', 0x08, 0x08, 0x08);
                        }
                        break;
                    default:
                        if (native)
                        {
                            Require(sent.ToString() == "\uAC00\x7f", "Backspace after erasing the composition: " + Escape(sent.ToString()));
                            trace.AppendLine("PASS: after Backspace erases the composing syllable, the next Backspace deletes the previous character");
                        }
                        Finish("PASS: Korean input and IME focus lifecycle" + (native ? " including native keyboard composition" : " (native IME not requested)"));
                        return;
                }
                next = EditorApplication.timeSinceStartup + 0.6;
                Repaint();
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void CreateInteractiveView()
        {
            var assembly = WindowType.Assembly;
            Type screenType = assembly.GetType("UnityTerminal.TerminalScreen");
            object screen = Activator.CreateInstance(screenType, new object[] { 100, 20 });
            Type viewType = assembly.GetType("UnityTerminal.InteractiveTerminalView");
            view = Activator.CreateInstance(viewType, new[] { screen });
            viewType.GetField("Send").SetValue(view, (Action<string>)(text => sent.Append(text)));
            interactive = true; painted = false;
        }
        private void EnableKoreanKeyboard()
        {
            originalLayout = GetKeyboardLayout(0);
            int count = GetKeyboardLayoutList(0, null);
            var layouts = new IntPtr[count];
            GetKeyboardLayoutList(count, layouts);
            IntPtr korean = Array.Find(layouts, layout => (layout.ToInt64() & 0xffff) == 0x0412);
            Require(korean != IntPtr.Zero, "No Korean keyboard is installed for native verification");
            ActivateKeyboardLayout(korean, 0);
        }
        private void EnableHangulConversion()
        {
            IntPtr window = GetFocus();
            IntPtr context = ImmGetContext(window);
            if (context == IntPtr.Zero) { window = GetAncestor(window, 2); context = ImmGetContext(window); }
            trace.AppendLine("Native IME focus=" + GetFocus() + ", foreground=" + GetForegroundWindow() + ", context=" + context);
            // TSF may expose no legacy context. Try the current mode first; the guarded
            // primary-input stage toggles Hangul once only if actual composition is absent.
            if (context == IntPtr.Zero) return;
            try
            {
                int conversion, sentence;
                ImmGetConversionStatus(context, out conversion, out sentence);
                ImmSetOpenStatus(context, true);
                Require(ImmSetConversionStatus(context, conversion | 1, sentence), "Cannot enable Hangul conversion");
            }
            finally { ImmReleaseContext(window, context); }
        }
        private void SendKoreanKeys() { foreach (char c in "GKSRMF") SendKey(c); }

        // Right after switching layouts the IME may still be in Latin mode for the first key
        // (G -> "g", then the rest composes: "gㅏㄴ글"). Before the real input, type a priming G and
        // require the composition "ㅎ"; cancel it with Backspace and clear the field. A Latin commit
        // toggles Hangul mode and retries. Not reaching Hangul mode ends as NOT READY (environment),
        // separate from a product FAIL.
        private const int MaxPrimeAttempts = 4;
        private int primeStep = -1;
        private int primeAttempts;

        private void BeginPriming() { primeStep = 0; primeAttempts = 0; }

        private void PrimeKoreanMode()
        {
            switch (primeStep)
            {
                case 0:
                    ClearProbeInput();
                    SendKey('G');
                    primeStep = 1;
                    return;
                case 1:
                    string composition = UnityEngine.Input.compositionString;
                    if (composition == "ㅎ")
                    {
                        SendKey(0x08); // Backspace removes the priming jamo without committing it.
                        trace.AppendLine("Korean IME ready after " + (primeAttempts + 1) + " priming attempt(s)");
                        primeStep = 2;
                        return;
                    }
                    primeAttempts++;
                    string committed = interactive ? sent.ToString() : (string)Get("currentInput");
                    trace.AppendLine("Korean IME not ready (attempt " + primeAttempts + "): composition=[" + composition + "] committed=[" + Escape(committed) + "]");
                    if (primeAttempts >= MaxPrimeAttempts) { NotReady("Korean IME did not enter Hangul composition mode"); return; }
                    if (composition.Length > 0) SendKey(0x08);
                    else if (committed.IndexOf('g') >= 0 || committed.IndexOf('G') >= 0) SendKey(0x15); // Latin mode: toggle Hangul.
                    primeStep = 0;
                    return;
                default:
                    Require(UnityEngine.Input.compositionString.Length == 0, "Priming composition was not cancelled");
                    ClearProbeInput();
                    sawComposition = false;
                    primeStep = -1;
                    SendKoreanKeys();
                    return;
            }
        }

        private void ClearProbeInput()
        {
            if (interactive == true) sent.Length = 0;
            else { Set("currentInput", ""); Set("pendingCaret", 0); }
        }

        private void NotReady(string reason)
        {
            primeStep = -1;
            Finish("NOT READY: " + reason + " (test environment, not a product result)");
        }
        private static bool EnsureNativeFocus()
        {
            uint foregroundPid;
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid);
            if (foregroundPid == System.Diagnostics.Process.GetCurrentProcess().Id) return true;
            IntPtr window = GetAncestor(GetFocus(), 2);
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            uint windowPid;
            GetWindowThreadProcessId(window, out windowPid);
            Require(windowPid == System.Diagnostics.Process.GetCurrentProcess().Id && title.ToString().Contains("Terminal Korean input check"),
                "Cannot focus an isolated Unity IME test window");
            uint thread = GetCurrentThreadId();
            bool attached = AttachThreadInput(thread, foregroundThread, true);
            try { SetForegroundWindow(window); }
            finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
            return false; // Let Unity process focus and IME activation before sending input.
        }
        private static void SendKey(int key)
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            Require(pid == System.Diagnostics.Process.GetCurrentProcess().Id, "Refusing to type outside this Unity process");
            var title = new StringBuilder(256);
            GetWindowText(GetForegroundWindow(), title, title.Capacity);
            Require(title.ToString().Contains("Terminal Korean input check"), "Refusing to type outside the isolated test window: " + title);
            var inputs = new[] { new NativeInput { Type = 1, Key = new KeyInput { VirtualKey = (ushort)key } },
                new NativeInput { Type = 1, Key = new KeyInput { VirtualKey = (ushort)key, Flags = 2 } } };
            Require(SendInput(2, inputs, Marshal.SizeOf(typeof(NativeInput))) == 2, "Native key injection failed");
        }
        // Keys 40 ms apart, like fast typing; keys sent with no gap at all can be dropped by the IME itself.
        private static void TypeAtTypingSpeed(params int[] keys)
        {
            new System.Threading.Thread(() =>
            {
                try { foreach (int key in keys) { SendKey(key); System.Threading.Thread.Sleep(40); } }
                catch (Exception) { } // the stage check reports what arrived
            }).Start();
        }
        private void Press(char c)
        {
            SendEvent(new Event { type = EventType.KeyDown, character = c });
            SendEvent(new Event { type = EventType.KeyUp, character = c });
        }
        private object Get(string name) { return WindowType.GetField(name, Private).GetValue(this); }
        private void Set(string name, object value) { WindowType.GetField(name, Private).SetValue(this, value); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static string Escape(string value) { return value.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\x1b", "<ESC>"); }
        private void Finish(string result)
        {
            if (finished) return;
            finished = true;
            trace.AppendLine(result);
            File.WriteAllText("Temp/TerminalImeProbe.result.txt", trace.ToString());
            Close();
            if (previous != null) previous.Focus();
        }
        [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
        // INPUT is 40 bytes on 64-bit Windows; the union also contains a 32-byte MOUSEINPUT.
        [StructLayout(LayoutKind.Explicit, Size = 40)] private struct NativeInput { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyInput Key; }
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
        [DllImport("user32.dll")] private static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
        [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
        [DllImport("imm32.dll")] private static extern bool ImmSetOpenStatus(IntPtr context, bool open);
        [DllImport("imm32.dll")] private static extern bool ImmGetConversionStatus(IntPtr context, out int conversion, out int sentence);
        [DllImport("imm32.dll")] private static extern bool ImmSetConversionStatus(IntPtr context, int conversion, int sentence);
    }
}
