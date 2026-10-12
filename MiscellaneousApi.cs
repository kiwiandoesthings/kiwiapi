namespace KiwiApi;

using static Program;

public class MiscellaneousApi {
	private readonly Logger logger;
	private readonly List<string> folderPaths = [];
	private readonly List<string> exclusionList = [];
	private readonly bool enableMusicEndpoints = true;

	public MiscellaneousApi() {
		logger = new Logger("MISC");
		string? musicFolders = Environment.GetEnvironmentVariable("KIWIAPI_MISC_MUSIC_FOLDERS");
		string? songExclusions = Environment.GetEnvironmentVariable("KIWIAPI_MISC_MUSIC_EXCLUSIONS");

		if (musicFolders == null) {
			logger.WARN("KIWIAPI_MISC_MUSIC_FOLDERS environment variable not set. Music endpoints will be disabled");
			enableMusicEndpoints = false;
		} else {
			foreach (string folderPath in musicFolders.Split(";")[..^1]) {
				if (!Directory.Exists(folderPath)) {
					logger.WARN("Folder path \"" + folderPath + "\" defined in KIWIAPI_MISC_MUSIC_FOLDERS does not exist, ignoring");
				} else {
					folderPaths.Add(folderPath);
				}
			}
		}

		if (songExclusions == null) {
			return;
		}
        foreach (string songPath in songExclusions.Split(";")[..^1]) {
            if (!File.Exists(songPath)) {
                logger.WARN("Song path \"" + songPath + "\" defined in KIWIAPI_MISC_MUSIC_EXCLUSIONS does not exist, ignoring");
            } else {
                exclusionList.Add(songPath);
            }
        }
    }

	public void MapApiFunctions(WebApplication app) {
		app.MapGet("/hello", () => {
			return Results.Text("Heallo!", "text/plain");
		});

		app.MapGet("/randint", (int min, int max) => {
			if (min > max) {
				return BadRequest("Parameter \"min\" must be smaller than or equal to \"max\".");
			}

			return Results.Text(Random.Shared.Next(min, max + 1).ToString(), "text/plain");
		});

		if (enableMusicEndpoints) {
			app.MapGet("/randsong", (HttpContext context, string folder = "rand") => {
				string folderPath = folderPaths[Random.Shared.Next(folderPaths.Count)];
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

				return Results.Content("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, viewport-fit=cover\"><title>" + displayName + "</title></head><body style=\"background: black; color: white; display: flex; justify-content: center; align-items: center; min-height: 100dvh; margin: 0; font-family: rockwell;\"><div style=\"display: flex; flex-direction: column; text-align: center;\"><h1>" + displayName + "</h1><audio controls autoplay style=\"margin-left: auto; margin-right: auto;\" src=\"/getsong?filename=" + escapedFile + "\"></audio><img style=\"width: 640px; height: 480px;\" src=\"/getcover?filename=" + escapedFile + "\"></div></body></html>", "text/html; charset=utf-8");
			});

			app.MapGet("/getcover", async (string filename) => {
				string folderPath = "C:\\Users\\Kiwian\\Music\\";
				string fullPath = Path.Combine(folderPath, filename);
				if (!File.Exists(fullPath)) {
					return NotFound("No song with that filename was found.");
				}

				using TagLib.File file = TagLib.File.Create(fullPath);
				TagLib.IPicture? firstPicture = file.Tag.Pictures.FirstOrDefault();
				if (firstPicture != null) {
					byte[] imageData = firstPicture.Data.Data;
					return Results.Bytes(imageData, firstPicture.MimeType);
				}

				return ServerError("The selected song did not contain a cover image to extract.");
			});

			app.MapGet("/getsong", async (string filename) => {
				string fullPath = Path.Combine("C:\\Users\\Kiwian\\Music\\", filename);
				if (!File.Exists(fullPath)) {
					return NotFound("No song with that filename was found.");
				}

				return Results.File(fullPath, "audio/mpeg");
			});
		}

		app.MapGet("/randstuck", async (int act = -1) => {
			string path = "C:\\Users\\Kiwian\\Downloads\\assets\\Asset_Pack\\storyfiles\\hs2";
			string[] files = Directory.GetFiles(path, "*.gif");

			int pageNum = Random.Shared.Next(0, files.Length);

			string file = files[pageNum];

			return Results.File(file, "image/gif");
		});
	}
}