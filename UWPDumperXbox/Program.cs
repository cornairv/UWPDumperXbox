using System;
using System.Linq;

namespace Microsoft
{
    namespace Build
    {
        namespace Utilities
        {
            public abstract class Task
            {
                public TaskLoggingHelper Log { get; }
                public abstract bool Execute();
            }

            public class TaskLoggingHelper
            {
                public void LogMessage(Framework.MessageImportance importance, string message, params object[] messageArgs)
                {
                    Console.WriteLine(string.Format(message, messageArgs));
                }
                public void LogWarning(string message, params object[] messageArgs)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("warning ");
                    Console.ResetColor();
                    Console.Error.WriteLine(string.Format(message, messageArgs));
                }
                public void LogError(string message, params object[] messageArgs)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("error ");
                    Console.ResetColor();
                    Console.Error.WriteLine(string.Format(message, messageArgs));
                }
            }
        }
        namespace Framework
        {
            public abstract class ITask
            {
                public abstract bool Execute();
            }

            public enum MessageImportance
            {
                High,
                Normal,
                Low,
            }
        }
    }
}

internal class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UWPDumperXbox <TargetName> <Destination> [OverrideSource] [OverridePid]");
            return 1;
        }
        var dumper = new UWPDumperXbox
        {
            TargetName = args[0],
            Destination = args[1],
            OverrideSource = args.ElementAtOrDefault(2),
            OverridePid = uint.Parse(args.ElementAtOrDefault(3)),
        };
        return dumper.Execute() ? 0 : 1;
    }
}