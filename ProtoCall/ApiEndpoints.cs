namespace KiwiApi.ProtoCall;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Security.Claims;

using static Globals;
using static Program;
using static ProtoCallHub;

public class ProtoCallApi {
    private readonly Logger logger;
    private readonly SqlInterface sql;

    private record NewRoomRequest(string name, bool isPublic);
    private record UpdateRoomRequest(bool isPublic);

    public ProtoCallApi(SqlInterface sql) {
        logger = new Logger("PTCA");
        this.sql = sql;
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder group = app.MapGroup("/protocall").RequireCors("FruitBowlPolicy");

        group.MapGet("/rooms/{roomID:guid}", async (int roomID) => {
            using SqlCommand queryCommand = sql.Command("SELECT name, author_id, privacy, created_at FROM protocall_rooms WHERE id = @id",
                ("id", roomID));
            using SqliteDataReader reader = await queryCommand.ExecuteGet();

            if (!await reader.ReadAsync()) {
                return NotFound("No room with that ID was found");
            }

            return Results.Ok(new {
                roomName = reader.GetString(0),
                authorID = reader.GetString(1),
                privacy = reader.GetString(2),
                createdAt = reader.GetString(3)
            });
        });

        group.MapGet("/rooms/known", async (HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            using SqlCommand command = sql.Command("SELECT room.id, room.name, room.description, room.total_messages FROM protocall_room_access access LEFT JOIN protocall_rooms room ON access.room_id = room.id WHERE access.user_id = @user_id AND access.access >= 0",
                ("user_id", userID));
            using SqliteDataReader reader = await command.ExecuteGet();
            List<object> results = [];

            while (await reader.ReadAsync()) {
                results.Add(new {
                    id = reader.GetString(0),
                    name = reader.GetString(1),
                    description = reader.GetString(2),
                    totalMessages = reader.GetInt64(3)
                });
            }

            return Results.Ok(new {
                rooms = results
            });

        }).RequireAuthorization();

        group.MapGet("/rooms/search", async (string query, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            using SqlCommand queryCommand = sql.Command("SELECT id, name FROM protocall_rooms LEFT JOIN protocall_room_access ON id = room_id AND user_id = @user_id WHERE name LIKE @name AND (is_public OR (NOT is_public AND access >= 0))",
                ("name", "%" + query + "%"),
                ("user_id", userID));
            using SqliteDataReader reader = await queryCommand.ExecuteGet();

            List<RoomResult> results = [];
            while (await reader.ReadAsync()) {
                results.Add(new RoomResult(reader.GetString(0), reader.GetString(1)));
            }

            return Results.Ok(results);
        }).RequireAuthorization();

        group.MapPost("/rooms", async([FromForm] NewRoomRequest request, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            if (await GetRoomID(request.name) != -1) {
                return BadRequest("A room with that name already exists!");
            }
            int roomID = await CreateRoom(request.name, request.isPublic, userID);

            return Results.Ok(new {
                roomID = roomID
            });
        }).RequireAuthorization();

        group.MapPost("/rooms/{roomID:guid}", async (string roomID, [FromForm] UpdateRoomRequest request, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            if (await UserAccessLevelInRoom(userID, roomID) < 2) {
                return Unauthorized("You do not have moderator permissions in this room!");
            }

            using SqlCommand roomCommand = sql.Command("UPDATE protocall_rooms SET privacy = @privacy WHERE id = @id",
                ("privacy", request.isPublic),
                ("id", roomID));
            await roomCommand.Execute();

            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/rooms/{roomID:guid}/access/self", async (string roomID, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            return Results.Ok(new {
                accessLevel = UserAccessLevelInRoom(userID, roomID)
            });
        }).RequireAuthorization();

        group.MapGet("/rooms/{roomID:guid}/access/level/{level}", async (string roomID, int level) => {
            using SqlCommand queryCommand = sql.Command("SELECT user_id FROM protocall_room_access WHERE room_id = @room_id AND access = @access",
                ("room_id", roomID),
                ("access", level));
            using SqliteDataReader reader = await queryCommand.ExecuteGet();

            List<string> results = [];
            while (await reader.ReadAsync()) {
                results.Add(reader.GetString(0));
            }

            return Results.Ok(results);
        });

        group.MapPost("/rooms/{roomID:guid}/access/user/{otherUserID:guid}", async (string roomID, string otherUserID, [FromForm] int level, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            if (!await users.IsUserValid(otherUserID)) {
                return BadRequest("No user with that ID was found");
            }

            if (await UserAccessLevelInRoom(userID, roomID) < 2) {
                return Unauthorized("You do not have moderator permissions in this room!");
            }

            using SqlCommand setCommand = sql.Command("INSERT INTO protocall_room_access (room_id, user_id, access) VALUES (@room_id, @user_id, @access) ON CONFLICT(room_id, user_id) DO UPDATE SET access_level = @access_level",
                ("room_id", roomID),
                ("user_id", otherUserID),
                ("access", level));
            await setCommand.Execute();

            return Results.Ok();
        }).RequireAuthorization();

        group.MapPost("/rooms/{roomID:guid}/messages/{messageID}/content", async (string roomID, int messageID, [FromBody] string newMessageContent, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            bool isOwnMessage = false;
            bool hasModDeletionPermissions = false;

            if (await UserAccessLevelInRoom(userID, roomID) >= 1) {
                hasModDeletionPermissions = true;
            }

            using SqlCommand queryCommand = sql.Command("SELECT author_id FROM protocall_messages WHERE id = @id",
                ("id", messageID));
            using SqliteDataReader reader = await queryCommand.ExecuteGet();

            if (await reader.ReadAsync()) {
                isOwnMessage = reader.GetString(0) == userID;
            }

            if (newMessageContent.Length == 0) {
                if (!isOwnMessage && !hasModDeletionPermissions) {
                    return Unauthorized("You do not have permissions to delete other users' messages!");
                }

                using SqlCommand deleteCommand = sql.Command("DELETE FROM protocall_messages WHERE id = @id",
                    ("id", messageID));
                await deleteCommand.Execute();
            } else {
                if (!isOwnMessage) {
                    return Unauthorized("You cannot edit other users' messages!");
                }

                using SqlCommand editCommand = sql.Command("UPDATE protocall_messages SET content = @content WHERE id = @id",
                    ("content", newMessageContent),
                    ("id", messageID));
                await editCommand.Execute();
            }

            return Results.Ok();
        }).RequireAuthorization();
    }
}
