namespace kiwiapi;

using Microsoft.Data.Sqlite;

using static Program;

public class KiwiBlogApi {
	private SqliteConnection database;

	private record RegisterBlog(string name, string email, string password, bool isEmailPublic);
	private record DeregisterBlog();
	private record LoginBlog(string email, string password);
	private record LogoutBlog();
	private record AddPost(string title, string content);
	private record GetPost(string blogID, int postID);
	private record SearchPosts(string blogID, DateRangeSearch? dateRangeSearch, KeywordSearch? keywordSearch);
	private record GetBlogInformation(string blogID);

	private record DateRangeSearch(string startDate, string endDate);
	private record KeywordSearch(string searchKey, bool fuzzySearch);

	public KiwiBlogApi(SqliteConnection database) {
		this.database = database;
		SqlCommand.Setup(database);
	}
	
	public void MapApiFunctions(WebApplication app) {
		RouteGroupBuilder blog = app.MapGroup("/blog");

		blog.MapPost("/register", async (RegisterBlog registration, HttpContext context) => {
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

		blog.MapPost("/deregister", async (DeregisterBlog deregistration, HttpContext context) => {
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

		blog.MapPost("/login", async (LoginBlog login, HttpContext context) => {
			using SqlCommand verifyCredentialsCommand = new SqlCommand("SELECT blog_id FROM blogs WHERE email = @email AND password_hash = @password_hash",
			("@email", login.email),
			("@password_hash", GetHashedString(login.password)));
			string? blogID = (string?)await verifyCredentialsCommand.ExecuteGetScalar();
			if (blogID == null) {
				return BadRequest("Incorrect credentials");
			}

			string loginToken = MakeUUID();
			using SqlCommand addSessionCommand = new SqlCommand("INSERT INTO sessions (blog_id, login_token) VALUES (@blog_id, @login_token)",
			("@blog_id", blogID),
			("@login_token", loginToken));
			await addSessionCommand.Execute();

			SetHttpCookie(context, "login_token", loginToken);

			return Results.Ok(new {
				blogID = blogID
			});
		});

		blog.MapPost("/logout", async (LogoutBlog logout, HttpContext context) => {
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

		blog.MapPost("/add_post", async (AddPost post, HttpContext context) => {
			string loginToken = GetHttpCookie(context, "login_token");

			string? blogID = await GetBlogIDFromToken(loginToken);
			if (blogID == null) {
				return Unauthorized("Invalid login token.");
			}

			using SqlCommand addPostCommand = new SqlCommand("INSERT INTO posts (blog_id, title, content) VALUES (@blog_id, @title, @content)",
			("@blog_id", blogID),
			("@title", post.title),
			("@content", post.content));
			await addPostCommand.Execute();

			return Results.Ok();
		});

		blog.MapGet("/get_post", async ([AsParameters] GetPost request, HttpContext context) => {
			using SqlCommand queryPostCommand = new SqlCommand("SELECT title, content FROM posts WHERE blog_id = @blog_id AND (SELECT COUNT(*) FROM posts post2 WHERE post2.blog_id = posts.blog_id AND post2.global_post_id < posts.global_post_id) = @post_id",
			("@blog_id", request.blogID),
			("@post_id", request.postID));
			List<object[]> info = await queryPostCommand.ExecuteGet();

			if (info.Count == 0) {
				return NotFound("Could not find a post from that blog ID with that post ID");
			}
			string postTitle = (string)info[0][0];
			string postContent = (string)info[0][1];

			return Results.Ok(new {
				postTitle = postTitle,
				postContent = postContent
			});

		});

		blog.MapPost("/search_posts", async (SearchPosts search, HttpContext context) => {
			string keyword = search.keywordSearch?.searchKey ?? string.Empty;
			using SqlCommand searchCommand = new SqlCommand("SELECT post.title, post.content, post.date_created FROM posts post JOIN posts_fts fts ON post.post_id = fts.rowid WHERE posts_fts MATCH @keyword ORDER BY rank", 
			("@keyword", keyword + "*"));

			List<object[]> results = await searchCommand.ExecuteGet();
			if (results.Count == 0) {
				return NotFound("No posts found that matched those filters");
			}

			return Results.Ok(results);
		});

		blog.MapGet("/get_blog_info", async ([AsParameters] GetBlogInformation request, HttpContext context) => {
			using SqlCommand queryBlogCommand = new SqlCommand("SELECT name, date_created FROM blogs WHERE blog_id = @blog_id",
			("@blog_id", request.blogID));
			List<object[]> info = await queryBlogCommand.ExecuteGet();

			if (info.Count == 0) {
				return NotFound("Could not find a blog with that blog ID");
			}
			string blogName = (string)info[0][0];
			string blogCreationDate = (string)info[0][1];

			return Results.Ok(new {
				blogName = blogName,
				blogCreationDate = blogCreationDate
			});
		});
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

		public async Task Execute() {
			await command.ExecuteNonQueryAsync();
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