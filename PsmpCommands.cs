namespace kiwiapi;

using Discord;
using Discord.Audio;
using Discord.Interactions;

public class PsmpModule : InteractionModuleBase<SocketInteractionContext> {
    private static readonly Dictionary<ulong, IAudioClient> audioClients = [];

    [Group("psmp", ":3")]
    public class PsmpGroup : InteractionModuleBase<SocketInteractionContext> {
        [SlashCommand("join", "hiii", runMode: RunMode.Async)]
        public async Task Join() {
            await DeferAsync();

            try {
                IGuildUser? user = Context.User as IGuildUser;
                IVoiceChannel? channel = user?.VoiceChannel;

                if (channel == null) {
                    await FollowupAsync("u big stupid :<");
                    return;
                }

                IAudioClient audioClient = await channel.ConnectAsync();
                audioClients[Context.Guild.Id] = audioClient;

                await FollowupAsync("heallo >:3");
            } catch (Exception ex) {
                Console.WriteLine("Error: " + ex.GetType().Name + " - " + ex.Message);
                await FollowupAsync("haha: " + ex.Message);
            }
        }

        [SlashCommand("edit", "change my voice !")]
        public async Task Edit(int pitch = 64, int speed = 72, int mouth = 128, int throat = 128) {
            await RespondAsync("ok new voice :3");
        }

        [SlashCommand("reset", "reset my voice (idiot)")]
        public async Task Reset() {
            await RespondAsync("its normal again !!");
        }

        [SlashCommand("status", "kiwian only !!")]
        public async Task Status(string newStatus) {
            if (Context.User.Username != "kiwiandoesthings") {
                await RespondAsync("ur NOT kiwian x3");
                return;
            }

            await Context.Client.SetActivityAsync(new Game(newStatus));
            await RespondAsync("okay !!!");
        }
    }
}