using System;
using System.IO;
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

        [NonSerialized] private HostedShellSession session;
        [NonSerialized] private HostedInteractiveSession interactiveSession;
        private TerminalScreen interactiveScreen;
        private InteractiveTerminalView interactiveView;
        private int interactiveBlink;
        [NonSerialized] private bool reloadInProgress;
        [NonSerialized] private InteractiveTerminalView.ViewState pendingInteractiveView;
        private PrimaryViewState primaryViewState;
        private bool preservePrimaryViewport;
        private Vector2 terminalBodySize;
        private string interactiveStatus;
        private int inputCursorIndex;
        private int inputSelectIndex;
        private int pendingSelection = -1;
        private bool inputMethodActive;
        private IMECompositionMode previousInputMethod;
        private readonly TerminalComposition inputComposition = new TerminalComposition();

        [Serializable] private sealed class PrimaryViewState
        {
            public string Input;
            public int Cursor, Selection, HistoryIndex;
            public Vector2 Scroll;
            public bool ScrollPending;
        }
        private readonly TerminalBuffer buffer = new TerminalBuffer();
        private readonly PathCompletion completion = new PathCompletion();
        private readonly List<string> commandHistory = new List<string>();
        private string currentInput = "";
        private string currentWorkingDirectory = "";
        private string shellType = "PowerShell";
        private ShellOption[] shellOptions;
        private string[] shellLabels;
        private int shellCatalogRevision = -1;
        private bool waitingForShellDiscovery;
        private int historyIndex = -1;
        private int fontSize = 12;
        private Vector2 scrollPosition;
        private bool autoScroll = true;
        private bool scrollToBottomPending;
        private bool focusInputPending = true;
        private int pendingCaret = -1;
        [NonSerialized] private Font terminalFont;
        [NonSerialized] private GUIStyle terminalStyle;
        [NonSerialized] private GUIStyle inputStyle;
        private int measuredVersion = -1;
        private float measuredWidth;
        private int measuredFontSize;
        private float measuredHeight;

        [Serializable] private sealed class WindowResume
        {
            public HostResumeInfo Connection;
            public string Shell, Directory, Input, InteractiveId, Status;
            public string[] History;
            public int Cursor, Selection, HistoryIndex, Columns, Rows;
            public Vector2 Scroll;
            public bool ScrollPending, PreserveViewport;
            public PrimaryViewState Primary;
            public InteractiveTerminalView.ViewState AppView;
        }
        private string ResumeKey { get { return "UnityTerminal.Host." + GetInstanceID(); } }

        private void OnEnable()
        {
            measuredVersion = -1;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += KillShellProcess;
            shellType = EditorPrefs.GetString("UnityTerminal_ShellType", "");
            if (!ShellCatalog.IsKnownShell(shellType)) shellType = "";
            fontSize = Mathf.Clamp(EditorPrefs.GetInt("UnityTerminal_FontSize", 12), 10, 24);
            autoScroll = EditorPrefs.GetBool("UnityTerminal_AutoScroll", true);
            ShellCatalog.Refresh();
            RefreshShellOptions();
            if (RestoreHostedSession()) return;
            waitingForShellDiscovery = shellType == "" || shellType == "PowerShell7";
            if (waitingForShellDiscovery) ClearTerminalBuffer();
            else StartShell();
        }

        private void OnDisable()
        {
            RestoreInputMethod();
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.quitting -= KillShellProcess;
            if (!reloadInProgress) KillShellProcess();
            if (terminalFont != null) DestroyImmediate(terminalFont);
            terminalFont = null;
            terminalStyle = null;
            inputStyle = null;
        }

        private void OnLostFocus() { RestoreInputMethod(); }

        private void BeforeReload()
        {
            reloadInProgress = true;
            RestoreInputMethod();
            if (session == null) return;
            var saved = new WindowResume { Shell = shellType, Directory = currentWorkingDirectory, Input = currentInput,
                History = commandHistory.ToArray(), Cursor = inputCursorIndex, Selection = inputSelectIndex, HistoryIndex = historyIndex,
                Scroll = scrollPosition, ScrollPending = scrollToBottomPending, PreserveViewport = preservePrimaryViewport,
                Primary = primaryViewState, Status = interactiveStatus,
                InteractiveId = interactiveSession == null ? "" : interactiveSession.Id,
                Columns = interactiveScreen == null ? 80 : interactiveScreen.Columns, Rows = interactiveScreen == null ? 24 : interactiveScreen.Rows,
                AppView = interactiveView == null ? null : interactiveView.CaptureState() };
            saved.Connection = session.Detach();
            SessionState.SetString(ResumeKey, JsonUtility.ToJson(saved));
            session = null; interactiveSession = null; interactiveScreen = null; interactiveView = null;
        }

        private bool RestoreHostedSession()
        {
            string json = SessionState.GetString(ResumeKey, "");
            if (json == "") return false;
            try
            {
                WindowResume saved = JsonUtility.FromJson<WindowResume>(json);
                if (saved == null || saved.Connection == null || string.IsNullOrEmpty(saved.Connection.PipeName)) { SessionState.EraseString(ResumeKey); return false; }
                session = HostedShellSession.Attach(saved.Connection);
                shellType = saved.Shell; currentWorkingDirectory = saved.Directory; currentInput = saved.Input ?? "";
                commandHistory.Clear();
                if (saved.History != null)
                    for (int i = Math.Max(0, saved.History.Length - 200); i < saved.History.Length; i++) commandHistory.Add(saved.History[i]);
                inputCursorIndex = saved.Cursor; inputSelectIndex = saved.Selection;
                pendingCaret = saved.Cursor; pendingSelection = saved.Selection; historyIndex = saved.HistoryIndex;
                scrollPosition = saved.Scroll; scrollToBottomPending = saved.ScrollPending; preservePrimaryViewport = saved.PreserveViewport;
                primaryViewState = saved.Primary; interactiveStatus = saved.Status;
                waitingForShellDiscovery = false; focusInputPending = true;
                if (!string.IsNullOrEmpty(saved.InteractiveId))
                {
                    interactiveSession = session.RestoreInteractive(saved.InteractiveId);
                    CreateInteractiveView(saved.Columns, saved.Rows);
                    pendingInteractiveView = saved.AppView;
                }
                return true;
            }
            catch (Exception ex)
            {
                SessionState.EraseString(ResumeKey);
                if (session != null) session.Dispose(); session = null;
                UnityEngine.Debug.LogWarning("[Terminal] Unable to reconnect: " + ex.Message);
                return false;
            }
        }

        private void UpdateInputMethod()
        {
            string focusedControl = GUI.GetNameOfFocusedControl();
            bool textFocused = focusedWindow == this && !waitingForShellDiscovery &&
                (focusedControl == "TerminalInput" || focusedControl == "InteractiveTerminal");
            if (!textFocused) { RestoreInputMethod(); return; }
            if (!inputMethodActive)
            {
                previousInputMethod = Input.imeCompositionMode;
                inputMethodActive = true;
            }
            // GUI.TextField does not enable editor IME the way EditorGUI.TextField does.
            EditorGUIUtility.editingTextField = true;
            Input.imeCompositionMode = IMECompositionMode.On;
        }

        private void RestoreInputMethod()
        {
            if (!inputMethodActive) return;
            inputMethodActive = false;
            Input.imeCompositionMode = previousInputMethod;
            inputComposition.Reset();
            if (interactiveView != null) interactiveView.ResetComposition();
        }

        private void StartShell()
        {
            KillShellProcess();
            ClearTerminalBuffer();
            completion.Reset();
            currentInput = "";
            historyIndex = -1;
            pendingCaret = 0;
            pendingSelection = -1;
            preservePrimaryViewport = false;
            interactiveStatus = null;
            focusInputPending = true;
            currentWorkingDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            AppendSystemMessage($"[System] Starting {shellType} session in:\n{currentWorkingDirectory}\n");
            try
            {
                session = HostedShellSession.Start(shellType, currentWorkingDirectory);
            }
            catch (Exception ex)
            {
                AppendSystemMessage($"[System Error] Failed to start shell process: {ex.Message}\n");
            }
            Repaint();
        }

        private void KillShellProcess()
        {
            pendingInteractiveView = null;
            SessionState.EraseString(ResumeKey);
            if (interactiveSession != null)
            {
                interactiveSession.Dispose();
                interactiveSession = null;
                interactiveScreen = null;
                interactiveView = null;
                EditorGUIUtility.editingTextField = false;
            }
            primaryViewState = null;
            HostedShellSession previous = session;
            session = null;
            if (previous == null) return;
            previous.Dispose();
            if (previous.CleanupError != null)
                UnityEngine.Debug.LogWarning("[Terminal] Process cleanup: " + previous.CleanupError);
        }

        private void Update()
        {
            if (shellOptions == null || shellCatalogRevision != ShellCatalog.Revision)
            {
                RefreshShellOptions();
                Repaint();
            }
            if (waitingForShellDiscovery)
            {
                if (!ShellCatalog.DiscoveryComplete) return;
                RefreshShellOptions();
                waitingForShellDiscovery = false;
                // Save only deliberate menu selections. An automatic fallback must not
                // prevent a future launch from preferring an installed PowerShell 7.
                shellType = ShellCatalog.ChooseInitialShell(shellType, shellOptions);
                StartShell();
            }
            if (session != null && session.Failure != null)
            {
                string error = session.Failure;
                KillShellProcess();
                AppendSystemMessage("\n[System Error] " + error + "\n");
                Repaint(); return;
            }
            int version = buffer.Version;
            if (session != null && session.ApplyBuffer(buffer))
            {
                string directory;
                if (PathCompletion.TryReadWorkingDirectory(buffer.GetLastLineText(), out directory)) currentWorkingDirectory = directory;
            }
            if (interactiveSession != null)
            {
                // Both processes and parsing continue in the host while this page is visible.
                UpdateInteractive();
                return;
            }
            if (session == null) return;
            int exitCode;
            if (session.TryGetExitCode(out exitCode))
            {
                KillShellProcess();
                AppendSystemMessage($"\n[System] Shell session exited with code {exitCode}.\n");
            }
            if (version != buffer.Version)
            {
                if (autoScroll && !preservePrimaryViewport) scrollToBottomPending = true;
                Repaint();
            }
        }

        private void ClearTerminalBuffer()
        {
            if (session != null) session.Clear();
            buffer.Clear();
            scrollPosition = Vector2.zero;
            Repaint();
        }

        private void AppendSystemMessage(string message)
        {
            if (session != null) session.AppendSystemMessage(message);
            buffer.Append(message, new TerminalStyle { Color = "#808080", Italic = true });
        }

        private bool StartInteractive(string command)
        {
            if (interactiveSession != null) return false;
            try
            {
                var saved = new PrimaryViewState
                {
                    Input = currentInput,
                    Cursor = Mathf.Clamp(inputCursorIndex, 0, currentInput.Length),
                    Selection = Mathf.Clamp(inputSelectIndex, 0, currentInput.Length),
                    HistoryIndex = historyIndex,
                    Scroll = scrollPosition,
                    ScrollPending = scrollToBottomPending
                };
                if (session == null) throw new InvalidOperationException("PowerShell is not running. Use Restart.");
                Vector2 area = terminalBodySize.x > 8 && terminalBodySize.y > 22 ? terminalBodySize :
                    new Vector2(position.width, Mathf.Max(1, position.height - EditorStyles.toolbar.fixedHeight));
                Vector2Int grid = InteractiveTerminalView.MeasureGridSize(area,
                    InteractiveTerminalView.MeasureCellSize(terminalStyle.font, fontSize));
                var started = session.StartInteractive(command, grid.x, grid.y);
                interactiveSession = started;
                primaryViewState = saved;
                interactiveStatus = null;
                CreateInteractiveView(grid.x, grid.y);
                completion.Reset();
                Repaint();
                return true;
            }
            catch (Exception ex)
            {
                if (interactiveSession != null) interactiveSession.Dispose();
                interactiveSession = null;
                interactiveScreen = null;
                interactiveView = null;
                primaryViewState = null;
                AppendSystemMessage("\n[System Error] Unable to start the interactive terminal: " + ex.Message + "\n");
                Repaint();
                return false;
            }
        }

        private void SendInteractive(string text)
        {
            string error;
            if (interactiveSession != null && !interactiveSession.TrySend(text, out error)) interactiveView.ShowError(error);
        }

        private void UpdateInteractive()
        {
            bool changed = interactiveSession.ApplyScreen(interactiveScreen);
            if (changed && pendingInteractiveView != null)
            {
                interactiveView.RestoreState(pendingInteractiveView); pendingInteractiveView = null;
            }
            if (!string.IsNullOrEmpty(interactiveSession.Notice)) interactiveView.ShowError(interactiveSession.Notice);
            int code;
            if (interactiveSession.TryGetExitCode(out code))
            {
                StopInteractive(code == 0 ? null : "[System Error] Interactive session exited with code " + code + ".");
                return;
            }
            if (interactiveSession.Failure != null)
            {
                StopInteractive("[System Error] " + interactiveSession.Failure);
                return;
            }
            int blink = (int)(EditorApplication.timeSinceStartup * 2);
            if (changed || blink != interactiveBlink) { interactiveBlink = blink; Repaint(); }
        }

        private void StopInteractive(string message)
        {
            if (interactiveSession == null) return;
            interactiveSession.Dispose();
            interactiveSession = null;
            interactiveView = null;
            interactiveScreen = null;
            pendingInteractiveView = null;
            EditorGUIUtility.editingTextField = false;
            // Closing the interactive page reveals the primary page; neither the app's
            // transcript nor a normal exit message belongs in the primary scrollback.
            interactiveStatus = message != null && message.StartsWith("[System Error]", StringComparison.Ordinal) ? message : null;
            PrimaryViewState saved = primaryViewState;
            primaryViewState = null;
            if (saved != null)
            {
                currentInput = saved.Input;
                historyIndex = saved.HistoryIndex;
                pendingCaret = saved.Cursor;
                pendingSelection = saved.Selection;
                scrollPosition = saved.Scroll;
                scrollToBottomPending = saved.ScrollPending;
            }
            focusInputPending = true;
            preservePrimaryViewport = true;
            Repaint();
        }

        private void CreateInteractiveView(int columns, int rows)
        {
            interactiveScreen = new TerminalScreen(columns, rows);
            interactiveView = new InteractiveTerminalView(interactiveScreen) { Send = SendInteractive,
                Resize = (width, height) =>
                {
                    try { if (interactiveSession != null) interactiveSession.Resize(width, height); }
                    catch (Exception ex) { if (interactiveView != null) interactiveView.ShowError("Resize: " + ex.Message); }
                } };
        }

        private void InitStyles()
        {
            Color defaultTextColor = new Color(0.83f, 0.83f, 0.83f);

            if (terminalStyle == null)
            {
                terminalStyle = new GUIStyle(EditorStyles.label);
                terminalStyle.richText = true;
                terminalStyle.wordWrap = true;

            }

            // GUIStyle survives editor serialization, but a dynamically created Font can
            // disappear on reload. Never let the terminal fall back to a proportional UI font.
            if (terminalFont == null || terminalFont.fontSize != fontSize)
            {
                if (terminalFont != null) DestroyImmediate(terminalFont);
                terminalFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "Courier", "monospace" }, fontSize);
                terminalFont.hideFlags = HideFlags.HideAndDontSave;
            }
            terminalStyle.font = terminalFont;
            terminalStyle.fontSize = fontSize;

            // Force text color on all states to prevent black text in Light Theme or when focused
            terminalStyle.normal.textColor = defaultTextColor;
            terminalStyle.hover.textColor = defaultTextColor;
            terminalStyle.active.textColor = defaultTextColor;
            terminalStyle.focused.textColor = defaultTextColor;
            terminalStyle.onNormal.textColor = defaultTextColor;
            terminalStyle.onHover.textColor = defaultTextColor;
            terminalStyle.onActive.textColor = defaultTextColor;
            terminalStyle.onFocused.textColor = defaultTextColor;

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
                inputStyle.fixedHeight = 0;
                inputStyle.wordWrap = false;
                inputStyle.alignment = TextAnchor.UpperLeft;
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
            if (interactiveSession == null && (Event.current.type == EventType.KeyDown ||
                Event.current.rawType == EventType.MouseDown || Event.current.type == EventType.ScrollWheel))
                preservePrimaryViewport = false;

            // 2. Draw Toolbar Control Area
            DrawToolbar();

            // Draw a single dark background for the entire terminal body (log + input)
            Rect bodyRect = EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint) terminalBodySize = bodyRect.size;
            EditorGUI.DrawRect(bodyRect, new Color(0.118f, 0.118f, 0.118f));

            bool drewInteractive = interactiveSession != null;
            if (drewInteractive)
            {
                Rect rect = GUILayoutUtility.GetRect(0, 10000, 0, 10000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                interactiveView.Draw(rect, terminalStyle.font, fontSize, focusedWindow == this);
            }
            else
            {
                EditorGUI.BeginDisabledGroup(waitingForShellDiscovery || (session != null && !session.IsReady));
                DrawOutputLog();
                DrawInputLine();
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.EndVertical();

            if (drewInteractive && interactiveView != null && interactiveView.HandleInput()) Repaint();

            // 5. Refocus input if requested (done at the end of the frame after controls are registered)
            if (focusInputPending && interactiveSession == null)
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
            UpdateInputMethod();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (shellOptions == null) RefreshShellOptions();
            int currentShell = Math.Max(0, Array.FindIndex(shellOptions, option => option.Id == shellType));
            EditorGUI.BeginDisabledGroup(waitingForShellDiscovery);
            int newShell = EditorGUILayout.Popup(currentShell, shellLabels, EditorStyles.toolbarDropDown, GUILayout.Width(165));
            EditorGUI.EndDisabledGroup();
            if (newShell != currentShell)
            {
                shellType = shellOptions[newShell].Id;
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

            if (interactiveStatus != null)
                GUILayout.Label(new GUIContent("Interactive error", interactiveStatus), EditorStyles.miniLabel);
            if (session != null && !session.IsReady)
                GUILayout.Label("Connecting...", EditorStyles.miniLabel);

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
                if (interactiveSession != null) SendInteractive("\x0c");
                else ClearTerminalBuffer();
            }

            if (interactiveSession != null && GUILayout.Button("Stop", EditorStyles.toolbarButton, GUILayout.Width(45)))
                StopInteractive(null);

            // Restart Shell
            EditorGUI.BeginDisabledGroup(waitingForShellDiscovery);
            if (GUILayout.Button("Restart", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                StartShell();
            }
            EditorGUI.EndDisabledGroup();

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

            string promptSymbol = ShellCatalog.IsPowerShell(shellType) ? "PS> " : "> ";
            GUILayout.Label(promptSymbol, terminalStyle, GUILayout.Width(30));

            // Let every control register its real geometry and ID before consuming custom keys.
            // A Used event makes GUILayout return dummy rectangles and may change control IDs.
            float lineHeight = Mathf.Max(fontSize + 2, inputStyle.lineHeight);
            int lines = 1;
            foreach (char c in currentInput) if (c == '\n') lines++;
            float maxHeight = Mathf.Max(lineHeight, Mathf.Min(lineHeight * 8, position.height * 0.4f));
            Rect inputRect = GUILayoutUtility.GetRect(0, 10000, Mathf.Min(lines * lineHeight, maxHeight),
                Mathf.Min(lines * lineHeight, maxHeight), GUILayout.ExpandWidth(true));
            Event inputEvent = Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp
                ? new Event(Event.current) : null;
            string previousInput = currentInput;
            int previousCursor = inputCursorIndex, previousSelection = inputSelectIndex;
            if (GUI.GetNameOfFocusedControl() == "TerminalInput" && GUIUtility.keyboardControl != 0)
            {
                var previousEditor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                previousCursor = previousEditor.cursorIndex; previousSelection = previousEditor.selectIndex;
            }
            inputComposition.BeginEvent();

            GUI.SetNextControlName("TerminalInput");
            // Use the same IMGUI TextEditor state as completion and caret updates.
            string newInput = GUI.TextArea(inputRect, currentInput, inputStyle);
            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(newInput) || session == null || !session.IsReady);
            bool runInteractive = GUILayout.Button(new GUIContent("Interactive", "Run this command in an interactive terminal (Ctrl+Enter). Exit or Stop returns to PowerShell."),
                EditorStyles.miniButton, GUILayout.Width(85));
            EditorGUI.EndDisabledGroup();
            
            // Filter out any literal tab characters inserted by Unity's TextField to keep currentInput clean
            string cleanInput = newInput.Replace("\t", "");
            if (cleanInput != currentInput) completion.Reset();
            currentInput = cleanInput;
            // Enter may finish composition inside TextField. It must not also submit the command.
            bool composing = inputComposition.ConsumesNavigation(inputEvent ?? Event.current);
            if (inputEvent != null)
            {
                bool returnText = inputEvent.type == EventType.KeyDown && inputEvent.keyCode == KeyCode.None &&
                    (inputEvent.character == '\r' || inputEvent.character == '\n');
                if (returnText)
                {
                    // IME can emit a separate return character as well as the physical key.
                    // Physical Enter below owns submission/newline; this is only its text tail.
                    RemoveControlLineBreak(previousInput);
                    Event.current.Use();
                }
                else if (composing)
                {
                    if (inputEvent.type == EventType.KeyDown && IsEnter(inputEvent)) RemoveControlLineBreak(previousInput);
                    Event.current.Use();
                }
                else
                {
                    if (inputEvent.type == EventType.KeyDown &&
                        (IsEnter(inputEvent) || inputEvent.keyCode == KeyCode.Tab || inputEvent.character == '\t') &&
                        GUI.GetNameOfFocusedControl() == "TerminalInput")
                    {
                        // TextArea edits Enter/Tab itself. Apply our key policy once,
                        // using the selection from before the native control handled the key.
                        currentInput = previousInput;
                        var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                        editor.text = currentInput;
                        editor.cursorIndex = Mathf.Clamp(previousCursor, 0, currentInput.Length);
                        editor.selectIndex = Mathf.Clamp(previousSelection, 0, currentInput.Length);
                    }
                    HandleKeyboardEvents(inputEvent);
                }
            }
            if (runInteractive && !composing) SubmitInput(true);
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
                // Within a multiline draft, the native editor owns vertical caret movement.
                if (currentInput.IndexOf('\n') >= 0 && (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow)) return;
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
                if (IsEnter(e))
                {
                    if (e.type == EventType.KeyDown)
                    {
                        if (e.shift && !e.control && !e.command && !e.alt) InsertInputLineBreak();
                        else SubmitInput(e.control || e.command);
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

        private static bool IsEnter(Event e) { return e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter; }

        private void RemoveControlLineBreak(string previousInput)
        {
            // TextArea can commit a syllable AND insert Enter. Keep the committed text,
            // removing only a newline in the newly inserted part, not an existing line.
            int prefix = 0, suffix = 0;
            while (prefix < previousInput.Length && prefix < currentInput.Length && previousInput[prefix] == currentInput[prefix]) prefix++;
            while (suffix < previousInput.Length - prefix && suffix < currentInput.Length - prefix &&
                previousInput[previousInput.Length - suffix - 1] == currentInput[currentInput.Length - suffix - 1]) suffix++;
            int end = currentInput.Length - suffix;
            if (end <= prefix) return;
            int newline = Mathf.Max(currentInput.LastIndexOf('\n', end - 1, end - prefix), currentInput.LastIndexOf('\r', end - 1, end - prefix));
            if (newline < 0) return;
            int count = 1;
            if (currentInput[newline] == '\n' && newline > prefix && currentInput[newline - 1] == '\r') { newline--; count = 2; }
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            int cursor = editor.cursorIndex, selection = editor.selectIndex;
            currentInput = currentInput.Remove(newline, count);
            editor.text = currentInput;
            pendingCaret = Mathf.Clamp(cursor > newline ? Mathf.Max(newline, cursor - count) : cursor, 0, currentInput.Length);
            pendingSelection = Mathf.Clamp(selection > newline ? Mathf.Max(newline, selection - count) : selection, 0, currentInput.Length);
        }

        private void InsertInputLineBreak()
        {
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            int start = Mathf.Clamp(Mathf.Min(editor.cursorIndex, editor.selectIndex), 0, currentInput.Length);
            int end = Mathf.Clamp(Mathf.Max(editor.cursorIndex, editor.selectIndex), 0, currentInput.Length);
            currentInput = currentInput.Substring(0, start) + "\n" + currentInput.Substring(end);
            pendingCaret = start + 1;
            pendingSelection = -1;
            historyIndex = -1;
            completion.Reset();
        }

        private void SubmitInput(bool forceInteractive)
        {
            if (ExecuteCommand(currentInput, forceInteractive))
            {
                currentInput = "";
                pendingCaret = 0;
                pendingSelection = -1;
                completion.Reset();
            }
            focusInputPending = true;
            Repaint();
        }

        private bool SendCommand(string command) { return ExecuteCommand(command, false); }

        private bool ExecuteCommand(string command, bool forceInteractive)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            if (forceInteractive || TerminalInput.IsCodexCommand(command) || TerminalInput.IsInteractiveWslCommand(command))
            {
                if (!StartInteractive(command)) return false;
                AddCommandHistory(command);
                if (primaryViewState.Input == command)
                {
                    // Enter consumed the launch command; return to its now-empty prompt.
                    primaryViewState.Input = "";
                    primaryViewState.Cursor = primaryViewState.Selection = 0;
                    primaryViewState.HistoryIndex = -1;
                }
                return true;
            }
            interactiveStatus = null;
            string trimmed = command.Trim();
            if (string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Clear-Host", StringComparison.OrdinalIgnoreCase))
            {
                // Redirected PowerShell has no console cursor for Clear-Host to move.
                // Clear our display and the host buffer without invoking its RawUI.
                AddCommandHistory(command);
                ClearTerminalBuffer();
                return true;
            }
            string error = "The shell session is not running. Use Restart to start a new session.";
            if (session == null || !session.TrySend(command, out error))
            {
                AppendSystemMessage("\n[System Error] " + error + "\n");
                Repaint();
                return false;
            }
            AddCommandHistory(command);
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

        private void RefreshShellOptions()
        {
            shellOptions = ShellCatalog.GetOptions(out shellCatalogRevision);
            shellLabels = Array.ConvertAll(shellOptions, option => option.Label);
        }
    }
}
