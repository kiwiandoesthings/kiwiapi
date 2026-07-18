namespace kiwiapi;

using System.Diagnostics;
using System.Text;

public class Ao3Api {
	public void MapApiFunctions(WebApplication app) {
		app.MapGet("/getao3storyid", async (HttpContext context, string storyTitle, int page) => {
			Console.WriteLine("AO3 | Getting story ID from searching \"" + storyTitle + "\" on page " + page);
			Console.WriteLine("AO3 | From " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());
			return GetAo3ApiResponse("search", [storyTitle, page.ToString()]);
		});

		app.MapGet("/getao3text", async (HttpContext context, int storyID, int page) => {
			Console.WriteLine("AO3 | Getting text from storyID " + storyID + " on chapter " + page);
			Console.WriteLine("AO3 | From " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());
			return GetAo3ApiResponse("text", [storyID.ToString(), page.ToString()]);
		});
	}

	public static IResult GetAo3ApiResponse(string file, string[] args) {
		ProcessStartInfo start = new ProcessStartInfo();
		start.FileName = "python";
		string arguments = "\"C:\\Users\\Kiwian\\Downloads\\Github Repos\\kiwiapi\\" + file + ".py\" ";
		foreach (string argument in args) {
			arguments += "\"" + argument + "\" ";
		}
		start.Arguments = arguments;
		start.UseShellExecute = false;
		start.RedirectStandardOutput = true;
		start.CreateNoWindow = true;
		start.StandardOutputEncoding = Encoding.UTF8;

		using (Process? process = Process.Start(start)) {
			using (StreamReader reader = process!.StandardOutput) {
				string result = reader.ReadToEnd();
				return Results.Content(result, "application/json");
			}
		}
	}
}