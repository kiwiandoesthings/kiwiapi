namespace KiwiApi.ProtoCall;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Security.Claims;

using static FruitBowl;
using static Program;

public class ProtoCallHub : Hub {
	private static readonly Logger logger = new Logger("PTCH");
    private static SqlInterface sql = null!;
	private static ProtoCallApi api = null!;

    private static Dictionary<string, string> userIDsToConnectionIDs;

    public record RoomInfo(string id, string name, string authorID, string description, bool isPublic, long totalMessages, DateTime updatedAt, DateTime createdAt, long earliestMessageIndex, long latestMessageIndex);
    public record MessageCollection(string roomID, MessageData[] messages, bool later);
    public record MessageData(MessageAuthor author, string content, int index, DateTime timestamp);
    public record MessageAuthor(string id, string username, string profilePictureUrl, string color);
	public record RoomResult(string id, string name);

	public static void Setup(WebApplication app, SqlInterface sql) {
        ProtoCallHub.sql = sql;

        api = new ProtoCallApi(sql);

		api.MapApiFunctions(app);
    }

    [Authorize]
    public override async Task OnConnectedAsync() {
        string? roomID = Context.GetHttpContext()?.Request.Query["roomID"];
        if (roomID == null) {
            logger.INFO("Rejected connection attempt from connection that didn't provide roomID parameter");
            Context.Abort();
            return;
        }

        using SqlCommand existsCommand = sql.Command("SELECT COUNT(*) FROM protocall_rooms WHERE id = @id",
            ("id", roomID));
        long rooms = (long)(await existsCommand.ExecuteGetScalar())!;
        if (rooms == 0) {
            logger.INFO("Rejected connection attempt from connection that provided an invalid roomID parameter");
            Context.Abort();
            return;
        }

        using SqlCommand command = sql.Command("SELECT IFNULL(MIN(local_id), 0), IFNULL(MAX(local_id), 0) FROM protocall_messages WHERE room_id = @room_id;",
            ("room_id", roomID));
        using SqliteDataReader messagesReader = await command.ExecuteGet();
        if (await messagesReader.ReadAsync()) {
            await Clients.Caller.SendAsync("post/room_info", await GetRoomInfo(roomID));
        } else {
            logger.CRITICAL("Failed to get room info, even after validating room exists");
            Context.Abort();
            return;
        }

        string? userID = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
        if (userID == null) {
            logger.INFO("Rejected connection attempt from client that didn't have user ID cookie");
            Context.Abort();
            return;
        }

        if (!userIDsToConnectionIDs.ContainsKey(userID)) {
            userIDsToConnectionIDs.Add(userID, Context.ConnectionId);
        } else {
            await Clients.Caller.SendAsync("post/alert", "ProtoCall currently only supports one connected client at a time!");
            Context.Abort();
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, roomID);

        logger.INFO("Client with user ID {" + userID + "} connected");

        await base.OnConnectedAsync();
    }

    [Authorize]
    public override async Task OnDisconnectedAsync(Exception? exception) {
        string? userID = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
        if (userID == null) {
            logger.INFO("Failed to handle disconnection for user that didn't have user ID cookie");
            return;
        }

        if (!userIDsToConnectionIDs.ContainsKey(userID)) {
            logger.CRITICAL("Client disconnected who was not registered as connected");
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomID);

        logger.INFO("Client with user ID {" + userID + "} disconnected");

        await base.OnDisconnectedAsync(exception);
    }

    [HubMethodName("post/messages")]
    public async Task PostMessages(string message, string roomID) {
        string? userID = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (userID == null) {
            await Clients.Caller.SendAsync("post/alert", "Authentication error. Please log out and in again.");
            return;
        }

        if (!await CanUserAccessRoom(userID, roomID)) {
            await Clients.Caller.SendAsync("post/alert", "You do not have access to this room!");
            return;
        }

        using SqlCommand idCommand = sql.Command("SELECT IFNULL(MAX(local_id), 0) + 1 FROM protocall_messages WHERE room_id = @room_id",
            ("room_id", roomID));
        int localID = Convert.ToInt32(await idCommand.ExecuteGetScalar());

        using SqlCommand sendCommand = sql.Command("INSERT INTO protocall_messages (content, author_id, local_id, room_id) VALUES (@content, @author_id, @local_id, @room_id) RETURNING id, created_at;",
            ("content", message),
            ("author_id", userID),
            ("local_id", localID),
            ("room_id", roomID));
        using SqliteDataReader reader = await sendCommand.ExecuteGet();

        User? user = await Globals.users.GetUser(userID);
        if (user == null) {
            LogDesynchronized();
            await Clients.Caller.SendAsync("post/alert", DesynchronizedResponse());
            return;
        }

        if (await reader.ReadAsync()) {
            MessageCollection messages = new MessageCollection(roomID, [ new MessageData(new MessageAuthor(userID, user.username, user.profilePictureUrl, user.color), message, reader.GetInt32(0), reader.GetDateTime(1)) ], true);
            await Clients.All.SendAsync("post/messages", messages);
        } else {
            await Clients.Caller.SendAsync("post/messages", "Encountered an unknown error while sending your message. Please try again. If the problem persists, contact @KiwianDoesThings");
        }
    }

    [HubMethodName("get/messages")]
    public async Task GetMessages(int messageIndex, int messageCount, string roomID, bool forward) {
        if (messageIndex == -1) {
            using SqlCommand latestIDCommand = sql.Command("SELECT IFNULL(MAX(local_id), 0) FROM protocall_messages WHERE room_id = @room_id",
                ("room_id", roomID));
            messageIndex = Convert.ToInt32(await latestIDCommand.ExecuteGetScalar());
        }

        List<MessageData> messages = [];

        string direction = forward ? ">=" : "<="; 
        string order = forward ? "ASC" : "DESC";

        using SqlCommand getCommand = sql.Command("SELECT message.local_id, message.content, message.author_id, message.created_at, user.username, user.profile_picture_url, user.color FROM (SELECT local_id, content, author_id, created_at FROM protocall_messages WHERE local_id " + direction + " @local_id AND room_id = @room_id ORDER BY local_id " + order + " LIMIT @limit) message LEFT JOIN users user ON message.author_id = user.id ORDER BY message.local_id ASC",
            ("local_id", messageIndex),
            ("limit", messageCount),
            ("room_id", roomID));

        using SqliteDataReader reader = await getCommand.ExecuteGet();

        while (await reader.ReadAsync()) {
            messages.Add(new MessageData(
                new MessageAuthor(reader.GetString(2), reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                reader.GetString(1),
                reader.GetInt32(0),
                reader.GetDateTime(3)
            ));
        }

        await Clients.Caller.SendAsync("post/messages", new MessageCollection(roomID, [.. messages], forward));
    }

    public static async Task<int> CreateRoom(string roomName, bool isPublic, string ownerID) {
        using SqlCommand roomCommand = sql.Command("INSERT INTO protocall_rooms (name, author_id, privacy) VALUES (@name, @author_id, @privacy); SELECT last_insert_rowid();",
            ("name", roomName),
            ("author_id", ownerID),
            ("privacy", isPublic));
        long? result = (long?)await roomCommand.ExecuteGetScalar();
        int roomID = (int)(result ?? -1);

        using SqlCommand accessCommand = sql.Command("INSERT INTO protocall_room_access (room_id, user_id, access) VALUES (@room_id, @user_id, @access_level)",
            ("room_id", roomID),
            ("user_id", ownerID),
            ("access", 2));
        await accessCommand.Execute();

        return roomID;
    }

    public static async Task<int> GetRoomID(string roomName) {
        using SqlCommand queryCommand = sql.Command("SELECT id FROM protocall_rooms WHERE name = @name",
            ("name", roomName));
        using SqliteDataReader reader = await queryCommand.ExecuteGet();

        if (await reader.ReadAsync()) {
            return reader.GetInt32(0);
        }

        return -1;
    }

    public static async Task<RoomInfo?> GetRoomInfo(string roomID) {
        logger.INFO(roomID);
        using SqlCommand roomQuery = sql.Command("SELECT name, author_id, description, is_public, total_messages, updated_at, created_at FROM protocall_rooms WHERE id = @id",
            ("id", roomID));
        using SqliteDataReader roomReader = await roomQuery.ExecuteGet();

        using SqlCommand messagesQuery = sql.Command("SELECT IFNULL(MIN(local_id), 0), IFNULL(MAX(local_id), 0) FROM protocall_messages WHERE room_id = @room_id;",
            ("room_id", roomID));
        using SqliteDataReader messagesReader = await messagesQuery.ExecuteGet();

        if (!await roomReader.ReadAsync() || !await messagesReader.ReadAsync()) {
            return null;
        }

        return new RoomInfo(roomID, roomReader.GetString(0), roomReader.GetString(1), roomReader.GetString(2), roomReader.GetBoolean(3), roomReader.GetInt64(4), roomReader.GetDateTime(5), roomReader.GetDateTime(6), messagesReader.GetInt64(0), messagesReader.GetInt64(1));
    }

    public static async Task<int> UserAccessLevelInRoom(string userID, string roomID) {
        using SqlCommand getCommand = sql.Command("SELECT access FROM protocall_room_access WHERE user_id = @user_id AND room_id = @room_id",
            ("user_id", userID),
            ("room_id", roomID));
        object? result = await getCommand.ExecuteGetScalar();

        return Convert.ToInt32(result);
    }

    public static async Task<bool> CanUserAccessRoom(string userID, string roomID) {
        using SqlCommand command = sql.Command("SELECT (room.is_public OR (NOT room.is_public AND access.access >= 0)) FROM protocall_rooms room LEFT JOIN protocall_room_access access ON room.id = access.room_id AND access.user_id = @id WHERE room.id = @room_id",
            ("id", userID),
            ("room_id", roomID));
        return Convert.ToInt32(await command.ExecuteGetScalar()) != 0;
    }
}