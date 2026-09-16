 namespace kiwiapi;

using kiwiapi.ProtoCall;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using System.Text;

using static BCrypt.Net.BCrypt;

public class Program {
    public static readonly string realBasePath = Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)!.FullName)!.FullName)!.FullName)!.FullName;
    public static bool isDebug { get; private set; }

    public static void Main(string[] args) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        isDebug = builder.Environment.IsDevelopment();
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

            options.AddPolicy("ForumsPolicy", policy => {
                policy.SetIsOriginAllowed(origin => {
                    return new Uri(origin).Host.EndsWith(".kiwiandoesthings.place");
                });
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
        builder.Services.AddAuthentication(options => {
            options.DefaultAuthenticateScheme = "FruitBowlAuth";
            options.DefaultChallengeScheme = "FruitBowlAuth";
        }).AddCookie("ProtoCallAuth", options => {
            options.Cookie.Name = "protocall_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.Domain = isDebug ? ".test.kiwiandoesthings.place" : ".kiwiandoesthings.place";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(365);
            options.SlidingExpiration = true;
        }).AddCookie("FruitBowlAuth", options => {
            options.Cookie.Name = "fruitbowl_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = isDebug ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Domain = isDebug ? ".test.kiwiandoesthings.place" : ".kiwiandoesthings.place";
            options.ExpireTimeSpan = TimeSpan.FromDays(365);
            options.SlidingExpiration = true;
        });
        builder.Services.AddAuthorization();
        builder.Services.AddHostedService<Bot>();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
		app.UseHsts();
		app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapOpenApi();

        app.MapGet("/", () => Results.Ok("KiwiApi v1.2"));

        Ao3Api ao3 = new Ao3Api();
        ao3.MapApiFunctions(app);

        MiscellaneousApi miscellaneous = new MiscellaneousApi();
        miscellaneous.MapApiFunctions(app);

        ProtoCallHub.Setup(app);

		KiwiBlogApi kiwiBlog = new KiwiBlogApi();
        kiwiBlog.MapApiFunctions(app);

        FruitBowlForums forums = new FruitBowlForums();
        forums.MapApiFunctions(app);

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

    public static string GetExceptionMessage(Exception exception) {
        return "Encountered unknown error. Report the following as well as any relevant information to Kiwian: \"" + exception.Message + "\"";
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
        private static readonly Logger logger = new Logger("SQL");
        private readonly string databaseConnectionString;

        public SqlInterface(string databaseName) {
            this.databaseConnectionString = "Data Source=" + databaseName + ".db";

            string databasePath = Path.Combine(realBasePath, databaseName + ".db");
            string schemaPath = Path.Combine(realBasePath, databaseName + ".sql");

            bool databaseExists = File.Exists(databasePath);

            using SqliteConnection connection = new SqliteConnection(databaseConnectionString);
            connection.Open();

            using SqliteCommand walCommand = connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            walCommand.ExecuteNonQuery();

            if (!databaseExists) {
                if (File.Exists(schemaPath)) {
                    logger.WARN("Couldn't find \"" + databaseName + ".db\" at \"" + databasePath + "\", creating from \"schema.sql\"");

                    string schemaSql = File.ReadAllText(schemaPath);
                    schemaSql = schemaSql.Replace("CREATE TABLE sqlite_sequence(name,seq);", "");

                    using SqliteCommand schemaCommand = connection.CreateCommand();
                    schemaCommand.CommandText = schemaSql;
                    schemaCommand.ExecuteNonQuery();
                } else {
                    throw new FileNotFoundException("Couldn't find \"" + databaseName + ".db\" at \"" + databasePath + "\" and couldn't find a \"schema.sql\" file in the same directory to create from.");
                }
            } else {
                logger.INFO("Found \"" + databaseName + ".db\" at \"" + databasePath + "\"");

                using SqliteCommand schemaQuery = connection.CreateCommand();
                schemaQuery.CommandText = "SELECT sql FROM sqlite_master WHERE sql NOT NULL AND type='table' AND name NOT LIKE 'sqlite_%';";

                using SqliteDataReader reader = schemaQuery.ExecuteReader();
                List<string> statements = [];
                while (reader.Read()) {
                    string sqlStatement = reader.GetString(0);
                    statements.Add(sqlStatement + ";");
                }

                File.WriteAllText(schemaPath, string.Join("\n", statements));
            }
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