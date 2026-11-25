using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor and create Temp/RunTerminalFontReloadProbe.
    // No shell is started. This deliberately reloads scripts once, then leaves an
    // isolated preview ready for a window-scoped screenshot until capture.done exists.
    public sealed class TerminalFontReloadProbe : UnityTerminalWindow
    {
        private static readonly Type WindowType = typeof(UnityTerminalWindow);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [SerializeField] private int phase;
        [SerializeField] private string report = "";
        [NonSerialized] private object screen, view;
        [NonSerialized] private bool painted;
        [NonSerialized] private double next;
        [NonSerialized] private bool finished;
        [NonSerialized] private bool awaitingReload;
        [NonSerialized] private double deadline;
        [NonSerialized] private EditorWindow previous;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists("Temp/RunTerminalFontReloadProbe")) return;
            File.Delete("Temp/RunTerminalFontReloadProbe");
            EditorApplication.update += Open;
        }
        private static void Open()
        {
            EditorApplication.update -= Open;
            try
            {
            var windows = Resources.FindObjectsOfTypeAll<UnityTerminalWindow>();
            Rect location = windows.Length > 0 ? windows[0].position : new Rect(100, 100, 900, 420);
            EditorWindow old = focusedWindow;
            var probe = CreateInstance<TerminalFontReloadProbe>();
            probe.previous = old;
            probe.titleContent = new GUIContent("Terminal font reload check");
            probe.position = new Rect(location.x, location.y, 900, 420);
            probe.ShowUtility(); probe.Focus();
            }
            catch (Exception ex) { File.WriteAllText("Temp/TerminalFontReloadProbe.result.txt", "FAIL: startup " + ex); }
        }
        private void OnEnable()
        {
            titleContent = new GUIContent("Terminal font reload check");
            File.WriteAllText("Temp/TerminalFontReloadProbe.result.txt", report + "Enabled phase " + phase + "\n");
            next = EditorApplication.timeSinceStartup + 0.5;
            awaitingReload = false;
            deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update += Tick;
        }
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            WindowType.GetMethod("OnDisable", Private).Invoke(this, null);
        }
        private void OnGUI()
        {
            if (finished) return;
            try
            {
                WindowType.GetMethod("InitStyles", Private).Invoke(this, null);
                if (screen == null)
                {
                    var assembly = WindowType.Assembly;
                    screen = Activator.CreateInstance(assembly.GetType("UnityTerminal.TerminalScreen"), new object[] { 120, 24 });
                    view = Activator.CreateInstance(assembly.GetType("UnityTerminal.InteractiveTerminalView"), new[] { screen });
                    string prompt = "\x1b[1;32mcatch@catch\x1b[0m:\x1b[1;34m/mnt/c/WorkDir/unity/UnityTerminal\x1b[0m$ ";
                    screen.GetType().GetMethod("Feed").Invoke(screen, new object[] {
                        prompt + "\uC548\uB155\uD558\uC138\uC694\r\n\uC548\uB155\uD558\uC138\uC694: command not found\r\n" + prompt + "ls\x1b[2 q" });
                }
                GUI.Label(new Rect(8, 4, position.width - 16, 22), "Same prompt, no trailing space after ls; font 12; after script reload");
                Font font = ((GUIStyle)Get("terminalStyle")).font;
                view.GetType().GetMethod("Draw").Invoke(view, new object[] { new Rect(8, 30, position.width - 16, position.height - 38), font, 12, focusedWindow == this });
                view.GetType().GetMethod("HandleInput").Invoke(view, null);
                if (Event.current.type == EventType.Repaint) painted = true;
            }
            catch (Exception ex) { Finish("FAIL: GUI " + ex); }
        }
        private void Tick()
        {
            if (finished || awaitingReload || !painted || EditorApplication.timeSinceStartup < next) return;
            try
            {
                if (phase == 0)
                {
                    VerifyFont("initial");
                    DestroyImmediate((Font)Get("terminalFont"));
                    WindowType.GetMethod("InitStyles", Private).Invoke(this, null);
                    VerifyFont("destroyed dynamic font recovered");
                    ((GUIStyle)Get("terminalStyle")).font = null;
                    WindowType.GetMethod("InitStyles", Private).Invoke(this, null);
                    VerifyFont("fontless cached style recovered");
                    phase = 1;
                    awaitingReload = true;
                    report += "Requesting an actual script reload\n";
                    File.WriteAllText("Temp/TerminalFontReloadProbe.result.txt", report);
                    EditorUtility.RequestScriptReload();
                    return;
                }
                if (phase == 1)
                {
                    Focus();
                    VerifyFont("after actual script reload");
                    IntPtr window = GetAncestor(GetFocus(), 2);
                    if (window == IntPtr.Zero) window = FindWindow(null, "Terminal font reload check");
                    var data = new CaptureReady { window = window.ToInt64(), pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                        font = ((GUIStyle)Get("terminalStyle")).font.name, pixelsPerPoint = EditorGUIUtility.pixelsPerPoint };
                    File.WriteAllText("Temp/TerminalFontReloadProbe.ready.json", JsonUtility.ToJson(data));
                    File.WriteAllText("Temp/TerminalFontReloadProbe.result.txt", report + "Ready for isolated preview capture\n");
                    phase = 2; next = EditorApplication.timeSinceStartup + 0.2;
                    return;
                }
                if (File.Exists("Temp/TerminalFontReloadProbe.capture.done"))
                {
                    File.Delete("Temp/TerminalFontReloadProbe.capture.done");
                    Finish("PASS: persistent fixed-width font ownership, missing-font recovery and real reload");
                }
                else if (EditorApplication.timeSinceStartup > deadline) Finish("FAIL: isolated preview was not captured before timeout");
                Repaint();
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void VerifyFont(string reason)
        {
            var style = (GUIStyle)Get("terminalStyle");
            Require(style.font != null && style.font == (Font)Get("terminalFont"), "Terminal is using a missing or borrowed UI font");
            foreach (FontStyle weight in new[] { FontStyle.Normal, FontStyle.Bold })
            {
                var measure = new GUIStyle { font = style.font, fontSize = 12, fontStyle = weight };
                float narrow = measure.CalcSize(new GUIContent("iiiiiiii")).x;
                float wide = measure.CalcSize(new GUIContent("MMMMMMMM")).x;
                Require(Mathf.Abs(narrow - wide) < 0.05f, "Proportional font leaked into the terminal: i=" + narrow + ", M=" + wide);
                report += "PASS: " + reason + ", " + weight + ", font=" + style.font.name + ", i=" + narrow + ", M=" + wide + "\n";
            }
        }
        private object Get(string field) { return WindowType.GetField(field, Private).GetValue(this); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private void Finish(string result)
        {
            if (finished) return;
            finished = true;
            File.WriteAllText("Temp/TerminalFontReloadProbe.result.txt", report + result + "\n");
            Close(); if (previous != null) previous.Focus();
        }
        [Serializable] private sealed class CaptureReady { public long window; public int pid; public string font; public float pixelsPerPoint; }
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    }
}
