using Bot.Builder.Community.Storage.EntityFramework;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Phoenix.DataHandle.Identity;
using Phoenix.DataHandle.Main.Models;
using System.Text;
using static Phoenix.DataHandle.Api.DocumentationHelper;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container

builder.Services
    .AddHttpClient()
    .AddControllers()
    .AddNewtonsoftJson();

Action<DbContextOptionsBuilder> buildDbContextOptions = o => o
    .UseLazyLoadingProxies()
    .UseSqlServer(builder.Configuration.GetConnectionString("PhoenixConnection"));

builder.Services.AddDbContext<ApplicationContext>(buildDbContextOptions);
builder.Services.AddDbContext<PhoenixContext>(buildDbContextOptions);
builder.Services.AddDbContext<BotDataContext>(buildDbContextOptions);

builder.Services.AddIdentity<ApplicationUser, ApplicationRole>()
    .AddRoles<ApplicationRole>()
    .AddUserStore<ApplicationStore>()
    .AddUserManager<ApplicationUserManager>()
    .AddEntityFrameworkStores<ApplicationContext>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateLifetime = true,
            ValidateAudience = false,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]))
        };
    });

builder.Services.AddApplicationInsightsTelemetry(
    o => o.ConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"]);

builder.Services.AddSingleton<IBotFrameworkHttpAdapter, CloudAdapter>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.EnableAnnotations();

    // SwaggerDoc name refers to the name of the documention and is included in the endpoint path
    o.SwaggerDoc("v3", new OpenApiInfo()
    {
        Title = "Phalarida API",
        Description = "A REST API to proactively brodcast messages to users from their school, or notify them about any updates",
        Version = "3.0"
    });

    o.AddSecurityDefinition(JWTSecurityScheme.Reference.Id, JWTSecurityScheme);

    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { JWTSecurityScheme, Array.Empty<string>() }
    });
});


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
    app.UseHsts();

app.UseSwagger(o => o.RouteTemplate = "doc/{documentname}/swagger.json");
app.UseSwaggerUI(
    o =>
    {
        o.SwaggerEndpoint("/doc/v3/swagger.json", "Phalarida v3");
        o.RoutePrefix = "doc";
    });


app.UseDefaultFiles()
    .UseStaticFiles()
    .UseWebSockets()
    .UseRouting()
    .UseAuthorization()
    .UseEndpoints(endpoints =>
    {
        endpoints.MapControllers();
    })
    .UseHttpsRedirection();

app.Run();