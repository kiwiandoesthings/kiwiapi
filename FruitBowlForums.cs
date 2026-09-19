namespace kiwiapi;

using ImageMagick;
using Markdig;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Data.SqlTypes;
using System.Net;
using System.Reflection.Metadata;
using System.Security.Claims;

using static Program;

public class FruitBowlForums {
    private readonly Logger logger;
    private readonly SqlInterface sql;
    private readonly MarkdownPipeline markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private readonly HttpClient client = null!;
    private readonly string? catboxHash;

    private record RegisterRequest([FromForm] string username, [FromForm] string password, [FromForm] IFormFile? profilePicture);
    private record UpdateUserRequest([FromForm] string username, [FromForm] string description, [FromForm] string signature);
    private record NewForumRequest([FromForm] string name, [FromForm] string description);
    private record NewTopicRequest([FromForm] string name, [FromForm] string description);
    private record NewPostRequest([FromForm] string content, [FromForm] bool showSignature = false);

    public FruitBowlForums() {
        logger = new Logger("FBF");
        sql = new SqlInterface("fruitbowlforums");

        client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FruitBowlForums/1.0");

        catboxHash = Environment.GetEnvironmentVariable("CATBOX_USER_HASH");
        if (catboxHash == null) {
            logger.WARN("CATBOX_HASH environment variable is not set. Catbox uploads will not work and users will fail to register.");
        }
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder group = app.MapGroup("/fruitbowl").RequireCors("ForumsPolicy").DisableAntiforgery();

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
            int rows = Convert.ToInt32(await queryCommand.ExecuteGetScalar());
            if (rows > 0) {
                logger.INFO("Failed to register user: Username is already in use");
                return BadRequest("That username is already in use");
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
                            return BadRequest("Invalid or corrupted image file");
                        }

                        collection.Coalesce();

                        foreach (MagickImage frame in collection) {
                            if (frame.Width != 512 || frame.Height != 512) {
                                logger.INFO("Failed to register user: Frame resolution mismatch");
                                return BadRequest("Resolution does not match required resolution of 512x512");
                            }
                        }
                    } catch (MagickException exception) {
                        logger.INFO("Failed to register user: Profile picture is malformed: \"" + exception.Message + "\"");
                        return BadRequest("Invalid or corrupted image file");
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
                        return ServerError("Failed to upload profile picture to file host service. Please try again.");
                    }

                    profilePictureUrl = await response.Content.ReadAsStringAsync();
                } catch (Exception error) {
                    logger.ERR("Failed to register user: Error while processing profile picture: \"" + error.ToString() + "\"");
                    return ServerError("Failed to process uploaded profile picture. Please try again. If the issue persists, please contact KiwianDoesThings with your profile picture (You can see my contact info on my main website \"kiwiandoesthings.place\"");
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

            return Results.Ok(new {
                id = userID
            });
        });

        group.MapGet("/users/{userID}", async (string userID) => {
            SqlCommand command = sql.Command("SELECT username, profile_picture_url, description, signature, created_at FROM users WHERE id = @id", 
                ("id", userID)
            );
            List<object[]> result = await command.ExecuteGet();
            if (result.Count == 0) {
                logger.INFO("User with ID {" + userID + "} was not found");
                return NotFound("User not found");
            }

            return Results.Ok(new {
                id = userID,
                username = (string)result[0][0],
                profile_picture_url = (string)result[0][1],
                description = (string)result[0][2],
                signature = (string)result[0][3],
                created_at = (string)result[0][4]
            });
        });

        group.MapGet("/users/name/{username}", async (string username) => {
            SqlCommand command = sql.Command("SELECT id, profile_picture_url, description, signature, created_at FROM users WHERE LOWER(username) = LOWER(@username)",
                ("username", username)
            );
            List<object[]> result = await command.ExecuteGet();
            if (result.Count == 0) {
                logger.INFO("User with username \"" + username + "\" was not found");
                return NotFound("User not found");
            }

            return Results.Ok(new {
                id = (string)result[0][0],
                username = username,
                profile_picture_url = (string)result[0][1],
                description = (string)result[0][2],
                signature = (string)result[0][3],
                created_at = (string)result[0][4]
            });
        });

        group.MapGet("/users/search", async (string query, bool searchType) => {
            string condition = searchType ? "LOWER(username) LIKE '%' || LOWER(@username) || '%'" : "id = @id";
            string parameterName = searchType ? "username" : "id";
            SqlCommand command = sql.Command("SELECT id, username, profile_picture_url, description, signature, created_at FROM users WHERE " + condition,
                (parameterName, query)
            );
            List<object[]> results = await command.ExecuteGet();

            return Results.Ok(results.Select(row => new {
                id = (string)row[0],
                username = (string)row[1],
                profile_picture_url = (string)row[2],
                description = (string)row[3],
                signature = (string)row[4],
                created_at = (string)row[5],
            }).ToList());
        });

        group.MapGet("/users/self", async (HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            SqlCommand command = sql.Command("SELECT username, profile_picture_url, description, signature FROM users WHERE id = @id",
                ("id", userID)
            );
            List<object[]> result = await command.ExecuteGet();
            if (result.Count == 0) {
                logger.ERR("User with ID \"" + userID + "\" was not found on a /self method");
                return NotFound("User not found");
            }

            return Results.Ok(new {
                id = userID,
                username = (string)result[0][0],
                profile_picture_url = (string)result[0][1],
                description = (string)result[0][2],
                signature = (string)result[0][3]
            });
        }).RequireAuthorization();

        group.MapPut("/users/self", async ([AsParameters] UpdateUserRequest request, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            SqlCommand command = sql.Command("UPDATE users SET username = @username, description = @description, signature = @signature WHERE id = @id",
                ("username", request.username),
                ("description", request.description),
                ("signature", request.signature),
                ("id", userID)
            );
            await command.Execute();

            await LoginUser(context, userID, request.username);

            return Results.Ok();
        }).RequireAuthorization();

        group.MapPut("/users/self/password", async ([FromForm] string password, HttpContext context) => {
            if (password.Length < 8 || password.Length > 32) {
                return BadRequest("Password must be between 8 and 32 characters long inclusive");
            }

            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            SqlCommand queryPasswordCommand = sql.Command("SELECT password_hash FROM users WHERE id = @id",
                ("id", userID)
            );
            string oldPasswordHash = queryPasswordCommand.ExecuteGet().Result[0][0].ToString()!;

            if (VerifyHashedString(password, oldPasswordHash)) {
                return BadRequest("New password cannot be the same as the old password");
            }

            string passwordHash = GetHashedString(password);

            SqlCommand command = sql.Command("UPDATE users SET password_hash = @password_hash WHERE id = @id",
                ("password_hash", passwordHash),
                ("id", userID)
            );

            await command.Execute();

            return Results.Ok();
        }).RequireAuthorization();

        group.MapDelete("/users/self", async (HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            SqlCommand command = sql.Command("DELETE FROM users WHERE id = @id",
                ("id", userID)
            );
            await command.Execute();

            await LogoutUser(context);

            return Results.Ok();
        }).RequireAuthorization();

        group.MapPost("/auth", async (string username, string password, HttpContext context) => {
            SqlCommand command = sql.Command("SELECT id, password_hash FROM users WHERE LOWER(username) = LOWER(@username)",
                ("username", username)
            );
            List<object[]> result = await command.ExecuteGet();
            if (result.Count == 0) {
                logger.INFO("Failed to log in user: No user with that username");
                return Unauthorized("Invalid username or password");
            }
            string userID = (string)result[0][0];
            string storedPasswordHash = (string)result[0][1];
            if (!VerifyHashedString(password, storedPasswordHash)) {
                logger.INFO("Failed to log in user: Incorrect password");
                return Unauthorized("Invalid username or password");
            }

            logger.INFO("Successfully logged in user with ID {" + userID + "}");
            await LoginUser(context, userID, username);

            return Results.Ok();
        });

        group.MapGet("/auth", async (HttpContext context) => {
            if (context.User.Identity?.IsAuthenticated == true) {
                string? username = context.User.FindFirst(ClaimTypes.Name)?.Value;
                return Results.Ok(new { 
                    loggedIn = true, 
                    username = username 
                });
            }

            return Results.Ok(new {
                loggedIn = false
            });
        });

        group.MapDelete("/auth", async (HttpContext context) => {
            await LogoutUser(context);

            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/forums", async (string? forum, HttpContext context) => {
            if (forum == null) {
                SqlCommand allCommand = sql.Command("SELECT id, name, description, creator_id, created_at FROM forums ORDER BY created_at");
                List<object[]> allResults = await allCommand.ExecuteGet();

                return Results.Ok(allResults.Select(row => new {
                    id = row[0],
                    name = row[1],
                    description = row[2],
                    creator_id = row[3],
                    created_at = row[4]
                }).ToList());
            }

            SqlCommand searchCommand = sql.Command("SELECT id, description, creator_id, created_at FROM forums WHERE name LIKE @name ORDER BY created_at",
                ("name", forum)
            );
            List<object[]> searchResults = await searchCommand.ExecuteGet();

            return Results.Ok(searchResults.Select(row => new {
                id = row[0],
                name = forum,
                description = row[1],
                creator_id = row[2],
                created_at = row[3]
            }).ToList());
        });

        group.MapPost("/forums", async ([AsParameters] NewForumRequest request, HttpContext context) => {
            if (request.name.ToLower() == "new") {
                return BadRequest("You cannot create a topic with name \"new\"");
            }

            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            string username = context.User.FindFirst(ClaimTypes.Name)?.Value!;

            SqlCommand command = sql.Command("INSERT into FORUMS (id, name, description, creator_id) VALUES (@id, @name, @description, @creator_id)",
                ("id", MakeUUID()),
                ("name", request.name),
                ("description", request.description),
                ("creator_id", userID)
            );
            await command.Execute();

            logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" created forum with name \"" + request.name + "\"");

            return Results.Ok();
        });

        group.MapGet("/forums/{forum}", async (string forum, HttpContext context) => {
            SqlCommand command = sql.Command("SELECT id, description, creator_id, created_at FROM forums WHERE name = @name",
                ("name", forum)
            );
            List<object[]> result = await command.ExecuteGet();
            if (result.Count == 0) {
                logger.INFO("Tried to get forum with name \"" + forum + "\", that doesn't exist");
                return NotFound("Forum not found");
            }

            return Results.Ok(new {
                id = result[0][0],
                description = result[0][1],
                creator_id = result[0][2],
                created_at = result[0][3]
            });
        });

        group.MapGet("/forums/{forum}/topics", async (string forum, HttpContext context) => {
            string? forumID = await GetForumID(forum);

            SqlCommand command = sql.Command("SELECT id, name, description, total_posts, locked, creator_id, created_at FROM topics WHERE forum_id = @forum_id ORDER BY updated_at",
                ("forum_id", forumID!)
            );
            List<object[]> result = await command.ExecuteGet();

            return Results.Ok(result.Select(row => new {
                id = row[0],
                name = row[1],
                description = row[2],
                total_posts = row[3],
                locked = row[4],
                creator_id = row[5],
                created_at = row[6]
            }).ToList());
        });

        group.MapPost("/forums/{forum}/topics", async (string forum, [AsParameters] NewTopicRequest request, HttpContext context) => {
            if (request.name.ToLower() == "new") {
                return BadRequest("You cannot create a topic with name \"new\"");
            }

            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            string username = context.User.FindFirst(ClaimTypes.Name)?.Value!;

            string? forumID = await GetForumID(forum);

            SqlCommand command = sql.Command("INSERT INTO topics (id, forum_id, name, description, creator_id) VALUES (@id, @forum_id, @name, @description, @creator_id)",
                ("id", MakeUUID()),
                ("forum_id", forumID!),
                ("name", request.name),
                ("description", request.description),
                ("creator_id", userID)
            );
            await command.Execute();

            logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" created topic with name \"" + request.name + "\" in forum \"" + forum + "\"");

            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/forums/{forum}/topics/{topic}/posts", async (string forum, string topic, HttpContext context) => {
            AuthenticateResult authResult = await context.AuthenticateAsync("FruitBowlAuth");
            if (authResult.Succeeded && authResult.Principal != null) {
                context.User = authResult.Principal;
            }

            string? forumID = await GetForumID(forum);
            string? topicID = await GetTopicID(forumID!, topic);
            SqlCommand command = sql.Command("SELECT post.id, post.content, post.show_signature, post.deleted, post.creator_id, post.created_at, user.id, user.username, user.description, user.signature, user.profile_picture_url, user.created_at FROM posts post LEFT JOIN users user ON post.creator_id = user.id WHERE post.topic_id = @topic_id ORDER BY post.id ASC", 
                ("topic_id", topicID!)
            );
            List<object[]> result = await command.ExecuteGet();

            string? currentUserID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            string deletablePostID = "-1";

            if (currentUserID != null && result.Count > 0) {
                object[] lastRow = result[^1];
                bool isDeleted = Convert.ToInt32(lastRow[3]) == 1;
                string creatorID = lastRow[4]?.ToString()!;
                string createdAtString = lastRow[5]?.ToString()!;
                if (!isDeleted && currentUserID == creatorID && DateTime.TryParse(createdAtString, out DateTime createdAt)) {
                    if ((DateTime.UtcNow - createdAt.ToUniversalTime()).TotalHours < 24) {
                        deletablePostID = lastRow[0]?.ToString()!;
                    } else {
                    }
                }
            }

            return Results.Ok(new {
                posts = result.Select(row => {
                    bool isDeleted = Convert.ToInt32(row[3]) == 1;

                    return new {
                        id = row[0],
                        content = isDeleted ? "[This post was deleted]" : row[1],
                        show_signature = row[2],
                        deleted = isDeleted,
                        creator_id = row[4],
                        created_at = row[5],
                        author = new {
                            id = row[6],
                            username = row[7],
                            description = row[8],
                            signature = row[9],
                            profile_picture_url = row[10],
                            created_at = row[11]
                        },
                    };
                }).ToList(),
                deletablePostID = deletablePostID
            });
        });

        group.MapPost("/forums/{forum}/topics/{topic}/posts", async (string forum, string topic, [AsParameters] NewPostRequest request, HttpContext context) => {
            if (request.content.Length < 1 || request.content.Length > 10000) {
                return BadRequest("Content must be between 1 and 10000 characters long inclusive");
            }

            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            string username = context.User.FindFirst(ClaimTypes.Name)?.Value!;
            string? forumID = await GetForumID(forum);
            string? topicID = await GetTopicID(forumID!, topic);
            SqlCommand command = sql.Command("INSERT INTO posts (topic_id, content, show_signature, creator_id) VALUES (@topic_id, @content, @show_signature, @creator_id)",
                ("topic_id", topicID!),
                ("content", request.content),
                ("show_signature", request.showSignature),
                ("creator_id", userID)
            );
            await command.Execute();

            logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" created post under \"" + forum + "-" + topic + "\"");

            return Results.Ok();
        }).RequireAuthorization();

        group.MapDelete("/forums/{forum}/topics/{topic}/posts/{postID}", async (string forum, string topic, int postID, HttpContext context) => {
            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            string username = context.User.FindFirst(ClaimTypes.Name)?.Value!;

            string? forumID = await GetForumID(forum);
            string? topicID = await GetTopicID(forumID!, topic);

            if (postID == -1) {
                SqlCommand queryCommand = sql.Command("SELECT id, creator_id, created_at FROM posts WHERE topic_id = @topic_id ORDER BY id DESC LIMIT 1",
                    ("topic_id", topicID!)
                );
                List<object[]> queryResult = await queryCommand.ExecuteGet();

                if (queryResult.Count == 0) {
                    logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" failed to delete post under \"" + forum + "-" + topic + "\" (Empty topic)");
                    return BadRequest("Could not find any posts in the topic to delete");
                }

                postID = Convert.ToInt32(queryResult[0][0]);
                string creatorID = queryResult[0][1].ToString()!;
                string createdAtString = queryResult[0][2].ToString()!;
                string currentUserID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

                if (currentUserID == creatorID && DateTime.TryParse(createdAtString, out DateTime createdAt)) {
                    if ((DateTime.UtcNow - createdAt.ToUniversalTime()).TotalHours >= 24) {
                        logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" failed to delete post under \"" + forum + "-" + topic + "\" (>24hr)");
                        return BadRequest("You must have posted this message within the last 24 hours to delete it");   
                    }
                } else {
                    logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" failed to delete post under \"" + forum + "-" + topic + "\" (No ownership)");
                    return Unauthorized("You can only delete your own posts");
                }

                SqlCommand deleteCommand = sql.Command("DELETE FROM posts WHERE id = @id",
                    ("id", postID)
                );
                await deleteCommand.Execute();

                logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" successfully deleted their last post under \"" + forum + "-" + topic + "\"");

                return Results.Ok();
            }

            // admin logic stuff
            logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" failed to delete post under \"" + forum + "-" + topic + "\"");

            return Unauthorized("You do not have permission to delete this post");

            //string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            //SqlCommand command = sql.Command("DELETE FROM posts WHERE id = @id AND creator_id = @creator_id",
            //    ("id", postID),
            //    ("creator_id", userID)
            //);
            //await command.Execute();
            //return Results.Ok();
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

    public async Task<string?> GetForumID(string forumName) {
        SqlCommand forumIDCommand = sql.Command("SELECT id FROM forums WHERE name = @name",
            ("name", forumName)
        );
        List<object[]> forumIDResult = await forumIDCommand.ExecuteGet();
        if (forumIDResult.Count == 0) {
            return null;
        }

        return forumIDResult[0][0].ToString()!;
    }

    public async Task<string?> GetTopicID(string forumID, string topicName) {
        SqlCommand topicIDCommand = sql.Command("SELECT id FROM topics WHERE name = @name AND forum_id = @forum_id",
            ("name", topicName),
            ("forum_id", forumID)
        );
        List<object[]> topicIDResult = await topicIDCommand.ExecuteGet();
        if (topicIDResult.Count == 0) {
            return null;
        }

        return topicIDResult[0][0].ToString()!;
    }
}
