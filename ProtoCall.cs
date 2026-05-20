namespace kiwiapi;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

public class ProtoCall : Hub {
    public record MessagesData(string authorID, string content, int messageIndex, string messageTimestamp);
    private Dictionary<string, bool> userConnections = new();

    public override async Task OnConnectedAsync() {
        string? userID = Context.GetHttpContext()!.Request.Query["userID"];
        string? userSecret = Context.GetHttpContext()!.Request.Cookies["userSecret"];
        if (string.IsNullOrEmpty(userID) || string.IsNullOrEmpty(userSecret)) {
            Context.Abort();
            return;
        }

        bool error = await Program.VerifyRequest(Clients.Caller, userID, userSecret, "connection");
        if (error) {
            Context.Abort();
			Program.LogBadUserSecret(userID, userSecret);
			return;
        }

        userConnections[userID] = true;

        await Clients.All.SendAsync("push_userStatus", userID, true);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception) {
        string? userID = Context.GetHttpContext()!.Request.Query["userID"];
        string? userSecret = Context.GetHttpContext()!.Request.Query["userSecret"];
        if (userID == null || userSecret == null) {
            return;
        }

		bool error = await Program.VerifyRequest(Clients.Caller, userID, userSecret, "connection");
		if (error) {
			Program.LogBadUserSecret(userID, userSecret);
			return;
		}

		userConnections[userID] = false;

        await Clients.All.SendAsync("push_userStatus", userID, false);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task push_sendMessage(string userID, string userSecret, string message, string messageTimestamp, int roomID) {
        Console.WriteLine("Got message from userID: " + userID + " with secret: " + userSecret + " with content: \"" + message + "\"");

		bool error = await Program.VerifyRequest(Clients.Caller, userID, userSecret, "message");
		if (error) {
            Program.LogBadUserSecret(userID, userSecret);
            return;
		}

		if (await Program.UserAccessLevelInRoom(userID, roomID) < 0) {
            await Clients.Caller.SendAsync("push_serverMessage", "You do not have access to this room!");
            return;
        }

        SqliteCommand idCommand = Program.database!.CreateCommand();
        idCommand.CommandText = "SELECT IFNULL(MAX(local_id), 0) + 1 FROM messages WHERE room_id = $roomID";
        idCommand.Parameters.AddWithValue("$roomID", roomID);
        int localID = (int)(long)idCommand.ExecuteScalar()!;
        idCommand.Dispose();

        SqliteCommand sendCommand = Program.database!.CreateCommand();
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
        sendCommand.Dispose();
    }

    public async Task push_messageRequest(int messageIndex, int messageCount, string userID, string userSecret, int roomID) {
		bool error = await Program.VerifyRequest(Clients.Caller, userID, userSecret, "request");
		if (error) {
			Program.LogBadUserSecret(userID, userSecret);
			return;
		}

		if (await Program.UserAccessLevelInRoom(userID, roomID) < 0) {
            await Clients.Caller.SendAsync("push_serverMessage", "You do not have access to this room!");
            return;
        }

        if (messageIndex == -1) {
            SqliteCommand latestIDCommand = Program.database!.CreateCommand();
            latestIDCommand.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM Messages";
            messageIndex = (int)(long)latestIDCommand.ExecuteScalar()!;
            latestIDCommand.Dispose();
        }

        List<MessagesData> messages = new();

        SqliteCommand getCommand = Program.database!.CreateCommand();
        getCommand.CommandText = "SELECT local_id, content, author_id, created_at FROM (SELECT local_id, content, author_id, created_at FROM messages WHERE local_id <= $messageIndex AND room_id = $roomID ORDER BY local_id DESC LIMIT $amount) ORDER BY local_id ASC";
        getCommand.Parameters.AddWithValue("$messageIndex", messageIndex);
        getCommand.Parameters.AddWithValue("$amount", messageCount);
        getCommand.Parameters.AddWithValue("$roomID", roomID);

        SqliteDataReader reader = await getCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            messages.Add(new MessagesData(
                reader.GetString(2),
                reader.GetString(1),
                reader.GetInt32(0),
                reader.GetString(3)
            ));

        }
        await Clients.Caller.SendAsync("push_recieveMessages", messages);
        getCommand.Dispose();
        reader.Dispose();
    }
}