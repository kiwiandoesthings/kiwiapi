namespace kiwiapi.ProtoCall;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;
using System.Net;
using System.Security.Claims;

using static Program;
using static ProtoCallHub;

public class ProtocallApi {
    private static readonly string[] allowedMimeTypes = { "image/jpeg", "image/png", "image/gif" };

    private readonly Logger logger;
    private readonly string? catboxHash;
    private readonly SqlInterface sql;
    private readonly SocketsHttpHandler handler = null!;
    private readonly HttpClient client = null!;

    public ProtocallApi(Logger logger, string? catboxHash, SqlInterface sql) {
        this.logger = logger;
        this.catboxHash = catboxHash;
        this.sql = sql;

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

            using SqlCommand registerCommand = sql.Command("INSERT INTO users (user_id, username, color, password_hash, profile_picture_link) VALUES (@user_id, @username, @color, @password_hash, @profile_picture_link)",
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
            using SqlCommand queryCommand = sql.Command("SELECT username, color, created_at FROM users WHERE user_id = @user_id",
                ("user_id", userID));
            List<object[]> info = await queryCommand.ExecuteGet();

            if (info.Count == 0) {
                return NotFound("No user with that ID was found");
            }

            return Results.Ok(new {
                userUsername = info[0][0],
                userColor = info[0][1],
                createdAt = info[0][2]
            });
        });

        api.MapGet("/request_userProfile", async (string userID) => {
            using SqlCommand queryCommand = sql.Command("SELECT profile_picture_link, about_me FROM users WHERE user_id = @user_id",
                ("user_id", userID));
            List<object[]> info = await queryCommand.ExecuteGet();

            if (info.Count > 0) {
                string profilePictureUrl = (string)info[0][0] == "" ? "none" : (string)info[0][0];
                string aboutMe = (string)info[0][1];

                return Results.Ok(new {
                    profilePictureUrl = profilePictureUrl,
                    aboutMe = aboutMe
                });
            }

            return BadRequest("No user with that ID was found");
        });

        api.MapGet("/request_roomID", async (string roomName) => {
            using SqlCommand queryCommand = sql.Command("SELECT id FROM rooms WHERE name = @name",
                ("name", roomName));
            List<object[]> info = await queryCommand.ExecuteGet();

            if (info.Count > 0) {
                return Results.Ok(new {
                    roomID = Convert.ToInt32(info[0][0])
                });
            }

            return NotFound("No room with that name was found");
        });

        api.MapGet("/request_roomInfo", async (int roomID) => {
            using SqlCommand queryCommand = sql.Command("SELECT name, author_id, privacy, created_at FROM rooms WHERE id = @room_id",
                ("room_id", roomID));
            List<object[]> info = await queryCommand.ExecuteGet();

            if (info.Count > 0) {
                return Results.Ok(new {
                    roomName = (string)info[0][0],
                    authorID = (string)info[0][1],
                    privacy = (string)info[0][2],
                    createdAt = (string)info[0][3]
                });
            }

            return NotFound("No room with that ID was found");
        });

        api.MapGet("/request_roomSearch", async (HttpContext context, string targetName) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            using SqlCommand queryCommand = sql.Command("SELECT id, name FROM rooms LEFT JOIN roomAccess ON id = room_id AND user_id = @user_id WHERE name LIKE @name AND name != 'HomeRoom' AND (privacy = 'PUBLIC' OR (privacy = 'PRIVATE' AND access_level >= 0))",
                ("name", "%" + targetName + "%"),
                ("user_id", userID));
            List<object[]> info = await queryCommand.ExecuteGet();

            List<RoomResult> results = new();
            foreach (object[] row in info) {
                results.Add(new RoomResult((string)row[1], Convert.ToInt32(row[0])));
            }

            return Results.Ok(results);
        });

        api.MapPost("/push_deleteAccount", async (HttpContext context) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            logger.INFO("Attempting to delete account with userID \"" + userID + "\"");

            using SqlCommand deleteCommand = sql.Command("DELETE FROM users WHERE user_id = @user_id",
                ("user_id", userID));
            await deleteCommand.Execute();

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

            using SqlCommand roomCommand = sql.Command("UPDATE rooms SET privacy = @privacy WHERE id = @id",
                ("privacy", newPrivacy.ToUpper()),
                ("id", roomID));
            await roomCommand.Execute();

            return Results.Ok();
        });

        api.MapGet("/request_accessLevel", async (string userID, int roomID) => {
            return Results.Ok(new {
                accessLevel = UserAccessLevelInRoom(userID, roomID)
            });
        });

        api.MapGet("/request_usersWithAccessLevel", async (int accessLevel, int roomID) => {
            using SqlCommand queryCommand = sql.Command("SELECT user_id FROM roomAccess WHERE room_id = @room_id AND access_level = @access_level",
                ("room_id", roomID),
                ("access_level", accessLevel));
            List<object[]> info = await queryCommand.ExecuteGet();

            List<string> results = new();
            foreach (object[] row in info) {
                results.Add((string)row[0]);
            }

            return Results.Ok(results);
        });

        api.MapGet("/request_userID", async (string userName) => {
            using SqlCommand queryCommand = sql.Command("SELECT user_id FROM users WHERE username = @username",
                ("username", userName));
            List<object[]> info = await queryCommand.ExecuteGet();

            if (info.Count > 0) {
                return Results.Ok(new {
                    userID = (string)info[0][0]
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

            using SqlCommand setCommand = sql.Command("INSERT INTO roomAccess (room_id, user_id, access_level) VALUES (@room_id, @user_id, @access_level) ON CONFLICT(room_id, user_id) DO UPDATE SET access_level = @access_level",
                ("room_id", roomID),
                ("user_id", otherID),
                ("access_level", accessLevel));
            await setCommand.Execute();

            return Results.Ok();
        });

        api.MapPost("/push_setUserUsername", async (HttpContext context, string newUsername) => {
            if (!AuthenticateUser(context, out string? userID, out IResult? result)) {
                return result;
            }

            if (!ValidUsername(newUsername)) {
                return BadRequest("Username can only use \"A-z, 0-9, -, _\", and must be greater than 3 characters and shorter than 18. You may not name yourself \"System\" or \"Unknown User\".");
            }

            using SqlCommand command = sql.Command("UPDATE users SET username = @username WHERE user_id = @user_id",
                ("username", newUsername),
                ("user_id", userID));
            await command.Execute();

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

            using SqlCommand queryCommand = sql.Command("SELECT author_id FROM messages WHERE id = @id",
                ("id", messageID));
            List<object[]> info = await queryCommand.ExecuteGet();

            if (info.Count > 0) {
                isOwnMessage = (string)info[0][0] == userID;
            }

            if (isDeletion) {
                if (!isOwnMessage && !hasModDeletionPermissions) {
                    return Unauthorized("You do not have permissions to delete other users' messages!");
                }

                using SqlCommand deleteCommand = sql.Command("DELETE FROM messages WHERE id = @id",
                    ("id", messageID));
                await deleteCommand.Execute();
            } else {
                if (!isOwnMessage) {
                    return Unauthorized("You cannot edit other users' messages!");
                }

                using SqlCommand editCommand = sql.Command("UPDATE messages SET content = @content WHERE id = @id",
                    ("content", newMessageContent),
                    ("id", messageID));
                await editCommand.Execute();
            }

            return Results.Ok();
        });
    }
}
