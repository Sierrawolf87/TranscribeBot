using Microsoft.EntityFrameworkCore;
using TranscribeBot.Data;
using TranscribeBot.Interfaces;
using TranscribeBot.Hostedservices;
using TranscribeBot.Options;
using TranscribeBot.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<ITranscribeService, TranscribeService>();
builder.Services.AddScoped<IAiService, AiService>();
builder.Services.AddSingleton<IUserProcessingQueue, UserProcessingQueue>();
builder.Services.AddHostedService<TelegramBotHostedService>();

builder.Services
    .AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName))
    .ValidateOnStart();

builder.Services
    .AddOptions<OpenRouterOptions>()
    .Bind(builder.Configuration.GetSection(OpenRouterOptions.SectionName))
    .ValidateOnStart();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.Run();
