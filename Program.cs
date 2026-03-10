using System;
using System.Text;
using System.Text.Json;
using MuxSwarm.Utils;
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
                
                static string? ArgValue(string[] args, string flag)
                    => Array.IndexOf(args, flag) is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;

                PlatformContext.ApplyOverrides(
                    ArgValue(args, "--cfg"),
                    ArgValue(args, "--swarmcfg")
                );
                
                var app = new App();
                return await app.Run(args);
            }
            catch (JsonException ex)
            {
                MuxConsole.WriteError("Failed to parse configuration file.");
                MuxConsole.WriteMuted("This usually means your config JSON is malformed or uses an outdated format.");
                MuxConsole.WriteMuted($"Details: {ex.Message}");
                MuxConsole.WriteLine();
                MuxConsole.WriteMuted($"Config path: {PlatformContext.ConfigPath}");
                MuxConsole.WriteMuted("Try fixing the JSON manually, or delete the file and re-run /setup.");
                return 1;
            }
            catch (FileNotFoundException ex)
            {
                MuxConsole.WriteError($"Required file not found: {ex.FileName}");
                MuxConsole.WriteMuted("Re-run /setup or check your config paths.");
                return 1;
            }
            catch (UnauthorizedAccessException ex)
            {
                MuxConsole.WriteError("Permission denied accessing a file or directory.");
                MuxConsole.WriteMuted(ex.Message);
                return 1;
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