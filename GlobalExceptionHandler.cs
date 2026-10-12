namespace KiwiApi;

using Microsoft.AspNetCore.Diagnostics;
using System.Diagnostics;
using System.Text.RegularExpressions;

public partial class GlobalExceptionHandler : IExceptionHandler {
    private readonly Logger logger;

    public GlobalExceptionHandler() {
        logger = new Logger("EXPT");
    }

    [GeneratedRegex(@":(\bline \d+)")]
    private static partial Regex LineMatchRegex();

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) {
        StackTrace stackTrace = new StackTrace(exception, true);
        StackFrame? lastFrame = stackTrace.GetFrames()?.FirstOrDefault(frame => frame.GetFileName() != null);

        string fileName = lastFrame?.GetFileName() ?? "Unknown File";
        int lineNumber = lastFrame?.GetFileLineNumber() ?? -1;

        string fullStackTrace = stackTrace.ToString();
        string[] lines = fullStackTrace.Split(Environment.NewLine);

        string finalStackTrace = string.Join(Environment.NewLine, lines.Where(line => line.Contains("kiwiapi")).Select(line => LineMatchRegex().Replace(line, match => $":\x1b[43;30m{match.Groups[1].Value}\x1b[0m")));

        logger.ERR("Encountered an unhandled exception in file \"" + fileName + "\" at line {" + lineNumber + "}");
        logger.ERR("Exception stack trace:\n" + finalStackTrace);
        logger.ERR("Exception message: " + exception.Message);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new {
            error = "Encountered an unknown error. Report the following as well as any relevant information to @KiwianDoesThings: \"" + exception.Message + "\""
        }, cancellationToken);

        return true;
    }
}