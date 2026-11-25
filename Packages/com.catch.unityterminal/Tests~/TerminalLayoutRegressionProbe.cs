using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor; create Temp/RunTerminalLayoutProbe and use Assets > Refresh.
    // Exercises actual font metrics and IMGUI geometry without starting any shell.
    public sealed class TerminalLayoutRegressionProbe : EditorWindow
    {
        private static readonly Assembly TerminalAssembly = typeof(UnityTerminalWindow).Assembly;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private object screen, view;
        private Font font;
        private int fontSize = 12;
        private int stage;
        private int paints;
        private bool finished;
        private double next;
        private EditorWindow previous;
        private readonly StringBuilder report = new StringBuilder();
        private static readonly FieldInfo DrawCallback = typeof(GUIStyle).GetField("onDraw", BindingFlags.Static | BindingFlags.NonPublic);
        private Delegate drawObserver, previousDraw;
        private float renderedTextEnd;
        private bool sawWideGlyph, sawHiddenInputCursor;
        private int boldGlyphs;
        private Matrix4x4 expectedMatrix;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists("Temp/RunTerminalLayoutProbe")) return;
            File.Delete("Temp/RunTerminalLayoutProbe");
            EditorApplication.update += Open;
        }
        private static void Open()
        {
            EditorApplication.update -= Open;
            EditorWindow old = focusedWindow;
            var probe = CreateInstance<TerminalLayoutRegressionProbe>();
            probe.previous = old;
            probe.position = new Rect(100, 100, 1000, 420);
            probe.titleContent = new GUIContent("Terminal layout check");
            probe.ShowUtility(); probe.Focus();
        }
        private void OnEnable()
        {
            font = Font.CreateDynamicFontFromOSFont("Consolas", fontSize);
            screen = Activator.CreateInstance(TerminalAssembly.GetType("UnityTerminal.TerminalScreen"), new object[] { 150, 20 });
            view = Activator.CreateInstance(TerminalAssembly.GetType("UnityTerminal.InteractiveTerminalView"), new[] { screen });
            Feed(new string('M', 60) + new string('\uD55C', 20) + "XYZ");
            next = EditorApplication.timeSinceStartup + 0.5;
            EditorApplication.update += Tick;
        }
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            if (font != null) DestroyImmediate(font);
        }
        private void OnGUI()
        {
            if (finished || view == null) return;
            Color color = GUI.skin.settings.cursorColor;
            float flash = GUI.skin.settings.cursorFlashSpeed;
            try
            {
                if (drawObserver == null)
                {
                    var signature = DrawCallback.FieldType.GetMethod("Invoke").GetParameters();
                    var args = new ParameterExpression[signature.Length];
                    for (int i = 0; i < args.Length; i++) args[i] = Expression.Parameter(signature[i].ParameterType);
                    drawObserver = Expression.Lambda(DrawCallback.FieldType,
                        Expression.Call(Expression.Constant(this), GetType().GetMethod("ObserveDraw", Hidden),
                            args[0], args[1], args[2], Expression.Convert(args[3], typeof(object))), args).Compile();
                }
                previousDraw = (Delegate)DrawCallback.GetValue(null);
                DrawCallback.SetValue(null, drawObserver);
                if (Event.current.type == EventType.Repaint) { renderedTextEnd = 0; sawWideGlyph = sawHiddenInputCursor = false; boldGlyphs = 0; }
                // Force the native caret into its visible blink phase, so alpha zero below
                // proves that the input receiver actually suppresses its second cursor.
                GUI.skin.settings.cursorColor = Color.white;
                GUI.skin.settings.cursorFlashSpeed = 0;
                Matrix4x4 matrix = GUI.matrix;
                expectedMatrix = matrix;
                CallView("Draw", new Rect(0, 0, position.width, position.height), font, fontSize, focusedWindow == this);
                CallView("HandleInput");
                Require(GUI.matrix == matrix, "Terminal drawing changed the following controls' transform");
                Require(GUI.skin.settings.cursorColor == Color.white, "IME caret visibility leaked into other controls");
                if (Event.current.type == EventType.Repaint) paints++;
                if (GUI.GetNameOfFocusedControl() == "InteractiveTerminal")
                {
                    var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                    Rect cursor = (Rect)CallView("GetCursorCellRect");
                    Near(editor.position.x, cursor.x, 0.01f, "IME and VT cursor x");
                    Near(editor.position.y, cursor.y, 0.01f, "IME and VT cursor y");
                }
            }
            catch (Exception ex) { Finish("FAIL: GUI " + ex); }
            finally
            {
                if (DrawCallback != null) DrawCallback.SetValue(null, previousDraw);
                GUI.skin.settings.cursorColor = color;
                GUI.skin.settings.cursorFlashSpeed = flash;
            }
        }
        private bool ObserveDraw(GUIStyle style, Rect rect, GUIContent content, object states)
        {
            if (style == view.GetType().GetField("textStyle", Hidden).GetValue(view) && Mathf.Abs(rect.y - 2) < 0.01f)
            {
                Require(GUI.matrix == expectedMatrix, "A terminal glyph was stretched during drawing");
                string visible = content.text.TrimEnd();
                if (visible.Length > 0)
                {
                    float advance = style.CalcSize(new GUIContent(visible)).x;
                    renderedTextEnd = Mathf.Max(renderedTextEnd, GUI.matrix.MultiplyPoint3x4(new Vector3(rect.x + advance, rect.y, 0)).x);
                    if (visible.Contains("\uD55C")) sawWideGlyph = true;
                    if (style.fontStyle == FontStyle.Bold)
                    {
                        Require(visible.Length == 1, "Bold text was batched with Unity's extra character spacing");
                        boldGlyphs++;
                    }
                }
            }
            if (style == view.GetType().GetField("compositionStyle", Hidden).GetValue(view) && content.text.Length == 0)
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var cursorColor = states.GetType().GetField("cursorColor", flags);
                if (cursorColor != null) sawHiddenInputCursor |= ((Color)cursorColor.GetValue(states)).a == 0;
            }
            return previousDraw != null && (bool)previousDraw.DynamicInvoke(style, rect, content, states);
        }
        private void Tick()
        {
            if (finished || paints == 0 || EditorApplication.timeSinceStartup < next) return;
            try
            {
                switch (stage++)
                {
                    case 0:
                        VerifyMixedLine();
                        Feed("\x1b[8D");
                        break;
                    case 1:
                        VerifyCursorGrid();
                        report.AppendLine("PASS: cursor and mouse hit testing share the grid after cursor movement");
                        Feed("\x1b[2J\x1b[H\x1b[1miiiiMMMM\x1b[0mls");
                        break;
                    case 2:
                        Require(boldGlyphs == 8, "Bold glyphs were not positioned independently on the grid");
                        Near(renderedTextEnd, ((Rect)CallView("GetCursorCellRect")).x, 0.1f, "Styled prompt plus ls cursor");
                        report.AppendLine("PASS: bold prompt spacing cannot shift ls or its cursor; glyphs are not stretched");
                        Feed("\r\uD55C\r\x1b[2 q");
                        break;
                    case 3:
                        float width = Width;
                        Rect block = (Rect)CallView("GetCursorPaintRect");
                        Near(block.width, 2 * width, 0.01f, "Wide character block cursor");
                        Feed("\x1b[6 q");
                        break;
                    case 4:
                        Rect bar = (Rect)CallView("GetCursorPaintRect");
                        Near(bar.width, Mathf.Max(1, 1 / EditorGUIUtility.pixelsPerPoint), 0.01f, "Bar cursor width");
                        Feed("\x1b[4 q");
                        break;
                    case 5:
                        Rect underline = (Rect)CallView("GetCursorPaintRect");
                        Near(underline.width, 2 * Width, 0.01f, "Wide underline cursor");
                        Near(underline.height, 2, 0.01f, "Underline height");
                        report.AppendLine("PASS: wide block, steady bar and underline cursor geometry");
                        fontSize = 24;
                        DestroyImmediate(font); font = Font.CreateDynamicFontFromOSFont("Consolas", fontSize);
                        position = new Rect(position.x, position.y, 1650, 600);
                        screen.GetType().GetMethod("Reset").Invoke(screen, null);
                        // Keep the row within both the old and new grid during the resize.
                        Feed(new string('M', 40) + new string('\uD55C', 15) + "XYZ");
                        break;
                    default:
                        VerifyMixedLine();
                        report.AppendLine("PASS: font-size changes and window resizing preserve text/cursor alignment");
                        Finish("PASS: mixed-language grid, cursor, IME anchor and glyph transform restoration");
                        return;
                }
                paints = 0; next = EditorApplication.timeSinceStartup + 0.2; Repaint();
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void VerifyMixedLine()
        {
            VerifyCursorGrid();
            int columns = (int)screen.GetType().GetProperty("CursorX").GetValue(screen, null);
            float cursorEnd = ((Rect)CallView("GetCursorCellRect")).x;
            Require(sawWideGlyph, "No Hangul draw command was observed");
            Require(sawHiddenInputCursor, "The IME receiver drew an additional caret");
            Near(renderedTextEnd, cursorEnd, 0.1f, "Actual transformed text end and terminal cursor");
            report.AppendLine("PASS: size=" + fontSize + ", cells=" + columns + ", drawn end=" + renderedTextEnd + ", cursor=" + cursorEnd + ", extra caret hidden");
        }
        private void VerifyCursorGrid()
        {
            int x = (int)screen.GetType().GetProperty("CursorX").GetValue(screen, null);
            int y = (int)screen.GetType().GetProperty("CursorY").GetValue(screen, null);
            Rect cursor = (Rect)CallView("GetCursorCellRect");
            Near(cursor.x, 4 + x * Width, 0.01f, "Cursor column");
            Near(cursor.y, 2 + y * Height, 0.01f, "Cursor row");
            int hit = (int)CallView("CellAt", new Vector2(cursor.x + Width / 2, cursor.y + Height / 2));
            int columns = (int)screen.GetType().GetProperty("Columns").GetValue(screen, null);
            Require(hit == y * columns + x, "Mouse hit testing differs from the cursor grid");
        }
        private float Width { get { return (float)view.GetType().GetField("cellWidth", Hidden).GetValue(view); } }
        private float Height { get { return (float)view.GetType().GetField("cellHeight", Hidden).GetValue(view); } }
        private void Feed(string text) { screen.GetType().GetMethod("Feed").Invoke(screen, new object[] { text }); }
        private object CallView(string method, params object[] args)
        {
            return view.GetType().GetMethod(method, Hidden | BindingFlags.Public).Invoke(view, args);
        }
        private static void Near(float a, float b, float tolerance, string message) { Require(Mathf.Abs(a - b) <= tolerance, message + ": " + a + " vs " + b); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private void Finish(string result)
        {
            if (finished) return;
            finished = true;
            report.AppendLine(result);
            File.WriteAllText("Temp/TerminalLayoutProbe.result.txt", report.ToString());
            Close(); if (previous != null) previous.Focus();
        }
    }
}
