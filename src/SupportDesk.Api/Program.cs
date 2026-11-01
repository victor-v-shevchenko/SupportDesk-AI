using SupportDesk.Api;
using SupportDesk.Api.AI;
using SupportDesk.Api.Endpoints;
using SupportDesk.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSupportDeskApi();
builder.Services.AddSupportDeskData();
builder.Services.AddSupportDeskAi();
builder.Services.AddSupportDeskRetrieval();
builder.Services.AddSupportDeskClassification();

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
app.MapSearchEndpoints();
app.MapClassificationEndpoints();

await app.RunAsync();
