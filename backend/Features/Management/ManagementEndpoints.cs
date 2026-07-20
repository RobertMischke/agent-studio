namespace AgentStudio.Management;

public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this WebApplication app)
    {
        app.MapGet("/recovery", () => Results.Content(RecoveryConsole.Html, "text/html"));
        var group = app.MapGroup("/api/v1/management");
        group.MapGet("/status", (HttpContext context, ManagementService service) =>
        {
            if (!TryAuthorize(context, mutation: false, out var denied, out _)) return denied!;
            return Results.Ok(service.Snapshot($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"));
        });
        group.MapGet("/diagnostics", (HttpContext context, ManagementService service) =>
        {
            if (!TryAuthorize(context, mutation: false, out var denied, out _)) return denied!;
            return Results.Ok(service.Diagnostics());
        });
        group.MapPost("/commands", (HttpContext context, ManagementCommandRequest request, ManagementService service) =>
        {
            if (!TryAuthorize(context, mutation: true, out var denied, out var actor)) return denied!;
            try
            {
                return Results.Ok(service.Execute(request, actor!, context.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? ""));
            }
            catch (ManagementException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        });
    }

    private static bool TryAuthorize(HttpContext context, bool mutation, out IResult? denied, out string? actor)
    {
        actor = context.Items["ClientId"]?.ToString();
        var principal = context.Items["AccessSecurity.HumanPrincipal"];
        if (principal is not null)
        {
            var user = principal.GetType().GetProperty("User")?.GetValue(principal);
            actor = user?.GetType().GetProperty("Id")?.GetValue(user)?.ToString() ?? actor;
            var role = user?.GetType().GetProperty("Role")?.GetValue(user)?.ToString();
            if (mutation && role is not ("owner" or "operator"))
            {
                denied = Results.Json(new { error = "management-role-required" }, statusCode: 403);
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(actor))
        {
            denied = Results.Json(new { error = "authentication-required" }, statusCode: 401);
            return false;
        }
        denied = null;
        return true;
    }
}

internal static class RecoveryConsole
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Task Server recovery</title><style>
:root{color-scheme:light dark;font:15px system-ui;--bg:#111827;--card:#1f2937;--fg:#f9fafb;--muted:#9ca3af;--accent:#60a5fa;--bad:#f87171}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg)}main{padding:2rem;max-width:1100px;margin:auto}header,section{background:var(--card);border:1px solid #374151;border-radius:12px;padding:1rem;margin-bottom:1rem}h1,h2{margin:.2rem 0 1rem}small,.muted{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:1rem}dl{display:grid;grid-template-columns:auto 1fr;gap:.5rem}dd{margin:0;text-align:right;overflow-wrap:anywhere}button,input{font:inherit;padding:.65rem;border-radius:7px;border:1px solid #4b5563}button{background:var(--accent);color:#071526;font-weight:700;cursor:pointer}button.danger{background:var(--bad)}button:disabled{opacity:.5}input{background:transparent;color:inherit}.actions{display:flex;flex-wrap:wrap;gap:.5rem}.error{color:var(--bad)}pre{white-space:pre-wrap;overflow-wrap:anywhere} @media(prefers-color-scheme:light){:root{--bg:#f3f4f6;--card:#fff;--fg:#111827;--muted:#4b5563}}
</style></head><body><main><header><h1>Task Server bootstrap and recovery</h1><p class="muted">This console uses the authenticated management API. The operating system service manager owns process start and restart.</p></header>
<section id="auth"><h2>Owner session</h2><div class="actions"><input id="user" placeholder="Username"><input id="password" type="password" placeholder="Password"><button onclick="login()">Login</button><button onclick="bootstrap()">Create first owner</button></div><p id="authResult" class="muted"></p></section>
<section><div class="actions"><button onclick="load()">Refresh authoritative state</button><button onclick="diagnostics()">Recovery diagnostics</button></div><p id="error" class="error"></p></section>
<div class="grid"><section><h2>Health</h2><dl id="health"></dl></section><section><h2>Store</h2><dl id="store"></dl></section><section><h2>Credentials and Runners</h2><div id="runners"></div></section><section><h2>Backup and migration</h2><div id="backup"></div></section></div>
<section><h2>Audited commands</h2><p class="muted">Every button previews first. Confirming requires a second click and a fresh idempotency key.</p><div class="actions" id="actions"></div><pre id="result"></pre></section>
</main><script>
let csrf='';const commands=['backup-create','restore-verify','backup-retention','archive-sweep','orphan-sweep','fixture-sweep','maintenance-enter','maintenance-read-only','maintenance-exit','shutdown-prepare'];
const headers=()=>({'Content-Type':'application/json','X-Client-Id':'local-default',...(csrf?{'X-CSRF-Token':csrf}:{})});
async function call(path,options={}){const r=await fetch(path,{credentials:'same-origin',...options,headers:{...headers(),...(options.headers||{})}});const body=await r.json().catch(()=>({}));if(!r.ok)throw new Error(body.message||body.error||r.statusText);return body}
async function login(){return authenticate('/api/auth/login')}async function bootstrap(){return authenticate('/api/auth/bootstrap')}
async function authenticate(path){try{const body=await call(path,{method:'POST',body:JSON.stringify({username:user.value,password:password.value,displayName:user.value})});csrf=body.csrfToken||body.csrf||'';authResult.textContent='Authenticated. Refreshing management state.';await load()}catch(e){authResult.textContent=e.message}}
function rows(obj,keys){return keys.map(k=>`<dt>${k}</dt><dd>${obj?.[k]??'-'}</dd>`).join('')}
async function load(){try{error.textContent='';const s=await call('/api/v1/management/status');health.innerHTML=rows({...s.server,...s.health},['id','url','version','protocolMinimum','protocolMaximum','uptimeSeconds','state','ready']);store.innerHTML=rows(s.store,['sizeBytes','projectCount','taskCount','archivedTaskCount','eventCount','artifactCount','identityCount']);runners.innerHTML=(s.runners||[]).map(x=>`<p><b>${x.displayName}</b><br><small>${x.state}, last used ${x.lastUsedAt||'never'}, active ${x.activeSlots}</small></p>`).join('')||'<p>No enrolled Runners.</p>';backup.innerHTML=`<p>Maintenance: <b>${s.maintenance.mode}</b></p><p>Backups: <b>${s.backups.items.length}</b>, failure: ${s.backups.lastFailure||'none'}</p><p>Migrations: <b>${s.migrations.length}</b></p>`}catch(e){error.textContent=e.message}}
async function diagnostics(){try{result.textContent=JSON.stringify(await call('/api/v1/management/diagnostics'),null,2)}catch(e){error.textContent=e.message}}
async function run(kind,apply=false){try{const key=crypto.randomUUID();const body={kind,dryRun:!apply,confirmation:apply?kind:null,idempotencyKey:key};const r=await call('/api/v1/management/commands',{method:'POST',headers:{'Idempotency-Key':key},body:JSON.stringify(body)});result.textContent=JSON.stringify(r,null,2);if(!apply){const b=document.createElement('button');b.className='danger';b.textContent=`Confirm ${kind}`;b.onclick=()=>{b.remove();run(kind,true)};actions.appendChild(b)}else await load()}catch(e){error.textContent=e.message}}
commands.forEach(kind=>{const b=document.createElement('button');b.textContent=`Preview ${kind}`;b.onclick=()=>run(kind);actions.appendChild(b)});load();
</script></body></html>
""";
}
