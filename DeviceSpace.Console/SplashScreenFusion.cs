using System;
using System.Threading;

namespace DeviceSpaceConsole
{
    public static class SplashScreenFusion
    {
        public static void Print()
        {
            // Set font size slightly smaller to fit the wide layout
            ConsoleHelper.SetConsoleFont("Lucida Console", 14);
            ConsoleHelper.SetConsoleWindowSize();
            Console.Clear();

            // --- Colors ---
            string white = "\u001b[97m";
            string cyan = "\u001b[36m";
            string blue = "\u001b[34m";
            string navy = "\u001b[38;5;19m";
            string yellow = "\u001b[33m";
            string gray = "\u001b[90m";

            string bold = "\u001b[1m";
            string reset = "\u001b[0m";

            Console.Clear();
// We use Black background to make the "Fire" colors pop
            Console.BackgroundColor = ConsoleColor.Black;

            string pad = "     ";

// Row 1 - Top Rays
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(pad + @"███████╗ ██████╗ ██████╗ ████████╗███╗   ██╗   █████╗");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(@"      \ | /");

// Row 2 - Sun Top
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(pad + @"██╔════╝██╔═══██╗██╔══██╗╚══██╔══╝████╗  ██║   ██║██║");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write(@"    - ( ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("▄▄▄");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" ) -");

// Row 3 - The "Horizon" (Main Logo Line)
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(pad + @"█████╗  ██║   ██║██████╔╝   ██║   ██╔██╗ ██║  ██║  ██║");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write(@"  -- ( ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("█████");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" ) --");

// Row 4 - Sun Bottom
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(pad + @"██╔══╝  ██║   ██║██╔══██╗   ██║   ██║╚██╗██║ ██║    ██║");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write(@"    - ( ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("▀▀▀");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(" ) -");

// Row 5 - Bottom Rays
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(pad + @"██║     ╚██████╔╝██║  ██║   ██║   ██║ ╚████║██║ ▄██▄ ██║");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(@"      / | \");

// Row 6 - Footer
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(pad + @"╚═╝      ╚═════╝ ╚═╝  ╚═╝   ╚═╝   ╚═╝  ╚═══╝╚═╝ ╚══╝ ╚═╝'s");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Universal Sortation Intelligence & Optimization Network" + reset + 
                              bold + cyan + " *F U S " + blue + "I O N*" + pad + reset);

            ConsoleHelper.SetConsoleFont("Arial", 18);
            // --- LOADING ANIMATION ---
            Console.ForegroundColor = ConsoleColor.Cyan;
            string description = cyan + @"    The Power of an Intelligent Core." + reset + white +
                                 "Seamless Integration. Solid Results." + reset;

            foreach (char c in description)
            {
                Console.Write(c);
                Thread.Sleep(10);
            }

            Console.WriteLine(reset + "\n");
        }


        public static int Length()
        {
            return 11;
        }
    }
}