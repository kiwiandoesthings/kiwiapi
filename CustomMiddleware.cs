namespace KiwiApi;

public class CustomMiddleware {
    private readonly Logger logger;
    private readonly RequestDelegate next;

    public CustomMiddleware(RequestDelegate next) {
        logger = new Logger(" MDW");
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext context) {
        await next(context);

        if (context.Response.StatusCode >= 400 && context.Response.StatusCode < 500) {
            string path = context.Request.Path;
            string method = context.Request.Method;
            string fullRoute = "\"" + method + path + "\"";

            if (context.Response.StatusCode == 404) {
                if (context.GetEndpoint() == null) {
                    logger.WARN("Client tried to use route " + fullRoute + " that does not exist");
                    return;
                }
            }

            if (Globals.verboseLogging || true) {
                logger.WARN("Client error {" + context.Response.StatusCode + "} on " + fullRoute);
            }
        }
    }
}
