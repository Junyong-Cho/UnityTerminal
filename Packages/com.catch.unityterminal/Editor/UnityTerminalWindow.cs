using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

#pragma warning disable 618 // Font.CreateDynamicFontFromOSFont obsolete 경고 억제

namespace UnityTerminal
{
    public class UnityTerminalWindow : EditorWindow
    {
        [MenuItem("Window/Terminal")]
        public static void ShowWindow()
        {
            UnityTerminalWindow window = GetWindow<UnityTerminalWindow>("Terminal");
            GUIContent iconContent = EditorGUIUtility.IconContent("UnityEditor.ConsoleWindow");
            window.titleContent = new GUIContent("Terminal", iconContent != null ? iconContent.image : null);
            window.minSize = new Vector2(300, 200);
            window.Show();
        }

        private ShellSession session;
        private int inputCursorIndex;
        private int inputSelectIndex;
        private int pendingSelection = -1;

        private readonly TerminalBuffer buffer = new TerminalBuffer();
        private AnsiParser outputParser;
        private AnsiParser errorParser;
        private readonly PathCompletion completion = new PathCompletion();
        private readonly List<string> commandHistory = new List<string>();
        private string currentInput = "";
        private string currentWorkingDirectory = "";
        private string shellType = "PowerShell";
        private enum ShellTypeEnum { PowerShell, CMD, WSL }
        private int historyIndex = -1;
        private int fontSize = 12;
        private Vector2 scrollPosition;
        private bool autoScroll = true;
        private bool scrollToBottomPending;
        private bool focusInputPending = true;
        private int pendingCaret = -1;
        private GUIStyle terminalStyle;
        private GUIStyle inputStyle;
        private int measuredVersion = -1;
        private float measuredWidth;
        private int measuredFontSize;
        private float measuredHeight;

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += KillShellProcess;
            EditorApplication.quitting += KillShellProcess;
            shellType = EditorPrefs.GetString("UnityTerminal_ShellType", "PowerShell");
            if (!Enum.IsDefined(typeof(ShellTypeEnum), shellType)) shellType = "PowerShell";
            fontSize = Mathf.Clamp(EditorPrefs.GetInt("UnityTerminal_FontSize", 12), 10, 24);
            autoScroll = EditorPrefs.GetBool("UnityTerminal_AutoScroll", true);
            outputParser = new AnsiParser(buffer);
            errorParser = new AnsiParser(buffer, true);
            StartShell();
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= KillShellProcess;
            EditorApplication.quitting -= KillShellProcess;
            KillShellProcess();
            if (terminalStyle != null && terminalStyle.font != null) DestroyImmediate(terminalStyle.font);
            terminalStyle = null;
            inputStyle = null;
        }

        private void StartShell()
        {
            KillShellProcess();
            ClearTerminalBuffer();
            outputParser.Reset();
            errorParser.Reset();
            completion.Reset();
            currentInput = "";
            historyIndex = -1;
            pendingCaret = 0;
            pendingSelection = -1;
            focusInputPending = true;
            currentWorkingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            AppendSystemMessage($"[System] Starting {shellType} session in:\n{currentWorkingDirectory}\n");
            try
            {
                session = ShellSession.Start(shellType, currentWorkingDirectory);
            }
            catch (Exception ex)
            {
                AppendSystemMessage($"[System Error] Failed to start shell process: {ex.Message}\n");
            }
            Repaint();
        }

        private void KillShellProcess()
        {
            ShellSession previous = session;
            session = null;
            if (previous == null) return;
            previous.Dispose();
            if (previous.CleanupError != null)
                UnityEngine.Debug.LogWarning("[Terminal] Process cleanup: " + previous.CleanupError);
        }

        private void Update()
        {
            if (session == null) return;
            int version = buffer.Version;
            var timer = Stopwatch.StartNew();
            int characters = 0;
            ShellSession.OutputChunk chunk;
            // Yield to the editor after at most 64 KiB or 6 ms of parsing per update.
            while (characters < 65536 && timer.ElapsedMilliseconds < 6 && session.TryDequeue(out chunk))
            {
                (chunk.IsError ? errorParser : outputParser).Feed(chunk.Text);
                characters += chunk.Text.Length;
            }

            if (version != buffer.Version)
            {
                string directory;
                if (PathCompletion.TryReadWorkingDirectory(buffer.GetLastLineText(), out directory))
                    currentWorkingDirectory = directory;
            }

            int exitCode;
            if (session.TryGetExitCode(out exitCode))
            {
                KillShellProcess();
                AppendSystemMessage($"\n[System] Shell session exited with code {exitCode}.\n");
            }
            if (version != buffer.Version)
            {
                if (autoScroll) scrollToBottomPending = true;
                Repaint();
            }
        }

        private void ClearTerminalBuffer()
        {
            buffer.Clear();
            scrollPosition = Vector2.zero;
            Repaint();
        }

        private void AppendSystemMessage(string message)
        {
            buffer.Append(message, new TerminalStyle { Color = "#808080", Italic = true });
        }

        private void InitStyles()
        {
            Color defaultTextColor = new Color(0.83f, 0.83f, 0.83f);

            if (terminalStyle == null)
            {
                terminalStyle = new GUIStyle(EditorStyles.label);
                terminalStyle.richText = true;
                terminalStyle.wordWrap = true;

                // Apply Monospaced dynamic OS fonts
                string[] monospacedFonts = { "Consolas", "Courier New", "Courier", "monospace" };
                foreach (string fontName in monospacedFonts)
                {
                    Font font = Font.CreateDynamicFontFromOSFont(fontName, fontSize);
                    if (font != null)
                    {
                        terminalStyle.font = font;
                        break;
                    }
                }
            }

            // Force text color on all states to prevent black text in Light Theme or when focused
            terminalStyle.normal.textColor = defaultTextColor;
            terminalStyle.hover.textColor = defaultTextColor;
            terminalStyle.active.textColor = defaultTextColor;
            terminalStyle.focused.textColor = defaultTextColor;
            terminalStyle.onNormal.textColor = defaultTextColor;
            terminalStyle.onHover.textColor = defaultTextColor;
            terminalStyle.onActive.textColor = defaultTextColor;
            terminalStyle.onFocused.textColor = defaultTextColor;

            if (terminalStyle.fontSize != fontSize)
            {
                terminalStyle.fontSize = fontSize;
                if (terminalStyle.font != null)
                {
                    // Re-create font with size to prevent text blurriness in IMGUI
                    Font oldFont = terminalStyle.font;
                    terminalStyle.font = Font.CreateDynamicFontFromOSFont(oldFont.name, fontSize);
                    DestroyImmediate(oldFont);
                }
            }

            if (inputStyle == null)
            {
                // Base on EditorStyles.textField but clear background to make it transparent and borderless
                inputStyle = new GUIStyle(EditorStyles.textField);
                inputStyle.normal.background = null;
                inputStyle.hover.background = null;
                inputStyle.active.background = null;
                inputStyle.focused.background = null;
                inputStyle.onNormal.background = null;
                inputStyle.onHover.background = null;
                inputStyle.onActive.background = null;
                inputStyle.onFocused.background = null;

                // Clear borders and margins to align nicely
                inputStyle.border = new RectOffset(0, 0, 0, 0);
                inputStyle.margin = new RectOffset(0, 0, 0, 0);
                inputStyle.padding = new RectOffset(0, 0, 0, 0);
            }

            Color inputTextColor = new Color(0.9f, 0.9f, 0.9f);
            inputStyle.normal.textColor = inputTextColor;
            inputStyle.hover.textColor = inputTextColor;
            inputStyle.active.textColor = inputTextColor;
            inputStyle.focused.textColor = inputTextColor;
            inputStyle.onNormal.textColor = inputTextColor;
            inputStyle.onHover.textColor = inputTextColor;
            inputStyle.onActive.textColor = inputTextColor;
            inputStyle.onFocused.textColor = inputTextColor;

            if (inputStyle.fontSize != fontSize || inputStyle.font != terminalStyle.font)
            {
                inputStyle.font = terminalStyle.font;
                inputStyle.fontSize = fontSize;
            }
        }

        private void OnGUI()
        {
            InitStyles();

            if (Event.current.rawType == EventType.MouseDown) completion.Reset();
            // 2. Draw Toolbar Control Area
            DrawToolbar();

            // Draw a single dark background for the entire terminal body (log + input)
            Rect bodyRect = EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(bodyRect, new Color(0.118f, 0.118f, 0.118f));

            DrawOutputLog();
            DrawInputLine();
            EditorGUILayout.EndVertical();

            // 5. Refocus input if requested (done at the end of the frame after controls are registered)
            if (focusInputPending)
            {
                // Match the IMGUI TextField below without activating an editor-only text editor.
                if (GUI.GetNameOfFocusedControl() != "TerminalInput")
                    GUI.FocusControl("TerminalInput");
                // Reset flag only during Layout event to ensure the focus manager processes it
                if (Event.current.type == EventType.Layout)
                {
                    focusInputPending = false;
                }
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            ShellTypeEnum currentShell = (ShellTypeEnum)Enum.Parse(typeof(ShellTypeEnum), shellType);
            ShellTypeEnum newShell = (ShellTypeEnum)EditorGUILayout.EnumPopup(currentShell, EditorStyles.toolbarDropDown, GUILayout.Width(100));
            if (newShell != currentShell)
            {
                shellType = newShell.ToString();
                EditorPrefs.SetString("UnityTerminal_ShellType", shellType);
                StartShell();
            }

            GUILayout.Space(10);

            // Font size
            GUILayout.Label("Font Size:", EditorStyles.miniLabel, GUILayout.Width(55));
            int newFontSize = EditorGUILayout.IntSlider(fontSize, 10, 24, GUILayout.Width(130));
            if (newFontSize != fontSize)
            {
                fontSize = newFontSize;
                EditorPrefs.SetInt("UnityTerminal_FontSize", fontSize);
            }

            GUILayout.Space(10);

            // Auto Scroll
            bool newAutoScroll = GUILayout.Toggle(autoScroll, "AutoScroll", EditorStyles.toolbarButton);
            if (newAutoScroll != autoScroll)
            {
                autoScroll = newAutoScroll;
                EditorPrefs.SetBool("UnityTerminal_AutoScroll", autoScroll);
            }

            GUILayout.FlexibleSpace();

            // Display current folder name (clean UX)
            string displayFolder = "Root";
            if (!string.IsNullOrEmpty(currentWorkingDirectory))
            {
                displayFolder = Path.GetFileName(currentWorkingDirectory);
                if (string.IsNullOrEmpty(displayFolder)) // Root drive case (e.g. C:\)
                {
                    displayFolder = currentWorkingDirectory;
                }
            }
            GUILayout.Label($"Dir: {displayFolder}", EditorStyles.miniLabel);

            GUILayout.Space(10);

            // Clear Log
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                ClearTerminalBuffer();
            }

            // Restart Shell
            if (GUILayout.Button("Restart", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                StartShell();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawOutputLog()
        {
            Rect viewRect = EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(viewRect, new Color(0.118f, 0.118f, 0.118f));
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            string text = buffer.GetRichText();
            float width = Mathf.Max(1, position.width - 25);
            if (measuredVersion != buffer.Version || measuredWidth != width || measuredFontSize != fontSize)
            {
                measuredHeight = terminalStyle.CalcHeight(new GUIContent(text), width);
                measuredVersion = buffer.Version;
                measuredWidth = width;
                measuredFontSize = fontSize;
            }
            float height = Mathf.Max(measuredHeight, viewRect.height - 10);
            EditorGUILayout.SelectableLabel(text, terminalStyle, GUILayout.Width(width), GUILayout.Height(height));
            if (scrollToBottomPending)
            {
                scrollPosition.y = height;
                scrollToBottomPending = false;
            }
            EditorGUILayout.EndScrollView();
            Event e = Event.current;
            if (e.type == EventType.MouseDown && viewRect.Contains(e.mousePosition) && GUIUtility.hotControl == 0)
            {
                focusInputPending = true;
                Repaint();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawInputLine()
        {
            EditorGUILayout.BeginHorizontal();

            string promptSymbol = (shellType == "PowerShell") ? "PS> " : "> ";
            GUILayout.Label(promptSymbol, terminalStyle, GUILayout.Width(30));

            // Let every control register its real geometry and ID before consuming custom keys.
            // A Used event makes GUILayout return dummy rectangles and may change control IDs.
            Rect inputRect = GUILayoutUtility.GetRect(new GUIContent(currentInput), inputStyle, GUILayout.ExpandWidth(true));
            Event inputEvent = Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp
                ? new Event(Event.current) : null;

            GUI.SetNextControlName("TerminalInput");
            // Use the same IMGUI TextEditor state as completion and caret updates.
            string newInput = GUI.TextField(inputRect, currentInput, inputStyle);
            
            // Filter out any literal tab characters inserted by Unity's TextField to keep currentInput clean
            string cleanInput = newInput.Replace("\t", "");
            if (cleanInput != currentInput) completion.Reset();
            currentInput = cleanInput;
            if (inputEvent != null) HandleKeyboardEvents(inputEvent);
            if (pendingCaret >= 0 && GUI.GetNameOfFocusedControl() == "TerminalInput")
            {
                TextEditor editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                editor.text = currentInput;
                editor.cursorIndex = Mathf.Clamp(pendingCaret, 0, currentInput.Length);
                editor.selectIndex = pendingSelection < 0 ? editor.cursorIndex : Mathf.Clamp(pendingSelection, 0, currentInput.Length);
                pendingCaret = -1;
                pendingSelection = -1;
            }
            if (GUI.GetNameOfFocusedControl() == "TerminalInput")
            {
                TextEditor editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                inputCursorIndex = editor.cursorIndex;
                inputSelectIndex = editor.selectIndex;
            }

            EditorGUILayout.EndHorizontal();
        }


        private void HandleKeyboardEvents(Event e)
        {
            // Only process keyboard input events (prevents Event.Use() error during Layout/Repaint)
            if (e.type != EventType.KeyDown && e.type != EventType.KeyUp)
                return;
            
            // Check if input field is focused
            if (GUI.GetNameOfFocusedControl() == "TerminalInput")
            {
                // Reset autocomplete if any key other than Tab, UpArrow, DownArrow is pressed
                if (e.type == EventType.KeyDown)
                {
                    if (e.keyCode != KeyCode.Tab && e.character != '\t' && 
                        e.keyCode != KeyCode.UpArrow && e.keyCode != KeyCode.DownArrow && 
                        e.keyCode != KeyCode.None)
                    {
                        completion.Reset();
                    }
                    else if (e.keyCode == KeyCode.None && e.character != '\0' && e.character != '\t')
                    {
                        completion.Reset();
                    }
                }

                // 1. Enter Key: Send Command
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (e.type == EventType.KeyDown)
                    {
                        if (!string.IsNullOrEmpty(currentInput) && SendCommand(currentInput))
                        {
                            currentInput = "";
                            pendingCaret = 0;
                            pendingSelection = -1;
                            completion.Reset();
                        }
                        focusInputPending = true;
                    }
                    Event.current.Use(); // Consume the live event after the input control is registered.
                    Repaint();
                }
                
                // 2. Up Arrow: History Previous
                else if (e.keyCode == KeyCode.UpArrow)
                {
                    if (e.type == EventType.KeyDown)
                    {
                        if (commandHistory.Count > 0)
                        {
                            if (historyIndex < commandHistory.Count - 1)
                            {
                                historyIndex++;
                                currentInput = commandHistory[commandHistory.Count - 1 - historyIndex];
                                pendingCaret = currentInput.Length;
                                pendingSelection = -1;
                                completion.Reset();
                            }
                        }
                    }
                    Event.current.Use();
                    Repaint();
                }
                
                // 3. Down Arrow: History Next
                else if (e.keyCode == KeyCode.DownArrow)
                {
                    if (e.type == EventType.KeyDown)
                    {
                        if (historyIndex > 0)
                        {
                            historyIndex--;
                            currentInput = commandHistory[commandHistory.Count - 1 - historyIndex];
                        }
                        else if (historyIndex == 0)
                        {
                            historyIndex = -1;
                            currentInput = "";
                        }
                        completion.Reset();
                        pendingCaret = currentInput.Length;
                        pendingSelection = -1;
                    }
                    Event.current.Use();
                    Repaint();
                }
                
                // 4. Tab Key: Smart File System Autocomplete
                else if (e.keyCode == KeyCode.Tab || e.character == '\t')
                {
                    if (e.type == EventType.KeyDown)
                    {
                        HandleTabComplete();
                        Repaint();
                    }
                    Event.current.Use(); // Stop Tab navigation after registering the input control.
                }
            }
        }

        private void HandleTabComplete()
        {
            TextEditor editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            string completed;
            int caret;
            if (completion.TryComplete(currentInput, editor.cursorIndex, currentWorkingDirectory, shellType, out completed, out caret))
            {
                currentInput = completed;
                editor.text = completed;
                editor.cursorIndex = editor.selectIndex = caret;
                pendingCaret = caret;
                pendingSelection = -1;
            }
        }

        private bool SendCommand(string command)
        {
            string error = "The shell session is not running. Use Restart to start a new session.";
            if (session == null || !session.TrySend(command, out error))
            {
                AppendSystemMessage("\n[System Error] " + error + "\n");
                Repaint();
                return false;
            }
            AddCommandHistory(command);
            string trimmed = command.Trim();
            if (string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase)) ClearTerminalBuffer();
            return true;
        }

        private void AddCommandHistory(string command)
        {
            if (commandHistory.Count == 0 || commandHistory[commandHistory.Count - 1] != command)
            {
                commandHistory.Add(command);
                if (commandHistory.Count > 200) commandHistory.RemoveAt(0);
            }
            historyIndex = -1;
        }

    }
}
