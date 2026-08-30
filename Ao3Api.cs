namespace kiwiapi;

using System.Diagnostics;
using System.Text;

using static Program;

public class Ao3Api {
	private Logger logger = new Logger("Ao3");

    public void MapApiFunctions(WebApplication app) {
		app.MapGet("/getao3storyid", async (HttpContext context, string storyTitle, int page) => {
			logger.INFO("Getting story ID from searching \"" + storyTitle + "\" on page " + page);

			return GetAo3ApiResponse("search", [storyTitle, page.ToString()]);
		});

		app.MapGet("/getao3text", async (HttpContext context, int storyID, int page) => {
			logger.INFO("Getting text from storyID " + storyID + " on chapter " + page);

			return GetAo3ApiResponse("text", [storyID.ToString(), page.ToString()]);
		});
	}

    public IResult GetAo3ApiResponse(string file, string[] arguments) {
        ProcessStartInfo start = new ProcessStartInfo();
        string pythonFile = Path.Combine(Environment.CurrentDirectory, file + ".py");

        if (!File.Exists(pythonFile)) {
            logger.ERR("Could not find python file at \"" + pythonFile + "\"");
        }

        start.FileName = "python3";
        start.ArgumentList.Add(file);
        foreach (string argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;

        using Process? process = Process.Start(start);
        if (process == null) {
            return ServerError("Failed to start Python process.");
        }

        string result = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrEmpty(errors)) {
            logger.ERR("Python Error: " + errors);
            return Results.Json(new { error = errors });
        }

        return Results.Json(result);
    }
}