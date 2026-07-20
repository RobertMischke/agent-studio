namespace AgentStudio.Security;

public static class AccessSecurityEndpoints
{
    public static void MapAccessSecurityEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/status", (HttpContext context, AccessSecurityStore store, IConfiguration configuration) =>
        {
            var principal = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal
                            ?? store.AuthenticateSession(context.Request.Cookies[AccessSecurityStore.SessionCookieName], touch: false);
            return Results.Ok(new AuthStatusResponse(
                SecurityProfiles.IsNetworked(configuration) ? SecurityProfiles.Networked : SecurityProfiles.Local,
                store.BootstrapRequired,
                principal is not null,
                principal is null ? null : ToResponse(principal.User)));
        });

        auth.MapPost("/bootstrap", (BootstrapRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var created = store.Bootstrap(request);
                SetSessionCookies(context, created.SessionToken, created.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.Networked, false, true, ToResponse(created.User), created.CsrfToken));
            }));

        auth.MapPost("/login", (LoginRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() =>
            {
                var key = $"{(request.Username ?? string.Empty).Trim().ToLowerInvariant()}|{context.Connection.RemoteIpAddress}";
                var result = store.Login(request.Username ?? string.Empty, request.Password ?? string.Empty, key);
                SetSessionCookies(context, result.SessionToken, result.CsrfToken);
                return Results.Ok(new AuthStatusResponse(SecurityProfiles.Networked, false, true, ToResponse(result.User), result.CsrfToken));
            }));

        auth.MapGet("/session", (HttpContext context) =>
        {
            var principal = RequireHuman(context);
            return Results.Ok(new AuthStatusResponse(SecurityProfiles.Networked, false, true, ToResponse(principal.User)));
        });

        auth.MapPost("/logout", (HttpContext context, AccessSecurityStore store) =>
        {
            store.Logout(RequireHuman(context).Session);
            context.Response.Cookies.Delete(AccessSecurityStore.SessionCookieName, SessionCookieOptions(https: true));
            context.Response.Cookies.Delete("__Host-agentstudio-csrf", CsrfCookieOptions(https: true));
            context.Response.Cookies.Delete(AccessSecurityStore.InsecureSessionCookieName, SessionCookieOptions(https: false));
            context.Response.Cookies.Delete("agentstudio-csrf", CsrfCookieOptions(https: false));
            return Results.NoContent();
        });

        auth.MapPost("/change-password", (ChangePasswordRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(ToResponse(store.ChangePassword(RequireHuman(context), request)))));

        auth.MapGet("/users", (AccessSecurityStore store) =>
            Results.Ok(store.ListUsers().Select(ToResponse)));

        auth.MapPost("/users", (CreateUserRequest request, AccessSecurityStore store) =>
            Execute(() =>
            {
                var result = store.CreateUser(request);
                return Results.Ok(new { user = ToResponse(result.User), temporaryPassword = result.TemporaryPassword, mustChangePassword = true });
            }));

        auth.MapPut("/users/{id}", (string id, UpdateUserRequest request, HttpContext context, AccessSecurityStore store) =>
            Execute(() => Results.Ok(ToResponse(store.UpdateUser(id, request, RequireHuman(context).User.Id)))));

        auth.MapPost("/users/{id}/reset-password", (string id, PasswordResetRequest request, AccessSecurityStore store) =>
            Execute(() =>
            {
                var reset = store.ResetPassword(id, request);
                return Results.Ok(new PasswordResetResponse(reset.User.Id, reset.TemporaryPassword, true));
            }));

        auth.MapPost("/runner-enrollments", (RunnerEnrollmentRequest request, AccessSecurityStore store) =>
            Execute(() =>
            {
                var created = store.CreateEnrollment(request);
                return Results.Ok(new OneTimeEnrollmentResponse(created.Code, created.Enrollment.Name, created.Enrollment.Scopes, created.Enrollment.ExpiresAt));
            }));

        auth.MapPost("/runner-enroll", (RunnerEnrollRequest request, AccessSecurityStore store) =>
            Execute(() =>
            {
                var enrolled = store.EnrollRunner(request.Code);
                return Results.Ok(new OneTimeSecretResponse(enrolled.Runner.Id, enrolled.Runner.Name, enrolled.Credential.Id,
                    enrolled.Secret, enrolled.Credential.Scopes, enrolled.Credential.ExpiresAt));
            }));

        auth.MapGet("/runners", (AccessSecurityStore store) => Results.Ok(store.ListRunners().Select(ToRunnerResponse)));

        // Provisioning can prove the authenticated service identity without
        // reopening the legacy client registry. No secret material is echoed.
        auth.MapGet("/runner", (HttpContext context) =>
        {
            if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal runner)
                return Results.Json(new { error = "runner-authentication-required" }, statusCode: StatusCodes.Status401Unauthorized);
            return Results.Ok(new
            {
                id = runner.RunnerId,
                name = runner.RunnerName,
                credentialId = runner.CredentialId,
                scopes = runner.Scopes.Order(StringComparer.Ordinal)
            });
        });

        auth.MapPost("/runners/{id}/credentials", (string id, RunnerRotateRequest request, AccessSecurityStore store) =>
            Execute(() =>
            {
                var rotated = store.RotateRunner(id, request);
                return Results.Ok(new OneTimeSecretResponse(rotated.Runner.Id, rotated.Runner.Name, rotated.Credential.Id,
                    rotated.Secret, rotated.Credential.Scopes, rotated.Credential.ExpiresAt));
            }));

        auth.MapDelete("/runners/{id}/credentials/{credentialId}", (string id, string credentialId, AccessSecurityStore store) =>
            Execute(() => { store.RevokeCredential(id, credentialId); return Results.NoContent(); }));

        auth.MapDelete("/runners/{id}", (string id, AccessSecurityStore store) =>
            Execute(() => { store.RevokeRunner(id); return Results.NoContent(); }));
    }

    private static IResult Execute(Func<IResult> operation)
    {
        try { return operation(); }
        catch (SecurityOperationException ex) { return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status); }
    }

    private static HumanPrincipal RequireHuman(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal
           ?? throw new SecurityOperationException(401, "authentication-required", "A human session is required.");

    private static AuthUserResponse ToResponse(StudioUser user)
        => new(user.Id, user.Username, user.DisplayName, user.Role, user.Projects, user.Disabled, user.MustChangePassword);

    private static object ToRunnerResponse(RunnerServiceIdentity runner) => new
    {
        runner.Id, runner.Name, runner.CreatedAt, runner.RevokedAt,
        credentials = runner.Credentials.Select(x => new { x.Id, x.Scopes, x.CreatedAt, x.ExpiresAt, x.LastUsedAt, x.RevokedAt })
    };

    private static void SetSessionCookies(HttpContext context, string token, string csrfToken)
    {
        // __Host--Cookies erfordern Secure+HTTPS; ueber HTTP (lokaler Dev-Betrieb
        // via ng-serve-Proxy) wuerde der Browser sie stumm verwerfen und der Login
        // bliebe wirkungslos. Namen und Secure-Flag folgen daher dem Schema.
        var https = context.Request.IsHttps;
        context.Response.Cookies.Append(
            https ? AccessSecurityStore.SessionCookieName : AccessSecurityStore.InsecureSessionCookieName,
            token, SessionCookieOptions(https));
        context.Response.Cookies.Append(
            https ? "__Host-agentstudio-csrf" : "agentstudio-csrf",
            csrfToken, CsrfCookieOptions(https));
        context.Response.Headers.CacheControl = "no-store";
    }

    private static CookieOptions SessionCookieOptions(bool https) => new()
    {
        HttpOnly = true,
        Secure = https,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/"
    };

    private static CookieOptions CsrfCookieOptions(bool https) => new()
    {
        HttpOnly = false,
        Secure = https,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/"
    };
}
