using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeviceSpaceConsole;

public interface ISplashScreen
{
    public void Print();
    public int Length();
}



public static class ConsoleHelper
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const int TMPF_TRUETYPE = 4;
    private const int LF_FACESIZE = 32;
    private static IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct CONSOLE_FONT_INFO_EX
    {
        internal uint cbSize;
        internal uint nFont;
        internal COORD dwFontSize;
        internal int FontFamily;
        internal int FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        internal string FaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        internal short X;
        internal short Y;

        internal COORD(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetCurrentConsoleFontEx(
        IntPtr consoleOutput,
        bool maximumWindow,
        ref CONSOLE_FONT_INFO_EX consoleCurrentFontEx);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int dwType);

    /// <summary>
    /// Sets the console font to a specific font family and size.
    /// Recommended for ASCII Art: "Consolas", "Lucida Console", or "Cascadia Code".
    /// </summary>
    /// <param name="fontName">Name of the font (must be installed on OS).</param>
    /// <param name="fontSize">Size in pixels (e.g., 16, 24).</param>
    public static void SetConsoleFont(string fontName = "Consolas", short fontSize = 16)
    {
        // 1. Get the handle to the console
        IntPtr hnd = GetStdHandle(STD_OUTPUT_HANDLE);
        if (hnd == INVALID_HANDLE_VALUE) return;

        // 2. Configure the font struct
        CONSOLE_FONT_INFO_EX info = new CONSOLE_FONT_INFO_EX();
        info.cbSize = (uint)Marshal.SizeOf(info);
        info.FaceName = fontName;
        
        // X = 0 means the engine calculates width based on height (Y)
        info.dwFontSize = new COORD(0, fontSize); 
        info.FontWeight = 400; // Normal weight

        // 3. Apply changes
        SetCurrentConsoleFontEx(hnd, false, ref info);
    }
    
    public static void SetConsoleWindowSize()
    {
        // This only works reliably on Windows
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform
                .Windows))
        {
            try
            {
                // Set a large buffer size (e.g., 200 columns, 50 rows)
                // The window size cannot be larger than the buffer size.
                Console.SetBufferSize(400, 400);

                // Set the window size (e.g., 180 columns, 40 rows)
                Console.SetWindowSize(350, 325);
            }
            catch (PlatformNotSupportedException)
            {
                // This happens on non-Windows systems (Linux/macOS)
                Console.WriteLine("Resizing not supported on this OS.");
            }
            catch (ArgumentOutOfRangeException)
            {
                // This happens if the size is too big for the user's monitor
                Console.WriteLine("The requested size is too large for this screen.");
            }
            catch (IOException ex)
            {
                // This can fail if the console doesn't support resizing
                // (e.g., running in a non-interactive shell)
            }
            catch (Exception ex1)
            {
                // This can fail if the size is too large for the screen
                // or invalid.
            }
        }
    }
}