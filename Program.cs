using System;
using System.Text;
using Spectre.Console;

namespace MuxSwarm
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            try
            {   
                Console.InputEncoding = Encoding.UTF8;
                Console.OutputEncoding = Encoding.UTF8;
                AnsiConsole.Profile.Encoding = Encoding.UTF8;
                AnsiConsole.Profile.Capabilities.Unicode = true;
                
                var app = new App();
                return await app.Run(args);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[FATAL] Unhandled exception:");
                Console.ResetColor();
                Console.WriteLine(ex);

                return ex.HResult != 0 ? ex.HResult : 1;
            }
            finally
            {
                if (ShouldPauseOnExit())
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("\nPress ENTER to close...");
                    Console.ResetColor();
                    Console.ReadLine();
                }
            }
        }

        private static bool ShouldPauseOnExit()
        {
            if (Console.IsOutputRedirected || Console.IsErrorRedirected)
                return false;

            if (!Environment.UserInteractive)
                return false;

            if (string.Equals(Environment.GetEnvironmentVariable("MUXSWARM_NO_PAUSE"), "1", StringComparison.OrdinalIgnoreCase))
                return false;

            return OperatingSystem.IsWindows();
        }
    }
}