using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Bot.Proactive.Controllers
{
    [Route("broadcast")]
    [ApiController]
    public class BroadcastController : ControllerBase
    {
        private readonly IBotFrameworkHttpAdapter Adapter;
        private readonly string BotAppId;

        public BroadcastController(IBotFrameworkHttpAdapter adapter, IConfiguration configuration)
        {
            Adapter = adapter;
            BotAppId = configuration["MicrosoftAppId"] ?? string.Empty;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var conversationReference = new ConversationReference()
            {
                Bot = new ChannelAccount(id: "1998322767104444"),
                ChannelId = "facebook",
                Conversation = new ConversationAccount(id: "1824061630972169-1998322767104444"),
                ServiceUrl = "https://facebook.botframework.com/",
                User = new ChannelAccount(id: "1824061630972169", name: "Θεόφιλος Σπύρου")
                //User = new ChannelAccount(id: "660706657386972", name: "Μεταξάς Γαμβρέλης")
            };

            await ((BotAdapter)Adapter).ContinueConversationAsync(
                BotAppId, conversationReference, BotCallback, default);

            // Let the caller know proactive messages have been sent
            return new ContentResult()
            {
                Content = "<html><body><h1>Proactive messages have been sent.</h1></body></html>",
                ContentType = "text/html",
                StatusCode = (int)HttpStatusCode.OK,
            };
        }

        private async Task BotCallback(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            string broadcastMessage = "📢 Ανακοίνωση: Από το εξωτερικό service!";
            var activity = MessageFactory.SuggestedActions(new string[1] { "🏠 Αρχική" }, broadcastMessage);

            activity.ChannelData = JObject.FromObject(new
            {
                //REGULAR, SILENT_PUSH, NO_PUSH
                notification_type = "REGULAR"
            });

            await turnContext.SendActivityAsync(activity);
        }
    }
}
