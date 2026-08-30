namespace kiwiapi.ProtoCall;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.RegularExpressions;

using static Program;

public class ProtoCallHub : Hub {
	private static readonly Logger logger = new Logger("PTC");
    private static SqlInterface sql = null!;
	private static ProtocallApi api = null!;

    public static readonly ConcurrentDictionary<string, bool> userConnections = new();
	public static readonly string? catboxHash = Environment.GetEnvironmentVariable("CATBOX_USER_HASH");

    public record MessagesData(string authorID, string content, int messageIndex, string messageTimestamp);
	public record RoomResult(string roomName, int roomID);

	public static void Setup(WebApplication app) {
        string databasePath = Path.Combine(realBasePath, "protocall.db");
        string connectionString = "Data Source=" + databasePath;
        sql = new SqlInterface(connectionString);

        if (!File.Exists(databasePath)) {
            throw new FileNotFoundException("Couldn't find \"protocall.db\" at \"" + databasePath + "\".");
        } else {
            logger.INFO("Found \"protocall.db\" at \"" + databasePath + "\"");
        }

        api = new ProtocallApi(logger, catboxHash, sql);

		api.MapApiFunctions(app);
    }

    [Authorize]
    public override async Task OnConnectedAsync() {
        //string? userID = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
        //userConnections[userID] = true;

        //Console.WriteLine("Client with user ID " + userID + " connected");

        //await Clients.All.SendAsync("push_userStatus", userID, true);

        await base.OnConnectedAsync();
    }

    [Authorize]
    public override async Task OnDisconnectedAsync(Exception? exception) {
        //string userID = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
        //userConnections[userID] = false;

        //Console.WriteLine("Client with user ID " + userID + " disconnected");

        //await Clients.All.SendAsync("push_userStatus", userID, false);

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

        using SqlCommand idCommand = sql.Command("SELECT IFNULL(MAX(local_id), 0) + 1 FROM messages WHERE room_id = @room_id",
            ("room_id", roomID));
        int localID = Convert.ToInt32(await idCommand.ExecuteGetScalar());

        using SqlCommand sendCommand = sql.Command("INSERT INTO messages (content, author_id, local_id, room_id, created_at) VALUES (@content, @author_id, @local_id, @room_id, @created_at); SELECT last_insert_rowid();",
            ("content", message),
            ("author_id", userID),
            ("local_id", localID),
            ("room_id", roomID),
            ("created_at", messageTimestamp));
        int newId = Convert.ToInt32(await sendCommand.ExecuteGetScalar());

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
            using SqlCommand latestIDCommand = sql.Command("SELECT IFNULL(MAX(local_id), 0) FROM messages");
            messageIndex = Convert.ToInt32(await latestIDCommand.ExecuteGetScalar());
        }

        List<MessagesData> messages = [];

        using SqlCommand getCommand = sql.Command("SELECT local_id, content, author_id, created_at FROM (SELECT local_id, content, author_id, created_at FROM messages WHERE local_id <= @local_id AND room_id = @room_id ORDER BY local_id DESC LIMIT @limit) ORDER BY local_id ASC",
            ("local_id", messageIndex),
            ("limit", messageCount),
            ("room_id", roomID));

        List<object[]> rows = await getCommand.ExecuteGet();
        foreach (object[] row in rows) {
            messages.Add(new MessagesData(
                (string)row[2],
                (string)row[1],
                Convert.ToInt32(row[0]),
                (string)row[3]
            ));
        }

        await Clients.Caller.SendAsync("push_recieveMessages", messages);
    }

    public static async Task<int> CreateRoom(string roomName, string ownerID, bool isPublic) {
        using SqlCommand roomCommand = sql.Command("INSERT INTO rooms (name, author_id, privacy) VALUES (@name, @author_id, @privacy); SELECT last_insert_rowid();",
            ("name", roomName),
            ("author_id", ownerID),
            ("privacy", isPublic ? "PUBLIC" : "PRIVATE"));
        object? result = await roomCommand.ExecuteGetScalar();
        int roomID = result != null ? Convert.ToInt32(result) : -1;

        using SqlCommand accessCommand = sql.Command("INSERT INTO roomAccess (room_id, user_id, access_level) VALUES (@room_id, @user_id, @access_level)",
            ("room_id", roomID),
            ("user_id", ownerID),
            ("access_level", 2));
        await accessCommand.Execute();

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
		int red = Convert.ToInt32(hex[0.. 2], 16);
		int green = Convert.ToInt32(hex[2.. 2], 16);
		int blue = Convert.ToInt32(hex[4.. 2], 16);

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
        using SqlCommand queryCommand = sql.Command("SELECT 1 FROM users WHERE user_id = @user_id",
            ("user_id", userID));
        object? result = await queryCommand.ExecuteGetScalar();

        return result != null;
    }

    public static async Task<int> GetRoomID(string roomName) {
        using SqlCommand queryCommand = sql.Command("SELECT id FROM rooms WHERE name = @name",
            ("name", roomName));
        List<object[]> rows = await queryCommand.ExecuteGet();

        if (rows.Count > 0) {
            return Convert.ToInt32(rows[0][0]);
        }

        return -1;
    }

    public static async Task<int> UserAccessLevelInRoom(string userID, int roomID) {
        if (roomID == 0) {
            if (userID == "82bc31a6-5f02-4d22-933c-566c60478aef") {
                return 2;
            }
            return 0;
        }

        using SqlCommand getCommand = sql.Command("SELECT access_level FROM roomAccess WHERE user_id = @user_id AND room_id = @room_id",
            ("user_id", userID),
            ("room_id", roomID));
        object? result = await getCommand.ExecuteGetScalar();

        if (result != null && result != DBNull.Value) {
            return Convert.ToInt32(result);
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