namespace kiwiapi;

using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;

using static BCrypt.Net.BCrypt;

public class Program {
    public static SqliteConnection? database;
    public static HttpClient? client;
    private static SocketsHttpHandler? handler;

	public static void Main(string[] args) {
        // I hate this line so much like wtf is this
		string connectionString = "Data Source=" + Path.Combine(Directory.GetParent(Directory.GetParent(Directory.GetParent(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)!.FullName)!.FullName)!.FullName)!.FullName, "protocall.db");
        database = new SqliteConnection(connectionString);
        database.Open();

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
            options.AddDefaultPolicy(policy => {
                policy.WithOrigins("http://localhost:8000", "https://localhost:8000", "https://test.kiwiandoesthings.place:8000", "https://api.kiwiandoesthings.place", "https://protocall.kiwiandoesthings.place", "https://kiwiandoesthings.place", "https://www.kiwiandoesthings.place", "https://www.api.kiwiandoesthings.place", "https://www.protocall.kiwiandoesthings.place");
                policy.AllowAnyMethod();
                policy.AllowAnyHeader();
                policy.AllowCredentials();
            });
        });

        WebApplication app = builder.Build();
		//app.UseExceptionHandler("/error");
		app.UseHsts();
        app.UseCors();
		app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapOpenApi();

        handler = new SocketsHttpHandler() {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10,
            ConnectCallback = async (context, cancellationToken) => {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken);
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, true);
            }
        };

        client = new HttpClient(handler);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
        client.DefaultRequestHeaders.Add("Dnt", "1");
        client.DefaultRequestHeaders.Add("Priority", "u=0, i");
        client.DefaultRequestHeaders.Add("Referer", "https://archiveofourown.org/");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Chromium\";v=\"146\", \"Not-A.Brand\";v=\"24\", \"Google Chrome\";v=\"146\"");
		client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
		client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
		client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
		client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
		client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
		client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.ConnectionClose = false;

        client.DefaultRequestVersion = HttpVersion.Version11;

        Ao3Api ao3 = new Ao3Api();
        ao3.MapApiFunctions(app);

        MiscellaneousApi miscellaneous = new MiscellaneousApi();
        miscellaneous.MapApiFunctions(app);

        ProtoCallApi protocall = new ProtoCallApi();
        protocall.MapApiFunctions(app);

		KiwiBlogApi kiwiBlog = new KiwiBlogApi(database);
        kiwiBlog.MapApiFunctions(app);

        app.MapHub<ProtoCallApi>("/protocall");

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

    public static int ComputeDistance(string? source, string? target) {
        if (source == null || target == null) {
            return 100;
        }
        if (source.Length == 0) {
            return target.Length;
        }
        if (target.Length == 0) {
            return source.Length;
        }

		int[,] distance = new int[source.Length + 1, target.Length + 1];
		for (int i = 0; i <= source.Length; i++) {
            distance[i, 0] = i;
		}
		for (int j = 0; j <= target.Length; j++) {
            distance[0, j] = j;
		}

		for (int i = 1; i <= source.Length; i++) {
			for (int j = 1; j <= target.Length; j++) {
				int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
				distance[i, j] = Math.Min(
					Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
					distance[i - 1, j - 1] + cost);
			}
		}
		return distance[source.Length, target.Length];
	}
}