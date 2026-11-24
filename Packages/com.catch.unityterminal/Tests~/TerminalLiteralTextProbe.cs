using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Optional: copy to Assets/Editor, create Temp/RunLiteralTextProbe, Assets > Refresh.
// Compares actual terminal-buffer output with literal text using Unity's font renderer.
internal static class TerminalLiteralTextProbe
{
    [InitializeOnLoadMethod]
    private static void Requested()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists("Temp/RunLiteralTextProbe")) return;
        File.Delete("Temp/RunLiteralTextProbe");
        EditorApplication.update += Verify;
    }
    private static void Verify()
    {
        EditorApplication.update -= Verify;
        Font font = null;
        try
        {
            font = Font.CreateDynamicFontFromOSFont("Consolas", 12);
            var style = new GUIStyle { font = font, fontSize = 12, richText = false };
            var report = new StringBuilder();
            Type bufferType = typeof(UnityTerminal.UnityTerminalWindow).Assembly.GetType("UnityTerminal.TerminalBuffer");
            Type parserType = typeof(UnityTerminal.UnityTerminalWindow).Assembly.GetType("UnityTerminal.AnsiParser");
            foreach (string literal in new[] { "PS C:\\Project>", "<b>literal</b>", "</noparse>",
                "List<T> x => x > 0 && x < 9", "&gt; &lt; &amp;", "<size=80>code</size>", "<noparse><b>text</b></noparse>", "\uD55C\uAE00 <T> >" })
            {
                foreach (string ansi in new[] { "", "\x1b[31m" })
                {
                    object buffer = Activator.CreateInstance(bufferType, new object[] { 2000, 262144, 16384 });
                    object parser = Activator.CreateInstance(parserType, new[] { buffer, (object)false });
                    parserType.GetMethod("Feed").Invoke(parser, new object[] { ansi + literal + "\x1b[0m" });
                    string plain = (string)bufferType.GetMethod("GetPlainText").Invoke(buffer, null);
                    string rich = (string)bufferType.GetMethod("GetRichText").Invoke(buffer, null);
                    style.richText = false;
                    Vector2 expected = style.CalcSize(new GUIContent(plain));
                    style.richText = true;
                    Vector2 actual = style.CalcSize(new GUIContent(rich));
                    if (Mathf.Abs(expected.x - actual.x) > 0.01f || Mathf.Abs(expected.y - actual.y) > 0.01f)
                        throw new Exception("Rendering differs for " + literal + ": expected " + expected + ", got " + actual);
                    report.AppendLine("PASS: " + (ansi.Length == 0 ? "plain " : "ANSI color ") + literal);
                }
            }
            report.AppendLine("PASS: literal prompt, brackets, markup, HTML entities and ANSI color render correctly");
            File.WriteAllText("Temp/LiteralTextProbe.result.txt", report.ToString());
        }
        catch (Exception ex) { File.WriteAllText("Temp/LiteralTextProbe.result.txt", "FAIL: " + ex); }
        finally { if (font != null) UnityEngine.Object.DestroyImmediate(font); }
    }
}
