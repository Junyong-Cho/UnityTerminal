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
        private bool retriedHangulMode;

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
                        if (native) { EnableHangulConversion(); SendKoreanKeys(); }
                        break;
                    case 2:
                        if (native)
                        {
                            if (!sawComposition && !retriedHangulMode)
                            {
                                retriedHangulMode = true;
                                Set("currentInput", ""); Set("pendingCaret", 0);
                                SendKey(0x15); SendKoreanKeys(); stage--;
                                break;
                            }
                            Require(sawComposition, "No native Korean composition was observed");
                            SendKey(0x0D); // Enter accepts composition; must not submit the command.
                        }
                        break;
                    case 3:
                        if (native)
                        {
                            Require((string)Get("currentInput") == "\uD55C\uAE00", "Native Korean result: " + Escape((string)Get("currentInput")));
                            object buffer = Get("buffer");
                            Require(!((string)buffer.GetType().GetMethod("GetPlainText").Invoke(buffer, null)).Contains("[System Error]"), "Composition Enter submitted a shell command");
                            trace.AppendLine("PASS: actual Windows Korean IME composes in primary field; Enter only commits");
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
                        if (native) SendKoreanKeys();
                        break;
                    case 5:
                        if (native)
                        {
                            Require(sawComposition, "Interactive input did not receive native composition");
                            SendKey(0x0D);
                        }
                        break;
                    default:
                        if (native)
                        {
                            Require(sent.ToString() == "\uD55C\uAE00", "Native interactive commits or Enter leaked: " + Escape(sent.ToString()));
                            trace.AppendLine("PASS: actual Windows Korean IME commits exactly once without forwarding composition Enter");
                            SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return, character = '\r' });
                            SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.Return, character = '\r' });
                            Require(sent.ToString() == "\uD55C\uAE00\r", "Enter after composition was swallowed or duplicated");
                            trace.AppendLine("PASS: the next Enter is forwarded normally after composition");
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
