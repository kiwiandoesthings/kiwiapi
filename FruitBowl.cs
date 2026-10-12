namespace KiwiApi;

using ImageMagick;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Security.Claims;

using static Program;

public class FruitBowl {
    private static readonly string[] allowedMimeTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    private readonly Logger logger;
    private readonly SqlInterface sql;
    private readonly HttpClient client = null!;
    private readonly string? catboxHash;

    private record RegisterRequest([FromForm] string username, [FromForm] string password, [FromForm] IFormFile? profilePicture);
    private record UpdateUserRequest([FromForm] string username, [FromForm] IFormFile? profilePicture, [FromForm] string description, [FromForm] string signature, [FromForm] string email, [FromForm] bool isEmailPublic, [FromForm] string color);
    private record LoginRequest([FromForm] string username, [FromForm] string password);

    public record User(string id, string username, string profilePictureUrl, string signature, string description, string email, bool isEmailPublic, string color, string createdAt);

    public FruitBowl(SqlInterface sql) {
        logger = new Logger(" FBL");
        this.sql = sql;

        client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FruitBowlForums/1.0");

        catboxHash = Environment.GetEnvironmentVariable("KIWIAPI_FRUITBOWL_CATBOX_USER_HASH");
        if (catboxHash == null) {
            logger.WARN("KIWIAPI_FRUITBOWL_CATBOX_USER_HASH environment variable is not set. Catbox uploads will not work and users will fail to register");
        }
    }

    public async Task ValidateUserPrincipal(CookieValidatePrincipalContext context) {
        string? userID = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userID)) {
            context.RejectPrincipal();
            await LogoutUser(context.HttpContext);
            logger.CRITICAL("Logged out desynced user with no user ID cookie");
            return;
        }

        SqlCommand command = sql.Command("SELECT COUNT(*) FROM users WHERE id = @id LIMIT 1",
            ("id", userID));
        long usersFound = (long)(await command.ExecuteGetScalar())!;

        if (usersFound == 0) {
            context.RejectPrincipal();
			await LogoutUser(context.HttpContext);
			logger.CRITICAL("Logged out desynced user with ID {" + userID + "}");
        }
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder group = app.MapGroup("/fruitbowl").RequireCors("FruitBowlPolicy").DisableAntiforgery();

        group.MapPost("/users", async ([AsParameters] RegisterRequest request, HttpContext context) => {
            string username = request.username;
            string password = request.password;
            IFormFile? profilePicture = request.profilePicture;

            if (username.Length < 3 || username.Length > 24) {
                return BadRequest("Username must be between 3 and 24 characters long inclusive");
            }
            if (password.Length < 8 || password.Length > 32) {
                return BadRequest("Password must be between 8 and 32 characters long inclusive");
            }

            if (profilePicture?.Length > 5 * 1024 * 1024) {
                logger.INFO("Failed to register user: Profile picture exceeds size limit");
                return BadRequest("Profile picture must be under 5MB");
            }

            SqlCommand queryCommand = sql.Command("SELECT COUNT(*) FROM users WHERE LOWER(username) = LOWER(@username) LIMIT 1",
                ("username", username)
            );
            long rows = (long)(await queryCommand.ExecuteGetScalar())!;
            if (rows > 0) {
                logger.INFO("Failed to register user: Username is already in use");
                return BadRequest("That username is already in use");
            }

            string userID = MakeUUID();

            string profilePictureUrl = "";
            if (profilePicture != null) {
                (IResult? result, string? url) catboxUpload = await UploadProfilePicture(profilePicture, userID);
                if (catboxUpload.result != null) {
                    return catboxUpload.result;
                }
                profilePictureUrl = catboxUpload.url!;
            }

            string passwordHash = GetHashedString(password);

            SqlCommand command = sql.Command("INSERT INTO users (id, username, password_hash, profile_picture_url, description, signature, email, email_public, color) VALUES (@id, @username, @password_hash, @profile_picture_url, @description, @signature, @email, @email_public, @color)",
                ("id", userID),
                ("username", username),
                ("password_hash", passwordHash),
                ("profile_picture_url", profilePictureUrl),
                ("description", ""),
                ("signature", ""),
                ("email", ""),
                ("email_public", false),
                ("color", "#FFFFF")
            );
            await command.Execute();

            logger.INFO("Successfully registered new user with ID {" + userID + "} and name \"" + username + "\"");

            await LoginUser(context, userID, username);

            return Results.Ok(new {
                id = userID
            });
        });

        group.MapPost("/users/self", async ([AsParameters] UpdateUserRequest request, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            if (request.username.Length < 3 || request.username.Length > 24) {
                return BadRequest("Username must be between 3 and 24 characters long inclusive");
            }

            if (request.profilePicture?.Length > 5 * 1024 * 1024) {
                logger.INFO("Failed to register user: Profile picture exceeds size limit");
                return BadRequest("Profile picture must be under 5MB");
            }

            SqlCommand queryCommand = sql.Command("SELECT COUNT(*) FROM users WHERE LOWER(username) = LOWER(@username) LIMIT 1",
                ("username", request.username));
            long rows = (long)(await queryCommand.ExecuteGetScalar())!;
            if (rows > 0) {
                logger.INFO("Failed to register user: Username is already in use");
                return BadRequest("That username is already in use");
            }

            // TODO: Better verif + on register

            SqlCommand command = sql.Command("UPDATE users SET username = @username, description = @description, signature = @signature, email = @email, email_public = @email_public, color = @color WHERE id = @id",
                ("username", request.username),
                ("description", request.description),
                ("signature", request.signature),
                ("email", request.email),
                ("email_public", request.isEmailPublic),
                ("color", "#" + request.color),
                ("id", userID));
            await command.Execute();

            if (request.profilePicture != null) {
            }

            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/users/guid/{userID:guid}", async (string userID) => {
            SqlCommand command = sql.Command("SELECT username, profile_picture_url, description, signature, email, email_public, color, created_at FROM users WHERE id = @id",
                ("id", userID)
            );
            using SqliteDataReader reader = await command.ExecuteGet();
            if (!await reader.ReadAsync()) {
                logger.INFO("User with ID {" + userID + "} was not found");
                return NotFound("User not found");
            }

            return Results.Ok(new {
                id = userID,
                username = reader.GetString(0),
                profilePictureUrl = reader.GetString(1),
                description = reader.GetString(2),
                signature = reader.GetString(3),
                email = reader.GetString(4),
                isEmailPublic = reader.GetBoolean(5),
                color = reader.GetString(6),
                createdAt = reader.GetDateTime(7)
            });
        });

        group.MapGet("/users/name/{username}", async (string username) => {
            string? userID = await GetUserID(username);
            if (userID == null) {
                logger.INFO("User with username \"" + username + "\" was not found");
                return NotFound("User not found");
            }
            User? user = await GetUser(userID);
            if (user == null) {
                logger.CRITICAL("Was able to find user ID \"" + userID + "\" via username \"" + username + "\" but was not able to locate user information via found user ID");
                return ServerError("Encountered an unexpected error. Please report to @KiwianDoesThings");
            }

            return UserToResult(user);
        });

        group.MapGet("/users/search", async (string query, bool searchType) => {
            string condition = searchType ? "LOWER(username) LIKE '%' || LOWER(@username) || '%'" : "id = @id";
            string parameterName = searchType ? "username" : "id";
            SqlCommand command = sql.Command("SELECT id, username, profile_picture_url, description, signature, created_at FROM users WHERE " + condition,
                (parameterName, query)
            );
            using SqliteDataReader reader = await command.ExecuteGet();
            List<object> results = [];

            while (await reader.ReadAsync()) {
                results.Add(new {
                    id = reader.GetString(0),
                    username = reader.GetString(1),
                    profilePictureUrl = reader.GetString(2),
                    description = reader.GetString(3),
                    signature = reader.GetString(4),
                    createdAt = reader.GetDateTime(5)
                });
            }

            return Results.Ok(results);
        });

        group.MapGet("/users/self", async (HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            User? user = await GetUser(userID);
            if (user == null) {
                logger.CRITICAL("Logged in user had invalid ID? Could not find user with ID \"" + userID + "\" provided by cookies");
                return NotFound("Could not find your user. Please log out and log back in again");
            }

            return UserToResult(user);
        }).RequireAuthorization();

        group.MapPost("/auth", async ([AsParameters] LoginRequest request, HttpContext context) => {
            SqlCommand command = sql.Command("SELECT id, password_hash FROM users WHERE LOWER(username) = LOWER(@username)",
                ("username", request.username)
            );
            using SqliteDataReader reader = await command.ExecuteGet();
            if (!await reader.ReadAsync()) {
                logger.INFO("Failed to log in user: No user with that username");
                return Unauthorized("Invalid username or password");
            }
            string userID = reader.GetString(0);
            string storedPasswordHash = reader.GetString(1);
            if (!VerifyHashedString(request.password, storedPasswordHash)) {
                logger.INFO("Failed to log in user: Incorrect password");
                return Unauthorized("Invalid username or password");
            }

            logger.INFO("Successfully logged in user with ID {" + userID + "}");
            await LoginUser(context, userID, request.username);

            return Results.Ok();
        });

        group.MapGet("/auth", async (HttpContext context) => {
            if (context.User.Identity?.IsAuthenticated == true) {
                string? username = context.User.FindFirst(ClaimTypes.Name)?.Value;
                string? userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return Results.Ok(new {
                    loggedIn = true,
                    username = username,
                    userID = userID
                });
            }

            return Results.Ok(new {
                loggedIn = false
            });
        });

        group.MapDelete("/auth", async (HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            await LogoutUser(context);

            logger.INFO("Successfully logged out user with ID {" + userID + "}");

            return Results.Ok();
        }).RequireAuthorization();
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
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });
        context.Response.Cookies.Append("fruitbowl_username", username, new CookieOptions {
            Domain = isDebug ? ".test.kiwiandoesthings.place" : ".kiwiandoesthings.place",
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });
    }

    public async Task LogoutUser(HttpContext context) {
        await context.SignOutAsync("FruitBowlAuth");

		CookieOptions deleteOptions = new CookieOptions {
			Domain = isDebug ? ".test.kiwiandoesthings.place" : ".kiwiandoesthings.place",
            HttpOnly = false,
			Secure = true,
			SameSite = SameSiteMode.None
		};
		context.Response.Cookies.Delete("fruitbowl_user_id", deleteOptions);
		context.Response.Cookies.Delete("fruitbowl_username", deleteOptions);
	}

    public async Task<User?> GetUser(string userID) {
        SqlCommand command = sql.Command("SELECT username, profile_picture_url, description, signature, email, email_public, color, created_at FROM users WHERE id = @id",
            ("id", userID)
        );
        using SqliteDataReader reader = await command.ExecuteGet();
        if (!await reader.ReadAsync()) {
            return null;
        }

        return new User(userID, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetString(6), reader.GetString(7));
    }

    public async Task<string?> GetUserID(string username) {
        SqlCommand command = sql.Command("SELECT id FROM users WHERE username = @username",
            ("username", username)
        );
        string? userID = (string?)await command.ExecuteGetScalar()!;

        return userID;
    }

    public async Task<bool> IsUserValid(string userID) {
        SqlCommand command = sql.Command("SELECT COUNT(*) FROM users WHERE id = @id LIMIT 1",
            ("id", userID));

        return (long)(await command.ExecuteGetScalar())! > 0;
    }

    public IResult UserToResult(User user) {
        return Results.Ok(new {
            id = user.id,
            username = user.username,
            profilePictureUrl = user.profilePictureUrl,
            description = user.description,
            signature = user.signature,
            email = user.email,
            isEmailPublic = user.isEmailPublic,
            color = user.color,
            createdAt = user.createdAt
        });
    }

    public async Task<bool> DeleteUser(string userID) {
        SqlCommand command = sql.Command("DELETE FROM users WHERE id = @id LIMIT 1",
            ("id", userID));
        return Convert.ToInt32(command.ExecuteGetScalar()) > 0;
    }

    public async Task<(IResult?, string?)> UploadProfilePicture(IFormFile profilePicture, string userID) {
        if (!allowedMimeTypes.Contains(profilePicture.ContentType.ToLower())) {
            return (BadRequest("Profile picture is not one of the allowed types: png, jpg/jpeg, gif, webp"), null);
        }

		string profilePictureUrl = "";
	    byte[] fileBytes;
	    try {
	    	using (Stream incomingStream = profilePicture.OpenReadStream()) {
	    		using MemoryStream memoryStream = new MemoryStream();
	    		await incomingStream.CopyToAsync(memoryStream);
	    		fileBytes = memoryStream.ToArray();
	    	}

	    	try {
	    		using MagickImageCollection collection = new MagickImageCollection(fileBytes);

	    		if (collection.Count == 0) {
	    			logger.INFO("Failed to register user: Empty image collection");
	    			return (BadRequest("Invalid or corrupted image file"), null);
	    		}

	    		collection.Coalesce();

	    		foreach (MagickImage frame in collection.Cast<MagickImage>()) {
	    			if (frame.Width != 512 || frame.Height != 512) {
	    				logger.INFO("Failed to register user: Frame resolution mismatch");
	    				return (BadRequest("Resolution does not match required resolution of 512x512"), null);
					}
	    		}
	    	} catch (MagickException exception) {
	    		logger.INFO("Failed to register user: Profile picture is malformed: \"" + exception.Message + "\"");
	    		return (BadRequest("Invalid or corrupted image file"), null);
	    	}

	    	using ByteArrayContent content = new ByteArrayContent(fileBytes);
	    	content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(profilePicture.ContentType);

	    	using MultipartFormDataContent uploadRequest = new MultipartFormDataContent();
	    	uploadRequest.Add(new StringContent("fileupload"), "reqtype");
	    	uploadRequest.Add(content, "fileToUpload", "forums_profile_picture_" + userID + Path.GetExtension(profilePicture.FileName));
	    	uploadRequest.Add(new StringContent(catboxHash!), "userhash");
	    	using HttpResponseMessage response = await client!.PostAsync("https://catbox.moe/user/api.php", uploadRequest);

	    	if (!response.IsSuccessStatusCode) {
	    		logger.ERR("Failed to register user: Profile picture failed to upload");
	    		return (ServerError("Failed to upload profile picture to file host service. Please try again."), null);
	    	}

	    	profilePictureUrl = await response.Content.ReadAsStringAsync();
	    } catch (Exception error) {
	    	logger.ERR("Failed to register user: Error while processing profile picture: \"" + error.ToString() + "\"");
	    	return (ServerError("Failed to process uploaded profile picture. Please try again. If the issue persists, please contact KiwianDoesThings with your profile picture (You can see my contact info on my main website \"kiwiandoesthings.place\""), null);
	    }

        return (null, profilePictureUrl);
	}
}
