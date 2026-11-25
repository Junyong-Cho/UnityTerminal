using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor and create Temp/RunTerminalClearProbe, then Refresh.
    // Runs hidden, without native key input, a real shell or changing focus.
    public sealed class TerminalClearRegressionProbe : UnityTerminalWindow
    {
        private static readonly Type Base = typeof(UnityTerminalWindow);
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private void OnEnable() { }
        private void OnDisable() { Base.GetMethod("OnDisable", Hidden).Invoke(this, null); }

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            const string marker = "Temp/RunTerminalClearProbe";
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
            File.Delete(marker);
            EditorApplication.update += Run;
        }
        private static void Run()
        {
            EditorApplication.update -= Run;
            var report = new StringBuilder();
            TerminalClearRegressionProbe window = null;
            try
            {
                window = CreateInstance<TerminalClearRegressionProbe>();
                object buffer = Base.GetField("buffer", Hidden).GetValue(window);
                MethodInfo send = Base.GetMethod("SendCommand", Hidden);
                var history = (IList)Base.GetField("commandHistory", Hidden).GetValue(window);
                foreach (string command in new[] { "clear", "cls", "Clear-Host", "  ClEaR  " })
                {
                    Base.GetMethod("AppendSystemMessage", Hidden).Invoke(window, new object[] { "old output\n" });
                    Base.GetField("scrollPosition", Hidden).SetValue(window, new Vector2(0, 200));
                    Require((bool)send.Invoke(window, new object[] { command }), "Clear was sent to the missing shell: " + command);
                    string text = (string)buffer.GetType().GetMethod("GetPlainText").Invoke(buffer, null);
                    Require(text.Trim().Length == 0, "Clear retained output or a shell error: " + text);
                    Require((Vector2)Base.GetField("scrollPosition", Hidden).GetValue(window) == Vector2.zero, "Clear retained the old scroll offset");
                    Require((string)history[history.Count - 1] == command, "Clear was not added to command history");
                    report.AppendLine("PASS: " + command.Trim() + " clears locally without a shell invocation");
                }
                Require(!(bool)send.Invoke(window, new object[] { "clear; echo preserved" }), "A compound PowerShell command was swallowed as local clear");
                Require(!(bool)send.Invoke(window, new object[] { "Clear-Host -?" }), "Clear-Host arguments were swallowed as local clear");
                report.AppendLine("PASS: compound commands and arguments retain the normal shell path");
            }
            catch (Exception ex) { report.AppendLine("FAIL: " + ex); }
            finally { if (window != null) DestroyImmediate(window); }
            File.WriteAllText("Temp/TerminalClearProbe.result.txt", report.ToString());
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
