namespace KiwiApi;

using Markdig;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

using static Program;

public partial class KiwiBlogApi {
    private readonly Logger logger;
    private readonly SqlInterface sql;
    private readonly MarkdownPipeline markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private record Register(string name, string email, string password, bool isEmailPublic);
    private record BlogSettings(string email, bool isEmailPublic);
    private record Login(string email, string password);
    private record AddPost(string title, string content, string? summary);
    private record EditPost(string title, string content, string? summary);
    private record GetPosts(string blogID, int lastPostID, int amount);
    private record SearchPost(string blogID, DateRangeSearch? dateRangeSearch, KeywordSearch? keywordSearch);
    private record AddComment(string content, string blogID, int postID, int? parentCommentID);
    private record EditComment(string content, string blogID, int postID);
    private record GetComments(int lastCommentID, int amount, string blogID, int postID);
    private record GetScript(string stylesheetName, string? containerID);

    private record DateRangeSearch(string startDate, string endDate);
    private record KeywordSearch(string searchKey);

    private record BlogPost(string blogID, int postID, string title, string content, string formattedContent, string summary, string creationDate, string lastEditDate);

    private const string baseEmbed = "<script src=\"https://kiwiblog.kiwiandoesthings.place/scripts/common.js\"></script>\n<script src=\"https://kiwiblog.kiwiandoesthings.place/scripts/home_blog.js\"></script>\n<script src=\"https://kiwiblog.kiwiandoesthings.place/scripts/embed_loader.js\"></script>\n<script>\n\tinitialize({\n\t\tstylesheet:\"@STYLESHEET@\",\n\t\tblogID:\"@BLOG_ID@\",\n\t\tcontainerID:\"@CONTAINER_ID@\"\n\t});\n</script>";

    public KiwiBlogApi(SqlInterface sql) {
        logger = new Logger(" KWB");

        this.sql = sql;
    }

    public void MapApiFunctions(WebApplication app) {
        RouteGroupBuilder group = app.MapGroup("/blog").RequireCors("BlogPolicy");

        group.MapPost("/blogs", async (Register registration, HttpContext context) => {
            string formattedEmail = registration.email.ToLowerInvariant();

            bool filledOut = registration.name != "" && registration.email != "" && registration.password != "";
            bool validUsername = ValidUsername(registration.name);
            bool validEmail = ValidEmail(formattedEmail);
            bool validPassword = ValidPassword(registration.password);

            string errorMessage = "";
            if (!filledOut) {
                errorMessage += "Login information is incomplete. ";
            }
            if (!validUsername) {
                errorMessage += "Username can only use \"A-z, 0-9, -, _\", and must be 4-20 characters long. ";
            }
            if (!validEmail) {
                errorMessage += "You must provide a valid email address. ";
            }
            if (!validPassword) {
                errorMessage += "Password must be 8-24 characters long, and can only use \"A-z, 0-9, and special characters\".";
            }

            if (errorMessage != "") {
                return BadRequest(errorMessage);
            }

            if (string.IsNullOrEmpty(registration.name) || string.IsNullOrEmpty(formattedEmail) || string.IsNullOrWhiteSpace(registration.password)) {
                logger.ERR("Failed to register blog. Required fields were not provided or valid.");
                return BadRequest("You must provide a blog name, email, and password");
            }

            using SqlCommand usernameConflictCommand = sql.Command("SELECT name FROM blogs WHERE name = @name",
                ("name", registration.name));
            using SqliteDataReader readerName = await usernameConflictCommand.ExecuteGet();
            while (readerName.Read()) {
                return BadRequest("That username is already taken");
            }

            using SqlCommand emailConflictCommand = sql.Command("SELECT email FROM blogs WHERE email = @email",
                ("email", formattedEmail));
            using SqliteDataReader readerEmail = await emailConflictCommand.ExecuteGet();
            while (readerEmail.Read()) {
                return BadRequest("That email is already being used");
            }

            string blogID = MakeUUID();
            using SqlCommand registerBlogCommand = sql.Command("INSERT INTO blogs (blog_id, name, email, password_hash, email_public) VALUES (@blog_id, @name, @email, @password_hash, @email_public)",
                ("blog_id", blogID),
                ("name", registration.name),
                ("email", formattedEmail),
                ("password_hash", GetHashedString(registration.password)),
                ("email_public", registration.isEmailPublic));
            await registerBlogCommand.Execute();

            string loginToken = await AddLoginToken(blogID);
            SetHttpCookie(context, "login_token", loginToken);

            logger.INFO("Successfully registered new blog with name: \"" + registration.name + "\", email: \"" + formattedEmail + "\" that is" + (registration.isEmailPublic ? "" : "n't") + " public");
            return Results.Ok(new {
                blogID = blogID
            });
        });

        group.MapPut("/blogs", async (BlogSettings settings, HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to edit blog settings. Invalid token");
                return result!;
            }

            using SqlCommand updateCommand = sql.Command("UPDATE blogs SET email = @email, email_public = @email_public WHERE blog_id = @blog_id",
                ("email", settings.email),
                ("email_public", settings.isEmailPublic),
                ("blog_id", blogID!));
            int rowsAffected = await updateCommand.Execute();

            if (rowsAffected == 0) {
                logger.ERR("Failed to edit blog settings. No blogs found with ID \"" + blogID + "\"");
                return ServerError("Failed to update blog info for unknown reason.");
            }

            return Results.Ok();
        });

        group.MapGet("/blogs/{blogID:guid}", async (string blogID, HttpContext context) => {
            using SqlCommand queryBlogCommand = sql.Command("SELECT name, date_created FROM blogs WHERE blog_id = @blog_id",
                ("blog_id", blogID));
            using SqliteDataReader readerBlog = await queryBlogCommand.ExecuteGet();

            string blogName = "";
            string blogCreationDate = "";
            int blogCount = 0;
            while (readerBlog.Read()) {
                blogName = readerBlog.GetString(0);
                blogCreationDate = readerBlog.GetString(1);
                blogCount++;
            }

            if (blogCount == 0) {
                logger.ERR("Failed to get info from blog with ID: \"" + blogID + "\"");
                return NotFound("Could not find a blog with that blog ID");
            }

            using SqlCommand queryPostsCommand = sql.Command("SELECT COUNT(*) FROM posts WHERE blog_id = @blog_id",
                ("blog_id", blogID));
            int totalPosts = Convert.ToInt32(await queryPostsCommand.ExecuteGetScalar());

            return Results.Ok(new {
                blogName = blogName,
                totalPosts = totalPosts,
                blogCreationDate = blogCreationDate
            });
        });

        group.MapGet("/blogs/account", async (HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to get blog settings. Invalid token");
                return result!;
            }

            using SqlCommand queryCommand = sql.Command("SELECT email, email_public FROM blogs WHERE blog_id = @blog_id",
                ("blog_id", blogID!));
            using SqliteDataReader reader = await queryCommand.ExecuteGet();

            string email = "";
            bool isEmailPublic = false;
            int rowCount = 0;
            while (await reader.ReadAsync()) {
                email = reader.GetString(0);
                isEmailPublic = reader.GetBoolean(1);
                rowCount++;
            }

            if (rowCount == 0) {
                logger.ERR("Failed to get account info from blog with ID: \"" + blogID + "\"");
                return NotFound("Could not find a blog with that blog ID");
            }

            return Results.Ok(new {
                email = email,
                isEmailPublic = isEmailPublic
            });
        });

        group.MapGet("/blogs/{blogID}/script", async (string blogID, [AsParameters] GetScript request, HttpContext context) => {
            string baseScript = baseEmbed.Replace("@STYLESHEET@", request.stylesheetName).Replace("@BLOG_ID@", blogID);
            bool needsContainer = string.IsNullOrEmpty(request.containerID);
            if (needsContainer) {
                baseScript += "\n<div id=\"blog-container\"></div>";
            }

            return Results.Ok(new {
                blogScript = baseScript.Replace("@CONTAINER_ID@", needsContainer ? "blog-container" : request.containerID)
            });
        });

        group.MapDelete("/blogs", async (HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to delete blog. Invalid token");
                return result!;
            }

            using SqlCommand deregisterCommand = sql.Command("DELETE FROM blogs WHERE blog_id = @blog_id",
                ("blog_id", blogID!));
            await deregisterCommand.Execute();

            SetHttpCookie(context, "login_token", "");

            logger.INFO("Successfully deregistered user with blog ID: \"" + blogID + "\"");
            return Results.Ok();
        });

        group.MapPost("/sessions", async (Login login, HttpContext context) => {
            string formattedEmail = login.email.ToLowerInvariant();

            using SqlCommand getHashCommand = sql.Command("SELECT blog_id, password_hash FROM blogs WHERE email = @email",
                ("email", formattedEmail));
            using SqliteDataReader reader = await getHashCommand.ExecuteGet();

            string blogID = "";
            bool foundUser = false;
            while (await reader.ReadAsync()) {
                blogID = reader.GetString(0);
                string storedHash = reader.GetString(1);

                if (!VerifyHashedString(login.password, storedHash)) {
                    logger.ERR("Failed to login. Invalid password for email \"" + formattedEmail + "\"");
                    return Unauthorized("Incorrect credentials");
                }
                foundUser = true;
            }

            if (!foundUser) {
                logger.ERR("Failed to login. No email matching \"" + formattedEmail + "\"");
                return Unauthorized("Incorrect credentials");
            }

            string loginToken = await AddLoginToken(blogID);
            SetHttpCookie(context, "login_token", loginToken);

            logger.INFO("Successfully logged in user with blog ID: \"" + blogID + "\"");
            return Results.Ok(new {
                blogID = blogID
            });
        });

        group.MapDelete("/sessions", async (HttpContext context) => {
            string loginToken = GetHttpCookie(context, "login_token");

            SetHttpCookie(context, "login_token", "");

            string? blogID = await GetBlogIDFromToken(loginToken);
            if (blogID == null) {
                logger.ERR("Failed to logout. Invalid token");
                return Unauthorized("Invalid login token.");
            }

            using SqlCommand removeSessionCommand = sql.Command("DELETE FROM sessions WHERE login_token = @login_token",
                ("login_token", loginToken));
            await removeSessionCommand.Execute();

            logger.INFO("Successfully logged out user with blog ID: \"" + blogID + "\"");
            return Results.Ok();
        });

        group.MapPost("/posts", async (AddPost post, HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to add post to blog. Invalid token");
                return result!;
            }

            using SqlCommand addPostCommand = sql.Command("INSERT INTO posts (blog_id, title, content, summary, date_edited) VALUES (@blog_id, @title, @content, @summary, @date_edited)",
                ("blog_id", blogID!),
                ("title", post.title),
                ("content", post.content),
                ("summary", post.summary ?? string.Empty),
                ("date_edited", string.Empty));
            await addPostCommand.Execute();

            logger.INFO("Successfully added new post to blog with ID: \"" + blogID + "\"");
            return Results.Ok();
        });

        group.MapPut("/posts/{postID}", async (int postID, EditPost edit, HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to edit post. Invalid token");
                return result!;
            }

            using SqlCommand editCommand = sql.Command("UPDATE posts SET title = @title, content = @content, summary = @summary, date_edited = CURRENT_TIMESTAMP WHERE post_id = @post_id",
                ("title", edit.title),
                ("content", edit.content),
                ("summary", edit.summary ?? string.Empty),
                ("post_id", postID));
            int rowsAffected = await editCommand.Execute();

            if (rowsAffected > 0) {
                logger.INFO("Successfully edited post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\"");
                BlogPost post = (await GetBlogPost(blogID!, postID))!;
                return Results.Ok(new {
                    postID = postID,
                    postTitle = post.title,
                    postRawContent = post.content,
                    postFormattedContent = post.formattedContent,
                    postSummary = post.summary,
                    postCreationDate = post.creationDate,
                    postEditDate = post.lastEditDate
                });
            }

            logger.ERR("Failed to edit post with post ID: {" + postID + "} from blog with ID: \"" + blogID + "\"");
            return ServerError("Failed to edit post.");
        });

        group.MapDelete("/posts/{postID}", async (int postID, HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to delete post. Invalid token");
                return result!;
            }

            using SqlCommand deleteCommand = sql.Command("DELETE FROM posts WHERE blog_id = @blog_id AND post_id = @post_id",
                ("blog_id", blogID!),
                ("post_id", postID));
            int rowsAffected = await deleteCommand.Execute();

            if (rowsAffected > 0) {
                logger.INFO("Successfully deleted post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\"");
                return Results.Ok();
            }

            logger.ERR("Failed to delete post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\" for unknown reason");
            return ServerError("Failed to delete post.");
        });

        group.MapGet("/posts", async ([AsParameters] GetPosts request, HttpContext context) => {
            long searchLastID = request.lastPostID == 0 ? long.MaxValue : request.lastPostID;

            using SqlCommand queryPostCommand = sql.Command("SELECT post_id, title, content, summary, date_created, date_edited FROM posts WHERE blog_id = @blog_id AND post_id < @last_id ORDER BY post_id DESC LIMIT @amount",
                ("blog_id", request.blogID),
                ("amount", request.amount),
                ("last_id", searchLastID));

            using SqliteDataReader reader = await queryPostCommand.ExecuteGet();

            List<object> posts = [];
            while (await reader.ReadAsync()) {
                long postIdValue = reader.GetInt64(0);
                string titleValue = reader.GetString(1);
                string rawMarkdown = reader.GetString(2);
                string? summaryValue = reader.IsDBNull(3) ? null : reader.GetString(3);
                string creationDateValue = reader.GetString(4);
                string? editDateValue = reader.IsDBNull(5) ? null : reader.GetString(5);

                string htmlContent = Markdown.ToHtml(rawMarkdown, markdown);

                posts.Add(new {
                    postID = postIdValue,
                    postTitle = titleValue,
                    postRawContent = rawMarkdown,
                    postFormattedContent = htmlContent,
                    postSummary = summaryValue,
                    postCreationDate = creationDateValue,
                    postEditDate = editDateValue
                });
            }

            if (posts.Count == 0) {
                logger.WARN("Failed to find any posts when requesting {" + request.amount + "} posts from blog with ID: \"" + request.blogID + "\" starting at post ID: {" + request.lastPostID + "}");
                return Results.Ok(new List<object>());
            }

            return Results.Ok(posts);
        });

        group.MapPost("/comments/", async ([AsParameters] AddComment request, HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to add comment. Invalid token");
                return result!;
            }

            using SqlCommand addCommand = sql.Command("INSERT INTO comments (blog_id, post_id, parent_comment_id, author_id, content) VALUES (@blog_id, @post_id, @parent_comment_id, @author_id, @content)",
                ("blog_id", blogID!),
                ("post_id", request.postID),
                ("parent_comment_id", (object?)request.parentCommentID ?? DBNull.Value),
                ("author_id", blogID!),
                ("content", request.content));

            await addCommand.Execute();
            return Results.Ok();
        });

        group.MapPut("/comments/{commentID}", async (int commentID, [AsParameters] EditComment request, HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to edit comment. Invalid token");
                return result!;
            }

            using SqlCommand editCommand = sql.Command("UPDATE comments SET content = @content WHERE blog_id = @blog_id AND post_id = @post_id AND comment_id = @comment_id",
                ("content", request.content),
                ("blog_id", blogID!),
                ("post_id", request.postID),
                ("comment_id", commentID));

            await editCommand.Execute();

            return Results.Ok();
        });

        group.MapGet("/comments", async ([AsParameters] GetComments request, HttpContext context) => {
            long searchLastID = request.lastCommentID == 0 ? long.MaxValue : request.lastCommentID;

            using SqlCommand queryPostCommand = sql.Command("SELECT comment_id, author_id, content, date_created, date_edited FROM comments WHERE blog_id = @blog_id AND post_id = @post_id AND comment_id < @last_id ORDER BY comment_id DESC LIMIT @amount",
                ("blog_id", request.blogID),
                ("post_id", request.postID),
                ("amount", request.amount),
                ("last_id", searchLastID));

            using SqliteDataReader reader = await queryPostCommand.ExecuteGet();

            List<object> comments = [];
            while (await reader.ReadAsync()) {
                long commentIdValue = reader.GetInt64(0);
                string authorIdValue = reader.GetString(1);
                string rawMarkdown = reader.GetString(2);
                string creationDateValue = reader.GetString(3);
                string? editDateValue = reader.IsDBNull(4) ? null : reader.GetString(4);

                string htmlContent = Markdown.ToHtml(rawMarkdown, markdown);

                comments.Add(new {
                    commentID = commentIdValue,
                    commentAuthorID = authorIdValue,
                    commentRawContent = rawMarkdown,
                    commentFormattedContent = htmlContent,
                    postCreationDate = creationDateValue,
                    postEditDate = editDateValue
                });
            }

            if (comments.Count == 0) {
                logger.WARN("Failed to find any comments when requesting {" + request.amount + "} comments from post with ID: {" + request.postID + "}from blog with ID: \"" + request.blogID + "\" starting at comment ID: {" + request.lastCommentID + "}");
                return Results.Ok(new List<object>());
            }

            return Results.Ok(comments);
        });

        group.MapDelete("/comments/{commentID}", async (int commentID, [AsParameters] GetComments request, HttpContext context) => {
            (bool success, string? blogID, IResult? result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to delete comment. Invalid token");
                return result!;
            }

            using SqlCommand deleteCommand = sql.Command("DELETE FROM comments WHERE blog_id = @blog_id AND post_id = @post_id AND comment_id = @comment_id",
                ("blog_id", blogID!),
                ("post_id", request.postID),
                ("comment_id", commentID));

            await deleteCommand.Execute();

            return Results.Ok();
        });

        group.MapPost("/posts/search", async ([FromBody] SearchPost search, HttpContext context) => {
            HashSet<int>? dateRangeIDs = null;
            HashSet<int>? keywordIDs = null;
            if (search.dateRangeSearch != null) {
                using SqlCommand dateRangeSearch = sql.Command("SELECT post_id FROM posts WHERE blog_id = @blog_id AND date_created BETWEEN @start_date AND @end_date",
                    ("blog_id", search.blogID),
                    ("start_date", search.dateRangeSearch.startDate),
                    ("end_date", search.dateRangeSearch.endDate));

                dateRangeIDs = [];
                using SqliteDataReader reader = await dateRangeSearch.ExecuteGet();
                while (await reader.ReadAsync()) {
                    dateRangeIDs.Add(reader.GetInt32(0));
                }
            }
            if (search.keywordSearch != null) {
                using SqlCommand keywordSearchCommand = sql.Command("SELECT post_id FROM posts WHERE blog_id = @blog_id AND (title LIKE @keyword OR content LIKE @keyword OR summary LIKE @keyword)",
                    ("blog_id", search.blogID),
                    ("keyword", "%" + search.keywordSearch.searchKey + "%"));

                keywordIDs = [];
                using SqliteDataReader reader = await keywordSearchCommand.ExecuteGet();
                while (await reader.ReadAsync()) {
                    keywordIDs.Add(reader.GetInt32(0));
                }
            }

            IEnumerable<int> results;

            if (dateRangeIDs != null && keywordIDs != null) {
                results = dateRangeIDs.Intersect(keywordIDs);
            } else if (dateRangeIDs != null) {
                results = dateRangeIDs;
            } else if (keywordIDs != null) {
                results = keywordIDs;
            } else {
                results = [];
            }

            return Results.Ok(results);
        });
    }

    private async Task<string> AddLoginToken(string blogID) {
        string loginToken = MakeUUID();
        using SqlCommand addSessionCommand = sql.Command("INSERT INTO sessions (blog_id, login_token) VALUES (@blog_id, @login_token)",
            ("blog_id", blogID),
            ("login_token", loginToken));
        await addSessionCommand.Execute();

        return loginToken;
    }

    private async Task<BlogPost?> GetBlogPost(string blogID, int postID) {
        using SqlCommand queryCommand = sql.Command("SELECT title, content, summary, date_created, date_edited FROM posts WHERE blog_id = @blog_id AND post_id = @post_id",
            ("blog_id", blogID),
            ("post_id", postID));
        using SqliteDataReader reader = await queryCommand.ExecuteGet();

        string title = "";
        string content = "";
        string summary = "";
        string dateCreated = "";
        string dateEdited = "";
        int count = 0;
        while (await reader.ReadAsync()) {
            title = reader.GetString(0);
            content = reader.GetString(1);
            summary = reader.GetString(2);
            dateCreated = reader.GetString(3);
            dateEdited = reader.GetString(4);
            count++;
        }

        if (count != 1) {
            return null;
        }

        MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        return new BlogPost(blogID, postID, title, content, Markdown.ToHtml(content, pipeline), summary, dateCreated, dateEdited);
    }

    private async Task<(bool success, string? blogID, IResult? result)> AuthenticateUser(HttpContext context) {
        string loginToken = GetHttpCookie(context, "login_token");
        if (string.IsNullOrWhiteSpace(loginToken)) {
            logger.ERR("Failed to authenticate user. No token provided");
            return (false, null, Unauthorized("Invalid token."));
        }

        string? blogID = await GetBlogIDFromToken(loginToken);
        if (blogID == null) {
            logger.ERR("Failed to authenticate user. Invalid token");
            return (false, null, Unauthorized("Invalid login token."));
        }

        return (true, blogID, null);
    }

    private async Task<string?> GetBlogIDFromToken(string loginToken) {
        using SqlCommand queryCommand = sql.Command("SELECT blog_id FROM sessions WHERE login_token = @login_token",
            ("login_token", loginToken));
        return (string?)await queryCommand.ExecuteGetScalar();
    }

    private static string GetHttpCookie(HttpContext context, string key) {
        if (context.Request.Cookies.TryGetValue(key, out string? value)) {
            return value ?? string.Empty;
        }

        return string.Empty;
    }

    private static void SetHttpCookie(HttpContext context, string key, string value) {
        context.Response.Cookies.Append(key, value, new CookieOptions {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Domain = ".kiwiandoesthings.place",
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_]+$")]
    public static partial Regex ValidStringRegex();

    public static bool ValidString(string toCheck) {
        return ValidStringRegex().IsMatch(toCheck);
    }

    [GeneratedRegex(@"^[\x21-\x7E]+$")]
    private static partial Regex AdvancedStringRegex();

    public static bool ValidAdvancedString(string toCheck) {
        return AdvancedStringRegex().IsMatch(toCheck);
    }

    public static bool ValidUsername(string username) {
        return username.Length >= 4 && username.Length <= 20 && ValidString(username);
    }

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex EmailRegex();

    public static bool ValidEmail(string email) {
        return EmailRegex().IsMatch(email);
    }

    public static bool ValidPassword(string password) {
        return password.Length >= 8 && password.Length <= 24 && ValidAdvancedString(password);
    }
}