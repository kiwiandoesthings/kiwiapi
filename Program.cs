namespace kiwiapi;

using HtmlAgilityPack;
using System.Net;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public class Program {
    private static SocketsHttpHandler? handler;
    private static HttpClient? client;
    public static SqliteConnection? database;

    public static void Main(string[] args) {
        string connectionString = "Data Source=protocall.db";
        database = new SqliteConnection(connectionString);
        database.Open();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();
        builder.Services.AddSignalR(options => {
            options.EnableDetailedErrors = true;
        });
        builder.Services.AddCors(options => {
            options.AddDefaultPolicy(policy => {
                policy.WithOrigins("http://localhost:8000", "https://api.kiwiandoesthings.place", "https://protocall.kiwiandoesthings.place", "https://kiwiandoesthings.place", "https://www.kiwiandoesthings.place", "https://www.api.kiwiandoesthings.place", "https://www.protocall.kiwiandoesthings.place");
                policy.AllowAnyMethod();
                policy.AllowAnyHeader();
                policy.AllowCredentials();
            });
        });

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseCors();
        app.MapOpenApi();

        handler = new SocketsHttpHandler() {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10
        };

        client = new HttpClient(handler);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Referer", "https://archiveofourown.org/");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        client.DefaultRequestHeaders.Add("Connection", "keep-alive");
        client.DefaultRequestHeaders.ConnectionClose = false;

        client.DefaultRequestVersion = HttpVersion.Version11;

        app.MapGet("/hello", () => "Hello World!");

        app.MapGet("/randint", (int min, int max) => {
            if (min > max) {
                return "Invalid parameters";
            }
            return Random.Shared.Next(min, max + 1).ToString();
        });

        app.MapGet("/randsong", (HttpContext context, string folder = "rand") => {
            Console.WriteLine("Randsong from " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());
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

        app.MapGet("/getao3storyid", async (HttpContext context, string storyTitle) => {
            Console.WriteLine("Getao3storyid from " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());
            string searchUrl = "https://archiveofourown.org/works/search?work_search%5Bquery%5D=&work_search%5Btitle%5D=" + Uri.EscapeDataString(storyTitle) + "&work_search%5Bcreators%5D=&work_search%5Brevised_at%5D=&work_search%5Bcomplete%5D=&work_search%5Bcrossover%5D=&work_search%5Bsingle_chapter%5D=0&work_search%5Bword_count%5D=&work_search%5Blanguage_id%5D=&work_search%5Bfandom_names%5D=&work_search%5Brating_ids%5D=&work_search%5Bcharacter_names%5D=&work_search%5Brelationship_names%5D=&work_search%5Bfreeform_names%5D=&work_search%5Bhits%5D=&work_search%5Bkudos_count%5D=&work_search%5Bcomments_count%5D=&work_search%5Bbookmarks_count%5D=&work_search%5Bsort_column%5D=_score&work_search%5Bsort_direction%5D=desc&commit=Search";

            HtmlNodeCollection workNodes = null;
            for (int i = 0; i < 10; i++) {
                Console.WriteLine("Requesting results for \"" + storyTitle + "\" for the " + (i + 1) + " try");
                HttpResponseMessage response = await client.GetAsync(searchUrl);
                Console.WriteLine("Got response");
                string html = await response.Content.ReadAsStringAsync();
                Console.WriteLine("Got response HTML string");
                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(html);
                Console.WriteLine("Loaded HTML string into HTML context");
                workNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'work')]");

                if (workNodes != null) {
                    break;
                }
                Console.WriteLine("Found empty response");
                await Task.Delay(1500);
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

        app.MapGet("/getao3text", async (HttpContext context, int storyID, int page) => {
            Console.WriteLine("Getao3text from " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());
            string url = "https://archiveofourown.org/works/" + storyID + "/chapters/";

            string navUrl = "https://archiveofourown.org/works/" + storyID + "/navigate";
            HttpResponseMessage navResponse = await client.GetAsync(navUrl);
            string navHtml = await navResponse.Content.ReadAsStringAsync();
            HtmlDocument navDoc = new HtmlDocument();
            navDoc.LoadHtml(navHtml);

            HtmlNode contentNode = null;
            string title = "";
            for (int i = 0; i < 10; i++) {
                Console.WriteLine("Trying to get story data for story with id " + storyID + " at chapter " + page + " for the " + (i + 1) + " try");

                HtmlNodeCollection chapterLinks = navDoc.DocumentNode.SelectNodes("//ol[contains(@class,'index')]//li/a");

                string finalUrl = "";

                if (chapterLinks != null && page > 0 && page <= chapterLinks.Count) {
                    finalUrl = "https://archiveofourown.org" + chapterLinks[page - 1].GetAttributeValue("href", "") + "?view_adult=true";
                } else {
                    finalUrl = "https://archiveofourown.org/works/" + storyID + "?view_adult=true";
                }

                HttpResponseMessage response = await client.GetAsync(finalUrl);
                string html = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) {
                    continue;
                }
                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(html);

                title = WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//h2[@class='title heading']")?.InnerText?.Trim() ?? "Unknown");

                contentNode = doc.DocumentNode.SelectSingleNode("//div[@id='workskin']//div[contains(@class, 'userstuff')]");

                if (contentNode != null) {
                    break;
                }
                await Task.Delay(1500);
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

        app.MapGet("/randstuck", async (int act = -1) => {
            string path = "C:\\Users\\Kiwian\\Downloads\\assets\\Asset_Pack\\storyfiles\\hs2";
            string[] files = Directory.GetFiles(path);

            int pageNum = Random.Shared.Next(0, files.Length);
            Console.WriteLine(pageNum);

            string file = files[pageNum];

            return Results.File(file, "image/gif");
        });
        
        app.MapGet("/request_registerAccount", async (string username, string password, string color, string info) => {
            if (username == "" || password == "" || color == "" || username.ToLower() == "system" || username.ToLower() == "unknown user" || color == "000000" || !ValidString(username) || !ValidString(password) || !ValidHex(color)) {
                return Results.Text("-1");
            }

            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT username FROM users WHERE username = $username";
            queryCommand.Parameters.AddWithValue("$username", username);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                queryCommand.Dispose();
                reader.Dispose();
                return Results.Text("-1");
            }
            queryCommand.Dispose();
            reader.Dispose();

            Console.WriteLine(info);
            SqliteCommand registerCommand = database!.CreateCommand();
            registerCommand.CommandText = "INSERT INTO users (user_id, username, color, password, secret, info) VALUES ($userID, $username, $userColor, $userPassword, $userSecret, $info);";
            Guid userID = Guid.NewGuid();
            registerCommand.Parameters.AddWithValue("$userID", userID.ToString());
            registerCommand.Parameters.AddWithValue("$username", username);
            registerCommand.Parameters.AddWithValue("$userColor", color);
            registerCommand.Parameters.AddWithValue("$userPassword", password);
            byte[] inputBytes = Encoding.UTF8.GetBytes(password + username + color);
            byte[] hashBytes = SHA256.HashData(inputBytes);
            string userSecret = Convert.ToBase64String(hashBytes);
            registerCommand.Parameters.AddWithValue("$userSecret", userSecret);
            registerCommand.Parameters.AddWithValue("$info", info);
            registerCommand.ExecuteNonQuery();
            registerCommand.Dispose();

            return Results.Ok(new {
                userID = userID.ToString(),
                userSecret = userSecret
            });
        });

        app.MapGet("/request_loginInfo", async (string username, string password) => {
            Console.WriteLine("Attempted login with username: " + username + " and password: " + password);

            if (username == "" || password == "") {
                Console.WriteLine("Invalid info");
                return Results.Text("-1");
            }

            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT user_id, secret FROM users WHERE username = $username AND password = $password";
            queryCommand.Parameters.AddWithValue("$username", username);
            queryCommand.Parameters.AddWithValue("$password", password);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            object? result = null;
            while (await reader.ReadAsync()) {
                result = new {
                    userID = reader.GetString(0),
                    userSecret = reader.GetString(1)
                };
            }

            reader.Dispose();
            queryCommand.Dispose();

            return result != null ? Results.Ok(result) : Results.Text("-1");
        });

        app.MapGet("/request_userInfo", async (string userID) => {
            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT username, color, created_at FROM users WHERE user_id = $userID";
            queryCommand.Parameters.AddWithValue("$userID", userID);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            object? result = null;
            while (await reader.ReadAsync()) {
                result = new {
                    userUsername = reader.GetString(0),
                    userColor = reader.GetString(1),
                    createdAt = reader.GetString(2)
                };
            }

            reader.Dispose();
            queryCommand.Dispose();

            return result != null ? Results.Ok(result) : Results.Text("-1");
        });

        app.MapGet("/request_roomID", async (string roomName) => {
            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT id FROM rooms WHERE name = $roomName";
            queryCommand.Parameters.AddWithValue("$roomName", roomName);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            object? result = null;
            if (await reader.ReadAsync()) {
                result = new {
                    roomID = reader.GetInt32(0)
                };
            }
            reader.Dispose();
            queryCommand.Dispose();

            return result == null ? Results.Text("-1") : Results.Ok(result);
        });

        app.MapGet("/request_roomInfo", async (int roomID) => {
            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT name, author_id, privacy, created_at FROM rooms WHERE id = $roomID";
            queryCommand.Parameters.AddWithValue("$roomID", roomID);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            object? result = null;
            if (await reader.ReadAsync()) {
                result = new {
                    roomName = reader.GetString(0),
                    authorID = reader.GetString(1),
                    privacy = reader.GetString(2),
                    createdAt = reader.GetString(3)
                };
            }
            reader.Dispose();
            queryCommand.Dispose();

            return result == null ? Results.Text("-1") : Results.Ok(result);
        });

        app.MapGet("/request_accessLevel", async (string userID, int roomID) => {
            return Results.Ok(new {
                accessLevel = ProtoCall.UserAccessLevelInRoom(userID, roomID)
            });
        });

        app.MapGet("/request_deviceInfo", (HttpContext context) => {
            string? ip = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();

            if (string.IsNullOrEmpty(ip)) {
                ip = context.Connection.RemoteIpAddress?.ToString();
            }

            return Results.Ok(new {
                id = ip + " ||| " + context.Request.Headers.UserAgent.ToString()
            });
        });

        app.MapHub<ProtoCall>("/protocall");

        app.MapControllers();

        app.Run();
    }

    public static bool ValidString(string toCheck) {
        return Regex.IsMatch(toCheck, @"^[a-zA-Z0-9\-_]+$") && toCheck.Length <= 18;
    }

    public static bool ValidHex(string toCheck) {
        return Regex.IsMatch(toCheck, @"^#?([A-Fa-f0-9]{3}|[A-Fa-f0-9]{6})$");
    }
}