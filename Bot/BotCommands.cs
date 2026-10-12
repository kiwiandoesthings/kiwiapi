namespace KiwiApi.Bot;

using Discord;
using Discord.Audio;
using Discord.Interactions;
using Discord.WebSocket;

public class BotModule : InteractionModuleBase<SocketInteractionContext> {
    private static readonly Dictionary<ulong, IAudioClient> audioClients = [];
    private static bool mockMadeline = false;

    [Group("psmp", ":3")]
    public class CommandGroup : InteractionModuleBase<SocketInteractionContext> {
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
            if (Context.User.Username == "kiwiandoesthings") {
                await Context.Client.SetActivityAsync(new Game(newStatus));
                await RespondAsync("okay !!!");
            } else if (Context.User.Username == "cassiopeiai") {
                string[] responses = [
                        "literayl who the fuc are you :?",
                        "get out of here dude",
                        "who do you think you are :P",
                        "im scared someone help :<",
                        "GAHHHH what is THAT?!?!?!?!?",
                        "omg who tf",
                        "bruh absolute RANDOS tryna boss me around x3",
                        "crazyyyy",
                        "nice try but no ;)",
                        "pffff you fuckin wish el mao",
                        "maybe another day....",
                        "get a load of this ~~guy~~ gal"
                ];
                int index = Random.Shared.Next(responses.Length);
                await RespondAsync(responses[index]);
            } else {
                await RespondAsync("ur NOT kiwian x3");
                return;
            }
        }

        [SlashCommand("mock", "should i mock maddy?")]
        public async Task Mock(bool shouldMock) {
            mockMadeline = shouldMock;
            await RespondAsync("ok i am " + (shouldMock ? "" : "not ") + "mocking madeline now");
        }
    }

    public static async Task MessageSent(SocketMessage rawMessage) {
        if (rawMessage is not SocketMessage message) {
            return;
        }
        if (message.Author.IsBot) {
            return;
        }

        if (mockMadeline) {
            if (message.Author.Id == 696836187481964544) {
                char[] realMessage = message.Content.ToLowerInvariant().ToCharArray();
                for (int iterator = 0; iterator < realMessage.Length; iterator++) {
                    if (Random.Shared.Next(2) == 0) {
                        realMessage[iterator] = Char.ToUpperInvariant(realMessage[iterator]);
                    }
                }
                await message.Channel.SendMessageAsync("\"" + new string(realMessage) + "\" okay dude 😭");
                return;
            }
        }

        //ulong userID = 696836187481964544;
        //if (message.MentionedUserIds.Contains(userID)) {
        //    await message.Channel.SendMessageAsync("<@" + userID + ">");
        //}
    }
}