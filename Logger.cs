namespace kiwiapi;

using System;
using System.Runtime.CompilerServices;

public class Logger {
    public static readonly Dictionary<string, string> logColors = new() {
        {"DEBUG", "\u001b[1;34m"},
        {"INFO", "\u001b[1;32m"},
        {"EXTERNAL", "\u001b[1;38;5;226m"},
        {"WARN", "\u001b[1;38;5;214m"},
        {"ERROR", "\u001b[1;31m"},
        {"CRITICAL", "\u001b[1;41m"},
        {"RESET", "\u001b[0m"},
        {"SRCLOC", "\u001b[1;30m"},
        {"FUNCTION", "\u001b[1;35m"}
    };
    public static readonly string[] levelStrings = new string[] {
        "DEBUG",
        "INFO",
        "EXTERNAL",
        "WARN",
        "ERROR",
        "CRITICAL",
        "OFF"
    };
    public static readonly Logger shared = new Logger("General");
    public readonly string logName;
    private static string headerStructure = "[{level} | {function}]";

    public Logger(string logName) {
        this.logName = logName;
    }

    public void INFO(string message, [CallerMemberName] string sourceFunction = "Invalid", [CallerFilePath] string sourceFile = "Invalid", [CallerLineNumber] int sourceLine = -1) {
        WriteMessage(LogLevel.Info, message);
    }

    public void WARN(string message, [CallerMemberName] string sourceFunction = "Invalid", [CallerFilePath] string sourceFile = "Invalid", [CallerLineNumber] int sourceLine = -1) {
        WriteMessage(LogLevel.Warn, message);
    }

    public void ERR(string message, [CallerMemberName] string sourceFunction = "Invalid", [CallerFilePath] string sourceFile = "Invalid", [CallerLineNumber] int sourceLine = -1) {
        WriteMessage(LogLevel.Error, message);
    }

    public void CRITICAL(string message, [CallerMemberName] string sourceFunction = "Invalid", [CallerFilePath] string sourceFile = "Invalid", [CallerLineNumber] int sourceLine = -1) {
        WriteMessage(LogLevel.Critical, message);
    }

    public void LOG_CHECK(bool condition, string success, string error) {
        if (condition) {
            INFO(success);
        } else {
            ERR(error);
        }
    }
    public void LOG_CHECK_BAD(bool condition, string error) {
        if (!condition) {
            ERR(error);
        }
    }
    public int LOG_CHECK_RETURN(bool condition, string success, string error, int returnCode) {
        if (condition) {
            INFO(success);
            return 0;
        } else {
            ERR(error);
            return returnCode;
        }
    }
    public int LOG_CHECK_RETURN_BAD(bool condition, string error, int returnCode) {
        if (!condition) {
            ERR(error);
            return returnCode;
        }
        return 0;
    }
    public void LOG_CHECK_CRITICAL(bool condition, string success, string error) {
        if (condition) {
            INFO(success);
        } else {
            CRITICAL(error);
        }
    }
    public void LOG_CHECK_BAD_CRITICAL(bool condition, string error) {
        if (!condition) {
            CRITICAL(error);
        }
    }
    public int LOG_CHECK_RETURN_CRITICAL(bool condition, string success, string error, int returnCode) {
        if (condition) {
            INFO(success);
            return 0;
        } else {
            CRITICAL(error);
            return returnCode;
        }
    }
    public int LOG_CHECK_RETURN_BAD_CRITICAL(bool condition, string error, int returnCode) {
        if (!condition) {
            CRITICAL(error);
            return returnCode;
        }
        return 0;
    }

    private void WriteMessage(LogLevel level, string message) {
        string header = headerStructure;
        string levelString = LogLevelToString(level);
        string functionName = ColoredString("FUNCTION", logName);

        header = ReplaceString(header, "{level}", ColoredString(levelString, levelString));
        header = ReplaceString(header, "{function}", functionName);

        Console.WriteLine(header + " " + message);
    }

    private static string ReplaceString(string fullString, string oldString, string newString) {
        return fullString.Replace(oldString, newString);
    }

    private static string LogLevelToString(LogLevel level) {
        return levelStrings[(int)level];
    }

    private static string ColoredString(string color, string originalString) {
        string colorString = logColors.TryGetValue(color, out string? value) ? value : string.Empty;
        return colorString + originalString + logColors["RESET"];
    }

    private enum LogLevel : byte {
        Debug,
        Info,
        External,
        Warn,
        Error,
        Critical,
        Off
    }
}