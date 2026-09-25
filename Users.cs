namespace kiwiapi;

using ImageMagick;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

using static Program;

public class Users {
    private readonly Logger logger;
    private readonly SqlInterface sql;
    private readonly HttpClient client = null!;
    private readonly string? catboxHash;

    public record User(string username, string profilePictureUrl, string description);

    public Users() {
        logger = new Logger("ATN");
        sql = new SqlInterface("auth");

        client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FruitBowlForums/1.0");

        catboxHash = Environment.GetEnvironmentVariable("CATBOX_USER_HASH");
        if (catboxHash == null) {
            logger.WARN("CATBOX_USER_HASH environment variable is not set. Catbox uploads will not work and users will fail to register.");
        }
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder group = app.MapGroup("/auth").RequireCors("FruitbowlPolicy").DisableAntiforgery();
    }

    public async Task<AuthResult> RegisterUser(string username, string password, IFormFile? profilePicture, HttpContext context) {
        if (username.Length < 3 || username.Length > 24) {
            return new AuthResult(BadRequest("Username must be between 3 and 24 characters long inclusive"), false);
        }
        if (password.Length < 8 || password.Length > 32) {
            return new AuthResult(BadRequest("Password must be between 8 and 32 characters long inclusive"), false);
        }

        if (profilePicture?.Length > 5 * 1024 * 1024) {
            logger.INFO("Failed to register user: Profile picture exceeds size limit");
            return new AuthResult(BadRequest("Profile picture must be under 5MB"), false);
        }

        SqlCommand queryCommand = sql.Command("SELECT COUNT(*) FROM users WHERE LOWER(username) = LOWER(@username) LIMIT 1",
            ("username", username)
        );
        int rows = Convert.ToInt32(await queryCommand.ExecuteGetScalar());
        if (rows > 0) {
            logger.INFO("Failed to register user: Username is already in use");
            return new AuthResult(BadRequest("That username is already in use"), false);
        }

        string userID = MakeUUID();

        string profilePictureUrl = "";
        if (profilePicture != null) {
            byte[] fileBytes;
            try {
                using (Stream incomingStream = profilePicture.OpenReadStream()) {
                    using (MemoryStream memoryStream = new MemoryStream()) {
                        await incomingStream.CopyToAsync(memoryStream);
                        fileBytes = memoryStream.ToArray();
                    }
                }

                try {
                    using MagickImageCollection collection = new MagickImageCollection(fileBytes);

                    if (collection.Count == 0) {
                        logger.INFO("Failed to register user: Empty image collection");
                        return new AuthResult(BadRequest("Invalid or corrupted image file"), false);
                    }

                    collection.Coalesce();

                    foreach (MagickImage frame in collection) {
                        if (frame.Width != 512 || frame.Height != 512) {
                            logger.INFO("Failed to register user: Frame resolution mismatch");
                            return new AuthResult(BadRequest("Resolution does not match required resolution of 512x512"), false);
                        }
                    }
                } catch (MagickException exception) {
                    logger.INFO("Failed to register user: Profile picture is malformed: \"" + exception.Message + "\"");
                    return new AuthResult(BadRequest("Invalid or corrupted image file"), false);
                }

                using ByteArrayContent content = new ByteArrayContent(fileBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(profilePicture.ContentType);

                using MultipartFormDataContent uploadRequest = new MultipartFormDataContent();
                uploadRequest.Add(new StringContent("fileupload"), "reqtype");
                uploadRequest.Add(content, "fileToUpload", "forums_profile_picture_" + userID.ToString() + Path.GetExtension(profilePicture.FileName));
                uploadRequest.Add(new StringContent(catboxHash!), "userhash");
                using HttpResponseMessage response = await client!.PostAsync("https://catbox.moe/user/api.php", uploadRequest);

                if (!response.IsSuccessStatusCode) {
                    logger.ERR("Failed to register user: Profile picture failed to upload");
                    return new AuthResult(ServerError("Failed to upload profile picture to file host service. Please try again."), false);
                }

                profilePictureUrl = await response.Content.ReadAsStringAsync();
            } catch (Exception error) {
                logger.ERR("Failed to register user: Error while processing profile picture: \"" + error.ToString() + "\"");
                return new AuthResult(ServerError("Failed to process uploaded profile picture. Please try again. If the issue persists, please contact KiwianDoesThings with your profile picture (You can see my contact info on my main website \"kiwiandoesthings.place\""), false);
            }
        }

        string passwordHash = GetHashedString(password);

        SqlCommand command = sql.Command("INSERT INTO users (id, username, password_hash, profile_picture_url, description, signature) VALUES (@id, @username, @password_hash, @profile_picture_url, @description, @signature)",
            ("id", userID),
            ("username", username),
            ("password_hash", passwordHash),
            ("profile_picture_url", profilePictureUrl),
            ("description", ""),
            ("signature", "")
        );
        await command.Execute();

        logger.INFO("Successfully registered new user with ID {" + userID + "} and name \"" + username + "\"");

        await LoginUser(context, userID, username);

        return new AuthResult(Results.Ok(new {
            id = userID
        }));
    }

    public async Task LoginUser(HttpContext context, string userID, string username) {
        List<Claim> claims = [
            new Claim(ClaimTypes.NameIdentifier, userID),
            new Claim(ClaimTypes.Name, username)
        ];

        var claimsIdentity = new ClaimsIdentity(claims, "FruitBowlAuth");
        await context.SignInAsync("FruitBowlAuth", new ClaimsPrincipal(claimsIdentity));

        context.Response.Cookies.Append("fruitbowl_user_id", userID, new CookieOptions {
            Domain = isDebug ? ".test.kiwiandoesthings.place" : ".kiwiandoesthings.place",
            HttpOnly = false,
            Secure = !isDebug,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });
        context.Response.Cookies.Append("fruitbowl_username", username, new CookieOptions {
            Domain = isDebug ? ".test.kiwiandoesthings.place" : ".kiwiandoesthings.place",
            HttpOnly = false,
            Secure = !isDebug,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });
    }

    public async Task LogoutUser(HttpContext context) {
        await context.SignOutAsync("FruitBowlAuth");
    }

    public async Task<User> GetUser(string userID) {
        SqlCommand command = sql.Command("SELECT username, profile_picture_url, description FROM users WHERE id = @id",
            ("id", userID)
        );
        List<object[]> info = await command.ExecuteGet();

        return new User((string)info[0][0], (string)info[0][1], (string)info[0][2]);
    }

    public async Task<string?> GetUserID(string username) {
        SqlCommand command = sql.Command("SELECT id FROM users WHERE username = @username",
            ("username", username)
        );
        string? userID = (string?)await command.ExecuteGetScalar()!;

        return userID;
    }

    public readonly struct AuthResult(IResult result, bool success = true) {
        public readonly IResult result = result;
        public readonly bool success = success;
    }
}
