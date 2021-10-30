using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Phoenix.DataHandle.Main;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using System;
using System.Linq;
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

        private readonly BroadcastRepository broadcastRepository;
        private readonly SchoolRepository schoolRepository;
        private readonly AspNetUserRepository userRepository;

        private string BroadcastMessage { get; set; }
        private const string NotificationType = "REGULAR"; //REGULAR, SILENT_PUSH, NO_PUSH

        public BroadcastController(IBotFrameworkHttpAdapter adapter, 
            IConfiguration configuration, 
            PhoenixContext phoenixContext)
        {
            Adapter = adapter;
            BotAppId = configuration["MicrosoftAppId"] ?? string.Empty;

            this.broadcastRepository = new BroadcastRepository(phoenixContext);
            this.schoolRepository = new SchoolRepository(phoenixContext);
            this.userRepository = new AspNetUserRepository(phoenixContext);
        }

        [HttpPost]
        [Route("id/{broadcastId:int}")]
        public async Task<IActionResult> PostByBroadcastIdAsync(int broadcastId)
        {
            Broadcast broadcast;
            try
            {
                broadcast = await broadcastRepository.Find(broadcastId);
            }
            catch
            {
                return new BadRequestResult();
            }

            try
            {
                await SendBroadcastAsync(broadcast, force: true);
            }
            catch
            {
                return new StatusCodeResult((int)HttpStatusCode.InternalServerError);
            }

            return new OkResult();
        }

        [HttpPost]
        [Route("daypart/{daypartNum:int:range(1, 4)}")]
        public async Task<IActionResult> PostByDaypartAsync(int daypartNum)
        {
            //TODO: Take into account local time in DayPart

            //if (!Enum.IsDefined(typeof(Daypart), daypartNum))
            //    return new BadRequestResult();

            Daypart daypart = (Daypart)daypartNum;
            DateTime today = DateTimeOffset.UtcNow.Date;

            var broadcasts = broadcastRepository.FindForDateDaypart(today, daypart).ToList();

            try
            {
                foreach (var broadcast in broadcasts)
                    await SendBroadcastAsync(broadcast);
            }
            catch
            {
                return new StatusCodeResult((int)HttpStatusCode.InternalServerError);
            }

            return new OkObjectResult(broadcasts.Count);
        }

        private async Task SendBroadcastAsync(Broadcast broadcast, bool force = false)
        {
            //TODO: Support more channels than just Facebook

            if (broadcast.Status == BroadcastStatus.Sent && !force)
                return;
            if (broadcast.Visibility == BroadcastVisibility.Hidden)
                return;
            if (broadcast.Audience == BroadcastAudience.None)
                return;

            broadcast.Status = BroadcastStatus.Failed;
            broadcastRepository.Update(broadcast);

            // Find users to receive the message
            IQueryable<AspNetUsers> users = Enumerable.Empty<AspNetUsers>().AsQueryable();
            var students = Enumerable.Empty<AspNetUsers>().AsQueryable();
            var parents = Enumerable.Empty<AspNetUsers>().AsQueryable();
            var staff = Enumerable.Empty<AspNetUsers>().AsQueryable();

            if (broadcast.Visibility == BroadcastVisibility.Group)
            {
                switch (broadcast.Audience)
                {
                    case BroadcastAudience.Students:
                    case BroadcastAudience.Parents:
                    case BroadcastAudience.StudentsParents:
                    case BroadcastAudience.StudentsStaff:
                    case BroadcastAudience.ParentsStaff:
                        students = userRepository.FindStudentsForCourse((int)broadcast.CourseId);
                        break;
                }

                switch (broadcast.Audience)
                {
                    case BroadcastAudience.Parents:
                    case BroadcastAudience.StudentsParents:
                    case BroadcastAudience.ParentsStaff:
                        parents = userRepository.FindParents(students.Select(s => s.Id).ToList());
                        break;
                }

                switch (broadcast.Audience)
                {
                    case BroadcastAudience.Staff:
                    case BroadcastAudience.StudentsStaff:
                    case BroadcastAudience.ParentsStaff:
                        staff = userRepository.FindTeachersForCourse((int)broadcast.CourseId);
                        break;
                }

                users = broadcast.Audience switch
                {
                    BroadcastAudience.Students => students,
                    BroadcastAudience.Parents => parents,
                    BroadcastAudience.Staff => staff,
                    BroadcastAudience.StudentsParents => students.Concat(parents),
                    BroadcastAudience.StudentsStaff => students.Concat(staff),
                    BroadcastAudience.ParentsStaff => parents.Concat(staff),
                    BroadcastAudience.All => userRepository.FindAllForCourse((int)broadcast.CourseId),
                    _ => users
                };

                var superRoles = RoleExtensions.GetSuperRoles().ToArray();
                users = users.Where(u => !u.AspNetUserRoles.Any(ur => superRoles.Contains(ur.Role.Type)));
            }
            else if (broadcast.Visibility == BroadcastVisibility.Global)
            {
                users = userRepository.Find().
                    Where(u => u.UserSchool.Any(us => us.SchoolId == broadcast.SchoolId));

                if (broadcast.Audience != BroadcastAudience.All)
                {
                    Role[] visRoles = broadcast.Audience switch
                    {
                        BroadcastAudience.Students => new[] { Role.Student },
                        BroadcastAudience.Parents => new[] { Role.Parent },
                        BroadcastAudience.Staff => RoleExtensions.GetStaffRoles().ToArray(),
                        BroadcastAudience.StudentsParents => new[] { Role.Student, Role.Parent },
                        BroadcastAudience.StudentsStaff => RoleExtensions.GetStaffRoles().Append(Role.Student).ToArray(),
                        BroadcastAudience.ParentsStaff => RoleExtensions.GetStaffRoles().Append(Role.Parent).ToArray()
                    };

                    visRoles = visRoles.Concat(RoleExtensions.GetSchoolBackendRoles()).ToArray();
                    users = users.Where(u => u.AspNetUserRoles.Any(ur => visRoles.Contains(ur.Role.Type)));
                }
            }

            var userProviderKeys = users.
                SelectMany(u => u.AspNetUserLogins).
                Where(ul => ul.LoginProvider == LoginProvider.Facebook.ToString() && ul.IsActive).
                Select(ul => ul.ProviderKey);

            BroadcastMessage = broadcast.Message;
            string fbPageId = (await schoolRepository.Find(broadcast.SchoolId)).FacebookPageId;

            ConversationReference convRef = new()
            {
                Bot = new ChannelAccount(id: fbPageId),
                ChannelId = "facebook",
                ServiceUrl = "https://facebook.botframework.com/"
            };

            foreach (var userKey in userProviderKeys)
            {
                convRef.Conversation = new(id: $"{userKey}-{fbPageId}");
                convRef.User = new(id: userKey);

                await ((BotAdapter)Adapter).ContinueConversationAsync(BotAppId, convRef, BotCallback, default);
            }

            broadcast.Status = BroadcastStatus.Sent;
            broadcastRepository.Update(broadcast);
        }

        private async Task BotCallback(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            var activity = MessageFactory.SuggestedActions(new[] { "🏠 Αρχική" }, "📢 Ανακοίνωση: " + BroadcastMessage);
            activity.ChannelData = JObject.FromObject(new { notification_type = NotificationType });

            await turnContext.SendActivityAsync(activity);
        }
    }
}
