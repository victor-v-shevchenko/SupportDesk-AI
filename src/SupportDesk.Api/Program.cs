using SupportDesk.Api.Endpoints;
using SupportDesk.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSupportDeskData();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await app.Services.MigrateSupportDeskDatabaseAsync(app.Lifetime.ApplicationStopping);
}

app.MapHealthEndpoints();

await app.RunAsync();
