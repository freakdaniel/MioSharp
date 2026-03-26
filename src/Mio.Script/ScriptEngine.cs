using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Mio.Routing;

namespace Mio.Script;

/// <summary>
/// Hosts a Jint ES2021 engine with DOM bindings and in-process fetch routing.
/// </summary>
public sealed class ScriptEngine : IDisposable
{
    private readonly Engine _engine;
    private readonly Router _router;
    private readonly List<(TimeSpan at, JsValue fn)> _timers = [];
    private IDocument? _document;
    private DocumentShim? _documentShim;

    /// <summary>Fires whenever JS mutates the DOM (triggers re-layout).</summary>
    public event Action? DomChanged;

    public ScriptEngine(Router router)
    {
        _router = router;
        _engine = new Engine(cfg => cfg.AllowClrWrite());
    }

    /// <summary>Binds the DOM document and installs Web APIs into the JS global scope.</summary>
    public void SetDocument(IDocument document)
    {
        _document = document;
        InstallWebApis();
        InstallDomBindings();
    }

    /// <summary>Executes a JS script string.</summary>
    public void Execute(string script, string? sourceName = null)
    {
        try
        {
            _engine.Execute(script);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Script] Error{(sourceName != null ? $" in {sourceName}" : "")}: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers a C# delegate as window.mio.{name}() callable from JS.
    /// </summary>
    public void Register(string name, Delegate fn)
    {
        // Temporarily set as global, then attach to window.mio
        _engine.SetValue("__mio_reg_temp__", fn);
        _engine.Execute($"window.mio['{name}'] = __mio_reg_temp__;");
        _engine.SetValue("__mio_reg_temp__", JsValue.Undefined);
    }

    /// <summary>Fires pending setTimeout callbacks whose time has elapsed.</summary>
    public void Tick(TimeSpan elapsed)
    {
        var due = _timers.Where(t => t.at <= elapsed).ToList();
        foreach (var t in due)
        {
            _timers.Remove(t);
            try { _engine.Invoke(t.fn); }
            catch (Exception ex) { Console.Error.WriteLine($"[Timer] {ex.Message}"); }
        }
    }

    public void Dispose() => _engine.Dispose();

    private void InstallWebApis()
    {
        // console
        _engine.SetValue("console", new ConsoleShim());

        // window object
        _engine.Execute("var window = globalThis; window.mio = {};");

        // setTimeout / setInterval / clearTimeout / clearInterval
        _engine.SetValue("setTimeout", new Action<JsValue, int>((callback, delay) =>
        {
            _timers.Add((TimeSpan.FromMilliseconds(delay), callback));
        }));
        _engine.SetValue("clearTimeout", new Action<int>(_ => { }));
        _engine.SetValue("setInterval", new Action<JsValue, int>((callback, delay) =>
        {
            // Simple: schedule one shot; re-schedule on Tick for real interval
            _timers.Add((TimeSpan.FromMilliseconds(delay), callback));
        }));
        _engine.SetValue("clearInterval", new Action<int>(_ => { }));

        // Promise (basic implementation via globalThis.Promise already in Jint 4)
        _engine.Execute("if (typeof Promise === 'undefined') { Promise = { resolve: function(v){ return { then: function(f){ f(v); return this; }, catch: function(){ return this; } }; }, reject: function(e){ return { then: function(){ return this; }, catch: function(f){ f(e); return this; } }; } }; }");

        // fetch — in-process dispatch, returns a Promise-like object
        _engine.SetValue("__fetchImpl__", new Func<string, JsValue>((url) =>
        {
            try
            {
                var response = _router.Dispatch("GET", url);
                var body = System.Text.Json.JsonSerializer.Serialize(response.BodyText);
                var status = response.Status;
                var ok = status < 400 ? "true" : "false";
                var contentType = response.ContentType.Replace("'", "\\'");
                var script = $@"({{
ok: {ok},
status: {status},
headers: {{'content-type': '{contentType}'}},
json: function() {{ var b = {body}; try {{ return Promise.resolve(JSON.parse(b)); }} catch(e) {{ return Promise.reject(e); }} }},
text: function() {{ return Promise.resolve({body}); }}
}})";
                return _engine.Evaluate(script);
            }
            catch (Exception ex)
            {
                var msg = System.Text.Json.JsonSerializer.Serialize(ex.Message);
                return _engine.Evaluate($"({{ ok: false, status: 500, json: function(){{ return Promise.reject(new Error({msg})); }}, text: function(){{ return Promise.reject(new Error({msg})); }} }})");
            }
        }));
        _engine.Execute("function fetch(url, opts) { return Promise.resolve(__fetchImpl__(url)); }");

        // location stub
        _engine.Execute("window.location = { href: '/', pathname: '/', search: '', hash: '' };");
        _engine.Execute("window.history = { pushState: function(){}, replaceState: function(){} };");
        _engine.Execute("window.navigator = { userAgent: 'MioSharp/1.0' };");
    }

    private void InstallDomBindings()
    {
        if (_document == null) return;

        _documentShim = new DocumentShim(_document, _engine);
        _documentShim.DomMutated += () => DomChanged?.Invoke();
        _engine.SetValue("document", _documentShim);
        _engine.Execute("window.document = document;");
    }
}

internal sealed class ConsoleShim
{
    public void log(params object[] args) => Console.WriteLine("[JS] " + string.Join(" ", args));
    public void warn(params object[] args) => Console.WriteLine("[JS WARN] " + string.Join(" ", args));
    public void error(params object[] args) => Console.Error.WriteLine("[JS ERR] " + string.Join(" ", args));
    public void info(params object[] args) => Console.WriteLine("[JS INFO] " + string.Join(" ", args));
    public void debug(params object[] args) => Console.WriteLine("[JS DEBUG] " + string.Join(" ", args));
}
