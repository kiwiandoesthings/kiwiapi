 namespace KiwiApi;

using KiwiApi.Bot;
using KiwiApi.ProtoCall;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

using static BCrypt.Net.BCrypt;

public partial class Program {
    public static readonly string realBasePath = Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)!.FullName)!.FullName)!.FullName)!.FullName;
    public static readonly Logger logger = new Logger("MAIN");
    public static bool isDebug { get; private set; }

    public static void Main(string[] arguments) {
        foreach (string argument in arguments) {
            switch (argument) {
                case "--verbose":
                    Globals.verboseLogging = true;
                    break;
                default:
                    break;
            }
        }

        SqlInterface sql = new SqlInterface("fruitbowl");
        FruitBowl fruitbowl = new FruitBowl(sql);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(arguments);
        isDebug = builder.Environment.IsDevelopment();
        builder.WebHost.UseUrls("http://localhost:5201", "https://localhost:7164");
        builder.Services.AddAntiforgery();
        builder.WebHost.ConfigureKestrel(options => {
			options.AddServerHeader = false;
            string certificatePath = Path.Combine(Directory.GetCurrentDirectory(), "certificate.pfx");
            if (File.Exists(certificatePath)) {
                options.ConfigureHttpsDefaults(httpOptions => {
                    httpOptions.ServerCertificate = httpOptions.ServerCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certificatePath, "password");
                });
            }
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
            options.AddPolicy("FruitBowlPolicy", policy => {
                policy.SetIsOriginAllowed(origin => {
                    return new Uri(origin).Host.EndsWith(".kiwiandoesthings.place");
                });
                policy.AllowAnyMethod();
                policy.AllowAnyHeader();
                policy.AllowCredentials();
            });

            options.AddDefaultPolicy(policy => {
                policy.SetIsOriginAllowed(allowed => false);
            });
        });
        builder.Services.AddAuthentication(options => {
            options.DefaultAuthenticateScheme = "FruitBowlAuth";
            options.DefaultChallengeScheme = "FruitBowlAuth";
        }).AddCookie("FruitBowlAuth", options => {
            options.Cookie.Name = "fruitbowl_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = isDebug ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.Domain = isDebug ? ".test.kiwiandoesthings.place" : ".kiwiandoesthings.place";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents {
                OnValidatePrincipal = async context => {
                    await fruitbowl.ValidateUserPrincipal(context);
                },
                OnRedirectToLogin = context => {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context => {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddHostedService<BotHandler>();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();

        WebApplication app = builder.Build();
        app.UseExceptionHandler();
		app.UseHsts();
        app.UseMiddleware<CustomMiddleware>();
		app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapControllers();
        app.MapOpenApi();

        app.MapGet("/", () => Results.Ok("KiwiApi v1.4"));

        fruitbowl.MapApiFunctions(app);
        Globals.users = fruitbowl;

        Ao3Api ao3 = new Ao3Api();
        ao3.MapApiFunctions(app);

        MiscellaneousApi miscellaneous = new MiscellaneousApi();
        miscellaneous.MapApiFunctions(app);

        ProtoCallHub.Setup(app, sql);

		KiwiBlogApi kiwiBlog = new KiwiBlogApi(sql);
        kiwiBlog.MapApiFunctions(app);

        BlueberryForums forums = new BlueberryForums(sql);
        forums.MapApiFunctions(app);

        app.MapHub<ProtoCallHub>("/protocall/live").RequireCors("FruitBowlPolicy");

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

    public static void LogDesynchronized() {
        logger.CRITICAL("Desynchronized user attempted an authorized request");
    }

    public static string DesynchronizedResponse() {
        return "Your account is desynchronized. Please log out and back in. If issues persist, contact @KiwianDoesThings";
    }

    public static IResult DesynchronizedResult() {
        LogDesynchronized();
        return BadRequest(DesynchronizedResponse());
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
        return "Encountered unknown error. Report the following as well as any relevant information to @KiwianDoesThings: \"" + exception.Message + "\"";
    }

    public static bool StringEqualsExtensive(string a, string b) {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_]+$")]
    private static partial Regex ValidStringRegex();

    [GeneratedRegex(@"^[\x21-\x7E]+$")]
    private static partial Regex ValidAdvancedStringRegex();

    [GeneratedRegex(@"^#?([A-Fa-f0-9]{6})$")]
    private static partial Regex ValidHexRegex();

    public static bool ValidString(string toCheck) {
        return ValidStringRegex().IsMatch(toCheck);
    }

    public static bool ValidAdvancedString(string toCheck) {
        return ValidAdvancedStringRegex().IsMatch(toCheck);
    }

    public static bool ValidHex(string toCheck) {
        return ValidHexRegex().IsMatch(toCheck);
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

        public async Task<SqliteDataReader> ExecuteGet() {
            return await command.ExecuteReaderAsync();
        }

        public void Dispose() {
            command.Dispose();
            connection.Dispose();
        }
    }

    public class SqlInterface {
        private static readonly Logger logger = new Logger(" SQL");
        private readonly string databaseConnectionString;

        public SqlInterface(string databaseName) {
            string realDatabaseName = Path.Combine("storage", databaseName);
            databaseConnectionString = "Data Source=" + realDatabaseName + ".db";

            string databasePath = Path.Combine(realBasePath, realDatabaseName + ".db");
            string schemaPath = Path.Combine(realBasePath, realDatabaseName + ".sql");

            bool databaseExists = File.Exists(databasePath);

			using SqliteConnection connection = new SqliteConnection(databaseConnectionString);
			connection.Open();

			using SqliteCommand walCommand = connection.CreateCommand();
			walCommand.CommandText = "PRAGMA journal_mode=WAL;";
			walCommand.ExecuteNonQuery();

			if (!databaseExists) {
                if (File.Exists(schemaPath)) {
                    logger.WARN("Couldn't find \"" + databaseName + ".db\" at \"" + databasePath + "\", creating from \"" + databaseName + ".sql\"");

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
                schemaQuery.CommandText = "SELECT sql FROM sqlite_master WHERE sql NOT NULL AND type IN ('table', 'trigger', 'view') AND name NOT LIKE 'sqlite_%';";

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