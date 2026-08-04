namespace kiwiapi.ProtoCall;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.RegularExpressions;

using static Program;

public class ProtoCallHub : Hub {
	private static readonly Logger logger = new Logger("PTC");
	private static ProtocallApi api = null!;

    public static readonly ConcurrentDictionary<string, bool> userConnections = new();
	public static readonly string? catboxHash = Environment.GetEnvironmentVariable("CATBOX_USER_HASH");

    public record MessagesData(string authorID, string content, int messageIndex, string messageTimestamp);
	public record RoomResult(string roomName, int roomID);

	public void Setup(WebApplication app) {
		api = new ProtocallApi(logger, catboxHash);

		api.MapApiFunctions(app);
	}

	public override async Task OnConnectedAsync() {
        if (Context.User?.Identity?.IsAuthenticated != true) {
            Context.Abort();
            return;
        }

        string userID = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
        userConnections[userID] = true;

        //Console.WriteLine("Client with user ID " + userID + " connected");

        await Clients.All.SendAsync("push_userStatus", userID, true);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception) {
        if (Context.User?.Identity?.IsAuthenticated != true) {
            return;
        }

        string userID = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
        userConnections[userID] = false;

        //Console.WriteLine("Client with user ID " + userID + " disconnected");

        await Clients.All.SendAsync("push_userStatus", userID, false);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task push_sendMessage(string message, string messageTimestamp, int roomID) {
        if (Context.User?.Identity?.IsAuthenticated != true) {
			await Clients.Caller.SendAsync("push_serverMessage", "Authentication error. Please log out and in again.");
            return;
        }

        string userID = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

        if (await UserAccessLevelInRoom(userID, roomID) < 0) {
            await Clients.Caller.SendAsync("push_serverMessage", "You do not have access to this room!");
            return;
        }

        logger.INFO("Got message from user with ID " + userID + " in room with ID " + roomID + " and content of \"" + message + "\"");

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
        if (Context.User?.Identity?.IsAuthenticated != true) {
            await Clients.Caller.SendAsync("push_serverMessage", "Authentication error. Please log out and in again.");
            return;
        }

        string userID = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

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

	public static async Task<int> CreateRoom(string roomName, string ownerID, bool isPublic) {
		SqliteCommand roomCommand = database!.CreateCommand();
		roomCommand.CommandText = "INSERT INTO rooms (name, author_id, privacy) VALUES ($roomName, $authorID, $isPublic); SELECT last_insert_rowid();";
		roomCommand.Parameters.AddWithValue("$roomName", roomName);
		roomCommand.Parameters.AddWithValue("$authorID", ownerID);
		roomCommand.Parameters.AddWithValue("$isPublic", isPublic ? "PUBLIC" : "PRIVATE");
		int roomID = (int)(await roomCommand.ExecuteScalarAsync() as long? ?? -1);

		using SqliteCommand accessCommand = database!.CreateCommand();
		accessCommand.CommandText = "INSERT INTO roomAccess (room_id, user_id, access_level) VALUES ($roomID, $userID, $accessLevel)";
		accessCommand.Parameters.AddWithValue("$roomID", roomID);
		accessCommand.Parameters.AddWithValue("$userID", ownerID);
		accessCommand.Parameters.AddWithValue("$accessLevel", 2);
		await accessCommand.ExecuteNonQueryAsync();

		return roomID;
	}

	public static bool ValidString(string toCheck) {
		return Regex.IsMatch(toCheck, @"^[a-zA-Z0-9\-_]+$");
	}

    public static bool ValidAdvancedString(string toCheck) {
        return Regex.IsMatch(toCheck, @"^[\x21-\x7E]+$");
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

	public static IResult CouldNotGetAuth() {
        logger.WARN("User tried to make request, but server could not extract userID and secret from cookies");
		return BadRequest("Could not get user authentication information from request. Please log in again.");
	}

    public static bool AuthenticateUser(HttpContext context, [NotNullWhen(true)] out string? userID, out IResult? result) {
        userID = null;

        if (context.User.Identity?.IsAuthenticated != true) {
            result = Unauthorized("You are not authenticated");
            return false;
        }

        userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
        result = null;
        return true;
    }
}