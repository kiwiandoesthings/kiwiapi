namespace kiwiapi;

using kiwiapi.ProtoCall;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using System.Text;

using static BCrypt.Net.BCrypt;

public class Program {
    public static readonly string realBasePath = Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)!.FullName)!.FullName)!.FullName)!.FullName;

    public static void Main(string[] args) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://localhost:5201", "https://localhost:7164");
        builder.Services.AddAntiforgery();
        builder.WebHost.ConfigureKestrel(options => {
			options.AddServerHeader = false;
		});
		builder.Services.AddControllers();
        builder.Services.AddOpenApi();
        builder.Services.AddSignalR(options => {
            options.EnableDetailedErrors = true;
        });
        builder.Services.AddCors(options => {
            options.AddPolicy("BlogPolicy", policy => {
                policy.SetIsOriginAllowed(allowed => true);
                policy.AllowAnyMethod();
                policy.AllowAnyHeader();
                policy.AllowCredentials();
            });

            options.AddPolicy("ProtoCallPolicy", policy => {
                policy.SetIsOriginAllowed(allowed => true);
                policy.AllowAnyMethod();
                policy.AllowAnyHeader();
                policy.AllowCredentials();
            });

            options.AddDefaultPolicy(policy => {
                policy.SetIsOriginAllowed(origin => {
                    string host = new Uri(origin).Host;
                    return host.EndsWith(".kiwiandoesthings.place") || host == "kiwiandoesthings.place" || host == "localhost"; 
                }).AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
            });
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => {
            options.Cookie.Name = "protocall_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromDays(365);
            options.SlidingExpiration = true;
        });
        builder.Services.AddAuthorization();
        builder.Services.AddHostedService<Bot>();

        WebApplication app = builder.Build();
		//app.UseExceptionHandler("/error");
		app.UseHsts();
		app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapOpenApi();

        app.MapGet("/", () => Results.Ok("KiwiApi v1.0"));

        Ao3Api ao3 = new Ao3Api();
        ao3.MapApiFunctions(app);

        MiscellaneousApi miscellaneous = new MiscellaneousApi();
        miscellaneous.MapApiFunctions(app);

        ProtoCallHub.Setup(app);

		KiwiBlogApi kiwiBlog = new KiwiBlogApi();
        kiwiBlog.MapApiFunctions(app);

        app.MapHub<ProtoCallHub>("/protocall/connect");

        app.MapControllers();

        app.Run();
    }

	public static IResult Unauthorized(string error) {
		return Results.Content(
			error,
			"text/plain",
			Encoding.UTF8,
			statusCode: 401
		);
	}

	public static IResult NotFound(string error) {
		return Results.Content(
			error,
			"text/plain",
			Encoding.UTF8,
			statusCode: 404
		);
	}

	public static IResult BadRequest(string error) {
		return Results.Content(
			error,
			"text/plain",
			Encoding.UTF8,
			statusCode: 400
		);
	}

	public static IResult ServerError(string error) {
		return Results.Content(
			error,
			"text/plain",
			Encoding.UTF8,
			statusCode: 500
		);
	}

    public static string GetHashedString(string toHash) {
        return HashPassword(toHash);
	}

    public static bool VerifyHashedString(string input, string hashedInput) {
        return Verify(input, hashedInput);
    }

    public static string MakeUUID() {
        return Guid.NewGuid().ToString();
    }

    public readonly struct SqlCommand : IDisposable {
        private readonly SqliteConnection connection;
        public readonly SqliteCommand command;

        public SqlCommand(SqliteConnection connection, string commandText, params (string name, object value)[] parameters) {
            this.connection = connection;
            command = connection.CreateCommand();
            command.CommandText = commandText;
            foreach ((string name, object value) parameterTuple in parameters) {
                command.Parameters.AddWithValue("@" + parameterTuple.name, parameterTuple.value ?? DBNull.Value);
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
                for (int iterator = 0; iterator < reader.FieldCount; iterator++) {
                    if (reader.IsDBNull(iterator)) {
                        Console.WriteLine("Got null value from expression: " + command.CommandText);
                        row[iterator] = "";
                    } else {
                        row[iterator] = reader.GetValue(iterator);
                    }
                }
                rows.Add(row);
            }
            return rows;
        }

        public void Dispose() {
            command.Dispose();
            connection.Dispose();
        }
    }

    public class SqlInterface {
        private readonly string databaseConnectionString;

        public SqlInterface(string databaseConnectionString) { 
            this.databaseConnectionString = databaseConnectionString;

            using SqliteConnection connection = new SqliteConnection(databaseConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteNonQuery();
        }

        public SqlCommand Command(string commandText, params (string name, object value)[] parameters) {
            SqliteConnection connection = new SqliteConnection(databaseConnectionString);
            connection.Open();

            using SqliteCommand timeoutCommand = connection.CreateCommand();
            timeoutCommand.CommandText = "PRAGMA busy_timeout = 500;";
            timeoutCommand.ExecuteNonQuery();

            return new SqlCommand(connection, commandText, parameters);
        }
    }
}