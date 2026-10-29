using Alcifra.Infrastructure;
using Alcifra.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddInfrastructure(builder.Configuration);

var host = builder.Build();
host.Run();
