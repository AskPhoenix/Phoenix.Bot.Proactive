using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Main.Types;
using Phoenix.DataHandle.Repositories;
using System.Globalization;

namespace Phoenix.Bot.Proactive.Controllers
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    [ApiController]
    [Route("api/broadcast")]
    public class BroadcastController : ControllerBase
    {
        private readonly CloudAdapter _adapter;
        private readonly ApplicationUserManager _userManager;
        private readonly BroadcastRepository _broadcastRepository;
        private readonly string _botAppId;

        private const string NotificationType = "REGULAR"; //REGULAR, SILENT_PUSH, NO_PUSH

        private string BroadcastMessage { get; set; } = null!;

        public BroadcastController(
            IBotFrameworkHttpAdapter adapter,
            ApplicationUserManager userManager, 
            PhoenixContext phoenixContext,
            IConfiguration configuration)
        {
            _adapter = (CloudAdapter)adapter;
            _userManager = userManager;
            _broadcastRepository = new(phoenixContext);
            _botAppId = configuration["MicrosoftAppId"] ?? string.Empty;
        }

        [HttpPost]
        [Route("{id}")]
        public async Task<IActionResult> PostByBroadcastIdAsync(int id)
        {
            var broadcast = await _broadcastRepository.FindPrimaryAsync(id);
            if (broadcast is null)
                return NotFound();

            try
            {
                await SendBroadcastAsync(broadcast, force: true);
            }
            catch
            {
                broadcast.Status = BroadcastStatus.Failed;
                await _broadcastRepository.UpdateAsync(broadcast);
            }

            return Ok(broadcast.Status);
        }

        [HttpPost]
        [Route("daypart/{daypart}")]
        public async Task<IActionResult> PostByDaypartAsync(Daypart daypart, string? date = null)
        {
            int successNum = 0;
            var d = DateTime.UtcNow.Date;

            if (!string.IsNullOrEmpty(date))
                if (!DateTime.TryParseExact(date, "dd-MM-yyyy", null, DateTimeStyles.AllowWhiteSpaces, out d))
                    return BadRequest("Date string is malformed. Format it as dd-MM-yyyy");

            var broadcasts = _broadcastRepository.Search(d, daypart);

            foreach (var broadcast in broadcasts)
            {
                try
                {
                    await SendBroadcastAsync(broadcast);
                    successNum++;
                }
                catch
                {
                    broadcast.Status = BroadcastStatus.Failed;
                    await _broadcastRepository.UpdateAsync(broadcast);
                }
            }
            
            return Ok(successNum);
        }

        private async Task SendBroadcastAsync(Broadcast broadcast, bool force = false)
        {
            if ((broadcast.Status == BroadcastStatus.Succeeded || broadcast.Status == BroadcastStatus.Cancelled) && !force)
                return;
            if (broadcast.Visibility == BroadcastVisibility.Hidden)
                return;
            if (broadcast.Audience == BroadcastAudience.None)
                return;

            broadcast.Status = BroadcastStatus.Processing;
            await _broadcastRepository.UpdateAsync(broadcast);

            var users = broadcast.Visibility == BroadcastVisibility.Group && broadcast.CourseId is not null
                ? broadcast.Course.Users.ToArray()
                : broadcast.School.Users.ToArray();

            var audience = new List<User>(users.Length);

            if (broadcast.Audience == BroadcastAudience.Everyone)
                audience.AddRange(users);
            else
            {
                var appUsers = new ApplicationUser[users.Length];
                for (int i = 0; i < users.Length; i++)
                    appUsers[i] = await _userManager.FindByIdAsync(users[i].AspNetUserId.ToString());

                bool toAdd = false;
                for (int i = 0; i < users.Length; i++, toAdd = false)
                {
                    var userRoles = await _userManager.GetRoleRanksAsync(appUsers[i]);

                    toAdd = userRoles.Any(rr => rr.IsSuper());
                    
                    toAdd |= broadcast.Audience switch
                    {
                        BroadcastAudience.Students          => userRoles.Any(rr => rr == RoleRank.Student),
                        BroadcastAudience.Parents           => userRoles.Any(rr => rr == RoleRank.Parent),
                        BroadcastAudience.Staff             => userRoles.Any(rr => rr.IsStaff()),
                        BroadcastAudience.StudentsParents   => userRoles.Any(rr => rr == RoleRank.Student || rr == RoleRank.Parent),
                        BroadcastAudience.StudentsStaff     => userRoles.Any(rr => rr == RoleRank.Student || rr.IsStaff()),
                        BroadcastAudience.ParentsStaff      => userRoles.Any(rr => rr == RoleRank.Parent || rr.IsStaff()),
                        _ => false
                    };

                    if (toAdd)
                        audience.Add(users[i]);
                }
            }
            
            var userKeys = audience.SelectMany(u => u.UserConnections)
                .Where(uc => uc.Channel == ChannelProvider.Facebook.ToString())
                .Where(uc => uc.ActivatedAt.HasValue)
                .Select(uc => uc.ChannelKey);

            BroadcastMessage = broadcast.Message;

            var schoolKeys = broadcast.School
                .SchoolConnections
                .Where(sc => sc.Channel == ChannelProvider.Facebook.ToString())
                .Select(sc => sc.ChannelKey);

            foreach (var userKey in userKeys)
            {
                foreach (var schoolKey in schoolKeys)
                {
                    var convRef = new ConversationReference()
                    {
                        Bot = new(id: schoolKey),
                        User = new(id: userKey),
                        Conversation = new(id: $"{userKey}-{schoolKey}"),
                        ChannelId = "facebook",
                        ServiceUrl = "https://facebook.botframework.com/"
                    };

                    await _adapter.ContinueConversationAsync(_botAppId, convRef, BotCallback, default);
                }
            }

            broadcast.SentAt = DateTime.UtcNow;
            broadcast.Status = BroadcastStatus.Succeeded;

            await _broadcastRepository.UpdateAsync(broadcast);
        }

        private async Task BotCallback(ITurnContext turnCtx,
            CancellationToken canTkn)
        {
            var activity = MessageFactory.SuggestedActions(
                new[] { "👍 OK" }, "📢 Ανακοίνωση: " + BroadcastMessage);

            activity.ChannelData = JObject.FromObject(new { notification_type = NotificationType });

            await turnCtx.SendActivityAsync(activity);
        }
    }
}
