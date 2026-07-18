namespace kiwiapi;

using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.Hosting;

public class Bot : BackgroundService {
    private readonly DiscordSocketClient client;
    private readonly InteractionService commands;
    private readonly IServiceProvider services;

    public Bot(IServiceProvider services) {
        this.services = services;
        DiscordSocketConfig config = new DiscordSocketConfig {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
        };
        client = new DiscordSocketClient(config);
        commands = new InteractionService(client);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await commands.AddModuleAsync<PsmpModule>(services);

        client.Ready += async () => {
            await commands.RegisterCommandsGloballyAsync();
        };

        client.InteractionCreated += async (interaction) => {
            var ctx = new SocketInteractionContext(client, interaction);
            await commands.ExecuteCommandAsync(ctx, services);
        };

        await client.LoginAsync(TokenType.Bot, "MTUyMTI1OTcyMzczMzg2MDM1Mw.GGPG8O._jMrRPsJlL-WKo30yPgUZSn6ZJp9oEmAGYS24I");
        await client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
