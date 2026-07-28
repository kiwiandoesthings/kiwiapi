namespace kiwiapi;

using Markdig;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;

using static Program;

public class KiwiBlogApi {
	private SqliteConnection database;

	private record Register(string name, string email, string password, bool isEmailPublic);
	private record Deregister();
	private record Login(string email, string password);
	private record Logout();
	private record Add(string title, string content, string? summary);
	private record Edit(int postID, string title, string content, string? summary);
	private record Delete(int postID);
	private record Get(string blogID, int startPostID, int amount);
	private record Search(string blogID, DateRangeSearch? dateRangeSearch, KeywordSearch? keywordSearch);
	private record GetInformation(string blogID);
	private record GetScript(string blogID, string stylesheetName, string? containerID);

	private record DateRangeSearch(string startDate, string endDate);
	private record KeywordSearch(string searchKey, bool fuzzySearch);

    private const string baseEmbed = "<script src=\"https://test.kiwiandoesthings.place/scripts/common.js\"></script><script src=\"https://test.kiwiandoesthings.place/scripts/blog_functions.js\"></script><script src=\"https://test.kiwiandoesthings.place/scripts/home_blog.js\"></script><script src=\"https://test.kiwiandoesthings.place/scripts/embed_loader.js\"></script><script>initialize({stylesheet:\"@STYLESHEET@\",blogID:\"@BLOG_ID@\",containerID:\"@CONTAINER_ID@\"});</script>";

	public KiwiBlogApi() {
        string connectionString = "Data Source=" + Path.Combine(Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)!.FullName)!.FullName)!.FullName)!.FullName, "kiwiblog.db");
        database = new SqliteConnection(connectionString);
        database.Open();

        SqlCommand.Setup(database);
	}
	
	public void MapApiFunctions(WebApplication app) {
		RouteGroupBuilder blog = app.MapGroup("/blog").RequireCors("BlogPolicy");

		blog.MapPost("/register", async (Register registration, HttpContext context) => {
			string blogID = MakeUUID();
			using SqlCommand registerBlogCommand = new SqlCommand("INSERT INTO blogs (blog_id, name, email, password_hash, email_public) VALUES (@blog_id, @name, @email, @password_hash, @email_public)",
			("@blog_id", blogID),
			("@name", registration.name),
			("@email", registration.email),
			("@password_hash", GetHashedString(registration.password)),
			("@email_public", registration.isEmailPublic));
			await registerBlogCommand.Execute();

			return Results.Ok(new {
				blogID = blogID
			});
		});

		blog.MapPost("/deregister", async (Deregister deregistration, HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
			if (blogID == null) {
				return Unauthorized("Invalid login token.");
			}

			using SqlCommand deregisterCommand = new SqlCommand("DELETE FROM blogs WHERE blog_id = @blog_id",
			("@blog_id", blogID));
			await deregisterCommand.Execute();

			return Results.Ok();
		});

		blog.MapPost("/login", async (Login login, HttpContext context) => {
            using SqlCommand getHashCommand = new SqlCommand("SELECT blog_id, password_hash FROM blogs WHERE email = @email", 
			("@email", login.email));
            List<object[]> result = await getHashCommand.ExecuteGet();

			string blogID;
            if (result.Count > 0) {
                blogID = (string)result[0][0];
                string storedHash = (string)result[0][1];

                if (!VerifyHashedString(login.password, storedHash)) {
					Console.WriteLine("Attempted login: invalid");
                    return Unauthorized("Incorrect credentials");
                }
            } else {
                Console.WriteLine("Attempted login: invalid");
                return Unauthorized("Incorrect credentials");
			}

            string loginToken = MakeUUID();
			using SqlCommand addSessionCommand = new SqlCommand("INSERT INTO sessions (blog_id, login_token) VALUES (@blog_id, @login_token)",
			("@blog_id", blogID),
			("@login_token", loginToken));
			await addSessionCommand.Execute();

			SetHttpCookie(context, "login_token", loginToken);

			Console.WriteLine("Attempted login: valid. Returning blog ID \"" + blogID + "\"");

			return Results.Ok(new {
				blogID = blogID
			});
		});

		blog.MapPost("/logout", async (Logout logout, HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
			if (blogID == null) {
				return Unauthorized("Invalid login token.");
			}

			using SqlCommand removeSessionCommand = new SqlCommand("DELETE FROM sessions WHERE login_token = @login_token",
			("@login_token", loginToken));
			await removeSessionCommand.Execute();

			SetHttpCookie(context, "login_token", "");

			return Results.Ok();
		});

		blog.MapPost("/add", async (Add post, HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
			if (blogID == null) {
				return Unauthorized("Invalid login token.");
			}

			using SqlCommand addPostCommand = new SqlCommand("INSERT INTO posts (blog_id, title, content, summary) VALUES (@blog_id, @title, @content, @summary)",
			("@blog_id", blogID),
			("@title", post.title),
			("@content", post.content),
			("@summary", post.summary ?? string.Empty));
			await addPostCommand.Execute();

			return Results.Ok();
		});

		blog.MapPost("/edit", async (Edit edit, HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
            if (blogID == null) {
                return Unauthorized("Invalid login token.");
            }

			if (await DeletePost(blogID, edit.postID)) {

			}

			return Results.Ok();
        });

		blog.MapPost("/delete", async (Delete delete, HttpContext context) => {
            string loginToken = GetHttpCookie(context, "login_token");

            string? blogID = await GetBlogIDFromToken(loginToken);
            if (blogID == null) {
                return Unauthorized("Invalid login token.");
            }

            if (await DeletePost(blogID, delete.postID)) {
				return Results.Ok();
            }

			return ServerError("Failed to delete post.");
        });

        blog.MapGet("/get", async ([AsParameters] Get request, HttpContext context) => {
            int offset = Math.Max(0, request.startPostID - 1);

            using SqlCommand queryPostCommand = new SqlCommand(
                "SELECT title, content, summary, date_created FROM posts WHERE blog_id = @blog_id ORDER BY post_id ASC LIMIT @amount OFFSET @offset",
                ("@blog_id", request.blogID),
                ("@amount", request.amount),
                ("@offset", offset)
            );

            List<object[]> info = await queryPostCommand.ExecuteGet();

            if (info.Count == 0) {
                return Results.Ok(new List<object>());
            }

            MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

            List<object> posts = info.Select(row => {
                string rawMarkdown = (string)row[1];
                string htmlContent = Markdown.ToHtml(rawMarkdown, pipeline);

                return (object)new {
                    postTitle = (string)row[0],
                    postRawContent = rawMarkdown,
                    postFormattedContent = htmlContent,
                    postSummary = (string)row[2],
                    postCreationDate = (string)row[3]
                };
            }).ToList();

			return Results.Ok(posts);
        });

        blog.MapPost("/search", async (Search search, HttpContext context) => {
			string keyword = search.keywordSearch?.searchKey ?? string.Empty;
			using SqlCommand searchCommand = new SqlCommand("SELECT post.title, post.content, post.summary, post.date_created FROM posts post JOIN posts_fts fts ON post.post_id = fts.rowid WHERE posts_fts MATCH @keyword ORDER BY rank", 
			("@keyword", keyword + "*"));

			List<object[]> results = await searchCommand.ExecuteGet();
			if (results.Count == 0) {
				return NotFound("No posts found that matched those filters");
			}

			return Results.Ok(results);
		});

		blog.MapGet("/info", async ([AsParameters] GetInformation request, HttpContext context) => {
			using SqlCommand queryBlogCommand = new SqlCommand("SELECT name, date_created FROM blogs WHERE blog_id = @blog_id",
			("@blog_id", request.blogID));
			List<object[]> info = await queryBlogCommand.ExecuteGet();

			if (info.Count == 0) {
				return NotFound("Could not find a blog with that blog ID");
			}
			string blogName = (string)info[0][0];
			string blogCreationDate = (string)info[0][1];

			using SqlCommand queryPostsCommand = new SqlCommand("SELECT COUNT(*) FROM posts WHERE blog_id = @blog_id",
			("@blog_id", request.blogID));
			int totalPosts = Convert.ToInt32(await queryPostsCommand.ExecuteGetScalar());

			return Results.Ok(new {
				blogName = blogName,
				totalPosts = totalPosts,
				blogCreationDate = blogCreationDate
			});
		});

		blog.MapGet("/script", async ([AsParameters] GetScript request, HttpContext context) => {
			string baseScript = baseEmbed.Replace("@STYLESHEET@", request.stylesheetName).Replace("@BLOG_ID@", request.blogID);
			bool needsContainer = string.IsNullOrEmpty(request.containerID);
            if (needsContainer) {
				baseScript += "<div id=\"blog-container\"></div>";
			}

            return Results.Ok(new {
				blogScript = baseScript.Replace("@CONTAINER_ID@", needsContainer ? "blog-container" : request.containerID)
			});
        });
	}

    public async Task<bool> DeletePost(string blogID, int postID) {
        using SqlCommand deleteCommand = new SqlCommand(
            "DELETE FROM posts WHERE blog_id = @blog_id AND post_id = @post_id",
            ("@blog_id", blogID),
            ("@post_id", postID)
        );

        int rowsAffected = await deleteCommand.Execute();
        return rowsAffected > 0;
    }

    public async Task<string?> GetBlogIDFromToken(string loginToken) {
		using SqlCommand queryCommand = new SqlCommand("SELECT blog_id FROM sessions WHERE login_token = @login_token",
		("@login_token", loginToken));
		return (string?)await queryCommand.ExecuteGetScalar();
	}

	public string GetHttpCookie(HttpContext context, string key) {
		if (context.Request.Cookies.TryGetValue(key, out string? value)) {
			return value ?? string.Empty;
		}

		return string.Empty;
	}

	public void SetHttpCookie(HttpContext context, string key, string value) {
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
						return [];
					}
					row[i] = reader.GetValue(i);
				}
				rows.Add(row);
			}
			return rows;
		}

		public void Dispose() {
			command.Dispose();
		}
	}
}