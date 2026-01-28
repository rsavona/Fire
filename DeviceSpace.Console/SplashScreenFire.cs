using System;
using System.Threading;

namespace DeviceSpaceConsole;
 
public static class SplashScreenFire
{
    public static void Print()
    {
        // --- Read description from appsettings.json ---
        

        ConsoleHelper.SetConsoleWindowSize(); // Assuming this method exists
        // Escape code for switching to Yellow
        char esc = (char)0x1B;
        string yel = $"{esc}[33m";
        string red = $"{esc}[31m";
        string green = $"{esc}[32m";


        // --- Print FORTNA in default color -
      Console.BackgroundColor = ConsoleColor.Black; // Keep background consistent

    // --- 1. FORTNA Section: Radiant White/Gray ---
    // We use Gray for the "shadow" parts of the block text and White for the faces
    Console.ForegroundColor = ConsoleColor.White;
    string pad = "     ";
    Console.WriteLine(@"
    " + pad + @"███████╗ ██████╗ ██████╗ ████████╗███╗   ██╗   █████╗                   
    " + pad + @"██╔════╝██╔═══██╗██╔══██╗╚══██╔══╝████╗  ██║   ██║██║                   
    " + pad + @"█████╗  ██║   ██║██████╔╝   ██║   ██╔██╗ ██║  ██║  ██║                  
    " + pad + @"██╔══╝  ██║   ██║██╔══██╗   ██║   ██║╚██╗██║ ██║    ██║                 
    " + pad + @"██║     ╚██████╔╝██║  ██║   ██║   ██║ ╚████║██║ ▄██▄ ██║                
    " + pad + @"╚═╝      ╚═════╝ ╚═╝  ╚═╝   ╚═╝   ╚═╝  ╚═══╝╚═╝ ╚══╝ ╚═╝");

    // --- 2. Divider ---
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(pad + "-------------------------------------------------------------");


    // RGB Colors for a real fire gradient
    // RGB / Color Variables
    string sil = "\u001b[38;2;210;215;220m"; // Bright Silver
    string white  = "\u001b[38;2;255;255;255m"; 
    string gol   = "\u001b[38;2;255;215;0m";   // Gold
    string fed    = "\u001b[38;2;220;20;20m";   // Fire Engine Red
    string orange = "\u001b[38;2;255;102;0m";   // Orange
    string bold   = "\u001b[1m";
    string reset  = "\u001b[0m";

    // --- FIERCE FIRE SECTION ---
    // 2 Gold, 2 Red, 1 Orange. Fire widened by 1 click.
    // --- FIERCE FIRE SECTION ---
    // All shifted left, words aligned, base shifted 1
    // --- FIERCE FIRE SECTION ---
    // Shifted 4 left (0 pad), Big FIRE in Yellow/Red, Aligned Engine & Base
    Console.WriteLine($@"
    {gol}    ()      ███████╗██╗██████╗ ███████╗       )*
    {yel}   ) (      ██╔════╝██║██╔══██╗██╔════╝      * (   {green}F{sil}lexible
    {red}  ( * )     █████╗  ██║██████╔╝█████╗       ( | )  {green}I{sil}ndustrial
    {fed}  / ) \     ██╔══╝  ██║██╔══██╗██╔══╝       \\/ /   {green}R{sil}outing
    {red}  \| |/     ██║     ██║██║  ██║███████╗     || ||   {green}E{sil}ngine
    {sil}  ▒▒▒▒▒ {fed}    ╚═╝     ╚═╝╚═╝  ╚═╝╚══════╝   {sil}  ▒▒▒▒▒  {reset}");

            Console.ResetColor();

            // --- Print the App Description (NEW) ---
            Console.ForegroundColor = ConsoleColor.Yellow;
            string appDescription = $@"{sil}Without the WCS, the hardware is just cold steel. {fed}'FIRE'{sil} brings the system to life.";
           // PrintTypewriter($"\n    {appDescription}\n", 50); // 50ms delay

            // --- SCROLL UP 8 LINES ---
            Console.ResetColor(); // Ensure we don't scroll yellow/red backgrounds
            SmoothScrollUp(21, 100); // 8 lines, 100ms speed
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
            return 8;
        }
    
}