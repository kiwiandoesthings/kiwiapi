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

using Microsoft.Data.Sqlite;

using static Users;
using static Program;

public class FruitBowlForums {
    private readonly Logger logger;
    private readonly SqlInterface sql;
    private readonly Users users;
    private readonly MarkdownPipeline markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private record RegisterRequest([FromForm] string username, [FromForm] string password, [FromForm] IFormFile? profilePicture);
    private record UpdateUserRequest([FromForm] string username, [FromForm] string description, [FromForm] string signature);
    private record LoginRequest([FromForm] string username, [FromForm] string password);
    private record NewForumRequest([FromForm] string name, [FromForm] string description);
    private record NewTopicRequest([FromForm] string name, [FromForm] string description);
    private record NewPostRequest([FromForm] string content, [FromForm] bool showSignature = false);

    public FruitBowlForums(Users users) {
        this.users = users;
        logger = new Logger("FBF");
        sql = new SqlInterface("fruitbowlforums");
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder group = app.MapGroup("/fruitbowl").RequireCors("ForumsPolicy").DisableAntiforgery();

        group.MapPost("/users", async ([AsParameters] RegisterRequest request, HttpContext context) => {
            AuthResult result = await users.RegisterUser(request.username, request.password, request.profilePicture, context);

            return result.result;
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
            SqliteDataReader reader = await command.ExecuteGet();
            if (!reader.HasRows) {
                logger.INFO("User with username \"" + username + "\" was not found");
                return NotFound("User not found");
            }

            return Results.Ok(new {
                id = reader.GetString(0),
                username = username,
                profile_picture_url = reader.GetString(0),
                description = reader.GetString(2),
                signature = reader.GetString(3),
                created_at = reader.GetString(4)
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

            SqlCommand existsCommand = sql.Command("SELECT COUNT(*) FROM topics WHERE id = @id AND forum_id = @forum_id LIMIT 1",
                ("id", topicID!),
                ("forum_id", forumID!)
            );
            int rows = Convert.ToInt32(await existsCommand.ExecuteGetScalar());
            if (rows == 0) {
                return NotFound("That forum or topic does not exist");
            }

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
