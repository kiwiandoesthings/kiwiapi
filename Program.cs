namespace kiwiapi;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

public class Program {
    private static SocketsHttpHandler? handler;
    private static HttpClient? client;
    public static SqliteConnection? database;
    private record RoomResult(string roomName, int roomID);

    public static void Main(string[] args) {
        string connectionString = "Data Source=" + "C:\\Users\\Kiwian\\Downloads\\Github Repos\\kiwiapi\\protocall.db";
        database = new SqliteConnection(connectionString);
        database.Open();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://localhost:5201", "https://localhost:7164");
        builder.Services.AddControllers();
        builder.Services.AddOpenApi();
        builder.Services.AddSignalR(options => {
            options.EnableDetailedErrors = true;
        });
        builder.Services.AddCors(options => {
            options.AddDefaultPolicy(policy => {
                policy.WithOrigins("http://localhost:8000", "https://localhost:8000", "https://api.kiwiandoesthings.place", "https://protocall.kiwiandoesthings.place", "https://kiwiandoesthings.place", "https://www.kiwiandoesthings.place", "https://www.api.kiwiandoesthings.place", "https://www.protocall.kiwiandoesthings.place");
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
            MaxConnectionsPerServer = 10,
            ConnectCallback = async (context, cancellationToken) => {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork);
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, true);
            }
        };

        client = new HttpClient(handler);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
        client.DefaultRequestHeaders.Add("Dnt", "1");
        client.DefaultRequestHeaders.Add("Priority", "u=0, i");
        client.DefaultRequestHeaders.Add("Referer", "https://archiveofourown.org/");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Chromium\";v=\"146\", \"Not-A.Brand\";v=\"24\", \"Google Chrome\";v=\"146\"");
		client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
		client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
		client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
		client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
		client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
		client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.ConnectionClose = false;

        client.DefaultRequestVersion = HttpVersion.Version11;

        app.MapGet("/hello", () => "Hello World!");

        app.MapGet("/randint", (int min, int max) => {
            if (min > max) {
                return "Invalid parameters.";
            }
            return Random.Shared.Next(min, max + 1).ToString();
        });

        app.MapGet("/randsong", (HttpContext context, string folder = "rand") => {
            Console.WriteLine("API | Randsong from " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());
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
                        throw new ArgumentException("Parameter \"folder\" must be either \"miku\", \"lyrical\", \"instrumental\", or not passed to choose a random folder.");
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
			Console.WriteLine("API | Randsong returning " + finalName);

			return Results.Content("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, viewport-fit=cover\"><title>" + displayName + "</title></head><body style=\"background: black; color: white; display: flex; justify-content: center; align-items: center; min-height: 100dvh; margin: 0; font-family: rockwell;\"><div style=\"display: flex; flex-direction: column; text-align: center;\"><h1>" + displayName + "</h1><audio controls autoplay style=\"margin-left: auto; margin-right: auto;\" src=\"/getsong?filename=" + escapedFile + "\"></audio><img style=\"width: 640px; height: 480px;\" src=\"/getcover?filename=" + escapedFile + "\"></div></body></html>", "text/html; charset=utf-8");
        });

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

        app.MapGet("/randstuck", async (int act = -1) => {
            string path = "C:\\Users\\Kiwian\\Downloads\\assets\\Asset_Pack\\storyfiles\\hs2";
            string[] files = Directory.GetFiles(path, "*.gif");

            int pageNum = Random.Shared.Next(0, files.Length);

            string file = files[pageNum];

            return Results.File(file, "image/gif");
        });
        
        app.MapPost("/push_registerAccount", async (HttpRequest request, string username, string password, string color, string info) => {
            Console.WriteLine("PTC | Attempting registration with username \"" + username + "\", password \"" + password + "\", and color \"" + color + "\"");
			Console.WriteLine("PTC | From " + request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + request.Headers.UserAgent.ToString());

			bool filledOut = username != "" && password != "" && color != "";
            bool validInformation = ValidString(username) && ValidString(password)  &ValidHex(color);
            bool validUsernameAndColor = username.ToLower() != "system" && username.ToLower() != "unknown user" && color != "000000";

            string errorMessage = "";
            if (!filledOut) {
                errorMessage += "Login information is incomplete. ";
            }
            if (!validInformation) {
                errorMessage += "Login information is invalid. ";
            }
            if (!validUsernameAndColor) {
                errorMessage += "Username and/or color are using restricted values (username cannot be \"System\" or \"Unknown User\" and color cannot be black).";
            }
            if (errorMessage != "") {
                return Results.BadRequest(errorMessage);
            }

            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT username FROM users WHERE username = $username";
            queryCommand.Parameters.AddWithValue("$username", username);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                queryCommand.Dispose();
                reader.Dispose();
                return Results.BadRequest("There is already a user with that username.");
            }
            queryCommand.Dispose();
            reader.Dispose();

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

        app.MapGet("/request_loginInfo", async (HttpContext context, string username, string password) => {
            Console.WriteLine("PTC | Attempted login with username: " + username + " and password: " + password);
			Console.WriteLine("PTC | From " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());

			SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT user_id, secret FROM users WHERE username = $username AND password = $password";
            queryCommand.Parameters.AddWithValue("$username", username);
            queryCommand.Parameters.AddWithValue("$password", password);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            object? result = null;
            if (await reader.ReadAsync()) {
                string userID = reader.GetString(0);
                string userSecret = reader.GetString(1);

                AppendUserLoginfo(context, userID, userSecret);
                result = new {
                    userID = userID
                };
            }

            reader.Dispose();
            queryCommand.Dispose();

            return result != null ? Results.Ok(result) : Results.NotFound("No user with that login information was found");
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

            return result != null ? Results.Ok(result) : Results.NotFound("No user with that ID was found");
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

            return result != null ? Results.Ok(result) : Results.NotFound("No room with that name was found");
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

            return result != null ? Results.Ok(result) : Results.NotFound("No room with that ID was found");
        });

        app.MapGet("/request_roomSearch", async (HttpContext context, string targetName) => {
            if (GetUserInfo(context, out string userID, out string userSecret) == -1) {
                return Results.BadRequest();
            }

			bool error = await GoodSecret(userID, userSecret);
			if (error) {
				return LogBadUserSecret(userID, userSecret);
			}

			SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT id, name FROM rooms LEFT JOIN roomAccess ON id = room_id AND user_id = $userID WHERE name LIKE $targetName AND name != 'HomeRoom' AND (privacy = 'PUBLIC' OR (privacy = 'PRIVATE' AND access_level >= 0))";
            queryCommand.Parameters.AddWithValue("$targetName", "%" + targetName + "%");
            queryCommand.Parameters.AddWithValue("$userID", userID);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            List<RoomResult> results = new();
            while (await reader.ReadAsync()) {
                results.Add(new RoomResult(reader.GetString(1), reader.GetInt32(0)));
            }
            reader.Dispose();
            queryCommand.Dispose();

            return Results.Ok(results);
        });

        app.MapPost("/push_deleteAccount", async (HttpContext context) => {
            if (GetUserInfo(context, out string userID, out string userSecret) == -1) {
                return Results.BadRequest();
            }

            Console.WriteLine("PTC | Attempting to delete account with userID \"" + userID + "\", and userSecret \"" + userSecret + "\"");
			Console.WriteLine("PTC | From " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());

			bool error = await GoodSecret(userID, userSecret);
			if (error) {
                return LogBadUserSecret(userID, userSecret);
			}

			SqliteCommand deleteCommand = database!.CreateCommand();
			deleteCommand.CommandText = "DELETE FROM users WHERE user_id = $userID";
			deleteCommand.Parameters.AddWithValue("$userID", userID);
			deleteCommand.ExecuteNonQuery();
			deleteCommand.Dispose();

            AppendUserLoginfo(context, "", "");

            return Results.Ok();
		});

        app.MapPost("/push_createRoomPersonal", async (HttpContext context, string otherID) => {
            if (GetUserInfo(context, out string authorID, out string authorSecret) == -1) {
                return Results.BadRequest();
            }

            bool error = await GoodSecret(authorID, authorSecret);
			if (error) {
				return LogBadUserSecret(authorID, authorSecret);
			}

            int roomID = CreateRoom(authorID + " " + otherID, authorID);

            return Results.Ok(new {
				roomID = roomID,
            });
		});

        app.MapPost("/push_setRoomPrivacy", async (HttpContext context, int roomID, string newPrivacy) => {
            if (GetUserInfo(context, out string userID, out string userSecret) == -1) {
                return Results.BadRequest();
            }

            bool error = await GoodSecret(userID, userSecret);
			if (error) {
				return LogBadUserSecret(userID, userSecret);
			}

			if (await UserAccessLevelInRoom(userID, roomID) < 2) {
				return LogBadUserSecret(userID, userSecret);
			}

			newPrivacy = newPrivacy.ToLower();
			if (newPrivacy != "public" && newPrivacy != "private") {
				return LogBadUserSecret(userID, userSecret);
			}

			SqliteCommand roomCommand = Program.database!.CreateCommand();
			roomCommand.CommandText = "UPDATE rooms SET privacy = $newPrivacy WHERE id = $roomID";
			roomCommand.Parameters.AddWithValue("$newPrivacy", newPrivacy.ToUpper());
			roomCommand.Parameters.AddWithValue("$roomID", roomID);
			roomCommand.ExecuteNonQuery();
			roomCommand.Dispose();

            return Results.Ok();
		});

        app.MapGet("/request_accessLevel", async (string userID, int roomID) => {
            return Results.Ok(new {
                accessLevel = UserAccessLevelInRoom(userID, roomID)
            });
        });

        app.MapGet("/request_usersWithAccessLevel", async (int accessLevel, int roomID) => {
            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT user_id FROM roomAccess WHERE room_id = $roomID AND access_level = $accessLevel";
            queryCommand.Parameters.AddWithValue("$roomID", roomID);
            queryCommand.Parameters.AddWithValue("$accessLevel", accessLevel);
            SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            List<string> results = new();
            while (await reader.ReadAsync()) {
                results.Add(reader.GetString(0));
            }
            reader.Dispose();
            queryCommand.Dispose();

            return Results.Ok(results);
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

	public static IResult GetSong(string filename) {
		return Results.File(Path.Combine("C:\\Users\\Kiwian\\Music\\", filename), "audio/mpeg");
	}

	public static IResult GetCover(string filename) {
		string folderPath = "C:\\Users\\Kiwian\\Music\\";
		string fullPath = Path.Combine(folderPath, filename);

		TagLib.File file = TagLib.File.Create(fullPath);
		TagLib.IPicture? firstPicture = file.Tag.Pictures.FirstOrDefault();
		file.Dispose();
		if (firstPicture != null) {
			byte[] imageData = firstPicture.Data.Data;
			return Results.Bytes(imageData, firstPicture.MimeType);
		}

		return Results.NotFound();
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

    public static int CreateRoom(string roomName, string ownerID) {
		SqliteCommand roomCommand = database!.CreateCommand();
		roomCommand.CommandText = "INSERT INTO rooms (name, author_id) VALUES ($roomName, $authorID); SELECT last_insert_rowid();";
		roomCommand.Parameters.AddWithValue("$roomName", roomName);
		roomCommand.Parameters.AddWithValue("$authorID", ownerID);
		int roomID = (int)(long)roomCommand.ExecuteScalar()!;
		roomCommand.Dispose();

		SqliteCommand accessCommand = database!.CreateCommand();
		accessCommand.CommandText = "INSERT INTO roomAccess (room_id, user_id, access_level) VALUES ($roomID, $userID, $accessLevel)";
		accessCommand.Parameters.AddWithValue("$roomID", roomID);
		accessCommand.Parameters.AddWithValue("$userID", ownerID);
		accessCommand.Parameters.AddWithValue("$accessLevel", 2);
		accessCommand.ExecuteNonQuery();
		accessCommand.Dispose();

		return roomID;
	}

	public static async Task<bool> GoodSecret(string userID, string userSecret) {
		string realSecret = await GetUserSecret(userID);
		if (realSecret != ProcessSecret(userSecret)) {
			Console.WriteLine("API | Passed secret did not match real secret of " + realSecret);
			return true;
		}

		return false;
	}

	public static async Task<bool> VerifyRequest(ISingleClientProxy client, string userID, string userSecret, string requestType) {
        string trimmedSecret = ProcessSecret(userSecret);
		string realSecret = await GetUserSecret(userID);
		if (realSecret != trimmedSecret) {
			Console.WriteLine("WSS | Passed secret \"" + trimmedSecret + "\" did not match real secret of " + realSecret);
			await client.SendAsync("push_serverMessage", "Server could not authenticate your " + requestType + ", please clear your cookies and log in again");
			return true;
		}

		return false;
	}

    public static string ProcessSecret(string originalSecret) {
        return WebUtility.UrlDecode(new string(originalSecret.TrimEnd("=")));
    }

	public static bool ValidString(string toCheck) {
		return Regex.IsMatch(toCheck, @"^[a-zA-Z0-9\-_]+$") && toCheck.Length <= 18;
	}

	public static bool ValidHex(string toCheck) {
		return Regex.IsMatch(toCheck, @"^#?([A-Fa-f0-9]{3}|[A-Fa-f0-9]{6})$");
	}

	public static async Task<string> GetUserSecret(string userID) {
		SqliteCommand getCommand = database!.CreateCommand();
		getCommand.CommandText = "SELECT secret FROM users WHERE user_id = $userID LIMIT 1";
		getCommand.Parameters.AddWithValue("$userID", userID);
		object? result = await getCommand.ExecuteScalarAsync()!;
		getCommand.Dispose();

		if (result != null && result != DBNull.Value) {
			getCommand.Dispose();
			return result.ToString()!.TrimEnd("=").ToString();
		}

		return "";
	}

	public static async Task<int> UserAccessLevelInRoom(string userID, int roomID) {
		if (roomID == 0) {
			if (userID == "7f718957-5509-42a0-a18c-428989b3697a") {
				return 2;
			}
			return 0;
		}

		SqliteCommand getCommand = Program.database!.CreateCommand();
		getCommand.CommandText = "SELECT access_level FROM roomAccess WHERE user_id = $userID AND room_id = $roomID";
		getCommand.Parameters.AddWithValue("$userID", userID);
		getCommand.Parameters.AddWithValue("$roomID", roomID);
		object? result = await getCommand.ExecuteScalarAsync()!;
		getCommand.Dispose();

		if (result != null && result != DBNull.Value) {
			getCommand.Dispose();
			return (int)(long)result;
		}

		return -1;
	}

    public static IResult LogBadUserSecret(string userID, string userSecret) {
        Console.WriteLine("PTC | Request with bad secret using userID \"" + userID + "\", and userSecret \"" + userSecret + "\"");
        return Results.Unauthorized();
    }

    public static int GetUserInfo(HttpContext context, out string userID, out string userSecret) {
        userID = context.Request.Cookies["userID"]!;
        userSecret = context.Request.Cookies["userSecret"]!;

        if (string.IsNullOrEmpty(userID) || string.IsNullOrEmpty(userSecret)) {
            return -1;
        }
        return 0;
    }

    public static void AppendUserLoginfo(HttpContext context, string userID, string userSecret) {
        string? origin = context.Request.Headers.Origin.FirstOrDefault();
        string? referer = context.Request.Headers.Referer.FirstOrDefault();
        bool isLocalhost = (origin != null && origin.Contains("localhost")) || (referer != null && referer.Contains("localhost"));
        CookieOptions secretCookieOptions = new CookieOptions {
            HttpOnly = true,
            Domain = isLocalhost ? null : ".kiwiandoesthings.place",
            SameSite = isLocalhost ? SameSiteMode.Lax : SameSiteMode.Strict,
            Secure = !isLocalhost,
            Expires = DateTimeOffset.UtcNow.AddDays(365),
            Path = "/"
        };
        CookieOptions normalCookieOptions = new CookieOptions {
            HttpOnly = false,
            Domain = isLocalhost ? null : ".kiwiandoesthings.place",
            SameSite = isLocalhost ? SameSiteMode.Lax : SameSiteMode.Strict,
            Secure = !isLocalhost,
            Expires = DateTimeOffset.UtcNow.AddDays(365),
            Path = "/"
        };
        context.Response.Cookies.Append("userSecret", userSecret, secretCookieOptions);
        context.Response.Cookies.Append("userID", userID, normalCookieOptions);
    }
}