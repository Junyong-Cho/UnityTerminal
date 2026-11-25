using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Passive, without windows or keyboard injection. Copy to Assets/Editor and
    // create Temp/RunTerminalScrollProbe (contents: inspect for diagnostics only).
    internal static class TerminalScrollProbe
    {
        private static readonly Assembly Assembly = typeof(UnityTerminalWindow).Assembly;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static bool inspect;
        [InitializeOnLoadMethod]
        private static void Requested()
        {
            const string marker = "Temp/RunTerminalScrollProbe";
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
            inspect = File.ReadAllText(marker).Trim() == "inspect";
            File.Delete(marker);
            EditorApplication.update += Run;
        }
        private static void Run()
        {
            EditorApplication.update -= Run;
            var report = new StringBuilder();
            try
            {
                foreach (var window in Resources.FindObjectsOfTypeAll<UnityTerminalWindow>())
                {
                    object screen = typeof(UnityTerminalWindow).GetField("interactiveScreen", Hidden).GetValue(window);
                    if (screen == null) continue;
                    report.AppendLine("Active terminal: alternate=" + Property(screen, "Alternate") + ", history=" + Property(screen, "HistoryCount"));
                }
                if (!inspect) Test(report);
                report.AppendLine("PASS: passive scroll " + (inspect ? "inspection" : "regression checks"));
            }
            catch (Exception ex) { report.AppendLine("FAIL: " + ex); }
            File.WriteAllText("Temp/TerminalScrollProbe.result.txt", report.ToString());
        }
        private static object Property(object value, string name) { return value.GetType().GetProperty(name).GetValue(value, null); }
        private static void Test(StringBuilder report)
        {
            Type screenType = Assembly.GetType("UnityTerminal.TerminalScreen");
            object screen = Activator.CreateInstance(screenType, new object[] { 30, 6 });
            Type viewType = Assembly.GetType("UnityTerminal.InteractiveTerminalView");
            object view = Activator.CreateInstance(viewType, new[] { screen });
            viewType.GetField("bounds", Hidden).SetValue(view, new Rect(100, 50, 300, 200));
            var sent = new StringBuilder();
            viewType.GetField("Send").SetValue(view, (Action<string>)(value => sent.Append(value)));
            for (int i = 0; i < 40; i++) Feed(screen, "line-" + i + "\r\n");
            int history = (int)Property(screen, "HistoryCount");
            Require(history > 20, "No scroll history in fixture");

            Wheel(view, -1);
            Require(Offset(view) == 3 && sent.Length == 0, "Wheel up did not reveal older output locally");
            Wheel(view, 1);
            Require(Offset(view) == 0 && sent.Length == 0, "Wheel down did not return to latest output");
            Wheel(view, -1000);
            Require(Offset(view) == history, "Scroll exceeded the oldest line");
            Wheel(view, 1000);
            Require(Offset(view) == 0, "Scroll exceeded latest output");
            for (int i = 0; i < 8; i++) Wheel(view, -0.05f);
            Require(Offset(view) == 1, "Small trackpad deltas were discarded");
            report.AppendLine("PASS: wheel direction, scrollback bounds and fractional trackpad input");

            HistoryKey(view, KeyCode.Home);
            Require(Offset(view) == history, "Shift+Home did not reach oldest output");
            HistoryKey(view, KeyCode.PageDown);
            Require(Offset(view) == history - 5, "Shift+PageDown did not scroll one visible page");
            HistoryKey(view, KeyCode.End);
            Require(Offset(view) == 0, "Shift+End did not return to latest output");
            HistoryKey(view, KeyCode.PageUp);
            Require(Offset(view) == 5, "Shift+PageUp did not scroll one visible page");
            Call(view, "SendText", "draft");
            Require(Offset(view) == 0 && sent.ToString() == "draft", "Typing did not return to live output");
            report.AppendLine("PASS: Shift+PageUp/PageDown/Home/End and returning to live output on typing");

            sent.Length = 0;
            Feed(screen, "\x1b[?1049happ");
            Require((int)Property(screen, "HistoryCount") == 0, "Alternate screen unexpectedly has scrollback");
            Wheel(view, -3); Wheel(view, 3);
            Require(sent.ToString() == "\x1b[5~\x1b[6~" && Offset(view) == 0,
                "Alternate screen swallowed the wheel instead of paging the running app");
            sent.Length = 0;
            Wheel(view, -1000);
            Require(sent.ToString() == "\x1b[5~", "A wheel burst flooded the app with page keys");
            sent.Length = 0;
            for (int i = 0; i < 4; i++) Wheel(view, -0.3f);
            Require(sent.ToString() == "\x1b[5~", "Fractional scrolling did not reach the app");
            report.AppendLine("PASS: alternate-screen wheel sends bounded PageUp/PageDown without restarting the app");

            Feed(screen, "\x1b[?1049l");
            sent.Length = 0;
            Wheel(view, -1);
            Require(Offset(view) == 3 && sent.Length == 0, "Returning to the shell left application scroll routing active");
            Call(screen, "Reset");
            Wheel(view, -3);
            Require(Offset(view) == 0 && sent.Length == 0, "An empty shell received application page keys");
            report.AppendLine("PASS: exiting the app restores shell scrollback without leaking page keys");
        }
        private static void Wheel(object view, float delta) { Call(view, "ScrollWheel", delta); }
        private static void HistoryKey(object view, KeyCode code)
        {
            Require((bool)Call(view, "ScrollHistoryKey", new Event { type = EventType.KeyDown, keyCode = code, shift = true }), "History key was not handled");
        }
        private static int Offset(object view) { return (int)view.GetType().GetField("scrollBack", Hidden).GetValue(view); }
        private static object Call(object value, string method, params object[] args)
        { return value.GetType().GetMethod(method, Hidden | BindingFlags.Public).Invoke(value, args); }
        private static void Feed(object screen, string text) { Call(screen, "Feed", text); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
