using SupportDesk.Api;
using SupportDesk.Api.AI;
using SupportDesk.Api.Endpoints;
using SupportDesk.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSupportDeskApi();
builder.Services.AddSupportDeskData();
builder.Services.AddSupportDeskAi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await app.Services.MigrateAndSeedSupportDeskDatabaseAsync(app.Lifetime.ApplicationStopping);
}

app.MapHealthEndpoints();
app.MapTicketEndpoints();
app.MapArticleEndpoints();
app.MapModelCallEndpoints();

await app.RunAsync();
