using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
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

        public struct TextSpan
        {
            public string text;
            public string color; // Hex color string, e.g. "#FF5555" or null for default
            public bool bold;
            public bool italic;
        }

        public struct LogChunk
        {
            public string text;
            public bool isError;
        }

        // Process and stream redirection
        private Process shellProcess;
        private StreamWriter shellInput;
        private Thread outputThread;
        private Thread errorThread;
        private readonly object logLock = new object();
        private readonly Queue<LogChunk> outputQueue = new Queue<LogChunk>();

        // Terminal text buffer
        private readonly List<List<TextSpan>> terminalLines = new List<List<TextSpan>>();
        private const int MaxLines = 2000;

        // UI State
        private string currentInput = "";
        private Vector2 scrollPosition;
        private bool autoScroll = true;
        private bool scrollToBottomPending = false;
        private int fontSize = 12;
        private string shellType = "PowerShell"; // PowerShell or CMD
        private bool focusInputPending = true; // Start with true to focus on open
        
        // Command History
        private readonly List<string> commandHistory = new List<string>();
        private int historyIndex = -1;

        // Auto-complete (Tab)
        private string currentWorkingDirectory = "";
        private readonly List<string> autocompleteMatches = new List<string>();
        private int autocompleteIndex = -1;
        private string originalAutocompleteWord = "";
        private string inputBeforeAutocompleteWord = "";
        private bool isAutocompleting = false;

        // ANSI escape code parsing state
        private string activeColor = null;
        private bool activeBold = false;
        private bool activeItalic = false;

        // Styles
        private GUIStyle terminalStyle;
        private GUIStyle inputStyle;

        private void OnEnable()
        {
            // Set up events to avoid leaking background processes
            AssemblyReloadEvents.beforeAssemblyReload += CleanupOnReload;
            EditorApplication.quitting += CleanupOnQuit;

            // Load saved preferences
            shellType = EditorPrefs.GetString("UnityTerminal_ShellType", "PowerShell");
            fontSize = EditorPrefs.GetInt("UnityTerminal_FontSize", 12);
            autoScroll = EditorPrefs.GetBool("UnityTerminal_AutoScroll", true);

            // Initialize buffer
            ClearTerminalBuffer();

            // Start shell
            StartShell();
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CleanupOnReload;
            EditorApplication.quitting -= CleanupOnQuit;

            KillShellProcess();
        }

        private void CleanupOnReload()
        {
            KillShellProcess();
        }

        private void CleanupOnQuit()
        {
            KillShellProcess();
        }

        private void StartShell()
        {
            KillShellProcess();

            lock (logLock)
            {
                terminalLines.Clear();
                terminalLines.Add(new List<TextSpan>());
                outputQueue.Clear();
            }

            activeColor = null;
            activeBold = false;
            activeItalic = false;

            string executable = "powershell.exe";
            string arguments = "-NoLogo -NoExit";

            if (shellType == "CMD")
            {
                executable = "cmd.exe";
                arguments = "/K";
            }
            else if (shellType == "WSL")
            {
                executable = "wsl.exe";
                arguments = "";
            }

            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            currentWorkingDirectory = projectPath;

            AppendSystemMessage($"[System] Starting {shellType} session in:\n{projectPath}\n");

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = projectPath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // Force ANSI colors for child processes (npm, git, dotnet, etc.)
                startInfo.EnvironmentVariables["FORCE_COLOR"] = "1";
                startInfo.EnvironmentVariables["CLICOLOR_FORCE"] = "1";
                startInfo.EnvironmentVariables["TERM"] = "xterm-256color";
                startInfo.EnvironmentVariables["GIT_CONFIG_PARAMETERS"] = "'color.status=always' 'color.diff=always' 'color.branch=always' 'color.ui=always'";

                shellProcess = new Process();
                shellProcess.StartInfo = startInfo;
                shellProcess.Start();

                shellInput = shellProcess.StandardInput;
                shellInput.AutoFlush = true;

                // Start async reader threads (with isError parameter)
                outputThread = new Thread(() => ReadStream(shellProcess.StandardOutput, false));
                outputThread.IsBackground = true;
                outputThread.Start();

                errorThread = new Thread(() => ReadStream(shellProcess.StandardError, true));
                errorThread.IsBackground = true;
                errorThread.Start();

                // Initialization commands inside the shell for UTF-8 encoding support
                if (shellType == "PowerShell")
                {
                    shellInput.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
                }
                else if (shellType == "CMD")
                {
                    shellInput.WriteLine("chcp 65001");
                }
            }
            catch (Exception ex)
            {
                AppendSystemMessage($"[System Error] Failed to start shell process: {ex.Message}");
            }
        }

        private void KillShellProcess()
        {
            if (shellProcess != null)
            {
                int pid = shellProcess.Id;
                try
                {
                    // Kill process tree on Windows to ensure child processes started inside the shell are also cleaned up
                    KillProcessTree(pid);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[Terminal] Error during process tree cleanup: {ex.Message}");
                }
                finally
                {
                    shellProcess = null;
                    shellInput = null;
                }
            }

            if (outputThread != null && outputThread.IsAlive)
            {
                outputThread.Interrupt();
                outputThread = null;
            }

            if (errorThread != null && errorThread.IsAlive)
            {
                errorThread.Interrupt();
                errorThread = null;
            }
        }

        private void KillProcessTree(int pid)
        {
            try
            {
                ProcessStartInfo killStartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (Process killProcess = Process.Start(killStartInfo))
                {
                    killProcess.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Terminal] Failed to execute taskkill for PID {pid}: {ex.Message}");
            }
        }

        private void ReadStream(StreamReader reader, bool isError)
        {
            char[] buffer = new char[1024];
            try
            {
                while (shellProcess != null && !shellProcess.HasExited)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = reader.Read(buffer, 0, buffer.Length);
                    }
                    catch
                    {
                        break; // Stream or thread terminated
                    }

                    if (bytesRead > 0)
                    {
                        string chunk = new string(buffer, 0, bytesRead);
                        lock (logLock)
                        {
                            outputQueue.Enqueue(new LogChunk { text = chunk, isError = isError });
                        }
                    }
                    else
                    {
                        Thread.Sleep(15);
                    }
                }
            }
            catch (ThreadInterruptedException)
            {
                // Clean exit when thread is interrupted on window close or reload
            }
        }

        private void Update()
        {
            bool hasNewOutput = false;
            lock (logLock)
            {
                while (outputQueue.Count > 0)
                {
                    LogChunk chunk = outputQueue.Dequeue();
                    ProcessRawText(chunk.text, chunk.isError);
                    hasNewOutput = true;
                }
            }

            if (hasNewOutput)
            {
                if (autoScroll)
                {
                    scrollToBottomPending = true;
                }
                Repaint();
            }

            // Detect unexpected process exit
            if (shellProcess != null && shellProcess.HasExited)
            {
                int exitCode = shellProcess.ExitCode;
                KillShellProcess();
                AppendSystemMessage($"\n[System] Shell session exited unexpectedly with code {exitCode}.");
                Repaint();
            }
        }

        private void ProcessRawText(string text, bool isError)
        {
            int i = 0;
            while (i < text.Length)
            {
                int escIdx = text.IndexOf("\x1b[", i);
                if (escIdx == -1)
                {
                    AppendPlainText(text.Substring(i), isError);
                    break;
                }

                if (escIdx > i)
                {
                    AppendPlainText(text.Substring(i, escIdx - i), isError);
                }

                // Parse ANSI sequence
                int seqStart = escIdx + 2;
                int seqEnd = seqStart;
                while (seqEnd < text.Length && !char.IsLetter(text[seqEnd]))
                {
                    seqEnd++;
                }

                if (seqEnd >= text.Length)
                {
                    // Incomplete escape sequence
                    break;
                }

                char commandChar = text[seqEnd];
                string sequence = text.Substring(seqStart, seqEnd - seqStart);

                if (commandChar == 'm')
                {
                    // Style attributes
                    string[] parts = sequence.Split(';');
                    foreach (string part in parts)
                    {
                        if (string.IsNullOrEmpty(part))
                        {
                            ApplyAnsiStyle(0); // Treat empty parameter as 0 (Reset)
                        }
                        else if (int.TryParse(part, out int code))
                        {
                            ApplyAnsiStyle(code);
                        }
                    }
                }
                else if (commandChar == 'J')
                {
                    // Clear screen sequences (e.g. \x1b[2J)
                    if (sequence == "2" || sequence == "3" || sequence == "")
                    {
                        ClearTerminalBuffer();
                    }
                }

                i = seqEnd + 1;
            }
        }

        private void ApplyAnsiStyle(int code)
        {
            switch (code)
            {
                case 0: // Reset
                    activeColor = null;
                    activeBold = false;
                    activeItalic = false;
                    break;
                case 1: // Bold
                    activeBold = true;
                    break;
                case 3: // Italic
                    activeItalic = true;
                    break;
                
                // Dark Theme-tuned ANSI colors (VS Code style)
                case 30: activeColor = "#333333"; break; // Black
                case 31: activeColor = "#f44747"; break; // Red
                case 32: activeColor = "#608b4e"; break; // Green
                case 33: activeColor = "#dcdcaa"; break; // Yellow
                case 34: activeColor = "#569cd6"; break; // Blue
                case 35: activeColor = "#c586c0"; break; // Magenta
                case 36: activeColor = "#4fc1ff"; break; // Cyan
                case 37: activeColor = "#d4d4d4"; break; // White
                
                // Bright variants
                case 90: activeColor = "#808080"; break; // Gray
                case 91: activeColor = "#f44747"; break; // Bright Red
                case 92: activeColor = "#b5cea8"; break; // Bright Green
                case 93: activeColor = "#dcdcaa"; break; // Bright Yellow
                case 94: activeColor = "#569cd6"; break; // Bright Blue
                case 95: activeColor = "#c586c0"; break; // Bright Magenta
                case 96: activeColor = "#9cdcfe"; break; // Bright Cyan
                case 97: activeColor = "#ffffff"; break; // Bright White
                
                case 39: activeColor = null; break; // Default FG
            }
        }

        private void AppendPlainText(string text, bool isError)
        {
            if (string.IsNullOrEmpty(text)) return;

            int len = text.Length;
            int start = 0;

            for (int i = 0; i < len; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r')
                {
                    // Flush text before control char
                    if (i > start)
                    {
                        AddSpanToLastLine(text.Substring(start, i - start), isError);
                    }

                    if (c == '\n')
                    {
                        lock (logLock)
                        {
                            terminalLines.Add(new List<TextSpan>());
                            if (terminalLines.Count > MaxLines)
                            {
                                terminalLines.RemoveAt(0);
                            }
                        }
                    }
                    else if (c == '\r')
                    {
                        // Check if part of \r\n
                        if (i + 1 < len && text[i + 1] == '\n')
                        {
                            // Skip carriage return in \r\n, let \n process next
                            start = i + 1;
                            continue;
                        }
                        else
                        {
                            // Independent \r: Carriage return overwrites the current line
                            lock (logLock)
                            {
                                if (terminalLines.Count > 0)
                                {
                                    terminalLines[terminalLines.Count - 1].Clear();
                                }
                            }
                        }
                    }

                    start = i + 1;
                }
            }

            // Flush remaining text
            if (start < len)
            {
                AddSpanToLastLine(text.Substring(start, len - start), isError);
            }

            UpdateWorkingDirectoryFromLastLine();
        }

        private void AddSpanToLastLine(string content, bool isError = false)
        {
            if (string.IsNullOrEmpty(content)) return;

            lock (logLock)
            {
                if (terminalLines.Count == 0)
                {
                    terminalLines.Add(new List<TextSpan>());
                }

                List<TextSpan> lastLine = terminalLines[terminalLines.Count - 1];
                
                string spanColor = activeColor;
                if (isError && spanColor == null)
                {
                    spanColor = "#f44747"; // Default red style for errors (VS Code red)
                }

                lastLine.Add(new TextSpan
                {
                    text = content,
                    color = spanColor,
                    bold = activeBold,
                    italic = activeItalic
                });
            }
        }

        private void AppendSystemMessage(string message)
        {
            // Split system messages by newlines
            string[] msgs = message.Split('\n');
            for (int i = 0; i < msgs.Length; i++)
            {
                if (i > 0)
                {
                    lock (logLock)
                    {
                        terminalLines.Add(new List<TextSpan>());
                        if (terminalLines.Count > MaxLines)
                        {
                            terminalLines.RemoveAt(0);
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(msgs[i]))
                {
                    lock (logLock)
                    {
                        List<TextSpan> lastLine = terminalLines[terminalLines.Count - 1];
                        lastLine.Add(new TextSpan
                        {
                            text = msgs[i],
                            color = "#808080", // Grey style for system output
                            bold = false,
                            italic = true
                        });
                    }
                }
            }
        }

        private void ClearTerminalBuffer()
        {
            lock (logLock)
            {
                terminalLines.Clear();
                terminalLines.Add(new List<TextSpan>());
            }
        }

        private void UpdateWorkingDirectoryFromLastLine()
        {
            if (terminalLines.Count == 0) return;

            // Compile text of the last line
            List<TextSpan> lastLineSpans;
            lock (logLock)
            {
                lastLineSpans = new List<TextSpan>(terminalLines[terminalLines.Count - 1]);
            }

            StringBuilder sb = new StringBuilder();
            foreach (var span in lastLineSpans)
            {
                sb.Append(span.text);
            }
            string lastLineText = sb.ToString().Trim();

            // Match PowerShell prompt: PS C:\Path>
            Match psMatch = Regex.Match(lastLineText, @"^PS\s+([a-zA-Z]:\\[^>]*)(?:>|\s*>)");
            if (psMatch.Success)
            {
                string path = psMatch.Groups[1].Value.Trim();
                if (Directory.Exists(path))
                {
                    currentWorkingDirectory = path;
                }
                return;
            }

            // Match CMD prompt: C:\Path>
            Match cmdMatch = Regex.Match(lastLineText, @"^([a-zA-Z]:\\[^>]*)(?:>|\s*>)");
            if (cmdMatch.Success)
            {
                string path = cmdMatch.Groups[1].Value.Trim();
                if (Directory.Exists(path))
                {
                    currentWorkingDirectory = path;
                }
                return;
            }

            // Match WSL prompt: username@computername:/mnt/c/path$ or /mnt/c/path$
            // Regex matches: /mnt/ followed by drive letter (e.g. c) followed by subpath, ending with $ or #
            Match wslMatch = Regex.Match(lastLineText, @"/mnt/([a-zA-Z])(/[^$#]*)(?:\$|#|\s*\$|\s*#)");
            if (wslMatch.Success)
            {
                string drive = wslMatch.Groups[1].Value.ToUpper();
                string path = wslMatch.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar).Trim();
                string WindowsPath = drive + ":" + path;
                if (Directory.Exists(WindowsPath))
                {
                    currentWorkingDirectory = WindowsPath;
                }
                return;
            }
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
                    terminalStyle.font = Font.CreateDynamicFontFromOSFont(terminalStyle.font.name, fontSize);
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

            // 1. Listen to Keyboard events BEFORE drawing controls to intercept them
            HandleKeyboardEvents();

            // 2. Draw Toolbar Control Area
            DrawToolbar();

            // Draw a single dark background for the entire terminal body (log + input)
            Rect bodyRect = EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(bodyRect, new Color(0.118f, 0.118f, 0.118f));

            // 3. Draw Log Screen
            DrawOutputLog();

            // 4. Draw Terminal Command Input
            DrawInputLine();

            EditorGUILayout.EndVertical();

            // 5. Refocus input if requested (done at the end of the frame after controls are registered)
            if (focusInputPending)
            {
                EditorGUI.FocusTextInControl("TerminalInput");
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

            // Shell selection
            ShellTypeEnum currentShellEnum = ShellTypeEnum.PowerShell;
            if (Enum.TryParse(shellType, out ShellTypeEnum parsedEnum))
            {
                currentShellEnum = parsedEnum;
            }
            ShellTypeEnum newShellEnum = (ShellTypeEnum)EditorGUILayout.EnumPopup(currentShellEnum, EditorStyles.toolbarDropDown, GUILayout.Width(100));
            string newShell = newShellEnum.ToString();
            
            if (newShell != shellType)
            {
                shellType = newShell;
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
            // Main terminal view area container
            Rect viewRect = EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            
            // Render Terminal BG Color (#1e1e1e Dark color)
            EditorGUI.DrawRect(viewRect, new Color(0.118f, 0.118f, 0.118f));

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            // Accumulate Rich Text lines
            StringBuilder sb = new StringBuilder();
            lock (logLock)
            {
                foreach (var line in terminalLines)
                {
                    foreach (var span in line)
                    {
                        string open = "";
                        string close = "";
                        if (span.bold) { open += "<b>"; close = "</b>" + close; }
                        if (span.italic) { open += "<i>"; close = "</i>" + close; }
                        if (span.color != null) { open += $"<color={span.color}>"; close = "</color>" + close; }

                        sb.Append(open).Append(EscapeRichText(span.text)).Append(close);
                    }
                    sb.Append("\n");
                }
            }

            string fullText = sb.ToString();

            // Calculate height of rich text logs
            float viewWidth = position.width - 25;
            float textHeight = terminalStyle.CalcHeight(new GUIContent(fullText), viewWidth);
            // Minimum height equal to window to ensure the whole area acts as terminal body
            float renderHeight = Mathf.Max(textHeight, viewRect.height - 10);

            // Selectable label for rich text rendering & copy paste support
            EditorGUILayout.SelectableLabel(fullText, terminalStyle, GUILayout.Width(viewWidth), GUILayout.Height(renderHeight));

            if (scrollToBottomPending)
            {
                scrollPosition.y = renderHeight;
                scrollToBottomPending = false;
            }

            EditorGUILayout.EndScrollView();

            // Focus Command input field when terminal background is clicked
            Event e = Event.current;
            if (viewRect.Contains(e.mousePosition) && e.type == EventType.MouseDown)
            {
                focusInputPending = true;
                Repaint();
            }

            EditorGUILayout.EndVertical();
        }

        private string EscapeRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private void DrawInputLine()
        {
            EditorGUILayout.BeginHorizontal();

            string promptSymbol = shellType == "PowerShell" ? "PS> " : "> ";
            GUILayout.Label(promptSymbol, terminalStyle, GUILayout.Width(30));

            GUI.SetNextControlName("TerminalInput");
            string newInput = EditorGUILayout.TextField(currentInput, inputStyle, GUILayout.ExpandWidth(true));
            
            // Filter out any literal tab characters inserted by Unity's TextField to keep currentInput clean
            currentInput = newInput.Replace("\t", "");

            EditorGUILayout.EndHorizontal();
        }


        private void HandleKeyboardEvents()
        {
            Event e = Event.current;
            
            // Cancel autocomplete if user clicks anywhere with the mouse
            if (e.type == EventType.MouseDown)
            {
                isAutocompleting = false;
            }

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
                        isAutocompleting = false;
                    }
                    else if (e.keyCode == KeyCode.None && e.character != '\0' && e.character != '\t')
                    {
                        isAutocompleting = false;
                    }
                }

                // 1. Enter Key: Send Command
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (e.type == EventType.KeyDown)
                    {
                        if (!string.IsNullOrEmpty(currentInput))
                        {
                            SendCommand(currentInput);
                            currentInput = "";
                            isAutocompleting = false;
                        }
                        focusInputPending = true;
                    }
                    e.Use(); // Consume both KeyDown and KeyUp to prevent the TextField from submitting/losing focus
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
                                isAutocompleting = false;
                            }
                        }
                    }
                    e.Use(); // Consume both KeyDown and KeyUp to prevent caret from moving to start
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
                        isAutocompleting = false;
                    }
                    e.Use(); // Consume both KeyDown and KeyUp to prevent caret from moving to end
                }
                
                // 4. Tab Key: Smart File System Autocomplete
                // 4. Tab Key: Smart File System Autocomplete
                else if (e.keyCode == KeyCode.Tab || e.character == '\t')
                {
                    if (e.type == EventType.KeyDown)
                    {
                        HandleTabComplete();
                        focusInputPending = true;
                    }
                    e.Use(); // Consume both KeyDown and KeyUp to prevent shifting focus to other controls
                }
            }
        }

        private double lastTabTime = 0;

        private void HandleTabComplete()
        {
            double diff = EditorApplication.timeSinceStartup - lastTabTime;
            if (diff < 0.05)
            {
                return; // Debounce double triggers in the same frame
            }
            lastTabTime = EditorApplication.timeSinceStartup;

            if (string.IsNullOrEmpty(currentInput)) return;

            if (!isAutocompleting)
            {
                // Parse word to autocomplete (the last token)
                int lastSpaceIdx = currentInput.LastIndexOf(' ');
                string word = lastSpaceIdx == -1 ? currentInput : currentInput.Substring(lastSpaceIdx + 1);
                string before = lastSpaceIdx == -1 ? "" : currentInput.Substring(0, lastSpaceIdx + 1);

                if (string.IsNullOrEmpty(word)) return;

                string searchDir = currentWorkingDirectory;
                string searchPattern = word;

                // Handle subfolders inside the path (e.g. Assets/Sc)
                int lastSlash = word.LastIndexOfAny(new char[] { '/', '\\' });
                if (lastSlash != -1)
                {
                    string subPath = word.Substring(0, lastSlash + 1);
                    subPath = subPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    searchDir = Path.Combine(currentWorkingDirectory, subPath);
                    searchPattern = word.Substring(lastSlash + 1);
                }

                autocompleteMatches.Clear();

                if (Directory.Exists(searchDir))
                {
                    try
                    {
                        string[] entries = Directory.GetFileSystemEntries(searchDir, searchPattern + "*");
                        foreach (string entry in entries)
                        {
                            string name = Path.GetFileName(entry);
                            if (Directory.Exists(entry))
                            {
                                name += "/";
                            }

                            string completedWord = lastSlash != -1 ? (word.Substring(0, lastSlash + 1) + name) : name;
                            autocompleteMatches.Add(completedWord);
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[Terminal] Match search error: {ex.Message}");
                    }
                }

                // Initialize tab cycling state
                if (autocompleteMatches.Count > 0)
                {
                    isAutocompleting = true;
                    autocompleteIndex = 0;
                    originalAutocompleteWord = word;
                    inputBeforeAutocompleteWord = before;
                    currentInput = inputBeforeAutocompleteWord + autocompleteMatches[0];
                    Repaint();
                }
            }
            else
            {
                // Cycle autocomplete choices
                if (autocompleteMatches.Count > 0)
                {
                    autocompleteIndex = (autocompleteIndex + 1) % autocompleteMatches.Count;
                    currentInput = inputBeforeAutocompleteWord + autocompleteMatches[autocompleteIndex];
                    Repaint();
                }
            }
        }

        private void SendCommand(string cmd)
        {
            if (shellInput != null && shellProcess != null && !shellProcess.HasExited)
            {
                // Update history
                if (commandHistory.Count == 0 || commandHistory[commandHistory.Count - 1] != cmd)
                {
                    commandHistory.Add(cmd);
                    if (commandHistory.Count > 200)
                    {
                        commandHistory.RemoveAt(0);
                    }
                }
                historyIndex = -1;

                // Intercept custom clear screen commands
                string trimmed = cmd.Trim().ToLower();
                if (trimmed == "clear" || trimmed == "cls")
                {
                    ClearTerminalBuffer();
                    // Still send to shell to clear underlying terminal buffer if needed
                }

                // Send to background process (WSL requires only LF (\n), others default to CRLF (\r\n))
                if (shellType == "WSL")
                {
                    shellInput.Write(cmd + "\n");
                }
                else
                {
                    shellInput.Write(cmd + "\r\n");
                }
            }
        }

        private enum ShellTypeEnum
        {
            PowerShell,
            CMD,
            WSL
        }
    }
}
