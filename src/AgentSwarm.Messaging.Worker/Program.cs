using AgentSwarm.Messaging.Persistence;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMessagingPersistence(builder.Configuration);

var host = builder.Build();
host.Run();
