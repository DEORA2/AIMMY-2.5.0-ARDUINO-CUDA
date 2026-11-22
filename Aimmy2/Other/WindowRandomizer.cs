// Program.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class WindowRandomizer
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    static volatile bool running = true;

    public async void Randomize()
    {
        // run the loop on a background thread
        Task.Run(() =>
        {
            var rnd = new Random();
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            const int sleepMs = 5000;
            const int minLength = 8;
            const int maxLength = 20;

            while (running)
            {
                var myWindows = new List<IntPtr>();

                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == myPid)
                        myWindows.Add(hWnd);
                    return true;
                }, IntPtr.Zero);

                foreach (var h in myWindows)
                {
                    try
                    {
                        int titleLength = rnd.Next(minLength, maxLength + 1);
                        string newTitle = RandomString(rnd, titleLength);
                        SetWindowText(h, newTitle);
                    }
                    catch { }
                }

                Thread.Sleep(sleepMs);
            }
        });
    }


    static string RandomString(Random rnd, int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
            sb.Append(chars[rnd.Next(chars.Length)]);
        return sb.ToString();
    }
}

