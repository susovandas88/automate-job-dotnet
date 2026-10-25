using System.Text;
using AutomateJob.Web.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
AutomateJob.Web.Jobs.WebEnvFile.Load(builder.Environment.ContentRootPath);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<AccountStore>();

var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = string.IsNullOrWhiteSpace(auth.Issuer) ? "automate-job" : auth.Issuer,
            ValidateAudience = true,
            ValidAudience = string.IsNullOrWhiteSpace(auth.Audience) ? "automate-job" : auth.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(auth.JwtKey) ? "missing-signing-key-not-for-use" : auth.JwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier) is null)
                {
                    context.Fail("Sign in again.");
                }

                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token) && context.Request.Cookies.TryGetValue(JwtTokenService.CookieName, out var cookie))
                {
                    context.Token = cookie;
                }

                return Task.CompletedTask;
            },
            OnForbidden = context =>
            {
                context.Response.Redirect("/Jobs");
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                var target = context.Request.Path + context.Request.QueryString;
                var login = "/Login?returnUrl=" + Uri.EscapeDataString(target);
                context.Response.Redirect(login);
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Logout");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Privacy");
});
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
builder.Services.Configure<AutomateJob.Web.Matching.LlmOptions>(
    builder.Configuration.GetSection(AutomateJob.Web.Matching.LlmOptions.SectionName));
builder.Services.AddHttpClient<AutomateJob.Web.Matching.LlmJobMatcher>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddSingleton<AutomateJob.Web.Matching.ResumeTextReader>();
builder.Services.AddSingleton<AutomateJob.Web.Matching.ResumeLibrary>();
builder.Services.AddHttpClient<AutomateJob.Web.Jobs.LinkedInJobBoard>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(40);
});
builder.Services.AddSingleton<AutomateJob.Web.Jobs.JobStore>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.EvaluationStore>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.ApplicationStore>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.ApplicantProfileStore>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.AnswersBank>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.LangGraphApplyAgent>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.LinkedInSession>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.JobFetchRunner>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.NaukriJobBoard>();
builder.Services.AddSingleton<AutomateJob.Web.Jobs.NaukriFetchRunner>();

var app = builder.Build();
app.Services.GetRequiredService<AutomateJob.Web.Jobs.JobStore>();
app.Services.GetRequiredService<AutomateJob.Web.Jobs.EvaluationStore>();
var accounts = app.Services.GetRequiredService<AccountStore>();
if (accounts.SeededUserId is long resumeOwnerId)
{
    app.Services.GetRequiredService<AutomateJob.Web.Matching.ResumeLibrary>().ClaimUnowned(resumeOwnerId);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
