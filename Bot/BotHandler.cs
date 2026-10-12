namespace KiwiApi.Bot;

using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.Hosting;

public class BotHandler : BackgroundService {
    private readonly Logger logger;
    private readonly DiscordSocketClient client;
    private readonly InteractionService commands;
    private readonly IServiceProvider services;
    private readonly string? botToken;

    public BotHandler(IServiceProvider services) {
        logger = new Logger("PSMP");
        DiscordSocketConfig config = new DiscordSocketConfig {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
        };
        client = new DiscordSocketClient(config);
        client.MessageReceived += BotModule.MessageSent;
        commands = new InteractionService(client);
        this.services = services;
        botToken = Environment.GetEnvironmentVariable("KIWIAPI_DISCORD_BOT_TOKEN");
        if (botToken == null) {
            logger.WARN("KIWIAPI_DISCORD_BOT_TOKEN environment variable is not set. Disabling PSMP bot module");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (botToken == null) {
            return;
        }

        await commands.AddModuleAsync<BotModule>(services);

        client.Ready += async () => {
            await commands.RegisterCommandsGloballyAsync();
        };

        client.InteractionCreated += async (interaction) => {
            var ctx = new SocketInteractionContext(client, interaction);
            await commands.ExecuteCommandAsync(ctx, services);
        };

        await client.LoginAsync(TokenType.Bot, botToken);
        await client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
