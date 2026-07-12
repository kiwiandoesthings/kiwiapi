namespace kiwiapi;

using Microsoft.Data.Sqlite;

using static Program;

public class KiwiBlogApi {
	private SqliteConnection database;

	private record RegisterBlog(string name, string email, string password);
	private record DeregisterBlog(string blogID, string password);
	private record AddPost(string blogID, string title, string content);
	private record GetPost(string blogID, int postID);
	private record SearchPosts(DateRangeSearch? dateRangeSearch, KeywordSearch? keywordSearch);
	private record GetBlogInformation(string blogID);

	private record DateRangeSearch(string startDate, string endDate);
	private record KeywordSearch(string searchKey, bool fuzzySearch);

	public KiwiBlogApi(SqliteConnection database) {
		this.database = database;
	}
	
	public void MapApiFunctions(WebApplication app) {
		RouteGroupBuilder blog = app.MapGroup("/blog");

		blog.MapPost("/register_blog", async (RegisterBlog registration) => {
			Guid blogGuid = Guid.NewGuid();
			string blogID = blogGuid.ToString();
			using SqlCommand registerBlogCommand = new SqlCommand("INSERT INTO blogs (blog_id, name, email, password_hash) VALUES (@blog_id, @name, @email, @password_hash)",
			("@blog_id", blogID),
			("@name", registration.name),
			("@email", registration.email),
			("@password_hash", GetHashedString(registration.password)));
			await registerBlogCommand.Execute();

			return Results.Ok(new {
				id = blogID
			});
		});

		blog.MapPost("/deregister_blog", async (DeregisterBlog deregistration) => {
			using SqlCommand deregisterCommand = new SqlCommand("DELETE FROM blogs WHERE blog_id = @blog_id AND password_hash = @password_hash",
			("@blog_id", deregistration.blogID),
			("@password_hash", GetHashedString(deregistration.password)));
			await deregisterCommand.Execute();
		});

		blog.MapPost("/add_post", async (AddPost post) => {
			using SqlCommand addPostCommand = new SqlCommand("INSERT INTO posts (blog_id, title, content) VALUES (@blog_id, @title, @content)",
			("@blog_id", post.blogID),
			("@title", post.title),
			("@content", post.content));
			await addPostCommand.Execute();
		});

		blog.MapGet("/get_post", async ([AsParameters] GetPost request) => {
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
				title = postTitle,
				content = postContent
			});

		});

		blog.MapPost("/search_posts", async (SearchPosts search) => {
			string keyword = search.keywordSearch?.searchKey ?? "";
			using SqlCommand searchCommand = new SqlCommand("SELECT post.title, post.content, post.date_created FROM posts post JOIN posts_fts fts ON post.post_id = fts.rowid WHERE posts_fts MATCH @keyword ORDER BY rank", 
			("@keyword", keyword + "*"));

			List<object[]> results = await searchCommand.ExecuteGet();
			if (results.Count == 0) {
				return NotFound("No posts found that matched those filters");
			}

			return Results.Ok(results);
		});

		blog.MapGet("/get_blog_info", async ([AsParameters] GetBlogInformation request) => {
			using SqlCommand queryBlogCommand = new SqlCommand("SELECT name, date_created FROM blogs WHERE blog_id = @blog_id",
			("@blog_id", request.blogID));
			List<object[]> info = await queryBlogCommand.ExecuteGet();

			if (info.Count == 0) {
				return NotFound("Could not find a blog with that blog ID");
			}
			string blogName = (string)info[0][0];
			string blogCreationDate = (string)info[0][1];

			return Results.Ok(new {
				name = blogName,
				dateCreated = blogCreationDate
			});
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