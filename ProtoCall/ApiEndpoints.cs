namespace kiwiapi.ProtoCall;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using System.Net;
using System.Security.Claims;

using static Program;
using static ProtoCallHub;

public class ProtocallApi {
    private readonly Logger logger;
    private readonly string? catboxHash;
    private readonly SqlInterface sql;
    private readonly SocketsHttpHandler handler = null!;
    private readonly HttpClient client = null!;
    private readonly SqliteConnection database = null;

    public ProtocallApi(Logger logger, string? catboxHash) {
        this.logger = logger;
        this.catboxHash = catboxHash;

        string databasePath = Path.Combine(AppContext.BaseDirectory, "protocall.db");
        string connectionString = "Data Source=" + databasePath;
        sql = new SqlInterface(connectionString);

        if (!File.Exists(databasePath)) {
            throw new FileNotFoundException("Couldn't find \"protocall.db\" at \"" + databasePath + "\"");
        }

        if (catboxHash == null) {
            logger.WARN("Could not find environment variable \"CATBOX_USER_HASH\"");
            return;
        }

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
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken);
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, true);
            }
        };

        client = new HttpClient(handler);
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder api = app.MapGroup("/protocall").RequireCors("ProtoCallPolicy");

        api.MapPost("/push_registerAccount", async (HttpContext context, [FromForm] string username, [FromForm] string password, [FromForm] string color, [FromForm] IFormFile? profilePicture) => {
            logger.INFO("Attempting registration with username \"" + username + "\", password \"" + password + "\", and color \"" + color + "\"");

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
                logger.ERR("Error while processing profile picture: \"" + error.ToString() + "\"");
                return ServerError("Failed to process uploaded profile picture. Please try again. If the issue persists, please contact KiwianDoesThings with your profile picture (You can see my contact info on my main website \"kiwiandoesthings.place\"");
            }

            using SqlCommand queryCommand = sql.Command("SELECT username FROM users WHERE username = @username",
                ("username", username));
            List<object[]> results = await queryCommand.ExecuteGet();
            if (results.Count > 0) {
                return BadRequest("There is already a user with that username.");
            }

            using SqlCommand registerCommand = sql.Command("INSERT INTO users (user_id, username, color, password_hash, profile_picture_link) VALUES ($user_id, $username, $color, $password_hash, $profile_picture_link)",
                ("user_id", userID.ToString()),
                ("username", username),
                ("color", color),
                ("password_hash", GetHashedString(password)),
                ("profile_picture_link", profilePictureUrl));
            await registerCommand.Execute();

            return Results.Ok(new {
                userID = userID.ToString(),
            });
        }).DisableAntiforgery();


        api.MapGet("/request_loginInfo", async (HttpContext context, string username, string password) => {
            logger.INFO("Attempted login with username: " + username + " and password: " + password);

            using SqlCommand getHashCommand = sql.Command("SELECT user_id, password_hash FROM users WHERE username = @username",
                ("username", username));
            List<object[]> result = await getHashCommand.ExecuteGet();

            string userID;
            if (result.Count > 0) {
                userID = (string)result[0][0];
                string storedHash = (string)result[0][1];

                if (!VerifyHashedString(password, storedHash)) {
                    logger.ERR("Failed to login. Invalid password for username \"" + username + "\"");
                    return Unauthorized("Incorrect credentials");
                }
            } else {
                logger.ERR("Failed to login. No username matching \"" + username + "\"");
                return Unauthorized("Incorrect credentials");
            }

            var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userID) };
            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity));

            return Results.Ok(new {
                userID = userID
            });
        });

        api.MapPost("/push_logout", async (HttpContext context) => {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        api.MapGet("/request_userInfo", async (string userID) => {
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

        api.MapGet("/request_userProfile", async (string userID) => {
            using SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT profile_picture_link, about_me FROM users WHERE user_id = $userID";
            queryCommand.Parameters.AddWithValue("$userID", userID);
            using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync()) {
                string? profilePictureUrl = await reader.IsDBNullAsync(0) ? "none" : reader.GetString(0);
                string aboutMe = await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1);

                return Results.Ok(new {
                    profilePictureUrl = profilePictureUrl,
                    aboutMe = aboutMe
                });
            }

            return BadRequest("No user with that ID was found");
        });

        api.MapGet("/request_roomID", async (string roomName) => {
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

        api.MapGet("/request_roomInfo", async (int roomID) => {
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

        api.MapGet("/request_roomSearch", async (HttpContext context, string targetName) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            using SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT id, name FROM rooms LEFT JOIN roomAccess ON id = room_id AND user_id = $userID WHERE name LIKE $targetName AND name != 'HomeRoom' AND (privacy = 'PUBLIC' OR (privacy = 'PRIVATE' AND access_level >= 0))";
            queryCommand.Parameters.AddWithValue("$targetName", "%" + targetName + "%");
            queryCommand.Parameters.AddWithValue("$userID", userID);
            using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            List<RoomResult> results = new();
            while (await reader.ReadAsync()) {
                results.Add(new RoomResult(reader.GetString(1), reader.GetInt32(0)));
            }

            return Results.Ok(results);
        });

        api.MapPost("/push_deleteAccount", async (HttpContext context) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            logger.INFO("Attempting to delete account with userID \"" + userID + "\"");

            using SqliteCommand deleteCommand = database!.CreateCommand();
            deleteCommand.CommandText = "DELETE FROM users WHERE user_id = $userID";
            deleteCommand.Parameters.AddWithValue("$userID", userID);
            await deleteCommand.ExecuteNonQueryAsync();

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Ok();
        });

        api.MapPost("/push_createRoomPersonal", async (HttpContext context, string otherID) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            int roomID = await CreateRoom(userID + " " + otherID, userID, false);


            return Results.Ok(new {
                roomID = roomID,
            });
        });

        api.MapPost("/push_createRoom", async (HttpContext context, string roomName) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            if (await GetRoomID(roomName) != -1) {
                return BadRequest("A room with that name already exists!");
            }
            int roomID = await CreateRoom(roomName, userID, true);

            return Results.Ok(new {
                roomID = roomID
            });
        });

        api.MapPost("/push_setRoomPrivacy", async (HttpContext context, int roomID, string newPrivacy) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            if (await UserAccessLevelInRoom(userID, roomID) < 2) {
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

        api.MapGet("/request_accessLevel", async (string userID, int roomID) => {
            return Results.Ok(new {
                accessLevel = UserAccessLevelInRoom(userID, roomID)
            });
        });

        api.MapGet("/request_usersWithAccessLevel", async (int accessLevel, int roomID) => {
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

        api.MapGet("/request_userID", async (string userName) => {
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

        api.MapPost("/push_setUserAccess", async (HttpContext context, string otherID, int accessLevel, int roomID) => {
            if (!await GetUserIDExists(otherID)) {
                return BadRequest("No user with that ID was found");
            }

            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            if (await UserAccessLevelInRoom(userID, roomID) < 2) {
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

        api.MapPost("/push_setUserUsername", async (HttpContext context, string newUsername) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            if (!ValidUsername(newUsername)) {
                return BadRequest("Username can only use \"A-z, 0-9, -, _\", and must be greater than 3 characters and shorter than 18. You may not name yourself \"System\" or \"Unknown User\".");
            }

            using SqliteCommand command = database!.CreateCommand();
            command.CommandText = "UPDATE users SET username = $newUsername WHERE user_id = $userID";
            command.Parameters.AddWithValue("$newUsername", newUsername);
            command.Parameters.AddWithValue("$userID", userID);
            await command.ExecuteNonQueryAsync();

            return Results.Ok();
        });

        api.MapPost("/push_editMessage", async (HttpContext context, int roomID, int messageID, string newMessageContent, bool isDeletion) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            bool isOwnMessage = false;
            bool hasModDeletionPermissions = false;

            if (await UserAccessLevelInRoom(userID, roomID) >= 1) {
                hasModDeletionPermissions = true;
            }

            SqliteCommand queryCommand = database!.CreateCommand();
            queryCommand.CommandText = "SELECT author_id FROM MESSAGES where id = $messageID";
            queryCommand.Parameters.AddWithValue("$messageID", messageID);
            using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync()) {
                isOwnMessage = reader.GetString(0) == userID;
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
}
