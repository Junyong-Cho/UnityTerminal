using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class ConsoleProbe
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int number);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    public static void Main(string[] args)
    {
        uint mode;
        IntPtr output = GetStdHandle(-11);
        if (GetConsoleMode(output, out mode)) SetConsoleMode(output, mode | 4);
        Console.TreatControlCAsInput = true;
        Console.Write("\x1b[2J\x1b[H");
        Console.WriteLine("READY|TTY:" + Console.IsInputRedirected + ":" + Console.IsOutputRedirected);
        bool persistent = args.Length > 0 && args[0] == "persist";
        if (persistent) Console.WriteLine("PROCESS:" + Process.GetCurrentProcess().Id + "|END");
        if (args.Length > 0 && args[0] == "flood")
            for (int i = 0; i < 100000; i++) Console.WriteLine("FLOOD-" + i + "-abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz");
        if (args.Length > 0 && args[0] == "child")
        {
            var child = Process.Start(new ProcessStartInfo("cmd.exe", "/d /q /k") { UseShellExecute = false, CreateNoWindow = true });
            Console.WriteLine("CHILD:" + child.Id + "|END");
        }
        int keyCount = 0;
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.KeyChar == 4) break;
            if (persistent)
            {
                if (key.KeyChar == 'x') Console.Write("\x1b[?1049h\x1b[2J\x1b[HALT-KEEP-\uD55C\uAE00\r\n\x1b[6 q");
                Console.WriteLine("COUNT:" + (++keyCount) + "|CHAR:" + (int)key.KeyChar);
            }
            if (key.KeyChar == 'r') Console.WriteLine("SIZE:" + Console.WindowWidth + ":" + Console.WindowHeight);
            else Console.WriteLine("KEY:" + key.Key + ":" + (int)key.KeyChar + ":" + key.Modifiers);
        }
        Console.Write("TAIL-BEFORE-EXIT");
    }
}
