using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using System.Net;

public class Program {
    private static HtmlWeb web;
    private static HttpClientHandler handler;
    private static HttpClient client;

    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        var app = builder.Build();
        if (app.Environment.IsDevelopment()) {
            app.MapOpenApi();
        }

        web = new HtmlWeb();
        web.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        handler = new HttpClientHandler() {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/122.0.0.0");

        app.MapGet("/hello", () => "Hello World!");

        app.MapGet("/randint", (int min, int max) => {
            if (min > max) {
                return "Invalid parameters";
            }
            return Random.Shared.Next(min, max + 1).ToString();
        });

        app.MapGet("/randsong", (HttpContext context, string folder = "rand") => {
            string[] folderPaths = {
                "C:\\Users\\Kiwian\\Music\\“Real” music I guess",
                "C:\\Users\\Kiwian\\Music\\Kiwian's Listening",
                "C:\\Users\\Kiwian\\Music\\Miku Music _3"
            };
            string folderPath = folderPaths[Random.Shared.Next(folderPaths.Length)];
            if (folder != "rand") {
                switch (folder) {
                    case "miku":
                        folderPath = folderPaths[2];
                        break;
                    case "lyrical":
                        folderPath = folderPaths[0];
                        break;
                    case "instrumental":
                        folderPath = folderPaths[1];
                        break;
                    default:
                        throw new ArgumentException("Parameter \"folder\" must be either \"miku\", \"lyrical\", \"instrumental\", or not passed to choose a random folder");
                }
            }

            string[] files = Directory.GetFiles(folderPath, "*.mp3");
            string[] exclusionList = {
                "C:\\Users\\Kiwian\\Music\\“Real” music I guess\\ITTY BITTY TITTY COMMITTEE.mp3",
                "C:\\Users\\Kiwian\\Music\\“Real” music I guess\\TOMBOY TUESDAY!.mp3"
            }; // Exclude the more vulgar songs

            string randomFile = files[Random.Shared.Next(files.Length)];
            while (exclusionList.Contains(randomFile)) {
                randomFile = files[Random.Shared.Next(files.Length)];
            }
            string displayName = Path.GetFileNameWithoutExtension(randomFile);
            string finalName = Path.GetFileName(Path.GetDirectoryName(randomFile)) + "\\" + Path.GetFileName(randomFile);

            string escapedFile = Uri.EscapeDataString(finalName);

            return Results.Content("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, viewport-fit=cover\"><title>" + displayName + "</title></head><body style=\"background: black; color: white; display: flex; justify-content: center; align-items: center; min-height: 100dvh; margin: 0; font-family: rockwell;\"><div style=\"display: flex; flex-direction: column; text-align: center;\"><h1>" + displayName + "</h1><audio controls autoplay style=\"margin-left: auto; margin-right: auto;\" src=\"/getsong?filename=" + escapedFile + "\"></audio><img style=\"width: 640px; height: 480px;\" src=\"/getcover?filename=" + escapedFile + "\"></div></body></html>", "text/html; charset=utf-8");
        });

        app.MapGet("/getsong", (string filename) => {
            return Results.File(Path.Combine("C:\\Users\\Kiwian\\Music\\", filename), "audio/mpeg");
        });

        app.MapGet("/getcover", (string filename) => {
            string folderPath = "C:\\Users\\Kiwian\\Music\\";
            string fullPath = Path.Combine(folderPath, filename);

            TagLib.File file = TagLib.File.Create(fullPath);
            TagLib.IPicture? firstPicture = file.Tag.Pictures.FirstOrDefault();
            file.Dispose();
            if (firstPicture != null) {
                byte[] pData = firstPicture.Data.Data;
                return Results.Bytes(pData, firstPicture.MimeType);
            }

            return Results.NotFound();
        });

        app.MapGet("/getao3storyid", async (string storyTitle) => {
            string searchUrl = "https://archiveofourown.org/works/search?work_search[title]=" + Uri.EscapeDataString(storyTitle);

            HtmlNodeCollection workNodes = null;
            for (int i = 0; i < 10; i++) {
                Console.WriteLine("Requesting results for \"" + storyTitle + "\" for the " + i + " try");
                HttpResponseMessage response = await client.GetAsync(searchUrl);
                string html = await response.Content.ReadAsStringAsync();
                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(html);
                workNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'work')]");

                if (workNodes != null) {
                    break;
                }
            }

            if (workNodes == null) {
                return Results.BadRequest("Failed to search for stories 10 times. If the search continues to fail, please contact @KiwianDoesThings on Discord.");
            }

            Dictionary<string, string> stories = new();

            foreach (HtmlNode work in workNodes) {
                HtmlNode titleNode = work.SelectSingleNode(".//h4[@class='heading']/a[contains(@href, '/works/')]");
                HtmlNode authorNode = work.SelectSingleNode(".//h4[@class='heading']/a[contains(@href, '/users/')]");

                string title = WebUtility.HtmlDecode(titleNode?.InnerText.Trim()) ?? "Unknown Title";
                string author = WebUtility.HtmlDecode(authorNode?.InnerText.Trim()) ?? "Anonymous";
                string id = titleNode?.GetAttributeValue("href", "").Replace("/works/", "") ?? "0";

                if (!stories.ContainsKey(id)) {
                    stories.Add(id, title + ", by " + author);
                }
            }

            return Results.Json(stories);
        });

        app.MapGet("/getao3text", async (int storyID, int page) => {
            string url = "https://archiveofourown.org/works/" + storyID + "/chapters/";

            string navUrl = "https://archiveofourown.org/works/" + storyID + "/navigate";

            HtmlNode contentNode = null;
            string title = "";
            for (int i = 0; i < 10; i++) {
                Console.WriteLine("Trying to get story data for story with id " + storyID + " at chapter " + page + " for the " + i + " try");
                HttpResponseMessage navResponse = await client.GetAsync(navUrl);
                string navHtml = await navResponse.Content.ReadAsStringAsync();
                HtmlDocument navDoc = new HtmlDocument();
                navDoc.LoadHtml(navHtml);

                HtmlNode chapterLink = navDoc.DocumentNode.SelectSingleNode("//ol[@class='index group']//li[" + page + "]/a");

                string finalUrl = (page == 1 || chapterLink == null) ? "https://archiveofourown.org/works/" + storyID + "?view_adult=true" : "https://archiveofourown.org" + chapterLink.GetAttributeValue("href", "") + "?view_adult=true";

                HttpResponseMessage response = await client.GetAsync(finalUrl);
                string html = await response.Content.ReadAsStringAsync();
                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(html);

                title = WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//h2[@class='title heading']")?.InnerText?.Trim() ?? "Unknown");

                contentNode = doc.DocumentNode.SelectSingleNode("//div[@id='workskin']//div[contains(@class, 'userstuff')]");

                if (contentNode != null) {
                    break;
                }
            }

            if (contentNode == null) {
                return Results.NotFound("Failed to get story content after 10 tries. If the issue persists, please contact @KiwianDoesThings on Discord");
            }

            return Results.Json(new {
                Title = title,
                Chapter = page,
                Content = contentNode.InnerHtml
            });
        });

        app.MapControllers();

        app.Run();
    }
}