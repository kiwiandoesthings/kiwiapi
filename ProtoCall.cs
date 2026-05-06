namespace kiwiapi;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

public class ProtoCall : Hub {
    public record MessagesData(string authorID, string content, int messageIndex, string messageTimestamp);

    public async Task push_sendMessage(string userID, string userSecret, string message, string messageTimestamp, int roomID) {
        Console.WriteLine("Got message from userID: " + userID + " with secret: " + userSecret + " with content: \"" + message + "\"");

        string realSecret = await GetUserSecret(userID);
        if (realSecret != userSecret) {
            Console.WriteLine("Passed secret did not match real secret of " + realSecret);
            await Clients.Caller.SendAsync("push_serverMessage", "Server could not authenticate your message, please clear your cookies and log in again");
            return;
        }

        if (await UserAccessLevelInRoom(userID, roomID) < 0) {
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
        if (await UserAccessLevelInRoom(userID, roomID) < 0) {
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

    public async Task push_createRoom(string roomName, string userID, string userSecret) {
        string realSecret = await GetUserSecret(userID);
        if (realSecret != userSecret) {
            Console.WriteLine("Passed secret did not match real secret of " + realSecret);
            await Clients.Caller.SendAsync("push_serverMessage", "Server could not authenticate your request, please clear your cookies and log in again");
            return;
        }
        if (roomName.Length > 25) {
            return;
        }

        SqliteCommand roomCommand = Program.database!.CreateCommand();
        roomCommand.CommandText = "INSERT INTO rooms (name, author_id, admin_ids) VALUES ($roomName, $authorID, $adminIDs); SELECT last_insert_rowid();";
        roomCommand.Parameters.AddWithValue("$roomName", roomName);
        roomCommand.Parameters.AddWithValue("$authorID", userID);
        roomCommand.Parameters.AddWithValue("$adminIDs", userID + ",");
        int roomID = (int)(long)roomCommand.ExecuteScalar()!;
        roomCommand.Dispose();

        await Clients.Caller.SendAsync("push_recieveRoom", roomName, roomID);
    }

    public async Task push_setRoomPrivacy(int roomID, string newPrivacy, string userID, string userSecret) {
        string realSecret = await GetUserSecret(userID);
        if (realSecret != userSecret) {
            Console.WriteLine("Passed secret did not match real secret of " + realSecret);
            await Clients.Caller.SendAsync("push_serverMessage", "Server could not authenticate your request, please clear your cookies and log in again");
            return;
        }

        if (await UserAccessLevelInRoom(userID, roomID) < 2) {
            await Clients.Caller.SendAsync("push_serverMessage", "You do not have permission to set room privacy!");
            return;
        }

        newPrivacy = newPrivacy.ToLower();
        if (newPrivacy != "public" && newPrivacy != "private") {
            await Clients.Caller.SendAsync("push_serverMessage", "Cannot set room privacy to \"" + newPrivacy +"\", newPrivacy value must be either PUBLIC or PRIVATE!");
            return;
        }

        SqliteCommand roomCommand = Program.database!.CreateCommand();
        roomCommand.CommandText = "UPDATE rooms SET privacy = $newPrivacy WHERE id = $roomID";
        roomCommand.Parameters.AddWithValue("$newPrivacy", newPrivacy.ToUpper());
        roomCommand.Parameters.AddWithValue("$roomID", roomID);
        roomCommand.ExecuteNonQuery();
        roomCommand.Dispose();
    }

    public static async Task<string> GetUserSecret(string userID) {
        SqliteCommand getCommand = Program.database!.CreateCommand();
        getCommand.CommandText = "SELECT secret FROM users WHERE user_id = $userID LIMIT 1";
        getCommand.Parameters.AddWithValue("$userID", userID);
        object? result = await getCommand.ExecuteScalarAsync()!;
        getCommand.Dispose();

        if (result != null && result != DBNull.Value) {
            getCommand.Dispose();
            return result.ToString()!.TrimEnd("=").ToString();
        }

        return "-1";
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
}
