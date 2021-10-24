using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Phoenix.DataHandle.Main;
using Phoenix.DataHandle.Main.Models;
using Phoenix.DataHandle.Repositories;
using System.Linq;
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
        private string NotificationType { get; set; } = "REGULAR"; //REGULAR, SILENT_PUSH, NO_PUSH

        public BroadcastController(IBotFrameworkHttpAdapter adapter, 
            IConfiguration configuration, 
            PhoenixContext phoenixContext)
        {
            Adapter = adapter;
            BotAppId = configuration["MicrosoftAppId"] ?? string.Empty;

            this.broadcastRepository = new BroadcastRepository(phoenixContext);
            this.schoolRepository = new SchoolRepository(phoenixContext);
            this.userRepository = new AspNetUserRepository(phoenixContext);

            userRepository.Include(u => u.User);
        }

        [HttpGet]
        public async Task<IActionResult> Get(int id, bool force = false, bool include_backend = false)
        {
            //TODO: Convert DayPart to the correct local time
            //TODO: Support more channels than just Facebook

            Broadcast broadcast;
            try
            {
                broadcast = await broadcastRepository.Find(id);
            }
            catch 
            {
                return new BadRequestResult();
            }

            if (broadcast.Status == BroadcastStatus.Sent && !force)
                return new OkResult();
            if (broadcast.Visibility == BroadcastVisibility.Hidden)
                return new OkResult();
            if (broadcast.Audience == BroadcastAudience.None)
                return new OkResult();

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

                if (!include_backend)
                    users = users.Where(u => !u.AspNetUserRoles.Any(ur => ur.Role.Type.IsBackend()));
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

                    if (include_backend)
                        visRoles = visRoles.Concat(RoleExtensions.GetBackendRoles()).ToArray();

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

            return new OkResult();
        }

        private async Task BotCallback(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            var activity = MessageFactory.SuggestedActions(new[] { "🏠 Αρχική" }, "📢 Ανακοίνωση: " + BroadcastMessage);
            activity.ChannelData = JObject.FromObject(new { notification_type = NotificationType });

            await turnContext.SendActivityAsync(activity);
        }
    }
}
