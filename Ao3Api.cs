namespace kiwiapi;

using System.Diagnostics;
using System.Text;

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
		start.FileName = "python";
		string pythonFile = Path.Combine(Environment.CurrentDirectory, file + ".py");
		if (!File.Exists(pythonFile)) {
			logger.ERR("Could not find python file at " + pythonFile);
		}
		start.ArgumentList.Add(pythonFile);
		foreach (string argument in arguments) {
            start.ArgumentList.Add(argument);
        }
		start.UseShellExecute = false;
		start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
		start.StandardOutputEncoding = Encoding.UTF8;

		using Process? process = Process.Start(start);
		using StreamReader reader = process!.StandardOutput;
		string result = reader.ReadToEnd();

		return Results.Content(result, "application/json");
	}
}