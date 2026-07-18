namespace kiwiapi;

public class MiscellaneousApi {
	static readonly string[] folderPaths = {
		"C:\\Users\\Kiwian\\Music\\“Real” music I guess",
		"C:\\Users\\Kiwian\\Music\\Kiwian's Listening",
		"C:\\Users\\Kiwian\\Music\\Miku Music _3"
	};
	static readonly string[] exclusionList = {
		"C:\\Users\\Kiwian\\Music\\“Real” music I guess\\ITTY BITTY TITTY COMMITTEE.mp3",
		"C:\\Users\\Kiwian\\Music\\“Real” music I guess\\TOMBOY TUESDAY!.mp3"
	};

	public void MapApiFunctions(WebApplication app) {
		app.MapGet("/hello", () => {
			return Results.Text("Heallo!", "text/plain");
		});

		app.MapGet("/randint", (int min, int max) => {
			if (min > max) {
				return Results.BadRequest("Parameter \"min\" must be smaller than or equal to \"max\".");
			}

			return Results.Text(Random.Shared.Next(min, max + 1).ToString(), "text/plain");
		});

		app.MapGet("/randsong", (HttpContext context, string folder = "rand") => {
			Console.WriteLine("API | Randsong from " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());
			
			string folderPath = folderPaths[Random.Shared.Next(folderPaths.Length)];
			if (folder != "rand") {
				switch (folder) {
					case "vocaloid":
						folderPath = folderPaths[2];
						break;
					case "lyrical":
						folderPath = folderPaths[0];
						break;
					case "instrumental":
						folderPath = folderPaths[1];
						break;
					default:
						throw new ArgumentException("Parameter \"folder\" must be either \"vocaloid\", \"lyrical\", \"instrumental\", or not passed to choose a random folder.");
				}
			}

			string[] files = Directory.GetFiles(folderPath, "*.mp3");

			string randomFile = files[Random.Shared.Next(files.Length)];
			while (exclusionList.Contains(randomFile)) {
				randomFile = files[Random.Shared.Next(files.Length)];
			}
			string displayName = Path.GetFileNameWithoutExtension(randomFile);
			string finalName = Path.GetFileName(Path.GetDirectoryName(randomFile)) + "\\" + Path.GetFileName(randomFile);

			string escapedFile = Uri.EscapeDataString(finalName);
			Console.WriteLine("API | Randsong returning " + finalName);

			return Results.Content("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, viewport-fit=cover\"><title>" + displayName + "</title></head><body style=\"background: black; color: white; display: flex; justify-content: center; align-items: center; min-height: 100dvh; margin: 0; font-family: rockwell;\"><div style=\"display: flex; flex-direction: column; text-align: center;\"><h1>" + displayName + "</h1><audio controls autoplay style=\"margin-left: auto; margin-right: auto;\" src=\"/getsong?filename=" + escapedFile + "\"></audio><img style=\"width: 640px; height: 480px;\" src=\"/getcover?filename=" + escapedFile + "\"></div></body></html>", "text/html; charset=utf-8");
		});

		app.MapGet("/getcover", async (string filename) => {
			string folderPath = "C:\\Users\\Kiwian\\Music\\";
			string fullPath = Path.Combine(folderPath, filename);
			if (!File.Exists(fullPath)) {
				return Results.NotFound("No song with that filename was found.");
			}

			using TagLib.File file = TagLib.File.Create(fullPath);
			TagLib.IPicture? firstPicture = file.Tag.Pictures.FirstOrDefault();
			if (firstPicture != null) {
				byte[] imageData = firstPicture.Data.Data;
				return Results.Bytes(imageData, firstPicture.MimeType);
			}

			return Results.InternalServerError("The selected song did not contain a cover image to extract.");
		});

		app.MapGet("/getsong", async (string filename) => {
			string fullPath = Path.Combine("C:\\Users\\Kiwian\\Music\\", filename);
			if (!File.Exists(fullPath)) {
				return Results.NotFound("No song with that filename was found.");
			}

			return Results.File(fullPath, "audio/mpeg");
		});

		app.MapGet("/randstuck", async (int act = -1) => {
			string path = "C:\\Users\\Kiwian\\Downloads\\assets\\Asset_Pack\\storyfiles\\hs2";
			string[] files = Directory.GetFiles(path, "*.gif");

			int pageNum = Random.Shared.Next(0, files.Length);

			string file = files[pageNum];

			return Results.File(file, "image/gif");
		});
	}
}