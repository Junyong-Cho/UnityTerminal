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
        if (args.Length > 0 && args[0] == "flood")
            for (int i = 0; i < 100000; i++) Console.WriteLine("FLOOD-" + i + "-abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz");
        if (args.Length > 0 && args[0] == "child")
        {
            var child = Process.Start(new ProcessStartInfo("cmd.exe", "/d /q /k") { UseShellExecute = false, CreateNoWindow = true });
            Console.WriteLine("CHILD:" + child.Id);
        }
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.KeyChar == 4) break;
            if (key.KeyChar == 'r') Console.WriteLine("SIZE:" + Console.WindowWidth + ":" + Console.WindowHeight);
            else Console.WriteLine("KEY:" + key.Key + ":" + (int)key.KeyChar + ":" + key.Modifiers);
        }
        Console.Write("TAIL-BEFORE-EXIT");
    }
}
