namespace kiwiapi;

using Microsoft.AspNetCore.Diagnostics;

public class GlobalExceptionHandler : IExceptionHandler {
    private readonly Logger logger;

    public GlobalExceptionHandler() {
        logger = new Logger("XPT");
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken) {
        logger.ERR("Encountered unhandled exception: " + exception.Message);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new {
            error = "Encountered unknown error. Report the following as well as any relevant information to Kiwian: \"" + exception.Message + "\""
        }, cancellationToken);

        return true;
    }
}
