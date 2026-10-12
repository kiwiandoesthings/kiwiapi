namespace KiwiApi;

using Markdig;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Security.Claims;

using static Program;

public class BlueberryForums {
    private readonly Logger logger;
    private readonly SqlInterface sql;
    private readonly MarkdownPipeline markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private record NewForumRequest([FromForm] string name, [FromForm] string description);
    private record NewTopicRequest([FromForm] string name, [FromForm] string description);
    private record NewPostRequest([FromForm] string content, [FromForm] bool showSignature = false);

    public BlueberryForums(SqlInterface sql) {
        logger = new Logger(" BBF");
        this.sql = sql;
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder group = app.MapGroup("/forums").RequireCors("FruitBowlPolicy").DisableAntiforgery();

        group.MapGet("/forums", async (string? forum, HttpContext context) => {
            if (forum == null) {
                SqlCommand allCommand = sql.Command("SELECT id, name, description, creator_id, created_at FROM forum_forums ORDER BY created_at");
                using SqliteDataReader reader = await allCommand.ExecuteGet();
                List<object> results = [];

                while (await reader.ReadAsync()) {
                    results.Add(new {
                        id = reader.GetString(0),
                        name = reader.GetString(1),
                        description = reader.GetString(2),
                        creatorID = reader.GetString(3),
                        createdAt = reader.GetDateTime(4)
                    });
                }

                return Results.Ok(results);
            }

            SqlCommand searchCommand = sql.Command("SELECT id, description, creator_id, created_at FROM forums WHERE name LIKE @name ORDER BY created_at",
                ("name", forum)
            );
            using SqliteDataReader searchReader = await searchCommand.ExecuteGet();
            List<object> searchResults = [];

            while (searchReader.Read()) {
                searchResults.Add(new {
                    id = searchReader.GetString(0),
                    name = forum,
                    description = searchReader.GetString(1),
                    creatorID = searchReader.GetString(2),
                    createdAt = searchReader.GetDateTime(3)
                });
            }

            return Results.Ok(searchResults);
        });

        group.MapPost("/forums", async ([AsParameters] NewForumRequest request, HttpContext context) => {
            if (request.name.Equals("new", StringComparison.InvariantCultureIgnoreCase)) {
                return BadRequest("You cannot create a topic with name \"new\"");
            }

            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            string username = context.User.FindFirst(ClaimTypes.Name)?.Value!;

            SqlCommand command = sql.Command("INSERT INTO forum_forums (id, name, description, creator_id) VALUES (@id, @name, @description, @creator_id)",
                ("id", MakeUUID()),
                ("name", request.name),
                ("description", request.description),
                ("creator_id", userID)
            );
            await command.Execute();

            logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" created forum with name \"" + request.name + "\"");

            return Results.Ok();
        }).RequireAuthorization();

        group.MapGet("/forums/{forum}", async (string forum, HttpContext context) => {
            SqlCommand command = sql.Command("SELECT id, description, creator_id, created_at FROM forum_forums WHERE name = @name",
                ("name", forum)
            );
            using SqliteDataReader reader = await command.ExecuteGet();
            if (!await reader.ReadAsync()) {
                logger.INFO("Tried to get forum with name \"" + forum + "\", that doesn't exist");
                return NotFound("Forum not found");
            }

            return Results.Ok(new {
                id = reader.GetString(0),
                description = reader.GetString(1),
                creatorID = reader.GetString(2),
                createdAt = reader.GetDateTime(3)
            });
        });

        group.MapGet("/forums/{forum}/topics", async (string forum, HttpContext context) => {
            string? forumID = await GetForumID(forum);

            SqlCommand command = sql.Command("SELECT id, name, description, total_posts, locked, creator_id, created_at FROM forum_topics WHERE forum_id = @forum_id ORDER BY updated_at",
                ("forum_id", forumID!)
            );
            using SqliteDataReader reader = await command.ExecuteGet();
            List<object> result = [];

            while (await reader.ReadAsync()) {
                result.Add(new {
                    id = reader.GetString(0),
                    name = reader.GetString(1),
                    description = reader.GetString(2),
                    totalPosts = reader.GetInt32(3),
                    locked = reader.GetBoolean(4),
                    creatorID = reader.GetString(5),
                    createdAt = reader.GetDateTime(6)
                });
            }

            return Results.Ok(result);
        });

        group.MapPost("/forums/{forum}/topics", async (string forum, [AsParameters] NewTopicRequest request, HttpContext context) => {
            if (request.name.Equals("new", StringComparison.InvariantCultureIgnoreCase)) {
                return BadRequest("You cannot create a topic with name \"new\"");
            }

            string userID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            string username = context.User.FindFirst(ClaimTypes.Name)?.Value!;

            string? forumID = await GetForumID(forum);

            SqlCommand command = sql.Command("INSERT INTO forum_topics (id, forum_id, name, description, creator_id) VALUES (@id, @forum_id, @name, @description, @creator_id)",
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

            SqlCommand existsCommand = sql.Command("SELECT COUNT(*) FROM forum_topics WHERE id = @id AND forum_id = @forum_id LIMIT 1",
                ("id", topicID!),
                ("forum_id", forumID!)
            );
            int rows = Convert.ToInt32(await existsCommand.ExecuteGetScalar());
            if (rows == 0) {
                return NotFound("That forum or topic does not exist");
            }

            SqlCommand command = sql.Command("SELECT post.id, post.content, post.show_signature, post.deleted, post.creator_id, post.created_at, user.id, user.username, user.description, user.signature, user.profile_picture_url, user.created_at FROM forum_posts post LEFT JOIN users user ON post.creator_id = user.id WHERE post.topic_id = @topic_id ORDER BY post.id ASC",
                ("topic_id", topicID!)
            );
            using SqliteDataReader reader = await command.ExecuteGet();
            List<object> posts = [];
            string lastPostID = "-1";
            int lastIsDeleted = 0;
            string lastCreatorID = "";
            string lastCreatedAtString = "";

            while (await reader.ReadAsync()) {
                bool isDeleted = reader.GetBoolean(3);

                posts.Add(new {
                    id = reader.GetString(0),
                    content = isDeleted ? "[This post was deleted]" : reader.GetString(1),
                    showSignature = reader.GetBoolean(2),
                    deleted = isDeleted,
                    creatorID = reader.GetString(4),
                    createdAt = reader.GetDateTime(5),
                    author = new {
                        id = reader.GetString(6),
                        username = reader.GetString(7),
                        description = reader.GetString(8),
                        signature = reader.GetString(9),
                        profilePictureUrl = reader.GetString(10),
                        createdAt = reader.GetDateTime(11)
                    },
                });
            }

            string? currentUserID = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            string deletablePostID = "-1";

            if (currentUserID != null && posts.Count > 0) {
                if (lastIsDeleted == 0 && currentUserID == lastCreatorID && DateTime.TryParse(lastCreatedAtString, out DateTime createdAt)) {
                    if ((DateTime.UtcNow - createdAt.ToUniversalTime()).TotalHours < 24) {
                        deletablePostID = lastPostID;
                    }
                }
            }

            return Results.Ok(new {
                posts = posts,
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
            SqlCommand command = sql.Command("INSERT INTO forum_posts (topic_id, content, show_signature, creator_id) VALUES (@topic_id, @content, @show_signature, @creator_id)",
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
                SqlCommand queryCommand = sql.Command("SELECT id, creator_id, created_at FROM forum_posts WHERE topic_id = @topic_id ORDER BY id DESC LIMIT 1",
                    ("topic_id", topicID!)
                );
                using SqliteDataReader reader = await queryCommand.ExecuteGet();

                if (!await reader.ReadAsync()) {
                    logger.INFO("User with ID {" + userID + "} and name \"" + username + "\" failed to delete post under \"" + forum + "-" + topic + "\" (Empty topic)");
                    return BadRequest("Could not find any posts in the topic to delete");
                }

                postID = reader.GetInt32(0);
                string creatorID = reader.GetString(1);
                string createdAtString = reader.GetString(2);
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

                SqlCommand deleteCommand = sql.Command("DELETE FROM forum_posts WHERE id = @id",
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
        SqlCommand forumIDCommand = sql.Command("SELECT id FROM forum_forums WHERE name = @name",
            ("name", forumName)
        );
        using SqliteDataReader reader = await forumIDCommand.ExecuteGet();
        if (!await reader.ReadAsync()) {
            return null;
        }

        return reader.GetString(0);
    }

    public async Task<string?> GetTopicID(string forumID, string topicName) {
        SqlCommand topicIDCommand = sql.Command("SELECT id FROM forum_topics WHERE name = @name AND forum_id = @forum_id",
            ("name", topicName),
            ("forum_id", forumID)
        );
        using SqliteDataReader reader = await topicIDCommand.ExecuteGet();
        if (!await reader.ReadAsync()) {
            return null;
        }

        return reader.GetString(0);
    }
}