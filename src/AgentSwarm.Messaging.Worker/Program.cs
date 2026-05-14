using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMessagingPersistence(builder.Configuration);
builder.Services.AddTelegram(builder.Configuration);

var host = builder.Build();
host.Run();
