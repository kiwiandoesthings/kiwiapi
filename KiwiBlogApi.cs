namespace kiwiapi;

using Markdig;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

using static Program;

public class KiwiBlogApi {
	private SqliteConnection database;
	private Logger logger;

	private record Register(string name, string email, string password, bool isEmailPublic);
	private record BlogSettings(string email, bool isEmailPublic);
	private record Login(string email, string password);
	private record Add(string title, string content, string? summary);
	private record Edit(string title, string content, string? summary);
	private record Get(string blogID, int lastPostID, int amount);
	private record Search(string blogID, DateRangeSearch? dateRangeSearch, KeywordSearch? keywordSearch);
	private record GetScript(string stylesheetName, string? containerID);

	private record DateRangeSearch(string startDate, string endDate);
	private record KeywordSearch(string searchKey, bool fuzzySearch);

	private record BlogPost(string blogID, int postID, string title, string content, string formattedContent, string summary, string creationDate, string lastEditDate);

    private const string baseEmbed = "<script src=\"https://kiwiblog.kiwiandoesthings.place/scripts/common.js\"></script>\n<script src=\"https://kiwiblog.kiwiandoesthings.place/scripts/home_blog.js\"></script>\n<script src=\"https://kiwiblog.kiwiandoesthings.place/scripts/embed_loader.js\"></script>\n<script>\n\tinitialize({\n\t\tstylesheet:\"@STYLESHEET@\",\n\t\tblogID:\"@BLOG_ID@\",\n\t\tcontainerID:\"@CONTAINER_ID@\"\n\t});\n</script>";

	public KiwiBlogApi() {
		logger = new Logger("KWB");

        string basePath = Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)!.FullName)!.FullName)!.FullName)!.FullName;
        string dbPath = Path.Combine(basePath, "kiwiblog.db");
        string schemaPath = Path.Combine(basePath, "schema.sql");

        bool dbExists = File.Exists(dbPath);

        string connectionString = "Data Source=" + dbPath;
        database = new SqliteConnection(connectionString);
        database.Open();

        using var walCommand = database.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode=WAL;";
        walCommand.ExecuteNonQuery();

        if (!dbExists && File.Exists(schemaPath)) {
			logger.INFO("Creating new database from schema.sql file");

            string schemaSql = File.ReadAllText(schemaPath);
			schemaSql = schemaSql.Replace("CREATE TABLE sqlite_sequence(name,seq);", "");

            using var schemaCommand = database.CreateCommand();
            schemaCommand.CommandText = schemaSql;
            schemaCommand.ExecuteNonQuery();
        }

        SqlCommand.Setup(database);
    }
	
	public void MapApiFunctions(WebApplication app) {
		RouteGroupBuilder blog = app.MapGroup("/blog").RequireCors("BlogPolicy");

		blog.MapPost("/blogs", async (Register registration, HttpContext context) => {
            bool filledOut = registration.name != "" && registration.email != "" && registration.password != "";
            bool validUsername = ValidUsername(registration.name);
			bool validEmail = ValidEmail(registration.email);
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

            if (string.IsNullOrEmpty(registration.name) || string.IsNullOrEmpty(registration.email) || string.IsNullOrWhiteSpace(registration.password)) {
				logger.ERR("Failed to register blog. Required fields were not provided or valid.");
				return BadRequest("You must provide a blog name, email, and password");
			}

			string blogID = MakeUUID();
			using SqlCommand registerBlogCommand = new SqlCommand("INSERT INTO blogs (blog_id, name, email, password_hash, email_public) VALUES (@blog_id, @name, @email, @password_hash, @email_public)",
				("@blog_id", blogID),
				("@name", registration.name),
				("@email", registration.email),
				("@password_hash", GetHashedString(registration.password)),
				("@email_public", registration.isEmailPublic));
			await registerBlogCommand.Execute();

            string loginToken = await AddLoginToken(blogID);
			SetHttpCookie(context, "login_token", loginToken);

            logger.INFO("Successfully registered new blog with name: \"" + registration.name + "\", email: \"" + registration.email + "\" that is" + (registration.isEmailPublic ? "" : "n't") + " public");
			return Results.Ok(new {
				blogID = blogID
			});
		});

		blog.MapPut("/blogs", async (BlogSettings settings, HttpContext context) => {
			var (success, blogID, result) = await AuthenticateUser(context);
			if (!success) {
				logger.ERR("Failed to edit blog settings. Invalid token");
				return result;
			}

			using SqlCommand updateCommand = new SqlCommand("UPDATE blogs SET email = @email, email_public = @email_public WHERE blog_id = @blog_id",
				("@email", settings.email),
				("@email_public", settings.isEmailPublic),
				("@blog_id", blogID!));
			int rowsAffected = await updateCommand.Execute();

			if (rowsAffected == 0) {
				logger.ERR("Failed to edit blog settings. No blogs found with ID \"" + blogID + "\"");
				return ServerError("Failed to update blog info for unknown reason.");
			}

			return Results.Ok();
        });

        blog.MapGet("/blogs/{blogID:guid}", async (string blogID, HttpContext context) => {
            using SqlCommand queryBlogCommand = new SqlCommand("SELECT name, date_created FROM blogs WHERE blog_id = @blog_id",
                ("@blog_id", blogID));
            List<object[]> info = await queryBlogCommand.ExecuteGet();

            if (info.Count == 0) {
                logger.ERR("Failed to get info from blog with ID: \"" + blogID + "\"");
                return NotFound("Could not find a blog with that blog ID");
            }
            string blogName = (string)info[0][0];
            string blogCreationDate = (string)info[0][1];

            using SqlCommand queryPostsCommand = new SqlCommand("SELECT COUNT(*) FROM posts WHERE blog_id = @blog_id",
                ("@blog_id", blogID));
            int totalPosts = Convert.ToInt32(await queryPostsCommand.ExecuteGetScalar());

            return Results.Ok(new {
                blogName = blogName,
                totalPosts = totalPosts,
                blogCreationDate = blogCreationDate
            });
        });

		blog.MapGet("/blogs/account", async (HttpContext context) => {
            var (success, blogID, result) = await AuthenticateUser(context);
            if (!success) {
                logger.ERR("Failed to edit blog settings. Invalid token");
                return result;
            }

			using SqlCommand queryCommand = new SqlCommand("SELECT email, email_public FROM blogs WHERE blog_id = @blog_id",
				("@blog_id", blogID!));
			List<object[]> info = await queryCommand.ExecuteGet();

			if (info.Count == 0) {
				logger.ERR("Failed to get account info from blog with ID: \"" + blogID + "\"");
				return NotFound("Could not find a blog with that blog ID");
			}

			string email = (string)info[0][0];
			bool isEmailPublic = (long)info[0][1] == 0 ? false : true;

			return Results.Ok(new {
				email = email,
				isEmailPublic = isEmailPublic
			});
        });

        blog.MapGet("/blogs/{blogID}/script", async (string blogID, [AsParameters] GetScript request, HttpContext context) => {
            string baseScript = baseEmbed.Replace("@STYLESHEET@", request.stylesheetName).Replace("@BLOG_ID@", blogID);
            bool needsContainer = string.IsNullOrEmpty(request.containerID);
            if (needsContainer) {
                baseScript += "\n<div id=\"blog-container\"></div>";
            }

            return Results.Ok(new {
                blogScript = baseScript.Replace("@CONTAINER_ID@", needsContainer ? "blog-container" : request.containerID)
            });
        });

        blog.MapDelete("/blogs", async (HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
			if (blogID == null) {
				logger.ERR("Failed to deregister account. Invalid token");
				return Unauthorized("Invalid login token.");
			}

			using SqlCommand deregisterCommand = new SqlCommand("DELETE FROM blogs WHERE blog_id = @blog_id",
				("@blog_id", blogID));
			await deregisterCommand.Execute();

            SetHttpCookie(context, "login_token", "");

            logger.INFO("Successfully deregistered user with blog ID: \"" + blogID + "\"");
			return Results.Ok();
		});

		blog.MapPost("/sessions", async (Login login, HttpContext context) => {
            using SqlCommand getHashCommand = new SqlCommand("SELECT blog_id, password_hash FROM blogs WHERE email = @email", 
				("@email", login.email));
            List<object[]> result = await getHashCommand.ExecuteGet();

			string blogID;
            if (result.Count > 0) {
                blogID = (string)result[0][0];
                string storedHash = (string)result[0][1];

                if (!VerifyHashedString(login.password, storedHash)) {
					logger.ERR("Failed to login. Invalid password for email \"" + login.email + "\"");
                    return Unauthorized("Incorrect credentials");
                }
            } else {
                logger.ERR("Failed to login. No email matching \"" + login.email + "\"");
                return Unauthorized("Incorrect credentials");
			}

			string loginToken = await AddLoginToken(blogID);
			SetHttpCookie(context, "login_token", loginToken);

			logger.INFO("Successfully logged in user with blog ID: \"" + blogID + "\"");
			return Results.Ok(new {
				blogID = blogID
			});
		});

		blog.MapDelete("/sessions", async (HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			SetHttpCookie(context, "login_token", "");

			string? blogID = await GetBlogIDFromToken(loginToken);
			if (blogID == null) {
				logger.ERR("Failed to logout. Invalid token");
				return Unauthorized("Invalid login token.");
			}

			using SqlCommand removeSessionCommand = new SqlCommand("DELETE FROM sessions WHERE login_token = @login_token",
				("@login_token", loginToken));
			await removeSessionCommand.Execute();

			logger.INFO("Successfully logged out user with blog ID: \"" + blogID + "\"");
			return Results.Ok();
		});

		blog.MapPost("/posts", async (Add post, HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
			if (blogID == null) {
                logger.ERR("Failed to add post to blog with ID: \"" + blogID + "\". Invalid token");
                return Unauthorized("Invalid login token.");
			}

			using SqlCommand addPostCommand = new SqlCommand("INSERT INTO posts (blog_id, title, content, summary, date_edited) VALUES (@blog_id, @title, @content, @summary, @date_edited)",
				("@blog_id", blogID),
				("@title", post.title),
				("@content", post.content),
				("@summary", post.summary ?? string.Empty),
				("@date_edited", string.Empty));
			await addPostCommand.Execute();

			logger.INFO("Successfully added new post to blog with ID: \"" + blogID + "\"");
			return Results.Ok();
		});

		blog.MapPut("/posts/{postID}", async (int postID, Edit edit, HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
            if (blogID == null) {
                logger.ERR("Failed to edit post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\". Invalid token");
                return Unauthorized("Invalid login token.");
            }

			using SqlCommand editCommand = new SqlCommand("UPDATE posts SET title = @title, content = @content, summary = @summary, date_edited = CURRENT_TIMESTAMP WHERE post_id = @post_id",
				("@title", edit.title),
				("@content", edit.content),
				("@summary", edit.summary ?? string.Empty),
				("@post_id", postID));
			int rowsAffected = await editCommand.Execute();

			if (rowsAffected > 0) {
				logger.INFO("Successfully edited post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\"");
				BlogPost post = (await GetBlogPost(blogID, postID))!;
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

		blog.MapDelete("/posts/{postID}", async (int postID, HttpContext context) => {
            string loginToken = GetHttpCookie(context, "login_token");

            string? blogID = await GetBlogIDFromToken(loginToken);
            if (blogID == null) {
                logger.ERR("Failed to delete post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\". Invalid token");
                return Unauthorized("Invalid login token.");
            }

            using SqlCommand deleteCommand = new SqlCommand("DELETE FROM posts WHERE blog_id = @blog_id AND post_id = @post_id",
				("@blog_id", blogID),
				("@post_id", postID));
            int rowsAffected = await deleteCommand.Execute();

            if (rowsAffected > 0) {
				logger.INFO("Successfully deleted post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\"");
				return Results.Ok();
            }

			logger.ERR("Failed to delete post with ID: {" + postID + "} from blog with ID: \"" + blogID + "\" for unknown reason");
            return ServerError("Failed to delete post.");
        });

        blog.MapGet("/posts", async ([AsParameters] Get request, HttpContext context) => {
            long searchLastID = request.lastPostID == 0 ? long.MaxValue : request.lastPostID;

            using SqlCommand queryPostCommand = new SqlCommand("SELECT post_id, title, content, summary, date_created, date_edited FROM posts WHERE blog_id = @blog_id AND post_id < @last_id ORDER BY post_id DESC LIMIT @amount",
                ("@blog_id", request.blogID),
                ("@amount", request.amount),
                ("@last_id", searchLastID));

            List<object[]> info = await queryPostCommand.ExecuteGet();

            if (info.Count == 0) {
				logger.WARN("Failed to find any posts when requesting {" + request.amount + "} posts from blog with ID: \"" + request.blogID + "\" starting at post ID: {" + request.lastPostID + "}");
                return Results.Ok(new List<object>());
            }

            MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

            List<object?> posts = info.Select(row => {
                string rawMarkdown = (string)row[2];
                string htmlContent = Markdown.ToHtml(rawMarkdown, pipeline);

                return (object?)new {
                    postID = (long)row[0],
                    postTitle = (string)row[1],
                    postRawContent = rawMarkdown,
                    postFormattedContent = htmlContent,
                    postSummary = (string?)row[3],
                    postCreationDate = (string)row[4],
					postEditDate = (string?)row[5]
                };
            }).ToList();

            return Results.Ok(posts);
        });

        blog.MapPost("/posts/search", async (Search search, HttpContext context) => {
			string keyword = search.keywordSearch?.searchKey ?? string.Empty;
			using SqlCommand searchCommand = new SqlCommand("SELECT post.title, post.content, post.summary, post.date_created FROM posts post JOIN posts_fts fts ON post.post_id = fts.rowid WHERE posts_fts MATCH @keyword ORDER BY rank", 
				("@keyword", keyword + "*"));

			List<object[]> results = await searchCommand.ExecuteGet();
			if (results.Count == 0) {
				//logger.WARN("Found 0 posts from blog with ID: \"" + search.blogID + "\" ")
				return NotFound("No posts found that matched those filters");
			}

			return Results.Ok(results);
		});
	}

	private async Task<string> AddLoginToken(string blogID) {
        string loginToken = MakeUUID();
        using SqlCommand addSessionCommand = new SqlCommand("INSERT INTO sessions (blog_id, login_token) VALUES (@blog_id, @login_token)",
            ("@blog_id", blogID),
            ("@login_token", loginToken));
        await addSessionCommand.Execute();

		return loginToken;
    }

	private async Task<BlogPost?> GetBlogPost(string blogID, int postID) {
		using SqlCommand queryCommand = new SqlCommand("SELECT title, content, summary, date_created, date_edited FROM posts WHERE blog_id = @blog_id AND post_id = @post_id",
			("@blog_id", blogID),
			("@post_id", postID));
		List<object[]> fields = await queryCommand.ExecuteGet();
		if (fields.Count != 1) {
			return null;
		}

        MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        return new BlogPost(blogID, postID, (string)fields[0][0], (string)fields[0][1], Markdown.ToHtml((string)fields[0][1], pipeline), (string)fields[0][2], (string)fields[0][3], (string)fields[0][4]);
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
		using SqlCommand queryCommand = new SqlCommand("SELECT blog_id FROM sessions WHERE login_token = @login_token",
			("@login_token", loginToken));
		return (string?)await queryCommand.ExecuteGetScalar();
	}

    private string GetHttpCookie(HttpContext context, string key) {
		if (context.Request.Cookies.TryGetValue(key, out string? value)) {
			return value ?? string.Empty;
		}

		return string.Empty;
	}

    private void SetHttpCookie(HttpContext context, string key, string value) {
		context.Response.Cookies.Append(key, value, new CookieOptions{
			HttpOnly = true,
			Secure = true,
			SameSite = SameSiteMode.Strict,
			Domain = ".kiwiandoesthings.place",
			Expires = DateTimeOffset.UtcNow.AddDays(365)
		});
	}

	public readonly struct SqlCommand : IDisposable {
		public static SqliteConnection? database;
		public readonly SqliteCommand command;

		public static void Setup(SqliteConnection database) {
			SqlCommand.database = database;
		}
	
		public SqlCommand(string commandText, params (string name, object value)[] parameters) {
			command = database!.CreateCommand();
			command.CommandText = commandText;
			foreach ((string name, object value) parameterTuple in parameters) {
				command.Parameters.AddWithValue(parameterTuple.name, parameterTuple.value ?? DBNull.Value);
			}
		}

		public async Task<int> Execute() {
			return await command.ExecuteNonQueryAsync();
		}

		public async Task<object?> ExecuteGetScalar() {
			return await command.ExecuteScalarAsync();
		}

		public async Task<List<object[]>> ExecuteGet() {
			using SqliteDataReader reader = await command.ExecuteReaderAsync();
			List<object[]> rows = [];
			while (await reader.ReadAsync()) {
				object[] row = new object[reader.FieldCount];
				for (int i = 0; i < reader.FieldCount; i++) {
					if (reader.IsDBNull(i)) {
						Console.WriteLine("Got null value from expression: " + command.CommandText);
						row[i] = "";
                    } else {
						row[i] = reader.GetValue(i);
					}
				}
				rows.Add(row);
			}
			return rows;
		}

		public void Dispose() {
			command.Dispose();
		}
	}

    public static bool ValidString(string toCheck) {
        return Regex.IsMatch(toCheck, @"^[a-zA-Z0-9\-_]+$");
    }

    public static bool ValidAdvancedString(string toCheck) {
        return Regex.IsMatch(toCheck, @"^[\x21-\x7E]+$");
    }

    public static bool ValidUsername(string username) {
        return username.Length >= 4 && username.Length <= 20 && ValidString(username);
    }

    public static bool ValidEmail(string email) {
        return Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$");
    }

    public static bool ValidPassword(string password) {
        return password.Length >= 8 && password.Length <= 24 && ValidAdvancedString(password);
    }
}