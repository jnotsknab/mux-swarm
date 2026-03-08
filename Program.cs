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
        }
    }
}