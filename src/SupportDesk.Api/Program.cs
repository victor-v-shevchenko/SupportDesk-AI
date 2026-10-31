using SupportDesk.Api;
using SupportDesk.Api.Endpoints;
using SupportDesk.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSupportDeskApi();
builder.Services.AddSupportDeskData();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await app.Services.MigrateSupportDeskDatabaseAsync(app.Lifetime.ApplicationStopping);
    await app.Services.SeedSupportDeskDatabaseAsync(app.Lifetime.ApplicationStopping);
}

app.MapHealthEndpoints();
app.MapTicketEndpoints();
app.MapArticleEndpoints();

await app.RunAsync();
