using System;
using System.Threading;

namespace FusionConsole;

public static class SplashScreenFire
{
    public static void Print()
    {
        //ConsoleHelper.SetConsoleWindowSize();
        // --- Colors ---
        string red = "\u001b[38;2;220;20;20m"; // Fire Engine Red
        string green = "\u001b[32m";
        string sil = "\u001b[38;2;210;215;220m"; // Bright Silver
        string reset = "\u001b[0m";

        Console.BackgroundColor = ConsoleColor.Black;

        // --- 1. FORTNA Array ---
        string[] fortnaLines =
        {
            @"███████╗ ██████╗ ██████╗ ████████╗███╗   ██╗   █████╗",
            @"██╔════╝██╔═══██╗██╔══██╗╚══██╔══╝████╗  ██║   ██║██║",
            @"█████╗  ██║   ██║██████╔╝   ██║   ██╔██╗ ██║  ██║  ██║",
            @"██╔══╝  ██║   ██║██╔══██╗   ██║   ██║╚██╗██║ ██║    ██║",
            @"██║     ╚██████╔╝██║  ██║   ██║   ██║ ╚████║██║ ▄██▄ ██║",
            @"╚═╝      ╚═════╝ ╚═╝  ╚═╝   ╚═╝   ╚═╝  ╚═══╝╚═╝ ╚══╝ ╚═╝"
        };

        // --- 2. FIRE Array (Side graphics removed, text kept) ---
        string[] fireLines =
        {
            $@"{red}███████╗██╗██████╗ ███████╗",
            $@"{red}██╔════╝██║██╔══██╗██╔════╝   {green}F{sil}ramework for",
            $@"{red}█████╗  ██║██████╔╝█████╗     {green}I{sil}ntegration",
            $@"{red}██╔══╝  ██║██╔══██╗██╔══╝     {green}R{sil}outing and",
            $@"{red}██║     ██║██║  ██║███████╗   {green}E{sil}xecution",
            $@"{red}╚═╝     ╚═╝╚═╝  ╚═╝╚══════╝{reset}"
        };

        // --- 3. Layout Spacing ---
        string pad = "  "; // Left margin before FORTNA
        string gap = "    "; // Gap between FORTNA and FIRE

        Console.WriteLine();

        // --- 4. Print them Row by Row ---
        for (int i = 0; i < 6; i++)
        {
            // Set standard white for the FORTNA side
            Console.Write(pad + "\u001b[37m");

            // PadRight(58) ensures the FIRE logo always starts exactly at the same column
            Console.Write(fortnaLines[i].PadRight(58) + gap);

            // Print the FIRE line
            Console.WriteLine(fireLines[i]);
            
        }
       
        Console.WriteLine(reset);

        // Optional Divider below them both
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            "  --------------------------------------------------------------------------------------------------");
        Console.ResetColor();
         Thread.Sleep(2000);
        Console.Write("\x1b[2J\x1b[H");
    }

    static void SmoothScrollUp(int lines, int delay)
    {
        Console.CursorVisible = false;
        for (int i = 0; i < lines; i++)
        {
            Console.WriteLine();
            Thread.Sleep(delay);
        }

        Console.CursorVisible = true;
    }

    static void PrintTypewriter(string text, int delay)
    {
        foreach (char c in text)
        {
            Console.Write(c);
            Thread.Sleep(delay);
        }

        Console.WriteLine(); // Move to the next line after
    }

    /// <summary>
    /// Attempts to set the console window to a larger size.
    /// Will fail silently if not supported (e.g., not on Windows).
    /// </summary>
    public static int Length()
    {
        return 0;
    }
}