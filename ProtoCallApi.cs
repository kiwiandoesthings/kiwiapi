namespace kiwiapi;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

using static Program;

public class ProtoCallApi : Hub {
    public record MessagesData(string authorID, string content, int messageIndex, string messageTimestamp);
    private static readonly ConcurrentDictionary<string, bool> userConnections = new();
	public static readonly string? catboxHash = Environment.GetEnvironmentVariable("CATBOX_USER_HASH");
	private record RoomResult(string roomName, int roomID);

	public override async Task OnConnectedAsync() {
        if (GetUserInfo(Context.GetHttpContext()!, out string userID, out string userSecret) == -1) {
            Context.Abort();
            return;
        }

        if (await VerifyRequest(Clients.Caller, userID, userSecret, "connection")) {
            Context.Abort();
			LogBadUserSecret(userID, userSecret);
			return;
        }

        userConnections[userID] = true;

        //Console.WriteLine("PTC | Client with user ID " + userID + " connected");

        await Clients.All.SendAsync("push_userStatus", userID, true);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception) {
        if (GetUserInfo(Context.GetHttpContext()!, out string userID, out string userSecret) == -1) {
            return;
        }

		if (await VerifyRequest(Clients.Caller, userID, userSecret, "connection")) {
			LogBadUserSecret(userID, userSecret);
			return;
		}

		userConnections[userID] = false;

		//Console.WriteLine("PTC | Client with user ID " + userID + " disconnected");

		await Clients.All.SendAsync("push_userStatus", userID, false);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task push_sendMessage(string message, string messageTimestamp, int roomID) {
        if (GetUserInfo(Context.GetHttpContext()!, out string userID, out string userSecret) == -1) {
            await Clients.Caller.SendAsync("push_serverMessage", "Your cookies are messed up. Please clear them and log in again.");
            return;
        }

		if (await VerifyRequest(Clients.Caller, userID, userSecret, "message")) {
            LogBadUserSecret(userID, userSecret);
            await Clients.Caller.SendAsync("push_serverMessage", "The server could not authenticate your message. Please clear your cookies and log in again.");
            return;
		}

		if (await UserAccessLevelInRoom(userID, roomID) < 0) {
            await Clients.Caller.SendAsync("push_serverMessage", "You do not have access to this room!");
            return;
        }

        Console.WriteLine("PTC | Got message from user with ID " + userID + " in room with ID " + roomID + " and content of \"" + message + "\"" + userSecret);

        using SqliteCommand idCommand = database!.CreateCommand();
        idCommand.CommandText = "SELECT IFNULL(MAX(local_id), 0) + 1 FROM messages WHERE room_id = $roomID";
        idCommand.Parameters.AddWithValue("$roomID", roomID);
        int localID = (int)(long)idCommand.ExecuteScalar()!;

        using SqliteCommand sendCommand = database!.CreateCommand();
        sendCommand.CommandText = "INSERT INTO messages (content, author_id, local_id, room_id, created_at) VALUES ($message, $userID, $localID, $roomID, $messageTimestamp); SELECT last_insert_rowid();";
        sendCommand.Parameters.AddWithValue("$message", message);
        sendCommand.Parameters.AddWithValue("$userID", userID);
        sendCommand.Parameters.AddWithValue("$localID", localID);
        sendCommand.Parameters.AddWithValue("$roomID", roomID);
        sendCommand.Parameters.AddWithValue("$messageTimestamp", messageTimestamp);
        int newId = (int)(long)sendCommand.ExecuteScalar()!;

        MessagesData[] messageData = {
            new MessagesData(userID, message, newId, messageTimestamp)
        };
        await Clients.All.SendAsync("push_recieveMessages", messageData);
    }

    public async Task push_messageRequest(int messageIndex, int messageCount, int roomID) {
        if (GetUserInfo(Context.GetHttpContext()!, out string userID, out string userSecret) == -1) {
            return;
        }

		if (await VerifyRequest(Clients.Caller, userID, userSecret, "request")) {
			LogBadUserSecret(userID, userSecret);
			return;
		}

		if (await UserAccessLevelInRoom(userID, roomID) < 0) {
            await Clients.Caller.SendAsync("push_serverMessage", "You do not have access to this room!");
            return;
        }

        if (messageIndex == -1) {
            using SqliteCommand latestIDCommand = database!.CreateCommand();
            latestIDCommand.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM Messages";
            messageIndex = (int)(long)latestIDCommand.ExecuteScalar()!;
        }

        List<MessagesData> messages = new();

        using SqliteCommand getCommand = database!.CreateCommand();
        getCommand.CommandText = "SELECT local_id, content, author_id, created_at FROM (SELECT local_id, content, author_id, created_at FROM messages WHERE local_id <= $messageIndex AND room_id = $roomID ORDER BY local_id DESC LIMIT $amount) ORDER BY local_id ASC";
        getCommand.Parameters.AddWithValue("$messageIndex", messageIndex);
        getCommand.Parameters.AddWithValue("$amount", messageCount);
        getCommand.Parameters.AddWithValue("$roomID", roomID);

        using SqliteDataReader reader = await getCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            messages.Add(new MessagesData(
                reader.GetString(2),
                reader.GetString(1),
                reader.GetInt32(0),
                reader.GetString(3)
            ));

        }
        await Clients.Caller.SendAsync("push_recieveMessages", messages);
    }

    public void MapApiFunctions(WebApplication app) {
		if (catboxHash == null) {
			Console.WriteLine("INI | Could not find environment variable \"CATBOX_USER_HASH\"");
			return;
		}

		app.MapPost("/push_registerAccount", async (HttpContext context, [FromForm] string username, [FromForm] string password, [FromForm] string color, [FromForm] IFormFile? profilePicture) => {
			Console.WriteLine("PTC | Attempting registration with username \"" + username + "\", password \"" + password + "\", and color \"" + color + "\"");
			Console.WriteLine("PTC | From " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());

			string[] allowedMimeTypes = { "image/jpeg", "image/png", "image/gif" };
			string profilePictureType = profilePicture == null ? "" : profilePicture.ContentType.ToLower();
			long profilePictureSize = profilePicture == null ? long.MaxValue : profilePicture.Length;
			string profilePictureUrl = "";

			bool filledOut = username != "" && password != "" && color != "";
			bool validUsername = ValidUsername(username);
			bool validPassword = ValidPassword(password);
			bool validColor = ValidHex(color) && ValidUserColor(color);
			bool uploadedProfilePicture = profilePicture != null && profilePicture.Length >= 0;
			bool validProfilePicture = allowedMimeTypes.Contains(profilePictureType) && profilePictureSize <= 10 * 1024 * 1024;

			string errorMessage = "";
			if (!filledOut) {
				errorMessage += "Login information is incomplete. ";
			}
			if (!validUsername) {
				errorMessage += "Username can only use \"A-z, 0-9, -, _\", and must be greater than 3 characters and shorter than 19, and cannot be \"System\" or \"Unknown User\". ";
			}
			if (!validPassword) {
				errorMessage += "Password must be longer than 7 characters and shorter than 25, and can only use \"A-z, 0-9, and special characters\".";
			}
			if (!validColor) {
				if (!ValidHex(color)) {
					errorMessage += "Color is not a valid hex value.";
				} else {
					errorMessage += "Color is too dark.";
				}
			}
			if (!uploadedProfilePicture) {
				errorMessage += "You must add a profile picture! ";
			} else if (!validProfilePicture) {
				errorMessage += "Your profile picture must be smaller than 10MiB and be one of the supported formats: \"gif, png, jpg\"";
			}
			if (errorMessage != "") {
				return BadRequest(errorMessage);
			}

			Guid userID = Guid.NewGuid();

			try {
				using (Stream incomingStream = profilePicture!.OpenReadStream()) {
					using (SKBitmap bitmap = SKBitmap.Decode(incomingStream)) {
						if (bitmap == null) {
							return BadRequest("Invalid or corrupted image file.");
						}

						if (bitmap.Width != 512 || bitmap.Height != 512) {
							return BadRequest("Resolution does not match required resolution of 512x512.");
						}
					}
				}

				using Stream stream = profilePicture!.OpenReadStream();
				using StreamContent content = new StreamContent(stream);
				content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(profilePicture.ContentType);

				using MultipartFormDataContent uploadRequest = new MultipartFormDataContent();
				uploadRequest.Add(new StringContent("fileupload"), "reqtype");
				uploadRequest.Add(content, "fileToUpload", "profile_picture_" + userID.ToString() + Path.GetExtension(profilePicture.FileName));
				uploadRequest.Add(new StringContent(catboxHash!), "userhash");
				using HttpResponseMessage response = await client!.PostAsync("https://catbox.moe/user/api.php", uploadRequest);

				if (!response.IsSuccessStatusCode) {
					return ServerError("Failed to upload profile picture to file host service. Please try again.");
				}

				profilePictureUrl = await response.Content.ReadAsStringAsync();

			} catch (Exception error) {
				Console.WriteLine("PTC | Error while processing profile picture: \"" + error.ToString() + "\"");
				return ServerError("Failed to process uploaded profile picture. Please try again. If the issue persists, please contact KiwianDoesThings with your profile picture (You can see my contact info on my main website \"kiwiandoesthings.place\"");
			}

			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT username FROM users WHERE username = $username";
			queryCommand.Parameters.AddWithValue("$username", username);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
				return BadRequest("There is already a user with that username.");
			}

			using SqliteCommand registerCommand = database!.CreateCommand();
			registerCommand.CommandText = "INSERT INTO users (user_id, username, color, password, secret, profile_picture_link, info) VALUES ($userID, $username, $userColor, $userPassword, $userSecret, $profilePictureLink, $info);";
			registerCommand.Parameters.AddWithValue("$userID", userID.ToString());
			registerCommand.Parameters.AddWithValue("$username", username);
			registerCommand.Parameters.AddWithValue("$userColor", color);
			registerCommand.Parameters.AddWithValue("$userPassword", password);
			string userSecret = GetHashedString(password + username + color);
			registerCommand.Parameters.AddWithValue("$userSecret", userSecret);
			registerCommand.Parameters.AddWithValue("$profilePictureLink", profilePictureUrl);
			registerCommand.Parameters.AddWithValue("$info", GetDeviceInfo(context));
			await registerCommand.ExecuteNonQueryAsync();

			return Results.Ok(new {
				userID = userID.ToString(),
				userSecret = userSecret
			});
		}).DisableAntiforgery();


		app.MapGet("/request_loginInfo", async (HttpContext context, string username, string password) => {
			Console.WriteLine("PTC | Attempted login with username: " + username + " and password: " + password);
			Console.WriteLine("PTC | From " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());

			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT user_id, secret FROM users WHERE username = $username AND password = $password";
			queryCommand.Parameters.AddWithValue("$username", username);
			queryCommand.Parameters.AddWithValue("$password", password);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			object? result = null;
			if (await reader.ReadAsync()) {
				string userID = reader.GetString(0);
				string userSecret = reader.GetString(1);

				AppendUserLoginfo(context, userID, userSecret);
				result = new {
					userID = userID
				};
			}

			return result != null ? Results.Ok(result) : NotFound("No user with that login information was found");
		});

		app.MapGet("/request_userInfo", async (string userID) => {
			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT username, color, created_at FROM users WHERE user_id = $userID";
			queryCommand.Parameters.AddWithValue("$userID", userID);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			object? result = null;
			while (await reader.ReadAsync()) {
				result = new {
					userUsername = reader.GetString(0),
					userColor = reader.GetString(1),
					createdAt = reader.GetString(2)
				};
			}

			return result != null ? Results.Ok(result) : NotFound("No user with that ID was found");
		});

		app.MapGet("/request_userProfile", async (string userID) => {
			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT profile_picture_link, about_me FROM users WHERE user_id = $userID";
			queryCommand.Parameters.AddWithValue("$userID", userID);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			if (await reader.ReadAsync()) {
				string? profilePictureUrl = await reader.IsDBNullAsync(0) ? "none" : reader.GetString(0);
				string aboutMe = await reader.IsDBNullAsync(1) ? "" : reader.GetString(1);

				return Results.Ok(new {
					profilePictureUrl = profilePictureUrl,
					aboutMe = aboutMe
				});
			}

			return BadRequest("No user with that ID was found");
		});

		app.MapGet("/request_roomID", async (string roomName) => {
			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT id FROM rooms WHERE name = $roomName";
			queryCommand.Parameters.AddWithValue("$roomName", roomName);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			object? result = null;
			if (await reader.ReadAsync()) {
				result = new {
					roomID = reader.GetInt32(0)
				};
			}

			return result != null ? Results.Ok(result) : NotFound("No room with that name was found");
		});

		app.MapGet("/request_roomInfo", async (int roomID) => {
			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT name, author_id, privacy, created_at FROM rooms WHERE id = $roomID";
			queryCommand.Parameters.AddWithValue("$roomID", roomID);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			object? result = null;
			if (await reader.ReadAsync()) {
				result = new {
					roomName = reader.GetString(0),
					authorID = reader.GetString(1),
					privacy = reader.GetString(2),
					createdAt = reader.GetString(3)
				};
			}

			return result != null ? Results.Ok(result) : NotFound("No room with that ID was found");
		});

		app.MapGet("/request_roomSearch", async (HttpContext context, string targetName) => {
			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			bool error = await GoodSecret(auth.userID, auth.userSecret);
			if (error) {
				return LogBadUserSecret(auth.userID, auth.userSecret);
			}

			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT id, name FROM rooms LEFT JOIN roomAccess ON id = room_id AND user_id = $userID WHERE name LIKE $targetName AND name != 'HomeRoom' AND (privacy = 'PUBLIC' OR (privacy = 'PRIVATE' AND access_level >= 0))";
			queryCommand.Parameters.AddWithValue("$targetName", "%" + targetName + "%");
			queryCommand.Parameters.AddWithValue("$userID", auth.userID);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			List<RoomResult> results = new();
			while (await reader.ReadAsync()) {
				results.Add(new RoomResult(reader.GetString(1), reader.GetInt32(0)));
			}

			return Results.Ok(results);
		});

		app.MapPost("/push_deleteAccount", async (HttpContext context) => {
			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			Console.WriteLine("PTC | Attempting to delete account with userID \"" + auth.userID + "\", and userSecret \"" + auth.userSecret + "\"");
			Console.WriteLine("PTC | From " + context.Request.Headers["CF-Connecting-IP"].FirstOrDefault() + " ||| " + context.Request.Headers.UserAgent.ToString());

			bool error = await GoodSecret(auth.userID, auth.userSecret);
			if (error) {
				return LogBadUserSecret(auth.userID, auth.userSecret);
			}

			using SqliteCommand deleteCommand = database!.CreateCommand();
			deleteCommand.CommandText = "UPDATE users SET username, color, password, ";
			deleteCommand.Parameters.AddWithValue("$userID", auth.userID);
			await deleteCommand.ExecuteNonQueryAsync();

			AppendUserLoginfo(context, "", "");

			return Results.Ok();
		});

		app.MapPost("/push_createRoomPersonal", async (HttpContext context, string otherID) => {
			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			int roomID = CreateRoom(auth.userID + " " + otherID, auth.userID, false);


			return Results.Ok(new {
				roomID = roomID,
			});
		});

		app.MapPost("/push_createRoom", async (HttpContext context, string roomName) => {
			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			if (await GetRoomID(roomName) != -1) {
				return BadRequest("A room with that name already exists!");
			}
			int roomID = CreateRoom(roomName, auth.userID, true);

			return Results.Ok(new {
				roomID = roomID
			});
		});

		app.MapPost("/push_setRoomPrivacy", async (HttpContext context, int roomID, string newPrivacy) => {
			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			if (await UserAccessLevelInRoom(auth.userID, roomID) < 2) {
				return Unauthorized("You do not have moderator permissions in this room!");
			}

			newPrivacy = newPrivacy.ToLower();
			if (newPrivacy != "public" && newPrivacy != "private") {
				return BadRequest("Privacy must be either PUBLIC or PRIVATE, instead found \"" + newPrivacy + "\"");
			}

			using SqliteCommand roomCommand = database!.CreateCommand();
			roomCommand.CommandText = "UPDATE rooms SET privacy = $newPrivacy WHERE id = $roomID";
			roomCommand.Parameters.AddWithValue("$newPrivacy", newPrivacy.ToUpper());
			roomCommand.Parameters.AddWithValue("$roomID", roomID);
			await roomCommand.ExecuteNonQueryAsync();

			return Results.Ok();
		});

		app.MapGet("/request_accessLevel", async (string userID, int roomID) => {
			return Results.Ok(new {
				accessLevel = UserAccessLevelInRoom(userID, roomID)
			});
		});

		app.MapGet("/request_usersWithAccessLevel", async (int accessLevel, int roomID) => {
			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT user_id FROM roomAccess WHERE room_id = $roomID AND access_level = $accessLevel";
			queryCommand.Parameters.AddWithValue("$roomID", roomID);
			queryCommand.Parameters.AddWithValue("$accessLevel", accessLevel);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			List<string> results = new();
			while (await reader.ReadAsync()) {
				results.Add(reader.GetString(0));
			}

			return Results.Ok(results);
		});

		app.MapGet("/request_userID", async (string userName) => {
			using SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT user_id FROM users WHERE username = $userName";
			queryCommand.Parameters.AddWithValue("$userName", userName);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			if (await reader.ReadAsync()) {
				return Results.Ok(new {
					userID = reader.GetString(0)
				});
			}

			return NotFound("No user with that ID was found");
		});

		app.MapPost("/push_setUserAccess", async (HttpContext context, string otherID, int accessLevel, int roomID) => {
			if (!await GetUserIDExists(otherID)) {
				return BadRequest("No user with that ID was found");
			}

			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			if (await UserAccessLevelInRoom(auth.userID, roomID) < 2) {
				return Unauthorized("You do not have moderator permissions in this room!");
			}

			using SqliteCommand setCommand = database!.CreateCommand();
			setCommand.CommandText = "INSERT INTO roomAccess (room_id, user_id, access_level) VALUES ($roomID, $userID, $accessLevel) ON CONFLICT(room_id, user_id) DO UPDATE SET access_level = $accessLevel";
			setCommand.Parameters.AddWithValue("$roomID", roomID);
			setCommand.Parameters.AddWithValue("$userID", otherID);
			setCommand.Parameters.AddWithValue("$accessLevel", accessLevel);
			await setCommand.ExecuteNonQueryAsync();

			return Results.Ok();
		});

		app.MapPost("/push_setUserUsername", async (HttpContext context, string newUsername) => {
			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			if (!ValidUsername(newUsername)) {
				return BadRequest("Username can only use \"A-z, 0-9, -, _\", and must be greater than 3 characters and shorter than 18. You may not name yourself \"System\" or \"Unknown User\".");
			}

			using SqliteCommand command = database!.CreateCommand();
			command.CommandText = "UPDATE users SET username = $newUsername WHERE user_id = $userID";
			command.Parameters.AddWithValue("$newUsername", newUsername);
			command.Parameters.AddWithValue("$userID", auth.userID);
			await command.ExecuteNonQueryAsync();

			return Results.Ok();
		});

		app.MapPost("/push_editMessage", async (HttpContext context, int roomID, int messageID, string newMessageContent, bool isDeletion) => {
			AuthenticationResult auth = await IsAuthentic(context);
			if (!auth) {
				return auth.error;
			}

			bool isOwnMessage = false;
			bool hasModDeletionPermissions = false;

			if (await UserAccessLevelInRoom(auth.userID, roomID) >= 1) {
				hasModDeletionPermissions = true;
			}

			SqliteCommand queryCommand = database!.CreateCommand();
			queryCommand.CommandText = "SELECT author_id FROM MESSAGES where id = $messageID";
			queryCommand.Parameters.AddWithValue("$messageID", messageID);
			using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
			if (await reader.ReadAsync()) {
				isOwnMessage = reader.GetString(0) == auth.userID;
			}

			if (isDeletion) {
				if (!isOwnMessage && !hasModDeletionPermissions) {
					return Unauthorized("You do not have permissions to delete other users' messages!");
				}

				SqliteCommand deleteCommand = database!.CreateCommand();
				deleteCommand.CommandText = "DELETE FROM messages WHERE id = $messageID";
				deleteCommand.Parameters.AddWithValue("$messageID", messageID);
				await deleteCommand.ExecuteNonQueryAsync();
			} else {
				if (!isOwnMessage) {
					return Unauthorized("You cannot edit other users' messages!");
				}

				SqliteCommand editCommand = database!.CreateCommand();
				editCommand.CommandText = "UPDATE messages SET content = $newMessageContent WHERE id = $messageID";
				editCommand.Parameters.AddWithValue("$newMessageContent", newMessageContent);
				editCommand.Parameters.AddWithValue("$messageID", messageID);
				await editCommand.ExecuteNonQueryAsync();
			}

			return Results.Ok();
		});
	}

	public static string GetDeviceInfo(HttpContext context) {
		string? ip = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();

		if (string.IsNullOrEmpty(ip)) {
			ip = context.Connection.RemoteIpAddress?.ToString();
		}

		return ip + " ||| " + context.Request.Headers.UserAgent.ToString();
	}

	public static int CreateRoom(string roomName, string ownerID, bool isPublic) {
		SqliteCommand roomCommand = database!.CreateCommand();
		roomCommand.CommandText = "INSERT INTO rooms (name, author_id, privacy) VALUES ($roomName, $authorID, $isPublic); SELECT last_insert_rowid();";
		roomCommand.Parameters.AddWithValue("$roomName", roomName);
		roomCommand.Parameters.AddWithValue("$authorID", ownerID);
		roomCommand.Parameters.AddWithValue("$isPublic", isPublic ? "PUBLIC" : "PRIVATE");
		int roomID = (int)(long)roomCommand.ExecuteScalar()!;

		using SqliteCommand accessCommand = database!.CreateCommand();
		accessCommand.CommandText = "INSERT INTO roomAccess (room_id, user_id, access_level) VALUES ($roomID, $userID, $accessLevel)";
		accessCommand.Parameters.AddWithValue("$roomID", roomID);
		accessCommand.Parameters.AddWithValue("$userID", ownerID);
		accessCommand.Parameters.AddWithValue("$accessLevel", 2);
		accessCommand.ExecuteNonQueryAsync();

		return roomID;
	}

	public static async Task<bool> GoodSecret(string userID, string userSecret) {
		string realSecret = await GetUserSecret(userID);
		if (realSecret != ProcessSecret(userSecret, true)) {
			Console.WriteLine("API | Passed secret did not match real secret of " + realSecret);
			return true;
		}

		return false;
	}

	public static async Task<bool> VerifyRequest(ISingleClientProxy client, string userID, string userSecret, string requestType) {
		string trimmedSecret = ProcessSecret(userSecret, false);
		string realSecret = await GetUserSecret(userID);
		if (realSecret != trimmedSecret) {
			Console.WriteLine("WSS | Passed secret \"" + trimmedSecret + "\" did not match real secret of " + realSecret);
			await client.SendAsync("push_serverMessage", "Server could not authenticate your " + requestType + ", please clear your cookies and log in again");
			return true;
		}

		return false;
	}

	public static string ProcessSecret(string originalSecret, bool decode) {
		string newSecret = new string(originalSecret.TrimEnd("="));
		if (decode) {
			newSecret = WebUtility.UrlDecode(newSecret);
		}
		return newSecret;
	}

	public static bool ValidString(string toCheck) {
		return Regex.IsMatch(toCheck, @"^[a-zA-Z0-9\-_]+$");
	}

	public static bool ValidAdvancedString(string toCheck) {
		return Regex.IsMatch(toCheck, @"^[\x21-\x7E]$");
	}

	public static bool ValidHex(string toCheck) {
		return Regex.IsMatch(toCheck, @"^#?([A-Fa-f0-9]{6})$");
	}

	public static bool ValidUserColor(string hex) {
		int red = Convert.ToInt32(hex.Substring(0, 2), 16);
		int green = Convert.ToInt32(hex.Substring(2, 2), 16);
		int blue = Convert.ToInt32(hex.Substring(4, 2), 16);

		double brightness = (red * 0.299) + (green * 0.587) + (blue * 0.114);

		Console.WriteLine("bright " + brightness);
		return brightness >= 80;
	}

	public static bool ValidUsername(string username) {
		return username.Length > 3 && username.Length <= 18 && ValidString(username) && username.ToLower() != "system" && username.ToLower() != "unknown user";
	}

	public static bool ValidPassword(string password) {
		return password.Length > 7 && password.Length <= 24 && ValidAdvancedString(password);
	}

	public static async Task<string> GetUserSecret(string userID) {
		using SqliteCommand getCommand = database!.CreateCommand();
		getCommand.CommandText = "SELECT secret FROM users WHERE user_id = $userID LIMIT 1";
		getCommand.Parameters.AddWithValue("$userID", userID);
		object? result = await getCommand.ExecuteScalarAsync()!;

		if (result != null && result != DBNull.Value) {
			return result.ToString()!.TrimEnd("=").ToString();
		}

		return "";
	}

	public static async Task<bool> GetUserIDExists(string userID) {
		using SqliteCommand queryCommand = database!.CreateCommand();
		queryCommand.CommandText = "SELECT 1 FROM users WHERE user_id = $userID";
		queryCommand.Parameters.AddWithValue("$userID", userID);
		object? result = await queryCommand.ExecuteScalarAsync();

		return result != null;
	}

	public static async Task<int> GetRoomID(string roomName) {
		using SqliteCommand queryCommand = database!.CreateCommand();
		queryCommand.CommandText = "SELECT id FROM rooms WHERE name = $roomName";
		queryCommand.Parameters.AddWithValue("$roomName", roomName);
		using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
		object? result = null;
		if (await reader.ReadAsync()) {
			result = reader.GetInt32(0);
		}

		return result != null ? (int)result : -1;
	}

	public static async Task<int> UserAccessLevelInRoom(string userID, int roomID) {
		if (roomID == 0) {
			if (userID == "82bc31a6-5f02-4d22-933c-566c60478aef") {
				return 2;
			}
			return 0;
		}

		using SqliteCommand getCommand = database!.CreateCommand();
		getCommand.CommandText = "SELECT access_level FROM roomAccess WHERE user_id = $userID AND room_id = $roomID";
		getCommand.Parameters.AddWithValue("$userID", userID);
		getCommand.Parameters.AddWithValue("$roomID", roomID);
		object? result = await getCommand.ExecuteScalarAsync()!;

		if (result != null && result != DBNull.Value) {
			return (int)(long)result;
		}

		return -1;
	}

	public static IResult LogBadUserSecret(string userID, string userSecret) {
		Console.WriteLine("PTC | Request with bad secret using userID \"" + userID + "\", and userSecret \"" + userSecret + "\"");
		return Unauthorized("Your passed user secret does not match the authoritative one. Please clear your cookies and log in again.");
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
		bool isLocalhost = (origin != null && origin.Contains("test")) || (referer != null && referer.Contains("test"));
		CookieOptions secretCookieOptions = new CookieOptions {
			HttpOnly = true,
			Domain = "kiwiandoesthings.place",
			SameSite = isLocalhost ? SameSiteMode.None : SameSiteMode.Strict,
			Secure = true,
			Expires = DateTimeOffset.UtcNow.AddDays(365),
			Path = "/"
		};
		CookieOptions normalCookieOptions = new CookieOptions {
			HttpOnly = false,
			Domain = "kiwiandoesthings.place",
			SameSite = isLocalhost ? SameSiteMode.Lax : SameSiteMode.Strict,
			Secure = !isLocalhost,
			Expires = DateTimeOffset.UtcNow.AddDays(365),
			Path = "/"
		};
		context.Response.Cookies.Append("userSecret", userSecret, secretCookieOptions);
		context.Response.Cookies.Append("userID", userID, normalCookieOptions);
	}

	public static IResult CouldNotGetAuth() {
		Console.WriteLine("PTC | User tried to make request, but server could not extract userID and secret from cookies");
		return BadRequest("Could not get user authentication information from request. Please log in again.");
	}

	public static async Task<AuthenticationResult> IsAuthentic(HttpContext context) {
		if (GetUserInfo(context, out string userID, out string userSecret) == -1) {
			return new AuthenticationResult(CouldNotGetAuth());
		}

		bool goodSecret = await GoodSecret(userID, userSecret);
		if (goodSecret) {
			return new AuthenticationResult(LogBadUserSecret(userID, userSecret));
		}

		return new AuthenticationResult(true, Results.Ok(), userID, userSecret);
	}

	public struct AuthenticationResult {
		public bool isValid;
		public IResult error;
		public string userID;
		public string userSecret;

		public AuthenticationResult(bool isValid, IResult error, string userID, string userSecret) {
			this.isValid = isValid;
			this.error = error;
			this.userID = userID;
			this.userSecret = userSecret;
		}

		public AuthenticationResult(IResult error) {
			isValid = false;
			this.error = error;
			userID = string.Empty;
			userSecret = string.Empty;
		}

		public static bool operator !(AuthenticationResult auth) {
			return !auth.isValid;
		}

		public static bool operator true(AuthenticationResult auth) {
			return auth.isValid;
		}

		public static bool operator false(AuthenticationResult auth) {
			return !auth.isValid;
		}
	}
}