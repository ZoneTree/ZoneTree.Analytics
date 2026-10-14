builder.Services.AddZoneTreeAnalytics(
    options => options.DefaultSite = "public",
    configureWeb: web =>
    {
        web.SiteResolver = context => context.Request.Host.Host;
        web.AuthorizeSite = (context, site) =>
            context.User.HasClaim("analytics-site", site);
    });

// Configure the host's authentication and authorization policies first.
app.MapZoneTreeAnalyticsApi().RequireAuthorization();

